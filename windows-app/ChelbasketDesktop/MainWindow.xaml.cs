using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace ChelbasketDesktop;

public partial class MainWindow : Window
{
    private readonly ChelbasketClient _client = new();
    private List<GameRecord> _games = [];
    private List<VkStreamRow> _vkStreamRows = [];
    private readonly Dictionary<string, string> _createdStreamLinks = LoadCreatedStreamLinks();
    private string? _lastOutputFolder;
    private bool _browserReady;
    private bool _vkBrowserReady;
    private WebView2? _vkVideoBrowser;
    private string? _pendingVkGameId;
    private bool _editingAutoFields;
    private bool _userEditedIntro;
    private bool _userEditedHashtags;
    private bool _windowInitialized;

    public MainWindow()
    {
        InitializeComponent();
        _windowInitialized = true;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        GameDatePicker.SelectedDate = DateTime.Today;
        OutputFolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChelbasketPackages");
        VkStreamsStatusText.Text = "Открой VK Видео, войди в аккаунт и открой форму создания трансляции.";
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
        _ = CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update is null || !IsLoaded) return;
            var result = MessageBox.Show(this,
                $"Доступно обновление Челбаскет ТВ-пакета {update.Version}.\n\nУстановлена версия {UpdateChecker.CurrentVersion}. Открыть страницу скачивания?",
                "Доступно обновление", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo(update.ReleasePage.AbsoluteUri) { UseShellExecute = true });
        }
        catch { }
    }

    private void Inputs_Changed(object sender, RoutedEventArgs e)
    {
        if (!_windowInitialized) return;
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
            RefreshVkStreamRows(tournament, date);
            GamesGrid.ItemsSource = _games;
            HallFilterCombo.Items.Clear();
            HallFilterCombo.Items.Add(new ComboBoxItem { Content = "Все залы" });
            foreach (var hall in _games.Select(game => game.Hall).Where(hall => !string.IsNullOrWhiteSpace(hall)).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(hall => hall, StringComparer.CurrentCultureIgnoreCase))
                HallFilterCombo.Items.Add(new ComboBoxItem { Content = hall });
            HallFilterCombo.SelectedIndex = 0;
            CreatePackageButton.IsEnabled = _games.Count > 0;
            CreateStreamsButton.IsEnabled = _games.Count > 0;
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
        var gamesToDownload = games.Where(game => game.IsSelected).ToList();
        var reusedCoverCount = 0;
        var downloadedCoverCount = 0;
        foreach (var game in games.Where(game => !game.IsSelected)) game.Status = "Не выбрана";
        string? packageFolder = null;
        var errors = new List<string>();
        try
        {
            packageFolder = PackageBuilder.CreatePackageFolder(outputRoot, tournament.Name, date);
            _lastOutputFolder = packageFolder;
            var existingCovers = IndexExistingCovers(outputRoot, packageFolder);

            try
            {
                await EnsureBrowserAsync();
                PreviewTabs.SelectedItem = BrowserTab;
            }
            catch (Exception ex)
            {
                errors.Add($"Не запустился WebView2: {ex.Message}");
            }

            for (var index = 0; index < gamesToDownload.Count; index++)
            {
                var game = gamesToDownload[index];
                WorkProgress.Value = index * 75.0 / gamesToDownload.Count;
                StatusText.Text = $"Обложка {index + 1} из {gamesToDownload.Count}: {game.Time} · {game.HomeTeam} — {game.GuestTeam}";
                game.Status = "Загружается…";
                GamesGrid.Items.Refresh();

                try
                {
                    var targetPath = Path.Combine(packageFolder, PackageBuilder.CoverFileName(game));
                    var reused = false;
                    if (existingCovers.TryGetValue(game.Id, out var existingPath))
                    {
                        try
                        {
                            await VerifyPngAsync(existingPath);
                            File.Copy(existingPath, targetPath, overwrite: false);
                            await VerifyPngAsync(targetPath);
                            reused = true;
                            reusedCoverCount++;
                        }
                        catch
                        {
                            TryDeletePartialCover(packageFolder, game);
                        }
                    }

                    if (!reused)
                    {
                        if (!_browserReady) throw new InvalidOperationException("Встроенный браузер не запустился, обложку пришлось пропустить.");
                        await NavigateToCoverAsync(game.CoverUrl);
                        await WaitForCoverReadyAsync();
                        await UpdateTeamsFromCoverPageAsync(game);
                        targetPath = Path.Combine(packageFolder, PackageBuilder.CoverFileName(game));
                        await TriggerCoverDownloadAsync(targetPath);
                        await VerifyPngAsync(targetPath);
                        downloadedCoverCount++;
                    }
                    game.CoverPath = targetPath;
                    game.Status = reused ? "Взята из папки" : "Скачана";
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
                IntroBox.Text, VenueBox.Text, HashtagsBox.Text, firstLink,
                streamLinksByGameId: _createdStreamLinks);
            _lastOutputFolder = result.FolderPath;
            WorkProgress.Value = 100;
            PreviewTabs.SelectedIndex = 0;
            OpenOutputButton.IsEnabled = true;
            var scheduleStatus = scheduleDownloaded ? "расписание скачано" : "без расписания";
            StatusText.Text = $"Найдено {result.GameCount} игр, выбрано {gamesToDownload.Count}, новых обложек {downloadedCoverCount}, взято из папки {reusedCoverCount}, {scheduleStatus}. Папка и ZIP готовы.";
            SummaryText.Text = $"Готово · {result.GameCount} игр · выбрано {gamesToDownload.Count} · скачано {downloadedCoverCount} · повторно использовано {reusedCoverCount} · {scheduleStatus}";

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

    private static Dictionary<string, string> IndexExistingCovers(string outputRoot, string currentPackageFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var currentFullPath = Path.GetFullPath(currentPackageFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var directories = new List<string> { outputRoot };
        try { directories.AddRange(Directory.EnumerateDirectories(outputRoot, "*", SearchOption.TopDirectoryOnly)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        foreach (var directory in directories)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*.png", SearchOption.TopDirectoryOnly))
                {
                    var fullPath = Path.GetFullPath(file);
                    if (fullPath.StartsWith(currentFullPath, StringComparison.OrdinalIgnoreCase)) continue;
                    var stem = Path.GetFileNameWithoutExtension(file);
                    var separator = stem.LastIndexOf('_');
                    if (separator < 0) continue;
                    var gameId = stem[(separator + 1)..];
                    if (gameId.Length == 0 || !gameId.All(char.IsDigit)) continue;
                    result.TryAdd(gameId, fullPath);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return result;
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
        if (!_windowInitialized || PostPreviewBox is null || CopyPostButton is null) return;
        if (_games.Count == 0 || TournamentCombo.SelectedItem is not TournamentInfo tournament || GameDatePicker.SelectedDate is not DateTime date)
        {
            PostPreviewBox.Text = string.Empty;
            CopyPostButton.IsEnabled = false;
            return;
        }
        PostPreviewBox.Text = PostBuilder.BuildPost(
            tournament.Name, date, _games, IntroBox.Text, VenueBox.Text, HashtagsBox.Text, FirstLinkBox.Text,
            _createdStreamLinks);
        CopyPostButton.IsEnabled = true;
    }

    private void SetBusy(bool value)
    {
        FindGamesButton.IsEnabled = !value;
        TournamentCombo.IsEnabled = !value;
        GameDatePicker.IsEnabled = !value;
        ScheduleCategoryCombo.IsEnabled = !value;
        HallFilterCombo.IsEnabled = !value;
        GamesGrid.IsEnabled = !value;
        FirstLinkBox.IsEnabled = !value;
        OutputFolderBox.IsEnabled = !value;
        ChooseFolderButton.IsEnabled = !value;
        IntroBox.IsEnabled = !value;
        VenueBox.IsEnabled = !value;
        HashtagsBox.IsEnabled = !value;
        ConnectVkButton.IsEnabled = !value;
        CopyPostButton.IsEnabled = !value && _games.Count > 0;
        CreatePackageButton.IsEnabled = !value && _games.Count > 0;
        CreateStreamsButton.IsEnabled = !value && _games.Count > 0;
    }

    private void HallFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowInitialized || GamesGrid is null) return;
        var view = CollectionViewSource.GetDefaultView(GamesGrid.ItemsSource);
        if (view is null) return;
        var hall = (HallFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        view.Filter = item => item is GameRecord game &&
            (string.IsNullOrWhiteSpace(hall) || hall.Equals("Все залы", StringComparison.CurrentCultureIgnoreCase) ||
             game.Hall.Equals(hall, StringComparison.CurrentCultureIgnoreCase));
        view.Refresh();
    }

    private void RefreshVkStreamRows(TournamentInfo tournament, DateTime date)
    {
        _vkStreamRows = _games.Select(game => new VkStreamRow
        {
            Game = game,
            Title = PackageBuilder.GameTitle(tournament.Name, date, game),
            Link = _createdStreamLinks.GetValueOrDefault(game.Id, string.Empty),
            Status = _createdStreamLinks.ContainsKey(game.Id) ? "Создан" : "Ожидает"
        }).ToList();
        VkStreamsGrid.ItemsSource = _vkStreamRows;
        CreateStreamsButton.IsEnabled = _games.Count > 0;
    }

    private async Task EnsureVkVideoBrowserAsync()
    {
        if (_vkBrowserReady) return;
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChelbasketTV", "VkVideoBrowser");
        Directory.CreateDirectory(userData);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: userData);
        _vkVideoBrowser = new WebView2 { Height = 380, Visibility = Visibility.Visible };
        VkVideoBrowserHost.Content = _vkVideoBrowser;
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await _vkVideoBrowser.EnsureCoreWebView2Async(environment);
        _vkVideoBrowser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
        _vkVideoBrowser.CoreWebView2.Settings.IsStatusBarEnabled = true;
        _vkVideoBrowser.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _vkBrowserReady = true;
    }

    private WebView2 VkVideoBrowser => _vkVideoBrowser ?? throw new InvalidOperationException("Браузер VK ещё не открыт.");

    private async void OpenVkVideo_Click(object sender, RoutedEventArgs e)
    {
        VkVideoBrowserHost.Visibility = Visibility.Visible;
        PreviewTabs.SelectedItem = VkStreamsTab;
        VkStreamsStatusText.Text = "Открываю встроенный браузер VK Видео…";
        try
        {
            await EnsureVkVideoBrowserAsync();
            VkStreamsStatusText.Text = "Войди в VK вручную, затем открой форму создания трансляции. Этот вход останется в приложении.";
            VkVideoBrowser.CoreWebView2.Navigate("https://vkvideo.ru/");
        }
        catch (Exception ex)
        {
            VkStreamsStatusText.Text = "Не удалось открыть VK Видео.";
            MessageBox.Show(this, ex.Message, "VK Видео", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void FillStreamForm_Click(object sender, RoutedEventArgs e)
    {
        if (VkStreamsGrid.SelectedItem is not VkStreamRow row)
        {
            MessageBox.Show(this, "Сначала выбери игру в таблице.", "Выбери игру", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(_vkVideoBrowser?.Source?.AbsoluteUri))
        {
            MessageBox.Show(this, "Сначала открой VK Видео и перейди к форме создания эфира.", "VK Видео не открыто", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_createdStreamLinks.ContainsKey(row.Game.Id))
        {
            MessageBox.Show(this, "Для этой игры уже сохранена ссылка на эфир.", "Эфир уже отмечен", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        try
        {
            var titleJson = JsonSerializer.Serialize(row.Title);
            var script = "(() => {" +
                "const visible=e=>!!(e&&e.getClientRects().length);" +
                "const nodes=[...document.querySelectorAll('input,textarea,[contenteditable=true]')].filter(visible);" +
                "const meta=e=>[(e.getAttribute('placeholder')||''),(e.getAttribute('aria-label')||''),(e.getAttribute('name')||''),(e.labels?[...e.labels].map(x=>x.innerText).join(' '):''),(e.parentElement?.innerText||'')].join(' ').toLowerCase();" +
                "const title=nodes.find(e=>/(назван|смотрю фильмы|название эфира)/i.test(meta(e)));" +
                "const desc=nodes.find(e=>/(расскажите, о чём|описание)/i.test(meta(e)));" +
                "if(!title)return {ok:false,message:'Не вижу поле названия. Открой на VK первый шаг формы создания трансляции.'};" +
                "const set=(e,v)=>{const p=e instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;const d=Object.getOwnPropertyDescriptor(p,'value');if(d?.set)d.set.call(e,v);else e.textContent=v;e.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:v}));e.dispatchEvent(new Event('change',{bubbles:true}));};" +
                "if(!desc)return {ok:false,message:'Не вижу поле описания. Открой первый шаг формы эфира — описание останется пустым.'};" +
                $"set(title,{titleJson});" +
                "set(desc,'');" +
                "const sport=[...document.querySelectorAll('button,[role=button]')].find(e=>visible(e)&&e.innerText.trim()==='Спорт');if(sport)sport.click();" +
                "const next=[...document.querySelectorAll('button,[role=button]')].find(e=>visible(e)&&e.innerText.trim()==='Далее');if(next)next.click();" +
                "return {ok:true,descriptionCleared:true,sportSelected:!!sport,nextClicked:!!next};" +
                "})()";
            var raw = await VkVideoBrowser.CoreWebView2.ExecuteScriptAsync(script);
            using var result = JsonDocument.Parse(raw);
            var root = result.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                VkStreamsStatusText.Text = root.GetProperty("message").GetString() ?? "Открой форму создания трансляции.";
                return;
            }

            _pendingVkGameId = row.Game.Id;
            row.Status = "Форма заполнена — проверь и создай в VK";
            VkStreamsGrid.Items.Refresh();
            var sportText = root.GetProperty("sportSelected").GetBoolean() ? "Категория «Спорт» выбрана." : "Выбери категорию «Спорт» вручную.";
            VkStreamsStatusText.Text = root.GetProperty("nextClicked").GetBoolean()
                ? $"Название заполнено, описание пустое. {sportText} Проверь настройки на странице VK и создай эфир вручную."
                : $"Название заполнено, описание пустое. {sportText} Перейди дальше вручную и проверь параметры перед созданием.";
        }
        catch (Exception ex)
        {
            VkStreamsStatusText.Text = "Не получилось заполнить форму VK.";
            MessageBox.Show(this, ex.Message, "Заполнение формы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private void SaveCurrentStreamLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingVkGameId))
        {
            MessageBox.Show(this, "Сначала заполни форму для игры и создай эфир в VK.", "Нет выбранной игры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var value = string.IsNullOrWhiteSpace(VkCurrentLinkBox.Text)
            ? _vkVideoBrowser?.Source?.AbsoluteUri
            : VkCurrentLinkBox.Text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var link) ||
            !(link.Host.Equals("vkvideo.ru", StringComparison.OrdinalIgnoreCase) || link.Host.Equals("vk.ru", StringComparison.OrdinalIgnoreCase)) ||
            !link.AbsolutePath.StartsWith("/live", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "Вставь ссылку на созданный эфир вида https://vkvideo.ru/live-... или открой страницу эфира во встроенном браузере.",
                "Ссылка на эфир не найдена", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _createdStreamLinks[_pendingVkGameId] = link.AbsoluteUri;
        PersistCreatedStreamLinks();
        var row = _vkStreamRows.FirstOrDefault(item => item.Game.Id == _pendingVkGameId);
        if (row is not null)
        {
            row.Link = link.AbsoluteUri;
            row.Status = "Ссылка сохранена";
            VkStreamsGrid.Items.Refresh();
        }
        VkCurrentLinkBox.Clear();
        _pendingVkGameId = null;
        UpdatePostPreview();
        VkStreamsStatusText.Text = "Ссылка сохранена в пост и файл пакета. Выбери следующую игру.";
    }

    private static string StreamLinksPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChelbasketTV", "vk-stream-links.json");

    private static Dictionary<string, string> LoadCreatedStreamLinks()
    {
        try
        {
            return File.Exists(StreamLinksPath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(StreamLinksPath)) ?? new(StringComparer.Ordinal)
                : new(StringComparer.Ordinal);
        }
        catch { return new(StringComparer.Ordinal); }
    }

    private void PersistCreatedStreamLinks()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StreamLinksPath)!);
        File.WriteAllText(StreamLinksPath, JsonSerializer.Serialize(_createdStreamLinks));
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
