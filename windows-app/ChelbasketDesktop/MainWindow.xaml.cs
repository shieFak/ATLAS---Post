using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ChelbasketDesktop;

public partial class MainWindow : Window
{
    private readonly ChelbasketClient _client = new();
    private List<GameRecord> _games = [];
    private string? _lastOutputFolder;
    private bool _browserReady;
    private bool _editingAutoFields;
    private bool _userEditedIntro;
    private bool _userEditedHashtags;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        GameDatePicker.SelectedDate = DateTime.Today;
        OutputFolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChelbasketPackages");
        StatusText.Text = "Загружаю список турниров…";
        try
        {
            var tournaments = await _client.GetTournamentsAsync();
            TournamentCombo.ItemsSource = tournaments;
            if (tournaments.Count > 0) TournamentCombo.SelectedIndex = 0;
            RefreshAutoFields();
            StatusText.Text = $"Турниров в списке: {tournaments.Count}. Выбери дату и нажми «Найти игры».";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось загрузить сайт федерации.";
            MessageBox.Show(this,
                $"Не удалось загрузить список турниров с сайта федерации. Проверь подключение к интернету и попробуй ещё раз.\n\n{ex.Message}",
                "Нет доступа к расписанию", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Inputs_Changed(object sender, RoutedEventArgs e)
    {
        if (!_editingAutoFields)
        {
            if (ReferenceEquals(sender, IntroBox)) _userEditedIntro = true;
            if (ReferenceEquals(sender, HashtagsBox)) _userEditedHashtags = true;
            if (ReferenceEquals(sender, TournamentCombo) || ReferenceEquals(sender, GameDatePicker)) RefreshAutoFields();
        }
        UpdatePostPreview();
    }

    private void RefreshAutoFields()
    {
        if (TournamentCombo.SelectedItem is not TournamentInfo tournament || GameDatePicker.SelectedDate is not DateTime date) return;
        _editingAutoFields = true;
        try
        {
            if (!_userEditedIntro) IntroBox.Text = PostBuilder.DefaultIntro(tournament.Name, date);
            if (!_userEditedHashtags) HashtagsBox.Text = PostBuilder.DefaultHashtags(tournament.Name);
        }
        finally { _editingAutoFields = false; }
    }

    private async void FindGames_Click(object sender, RoutedEventArgs e)
    {
        if (TournamentCombo.SelectedItem is not TournamentInfo tournament)
        {
            MessageBox.Show(this, "Выбери турнир.", "Нужен турнир", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (GameDatePicker.SelectedDate is not DateTime date)
        {
            MessageBox.Show(this, "Выбери дату игр.", "Нужна дата", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        WorkProgress.Value = 0;
        StatusText.Text = $"Ищу игры: {PostBuilder.DisplayTournament(tournament.Name)}, {date:dd.MM.yyyy}…";
        try
        {
            var games = await _client.GetGamesForDateAsync(tournament, date);
            _games = games.ToList();
            GamesGrid.ItemsSource = _games;
            CreatePackageButton.IsEnabled = _games.Count > 0;
            CopyPostButton.IsEnabled = _games.Count > 0;
            SummaryText.Text = _games.Count == 0
                ? "На эту дату игры не найдены."
                : $"{PostBuilder.DisplayTournament(tournament.Name)} · {date:dd.MM.yyyy} · игр: {_games.Count}";
            WorkProgress.Value = 0;
            StatusText.Text = _games.Count == 0
                ? "Проверь турнир и дату или выбери другой день."
                : "Матчи загружены. Проверь ссылки и детали поста, затем собери пакет.";
            PreviewTabs.SelectedIndex = 0;
            UpdatePostPreview();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось прочитать список игр.";
            MessageBox.Show(this, $"Ошибка при чтении списка игр.\n\n{ex.Message}", "Не удалось загрузить игры", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { SetBusy(false); }
    }

    private async void CreatePackage_Click(object sender, RoutedEventArgs e)
    {
        if (_games.Count == 0 || TournamentCombo.SelectedItem is not TournamentInfo tournament || GameDatePicker.SelectedDate is not DateTime date)
            return;

        var firstLink = FirstLinkBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(firstLink) && !PostBuilder.TryExpandStreamLink(firstLink, 0, out _))
        {
            MessageBox.Show(this,
                "Вставь полную ссылку вида https://vkvideo.ru/live-123456789_456239001 или оставь поле пустым. Последний ID ссылки будет увеличиваться на 1 для каждой следующей игры.",
                "Проверь ссылку на трансляцию", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var outputRoot = OutputFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot) || !Path.IsPathRooted(outputRoot))
        {
            MessageBox.Show(this, "Укажи папку для сохранения результатов.", "Нужна папка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        _lastOutputFolder = null;
        var games = _games.ToList();
        string? packageFolder = null;
        var errors = new List<string>();
        try
        {
            packageFolder = PackageBuilder.CreatePackageFolder(outputRoot, tournament.Name, date);
            _lastOutputFolder = packageFolder;

            try
            {
                await EnsureBrowserAsync();
                PreviewTabs.SelectedItem = BrowserTab;
            }
            catch (Exception ex)
            {
                errors.Add($"Не запустился WebView2: {ex.Message}");
            }

            for (var index = 0; index < games.Count; index++)
            {
                var game = games[index];
                WorkProgress.Value = index * 75.0 / games.Count;
                StatusText.Text = $"Обложка {index + 1} из {games.Count}: {game.Time} · {game.HomeTeam} — {game.GuestTeam}";
                game.Status = "Загружается…";
                GamesGrid.Items.Refresh();

                if (!_browserReady)
                {
                    game.Status = "Нет браузера";
                    continue;
                }

                try
                {
                    await NavigateToCoverAsync(game.CoverUrl);
                    await WaitForCoverReadyAsync();
                    await UpdateTeamsFromCoverPageAsync(game);
                    var targetPath = Path.Combine(packageFolder, PackageBuilder.CoverFileName(game));
                    await TriggerCoverDownloadAsync(targetPath);
                    await VerifyPngAsync(targetPath);
                    game.CoverPath = targetPath;
                    game.Status = "Скачана";
                }
                catch (Exception ex)
                {
                    game.Status = "Ошибка";
                    errors.Add($"{game.Number:00}. {game.Time} {game.HomeTeam} - {game.GuestTeam}: {ex.Message}");
                    TryDeletePartialCover(packageFolder, game);
                }

                GamesGrid.Items.Refresh();
                UpdatePostPreview();
                await Task.Delay(180);
            }

            var scheduleDownloaded = false;
            if (_browserReady)
            {
                try
                {
                    WorkProgress.Value = 80;
                    StatusText.Text = "Формирую расписание на день…";
                    var category = await DownloadScheduleAsync(tournament, date, packageFolder);
                    scheduleDownloaded = true;
                    StatusText.Text = $"Расписание скачано · {category}";
                }
                catch (Exception ex)
                {
                    errors.Add($"Расписание на день: {ex.Message}");
                }
            }

            WorkProgress.Value = 92;
            StatusText.Text = "Сохраняю тексты и собираю ZIP…";
            var result = await PackageBuilder.WritePackageAsync(
                packageFolder, tournament.Name, date, games,
                IntroBox.Text, VenueBox.Text, HashtagsBox.Text, firstLink);
            _lastOutputFolder = result.FolderPath;
            WorkProgress.Value = 100;
            PreviewTabs.SelectedIndex = 0;
            OpenOutputButton.IsEnabled = true;
            var scheduleStatus = scheduleDownloaded ? "расписание скачано" : "без расписания";
            StatusText.Text = $"Найдено {result.GameCount} игр, скачано {result.CoverCount} обложек, {scheduleStatus}. Папка и ZIP готовы.";
            SummaryText.Text = $"Готово · {result.GameCount} игр · {result.CoverCount} обложек · {scheduleStatus}";

            if (errors.Count > 0)
            {
                var details = string.Join(Environment.NewLine, errors.Take(12));
                if (errors.Count > 12) details += Environment.NewLine + $"…и ещё {errors.Count - 12} ошибок.";
                MessageBox.Show(this,
                    $"Пакет собран, но некоторые материалы не скачались. Тексты и список ID включают все игры.\n\n{details}",
                    "Готово с ошибками", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось собрать пакет.";
            MessageBox.Show(this,
                $"Пакет не удалось завершить. Уже скачанные файлы сохранены в выбранной папке.\n\n{ex.Message}",
                "Ошибка сборки пакета", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            WorkProgress.Value = 0;
            SetBusy(false);
            CreatePackageButton.IsEnabled = _games.Count > 0;
        }
    }

    private async Task EnsureBrowserAsync()
    {
        if (_browserReady) return;
        PreviewTabs.SelectedItem = BrowserTab;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChelbasketTV", "WebView2");
        Directory.CreateDirectory(userData);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        await CoverBrowser.EnsureCoreWebView2Async(environment);
        CoverBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        CoverBrowser.CoreWebView2.Settings.IsStatusBarEnabled = false;
        CoverBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _browserReady = true;
    }

    private async Task NavigateToCoverAsync(string url)
    {
        var core = CoverBrowser.CoreWebView2 ?? throw new InvalidOperationException("Встроенный браузер не инициализирован.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, args) =>
        {
            if (handler is not null) core.NavigationCompleted -= handler;
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new IOException($"Страница обложки не загрузилась: {args.WebErrorStatus}."));
        };
        core.NavigationCompleted += handler;
        core.Navigate(url);
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private async Task<string> DownloadScheduleAsync(TournamentInfo tournament, DateTime date, string packageFolder)
    {
        var uri = new Uri(tournament.Url);
        var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
        query["datefrom"] = date.ToString("dd.MM.yyyy");
        query["dateto"] = date.ToString("dd.MM.yyyy");
        query["tab"] = "0";
        var scheduleUri = new UriBuilder(uri)
        {
            Path = "/games-schedule/schedule2022/",
            Query = query.ToString() ?? string.Empty
        }.Uri;

        await NavigateToCoverAsync(scheduleUri.ToString());
        var core = CoverBrowser.CoreWebView2 ?? throw new InvalidOperationException("Встроенный браузер не инициализирован.");
        await WaitForScheduleFormAsync();

        var tournamentTitle = PostBuilder.DisplayTournament(tournament.Name);
        var titleSet = await core.ExecuteScriptAsync(
            $"(() => {{ const el=document.querySelector('#leaguename'); if(!el) return false; el.value={JsonSerializer.Serialize(tournamentTitle)}; for(const type of ['input','keyup','change']) el.dispatchEvent(new Event(type,{{bubbles:true}})); return true; }})()");
        if (!titleSet.Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Не найдено поле названия турнира в форме расписания.");

        var categoryChoice = (ScheduleCategoryCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Автоматически (из сайта)";
        if (!categoryChoice.StartsWith("Автоматически", StringComparison.CurrentCultureIgnoreCase))
        {
            var categorySet = await core.ExecuteScriptAsync(
                $"(() => {{ const el=document.querySelector('#categorytitle'); if(!el) return false; el.value={JsonSerializer.Serialize(categoryChoice)}; for(const type of ['input','keyup','change']) el.dispatchEvent(new Event(type,{{bubbles:true}})); return true; }})()");
            if (!categorySet.Equals("true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Не найдено поле категории на форме расписания.");
        }

        var submitResult = await core.ExecuteScriptAsync(
            "(() => { const form=document.querySelector('#form-schedule'); if(!form) return false; const button=form.querySelector('#submit'); if(button) button.click(); else form.requestSubmit(); return true; })()");
        if (!submitResult.Equals("true", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Не найдена форма формирования расписания.");

        await WaitForScheduleReadyAsync();
        var categoryRaw = await core.ExecuteScriptAsync("document.querySelector('#categorytitle')?.value || ''");
        var category = JsonSerializer.Deserialize<string>(categoryRaw)?.Trim() ?? string.Empty;
        var targetPath = Path.Combine(packageFolder, PackageBuilder.ScheduleFileName(tournament.Name, date, category));
        await TriggerCoverDownloadAsync(targetPath);
        await VerifyPngAsync(targetPath);
        return string.IsNullOrWhiteSpace(category) ? "категория сайта" : category;
    }

    private async Task WaitForScheduleFormAsync()
    {
        var core = CoverBrowser.CoreWebView2 ?? throw new InvalidOperationException("Встроенный браузер не инициализирован.");
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var ready = await core.ExecuteScriptAsync(
                "document.readyState==='complete' && !!document.querySelector('#form-schedule') && !!document.querySelector('#leaguename') && !!document.querySelector('#categorytitle')");
            if (ready.Equals("true", StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("Не дождался формы нового расписания.");
    }

    private async Task WaitForScheduleReadyAsync()
    {
        var core = CoverBrowser.CoreWebView2 ?? throw new InvalidOperationException("Встроенный браузер не инициализирован.");
        for (var attempt = 0; attempt < 120; attempt++)
        {
            var ready = await core.ExecuteScriptAsync(
                "(() => { const target=document.querySelector('#downloadpic'); const rows=document.querySelectorAll('#schedule-loader .ig__sched-item'); return !!target && !!document.querySelector('.button-download-infographic') && rows.length>0 && Array.from(target.querySelectorAll('img')).every(i => i.complete && i.naturalWidth>0); })()");
            if (ready.Equals("true", StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("Сайт не сформировал расписание на выбранный день. Возможно, игр на эту дату нет.");
    }

    private async Task WaitForCoverReadyAsync()
    {
        var core = CoverBrowser.CoreWebView2 ?? throw new InvalidOperationException("Встроенный браузер не инициализирован.");
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var ready = await core.ExecuteScriptAsync(
                "(() => { const target=document.querySelector('#downloadpic'); return !!target && !!document.querySelector('.button-download-infographic') && document.readyState==='complete' && Array.from(target.querySelectorAll('img')).every(i => i.complete && i.naturalWidth>0); })()");
            if (ready.Equals("true", StringComparison.OrdinalIgnoreCase)) return;
            await Task.Delay(250);
        }
        throw new TimeoutException("Не дождался загрузки обложки и логотипов.");
    }

    private async Task UpdateTeamsFromCoverPageAsync(GameRecord game)
    {
        var core = CoverBrowser.CoreWebView2 ?? throw new InvalidOperationException("Встроенный браузер не инициализирован.");
        var raw = await core.ExecuteScriptAsync(
            "JSON.stringify({home:document.querySelector('#teamnameA')?.value||'', guest:document.querySelector('#teamnameB')?.value||''})");
        var json = JsonSerializer.Deserialize<string>(raw);
        if (string.IsNullOrWhiteSpace(json)) return;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var home = root.TryGetProperty("home", out var homeValue) ? homeValue.GetString()?.Trim() : null;
        var guest = root.TryGetProperty("guest", out var guestValue) ? guestValue.GetString()?.Trim() : null;
        if (!string.IsNullOrWhiteSpace(home)) game.HomeTeam = home;
        if (!string.IsNullOrWhiteSpace(guest)) game.GuestTeam = guest;
        GamesGrid.Items.Refresh();
        UpdatePostPreview();
    }

    private async Task TriggerCoverDownloadAsync(string targetPath)
    {
        var core = CoverBrowser.CoreWebView2 ?? throw new InvalidOperationException("Встроенный браузер не инициализирован.");
        if (File.Exists(targetPath)) throw new IOException("Файл с таким именем уже существует; он не будет перезаписан.");
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        EventHandler<CoreWebView2DownloadStartingEventArgs>? startingHandler = null;
        startingHandler = (_, args) =>
        {
            args.ResultFilePath = Path.GetFullPath(targetPath);
            args.Handled = true;
            var operation = args.DownloadOperation;
            EventHandler<object> stateChanged = null!;
            stateChanged = (_, _) =>
            {
                if (operation.State == CoreWebView2DownloadState.Completed)
                {
                    operation.StateChanged -= stateChanged;
                    completion.TrySetResult(operation.ResultFilePath);
                }
                else if (operation.State == CoreWebView2DownloadState.Interrupted)
                {
                    operation.StateChanged -= stateChanged;
                    completion.TrySetException(new IOException($"Скачивание прервано: {operation.InterruptReason}."));
                }
            };
            operation.StateChanged += stateChanged;
            if (operation.State == CoreWebView2DownloadState.Completed)
                completion.TrySetResult(operation.ResultFilePath);
        };

        core.DownloadStarting += startingHandler;
        try
        {
            var result = await core.ExecuteScriptAsync(
                "(() => { const button=document.querySelector('.button-download-infographic'); if(!button) return false; document.querySelector('#downloadpic').scrollIntoView({block:'center'}); button.click(); return true; })()");
            if (!result.Equals("true", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("На странице не найдена кнопка скачивания PNG.");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(90));
        }
        finally
        {
            if (startingHandler is not null) core.DownloadStarting -= startingHandler;
        }
    }

    private static async Task VerifyPngAsync(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("PNG-файл не появился после скачивания.", path);
        await using var stream = File.OpenRead(path);
        var signature = new byte[8];
        var read = await stream.ReadAsync(signature);
        byte[] expected = [137, 80, 78, 71, 13, 10, 26, 10];
        if (read != expected.Length || !signature.SequenceEqual(expected))
            throw new InvalidDataException("Скачанный файл не распознан как PNG-обложка.");
    }

    private static void TryDeletePartialCover(string packageFolder, GameRecord game)
    {
        try
        {
            var path = Path.Combine(packageFolder, PackageBuilder.CoverFileName(game));
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private void UpdatePostPreview()
    {
        if (_games.Count == 0 || TournamentCombo.SelectedItem is not TournamentInfo tournament || GameDatePicker.SelectedDate is not DateTime date)
        {
            PostPreviewBox.Text = string.Empty;
            CopyPostButton.IsEnabled = false;
            return;
        }
        PostPreviewBox.Text = PostBuilder.BuildPost(
            tournament.Name, date, _games, IntroBox.Text, VenueBox.Text, HashtagsBox.Text, FirstLinkBox.Text);
        CopyPostButton.IsEnabled = true;
    }

    private void SetBusy(bool value)
    {
        FindGamesButton.IsEnabled = !value;
        TournamentCombo.IsEnabled = !value;
        GameDatePicker.IsEnabled = !value;
        ScheduleCategoryCombo.IsEnabled = !value;
        FirstLinkBox.IsEnabled = !value;
        OutputFolderBox.IsEnabled = !value;
        ChooseFolderButton.IsEnabled = !value;
        IntroBox.IsEnabled = !value;
        VenueBox.IsEnabled = !value;
        HashtagsBox.IsEnabled = !value;
        CopyPostButton.IsEnabled = !value && _games.Count > 0;
        CreatePackageButton.IsEnabled = !value && _games.Count > 0;
    }

    private void ChooseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Папка для пакетов Челбаскета",
            InitialDirectory = Directory.Exists(OutputFolderBox.Text) ? OutputFolderBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dialog.ShowDialog(this) == true) OutputFolderBox.Text = dialog.FolderName;
    }

    private void CopyPost_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PostPreviewBox.Text)) return;
        try
        {
            Clipboard.SetText(PostPreviewBox.Text);
            StatusText.Text = "Пост скопирован в буфер обмена.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Не удалось скопировать пост.\n\n{ex.Message}", "Буфер обмена недоступен", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastOutputFolder) || !Directory.Exists(_lastOutputFolder)) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutputFolder}\"") { UseShellExecute = true });
    }

    protected override void OnClosed(EventArgs e)
    {
        _client.Dispose();
        base.OnClosed(e);
    }
}
