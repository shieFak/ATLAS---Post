using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace ChelbasketDesktop;

public partial class MainWindow : Window
{
    private readonly ChelbasketClient _client = new();
    private readonly AsbClient _asbClient = new();
    private List<GameRecord> _games = [];
    private List<VkStreamRow> _vkStreamRows = [];
    private readonly Dictionary<string, string> _createdStreamLinks = LoadCreatedStreamLinks();
    private string? _lastOutputFolder;
    private bool _browserReady;
    private bool _vkBrowserReady;
    private Process? _vkEdgeProcess;
    private HttpClient? _vkEdgeHttp;
    private VkCdpSession? _vkCdp;
    private int _vkEdgeDebugPort;
    private string? _pendingVkGameId;
    private string? _preparedVkGameId;
    private string? _preparedVkGroupId;
    private bool _streamCreationSubmitted;
    private bool _editingAutoFields;
    private bool _userEditedIntro;
    private bool _userEditedHashtags;
    private bool _windowInitialized;
    private string? _scheduleTournamentName;

    public MainWindow()
    {
        InitializeComponent();
        var vkTarget = LoadVkTargetSettings();
        VkGroupIdBox.Text = vkTarget.GroupId;
        VkChannelNameBox.Text = vkTarget.ChannelName;
        _windowInitialized = true;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        GameDatePicker.SelectedDate = DateTime.Today;
        OutputFolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AtlasPackages");
        VkStreamsStatusText.Text = "Открой VK Видео, войди в аккаунт и открой форму создания трансляции.";
        StatusText.Text = "Загружаю список турниров…";
        try
        {
            var tournaments = new List<TournamentInfo>();
            var sourceErrors = new List<string>();
            try { tournaments.AddRange(await _client.GetTournamentsAsync()); }
            catch (Exception ex) { sourceErrors.Add($"Федерация: {ex.Message}"); }
            try { tournaments.AddRange(await _asbClient.GetTournamentsAsync()); }
            catch (Exception ex) { sourceErrors.Add($"АСБ: {ex.Message}"); }
            if (tournaments.Count == 0) throw new InvalidOperationException(string.Join(Environment.NewLine, sourceErrors));
            TournamentCombo.ItemsSource = tournaments;
            if (tournaments.Count > 0) TournamentCombo.SelectedIndex = 0;
            RefreshAutoFields();
            UpdateTournamentModeUi();
            StatusText.Text = sourceErrors.Count == 0
                ? $"Турниров в списке: {tournaments.Count}. Выбери дату и нажми «Найти игры»."
                : $"Загружено турниров: {tournaments.Count}. Один из сайтов временно недоступен.";
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
                $"Доступно обновление Атласа {update.Version}.\n\nУстановлена версия {UpdateChecker.CurrentVersion}. Открыть страницу скачивания?",
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
            if (ReferenceEquals(sender, TournamentCombo)) UpdateTournamentModeUi();
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

    private void UpdateTournamentModeUi()
    {
        if (TournamentCombo.SelectedItem is not TournamentInfo tournament) return;
        var isAsb = tournament.Source == TournamentSource.Asb;
        var autoScheduleAllowed = !RequiresManualScheduleCategory(tournament.Name);
        AutoScheduleCategoryItem.IsEnabled = autoScheduleAllowed;
        if (!string.Equals(_scheduleTournamentName, tournament.Name, StringComparison.Ordinal))
        {
            ScheduleCategoryCombo.SelectedIndex = autoScheduleAllowed ? 0 : 1;
            _scheduleTournamentName = tournament.Name;
        }
        ScheduleOptionsBorder.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        FirstLinkLabel.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        FirstLinkBox.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        StreamLinkHelpText.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        PostFieldsHeader.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        IntroLabel.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        IntroBox.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        VenueLabel.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        VenueBox.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        HashtagsLabel.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        HashtagsBox.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        OutputFolderHelpText.Text = isAsb
            ? tournament.IsAsbSpo
                ? "В папке будут обложки выбранных игр, общий пост и список названий эфиров."
                : "В папке будут отдельные посты, названия эфиров и ID игр."
            : "В папке будут обложки, расписание, тексты и ZIP.";
        PostFormatLabel.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        PostFormatCombo.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        PostHallLabel.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        PostHallCombo.Visibility = isAsb ? Visibility.Collapsed : Visibility.Visible;
        PostGameLabel.Visibility = isAsb && !tournament.IsAsbSpo ? Visibility.Visible : Visibility.Collapsed;
        PostGameCombo.Visibility = isAsb && !tournament.IsAsbSpo ? Visibility.Visible : Visibility.Collapsed;
        CreatePackageButton.Content = isAsb && !tournament.IsAsbSpo
            ? "Собрать посты и названия эфиров  →"
            : isAsb
                ? "Скачать обложки и собрать пост  →"
                : "Скачать обложки и собрать пакет  →";
        if (GamesGrid.Columns.Count > 0)
            GamesGrid.Columns[0].Visibility = isAsb && !tournament.IsAsbSpo ? Visibility.Collapsed : Visibility.Visible;
        UpdatePostPreview();
    }

    private static bool RequiresManualScheduleCategory(string tournamentName) =>
        tournamentName.Contains("Кубок Голоса", StringComparison.CurrentCultureIgnoreCase) ||
        tournamentName.Contains("Лига Зубкова", StringComparison.CurrentCultureIgnoreCase);

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
            var games = tournament.Source == TournamentSource.Asb
                ? await _asbClient.GetGamesForDateAsync(tournament, date)
                : await _client.GetGamesForDateAsync(tournament, date);
            _games = games.ToList();
            foreach (var game in _games)
                if (tournament.Source == TournamentSource.Asb)
                {
                    game.HomeTeam = AsbPackageBuilder.NormalizeTeam(game.HomeTeam);
                    game.GuestTeam = AsbPackageBuilder.NormalizeTeam(game.GuestTeam);
                    game.Status = tournament.IsAsbSpo ? "Ожидает" : "Не требуется";
                }
            RefreshVkStreamRows(tournament, date);
            GamesGrid.ItemsSource = _games;
            HallFilterCombo.Items.Clear();
            HallFilterCombo.Items.Add(new ComboBoxItem { Content = "Все залы" });
            var halls = _games.Select(game => game.Hall).Where(hall => !string.IsNullOrWhiteSpace(hall))
                .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(hall => hall, StringComparer.CurrentCultureIgnoreCase).ToList();
            foreach (var hall in halls)
                HallFilterCombo.Items.Add(new ComboBoxItem { Content = hall });
            HallFilterCombo.SelectedIndex = 0;
            PostHallCombo.Items.Clear();
            foreach (var hall in halls)
                PostHallCombo.Items.Add(new ComboBoxItem { Content = hall });
            if (_games.Any(game => string.IsNullOrWhiteSpace(game.Hall)))
                PostHallCombo.Items.Add(new ComboBoxItem { Content = "Зал не указан" });
            PostHallCombo.SelectedIndex = PostHallCombo.Items.Count > 0 ? 0 : -1;
            PostGameCombo.Items.Clear();
            foreach (var game in _games)
                PostGameCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{game.Time} · {game.HomeTeam} — {game.GuestTeam} ({game.BroadcastGender})",
                    Tag = game
                });
            PostGameCombo.SelectedIndex = PostGameCombo.Items.Count > 0 ? 0 : -1;
            CreatePackageButton.IsEnabled = _games.Count > 0;
            _preparedVkGameId = null;
            _preparedVkGroupId = null;
            _pendingVkGameId = null;
            _streamCreationSubmitted = false;
            RefreshVkActionButtons();
            CopyPostButton.IsEnabled = _games.Count > 0;
            SummaryText.Text = _games.Count == 0
                ? "На эту дату игры не найдены."
                : $"{PostBuilder.DisplayTournament(tournament.Name)} · {date:dd.MM.yyyy} · игр: {_games.Count}";
            WorkProgress.Value = 0;
            StatusText.Text = _games.Count == 0
                ? "Проверь турнир и дату или выбери другой день."
                : tournament.Source == TournamentSource.Asb && !tournament.IsAsbSpo
                    ? "Матчи загружены. Выбери игру во вкладке «Пост» и скопируй отдельный текст для ВК."
                    : tournament.Source == TournamentSource.Asb
                        ? "Матчи загружены. Отметь игры, для которых нужны обложки, затем собери пост дня."
                        : "Матчи загружены. Проверь ссылки и детали поста, затем собери пакет.";
            UpdateTournamentModeUi();
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

        if (tournament.Source == TournamentSource.Asb)
        {
            await CreateAsbPackageAsync(tournament, date);
            return;
        }

        if (ScheduleCategoryCombo.SelectedItem is not ComboBoxItem selectedScheduleCategory ||
            string.IsNullOrWhiteSpace(selectedScheduleCategory.Content?.ToString()))
        {
            MessageBox.Show(this, "Выбери, кто играет, чтобы добавить категорию в расписание.",
                "Выбери категорию расписания", MessageBoxButton.OK, MessageBoxImage.Information);
            ScheduleCategoryCombo.Focus();
            return;
        }

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

    private async Task CreateAsbPackageAsync(TournamentInfo tournament, DateTime date)
    {
        var outputRoot = OutputFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(outputRoot) || !Path.IsPathRooted(outputRoot))
        {
            MessageBox.Show(this, "Укажи папку для сохранения результатов.", "Нужна папка", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        _lastOutputFolder = null;
        var packageFolder = string.Empty;
        var errors = new List<string>();
        var selectedGames = _games.Where(game => game.IsSelected).ToList();
        var reused = 0;
        var downloaded = 0;
        foreach (var game in _games.Where(game => !game.IsSelected)) game.Status = "Не выбрана";

        try
        {
            packageFolder = PackageBuilder.CreatePackageFolder(outputRoot, tournament.Name, date);
            _lastOutputFolder = packageFolder;
            if (tournament.IsAsbSpo && selectedGames.Count > 0)
            {
                try
                {
                    await EnsureBrowserAsync();
                    PreviewTabs.SelectedItem = BrowserTab;
                }
                catch (Exception ex) { errors.Add($"Не запустился встроенный браузер: {ex.Message}"); }

                var existingCovers = IndexExistingCovers(outputRoot, packageFolder);
                for (var index = 0; index < selectedGames.Count; index++)
                {
                    var game = selectedGames[index];
                    WorkProgress.Value = index * 85.0 / selectedGames.Count;
                    StatusText.Text = $"Обложка АСБ СПО {index + 1} из {selectedGames.Count}: {game.HomeTeam} — {game.GuestTeam}";
                    game.Status = "Загружается…";
                    GamesGrid.Items.Refresh();
                    try
                    {
                        var targetPath = Path.Combine(packageFolder, PackageBuilder.CoverFileName(game));
                        var wasReused = false;
                        if (existingCovers.TryGetValue(game.Id, out var existingPath))
                        {
                            try
                            {
                                await VerifyPngAsync(existingPath);
                                File.Copy(existingPath, targetPath, overwrite: false);
                                await VerifyPngAsync(targetPath);
                                wasReused = true;
                                reused++;
                            }
                            catch { TryDeletePartialCover(packageFolder, game); }
                        }
                        if (!wasReused)
                        {
                            if (!_browserReady) throw new InvalidOperationException("Встроенный браузер не запустился.");
                            await NavigateToCoverAsync(game.CoverUrl);
                            await WaitForCoverReadyAsync();
                            await UpdateTeamsFromCoverPageAsync(game);
                            targetPath = Path.Combine(packageFolder, PackageBuilder.CoverFileName(game));
                            await TriggerCoverDownloadAsync(targetPath);
                            await VerifyPngAsync(targetPath);
                            downloaded++;
                        }
                        game.CoverPath = targetPath;
                        game.Status = wasReused ? "Взята из папки" : "Скачана";
                    }
                    catch (Exception ex)
                    {
                        game.Status = "Ошибка";
                        errors.Add($"{game.Number:00}. {game.HomeTeam} - {game.GuestTeam}: {ex.Message}");
                        TryDeletePartialCover(packageFolder, game);
                    }
                    GamesGrid.Items.Refresh();
                }
            }

            StatusText.Text = "Сохраняю тексты и собираю ZIP…";
            var result = await AsbPackageBuilder.WritePackageAsync(packageFolder, tournament, date, _games);
            _lastOutputFolder = result.FolderPath;
            OpenOutputButton.IsEnabled = true;
            WorkProgress.Value = 100;
            var summary = tournament.IsAsbSpo
                ? $"Готово · {_games.Count} игр · обложек выбрано {selectedGames.Count} · скачано {downloaded} · использовано {reused} · расписание не требуется"
                : $"Готово · {_games.Count} игр · отдельные посты и названия эфиров подготовлены · обложки и расписание не требуются";
            SummaryText.Text = summary;
            StatusText.Text = $"Материалы АСБ сохранены: {packageFolder}";
            PreviewTabs.SelectedIndex = 0;

            if (errors.Count > 0)
            {
                var details = string.Join(Environment.NewLine, errors.Take(12));
                if (errors.Count > 12) details += Environment.NewLine + $"…и ещё {errors.Count - 12} ошибок.";
                MessageBox.Show(this,
                    $"Пакет собран, но часть обложек не загрузилась. Тексты, названия эфиров и ID сохранены.\n\n{details}",
                    "Готово с ошибками", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось собрать материалы АСБ.";
            MessageBox.Show(this, $"Не удалось завершить сборку. Уже сохранённые файлы находятся в выбранной папке.\n\n{ex.Message}",
                "Ошибка сборки", MessageBoxButton.OK, MessageBoxImage.Error);
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

        var categoryChoice = (ScheduleCategoryCombo.SelectedItem as ComboBoxItem)?.Content?.ToString()
            ?? throw new InvalidOperationException("Выбери категорию расписания.");
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
            "JSON.stringify({home:document.querySelector('#teamnameA, #teamnamea, [data-target=teamnamea]')?.value||'', guest:document.querySelector('#teamnameB, #teamnameb, [data-target=teamnameb]')?.value||''})");
        var json = JsonSerializer.Deserialize<string>(raw);
        if (string.IsNullOrWhiteSpace(json)) return;
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var home = root.TryGetProperty("home", out var homeValue) ? homeValue.GetString()?.Trim() : null;
        var guest = root.TryGetProperty("guest", out var guestValue) ? guestValue.GetString()?.Trim() : null;
        var isAsb = TournamentCombo.SelectedItem is TournamentInfo { Source: TournamentSource.Asb };
        if (!string.IsNullOrWhiteSpace(home)) game.HomeTeam = isAsb ? AsbPackageBuilder.NormalizeTeam(home) : home;
        if (!string.IsNullOrWhiteSpace(guest)) game.GuestTeam = isAsb ? AsbPackageBuilder.NormalizeTeam(guest) : guest;
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
        if (tournament.Source == TournamentSource.Asb)
        {
            PostPreviewBox.Text = tournament.IsAsbSpo
                ? AsbPackageBuilder.BuildSpoDayPost(tournament, _games)
                : PostGameCombo.SelectedItem is ComboBoxItem { Tag: GameRecord selectedGame }
                    ? AsbPackageBuilder.BuildSingleGamePost(tournament, selectedGame)
                    : string.Empty;
            CopyPostButton.IsEnabled = !string.IsNullOrWhiteSpace(PostPreviewBox.Text);
            return;
        }
        var allHalls = IsAllHallsPost();
        PostHallCombo.IsEnabled = !allHalls;
        PostPreviewBox.Text = allHalls
            ? PostBuilder.BuildPostGroupedByHall(
                tournament.Name, date, _games, IntroBox.Text, VenueBox.Text, HashtagsBox.Text, FirstLinkBox.Text,
                _createdStreamLinks)
            : PostBuilder.BuildPost(
                tournament.Name, date, GetPostGames(), IntroBox.Text, VenueBox.Text, HashtagsBox.Text, FirstLinkBox.Text,
                _createdStreamLinks);
        CopyPostButton.IsEnabled = true;
    }

    private bool IsAllHallsPost() =>
        (PostFormatCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() == "На все залы";

    private IReadOnlyList<GameRecord> GetPostGames()
    {
        if (IsAllHallsPost()) return _games;
        var selectedHall = (PostHallCombo.SelectedItem as ComboBoxItem)?.Content?.ToString();
        if (string.IsNullOrWhiteSpace(selectedHall)) return Array.Empty<GameRecord>();
        return selectedHall.Equals("Зал не указан", StringComparison.CurrentCultureIgnoreCase)
            ? _games.Where(game => string.IsNullOrWhiteSpace(game.Hall)).ToArray()
            : _games.Where(game => game.Hall.Equals(selectedHall, StringComparison.CurrentCultureIgnoreCase)).ToArray();
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
        VkGroupIdBox.IsEnabled = !value;
        VkChannelNameBox.IsEnabled = !value;
        ConnectVkButton.IsEnabled = !value;
        CopyPostButton.IsEnabled = !value && _games.Count > 0;
        CreatePackageButton.IsEnabled = !value && _games.Count > 0;
        RefreshVkActionButtons(value);
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
            Title = tournament.Source == TournamentSource.Asb
                ? AsbPackageBuilder.BroadcastTitle(tournament, date, game)
                : PackageBuilder.GameTitle(tournament.Name, date, game),
            Link = _createdStreamLinks.GetValueOrDefault(game.Id, string.Empty),
            Status = _createdStreamLinks.ContainsKey(game.Id) ? "Создан" : "Ожидает"
        }).ToList();
        VkStreamsGrid.ItemsSource = _vkStreamRows;
        _preparedVkGameId = null;
        _preparedVkGroupId = null;
        _pendingVkGameId = null;
        _streamCreationSubmitted = false;
        RefreshVkActionButtons();
    }

    private void VkStreamsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VkStreamsGrid.SelectedItem is VkStreamRow row && _preparedVkGameId != row.Game.Id)
        {
            _preparedVkGameId = null;
            _preparedVkGroupId = null;
            _pendingVkGameId = null;
            _streamCreationSubmitted = false;
        }
        RefreshVkActionButtons();
    }

    private void VkTarget_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_windowInitialized) return;
        _preparedVkGameId = null;
        _preparedVkGroupId = null;
        _pendingVkGameId = null;
        _streamCreationSubmitted = false;
        SaveVkTargetSettings();
        RefreshVkActionButtons();
    }

    private void RefreshVkActionButtons(bool busy = false)
    {
        if (!_windowInitialized || PrepareVkStreamButton is null) return;
        var row = VkStreamsGrid.SelectedItem as VkStreamRow;
        var alreadyCreated = row is not null && _createdStreamLinks.ContainsKey(row.Game.Id);
        PrepareVkStreamButton.IsEnabled = !busy && _vkBrowserReady && row is not null && !alreadyCreated;
        CreateVkStreamButton.IsEnabled = !busy && !_streamCreationSubmitted && row is not null &&
            row.Game.Id == _preparedVkGameId && !alreadyCreated;
    }

    private async Task EnsureVkVideoBrowserAsync()
    {
        if (_vkCdp is not null && _vkEdgeProcess is { HasExited: false }) return;
        var edgePath = FindMicrosoftEdgePath();
        if (edgePath is null) throw new FileNotFoundException("Microsoft Edge не найден. Установи Edge и повтори.");
        var userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChelbasketTV", "VkEdgeProfile");
        Directory.CreateDirectory(userData);
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            _vkEdgeDebugPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        var start = new ProcessStartInfo(edgePath) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Normal };
        foreach (var arg in new[] { $"--remote-debugging-port={_vkEdgeDebugPort}", "--remote-debugging-address=127.0.0.1", "--remote-allow-origins=*", $"--user-data-dir={userData}", "--no-first-run", "--new-window", "https://vkvideo.ru/" })
            start.ArgumentList.Add(arg);
        _vkEdgeProcess = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Microsoft Edge.");
        _vkEdgeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var endpoint = new Uri($"http://127.0.0.1:{_vkEdgeDebugPort}/json/list");
        var until = DateTime.UtcNow.AddSeconds(30);
        string? socketUrl = null;
        while (DateTime.UtcNow < until && socketUrl is null)
        {
            if (_vkEdgeProcess.HasExited) throw new InvalidOperationException("Microsoft Edge закрылся при запуске.");
            try
            {
                var targets = await _vkEdgeHttp.GetFromJsonAsync<List<EdgeTarget>>(endpoint);
                socketUrl = targets?.FirstOrDefault(target => target.Type == "page" && Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) &&
                    (uri.Host.Equals("vkvideo.ru", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("live.vkvideo.ru", StringComparison.OrdinalIgnoreCase)))?.WebSocketDebuggerUrl;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            if (socketUrl is null) await Task.Delay(250);
        }
        if (socketUrl is null) throw new TimeoutException("Edge запущен, но страница VK Видео не открылась для управления.");
        _vkCdp = await VkCdpSession.ConnectAsync(socketUrl);
        _vkBrowserReady = true;
        ExpandVkBrowserButton.IsEnabled = true;
        ResetVkSessionButton.IsEnabled = true;
        RefreshVkActionButtons();
    }

    private static string? FindMicrosoftEdgePath()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Edge", "Application", "msedge.exe")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private sealed record EdgeTarget(string Type, string Url, string WebSocketDebuggerUrl);

    private async void OpenVkVideo_Click(object sender, RoutedEventArgs e)
    {
        PreviewTabs.SelectedItem = VkStreamsTab;
        VkStreamsStatusText.Text = "Открываю VK Видео в отдельном окне…";
        try
        {
            await EnsureVkVideoBrowserAsync();
            if (!Uri.TryCreate(await VkVideoUrlAsync(), UriKind.Absolute, out var current) ||
                !(current.Host.Equals("vkvideo.ru", StringComparison.OrdinalIgnoreCase) ||
                  current.Host.Equals("live.vkvideo.ru", StringComparison.OrdinalIgnoreCase) ||
                  current.Host.Equals("vk.ru", StringComparison.OrdinalIgnoreCase) ||
                  current.Host.Equals("vk.com", StringComparison.OrdinalIgnoreCase)))
                await VkVideoNavigateAsync("https://vkvideo.ru/");
            await ShowVkVideoWindowAsync();
            VkStreamsStatusText.Text = "Войди в обычном окне Microsoft Edge. Профиль VK сохраняется для следующих запусков.";
            RefreshVkActionButtons();
        }
        catch (Exception ex)
        {
            VkStreamsStatusText.Text = "Не удалось открыть VK Видео.";
            MessageBox.Show(this, ex.Message, "VK Видео", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ExpandVkBrowser_Click(object sender, RoutedEventArgs e) => await ShowVkVideoWindowAsync();

    private async void ResetVkSession_Click(object sender, RoutedEventArgs e)
    {
        if (!_vkBrowserReady) return;
        var confirm = MessageBox.Show(this,
            "Очистить cookies и данные отдельного профиля Edge для VK Видео? Потребуется войти заново. Обычный профиль Edge и файлы пакетов не затрагиваются.",
            "Сбросить вход VK", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        ResetVkSessionButton.IsEnabled = false;
        VkStreamsStatusText.Text = "Очищаю устаревшую сессию VK Видео…";
        try
        {
            await _vkCdp!.ClearCookiesAsync();
            await VkVideoEvaluateAsync("(() => { try { localStorage.clear(); sessionStorage.clear(); } catch {} return true; })()");
            await VkVideoNavigateAsync("https://vkvideo.ru/");
            VkStreamsStatusText.Text = "Сессия очищена. Войди заново в VK Видео.";
        }
        catch (Exception ex)
        {
            VkStreamsStatusText.Text = "Не удалось очистить сессию VK.";
            MessageBox.Show(this, ex.Message, "Сброс входа VK", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { ResetVkSessionButton.IsEnabled = true; }
    }

    private async Task ShowVkVideoWindowAsync()
    {
        if (_vkEdgeProcess is { HasExited: false })
        {
            NativeMethods.ShowWindow(_vkEdgeProcess.MainWindowHandle, 9);
            NativeMethods.SetForegroundWindow(_vkEdgeProcess.MainWindowHandle);
        }
        await Task.CompletedTask;
    }

    private async void FillStreamForm_Click(object sender, RoutedEventArgs e)
    {
        if (VkStreamsGrid.SelectedItem is not VkStreamRow row)
        {
            MessageBox.Show(this, "Сначала выбери игру в таблице.", "Выбери игру", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_vkBrowserReady || _vkCdp is null)
        {
            MessageBox.Show(this, "Сначала открой VK Видео и перейди к форме создания эфира.", "VK Видео не открыто", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_createdStreamLinks.ContainsKey(row.Game.Id))
        {
            MessageBox.Show(this, "Для этой игры уже сохранена ссылка на эфир.", "Эфир уже отмечен", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var groupId = Regex.Replace(VkGroupIdBox.Text, @"\D", string.Empty);
        var channelName = VkChannelNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(channelName))
        {
            MessageBox.Show(this, "Укажи ID сообщества и название его канала в VK Видео.", "Не задан канал VK", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SetBusy(true);
        try
        {
            var targetChannel = await EnsureVkTargetChannelAsync(groupId, channelName);
            if (!targetChannel.Success)
            {
                VkStreamsStatusText.Text = targetChannel.Message;
                return;
            }

            var titleJson = JsonSerializer.Serialize(row.Title);
            var script = "(() => {" +
                "const visible=e=>!!(e&&e.getClientRects().length);" +
                "const nodes=[...document.querySelectorAll('input,textarea,[contenteditable=true]')].filter(visible);" +
                "const meta=e=>[(e.getAttribute('placeholder')||''),(e.getAttribute('aria-label')||''),(e.getAttribute('name')||''),(e.labels?[...e.labels].map(x=>x.innerText).join(' '):''),(e.parentElement?.innerText||'')].join(' ').toLowerCase();" +
                "const title=nodes.find(e=>/(назван|смотрю фильмы|название эфира)/i.test(meta(e)));" +
                "const desc=nodes.find(e=>/(расскажите, о чём|описание)/i.test(meta(e)));" +
                "if(!title)return {ok:false,message:'Не вижу поле названия. Открой первый шаг формы создания трансляции.'};" +
                "if(!desc)return {ok:false,message:'Не вижу поле описания на первом шаге формы.'};" +
                "const set=(e,v)=>{if(e instanceof HTMLInputElement||e instanceof HTMLTextAreaElement){const p=e instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;const d=Object.getOwnPropertyDescriptor(p,'value');if(d?.set)d.set.call(e,v);else e.value=v;}else e.textContent=v;e.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:v}));e.dispatchEvent(new Event('change',{bubbles:true}));};" +
                $"set(title,{titleJson});" +
                "set(desc,'');" +
                "const sport=[...document.querySelectorAll('button,[role=button]')].find(e=>visible(e)&&e.innerText.trim()==='Спорт');if(!sport)return {ok:false,message:'Не вижу категорию «Спорт». Выбери её вручную и повтори подготовку.'};sport.click();" +
                "const next=[...document.querySelectorAll('button,[role=button]')].find(e=>visible(e)&&e.innerText.trim()==='Далее');if(!next)return {ok:false,message:'Не вижу кнопку «Далее». Проверь первый шаг формы VK.'};next.click();" +
                "return {ok:true,descriptionCleared:true,sportSelected:true,nextClicked:true};" +
                "})()";
            var result = await ExecuteVkScriptAsync(script);
            if (!result.GetProperty("ok").GetBoolean())
            {
                VkStreamsStatusText.Text = result.GetProperty("message").GetString() ?? "Открой форму создания трансляции.";
                return;
            }

            _pendingVkGameId = row.Game.Id;
            _preparedVkGameId = row.Game.Id;
            _preparedVkGroupId = groupId;
            _streamCreationSubmitted = false;
            row.Status = "Форма заполнена — готово к созданию";
            VkStreamsGrid.Items.Refresh();
            var ready = await WaitForVkCreateButtonAsync(TimeSpan.FromSeconds(15));
            if (!ready)
            {
                row.Status = "Проверь настройки VK вручную";
                VkStreamsStatusText.Text = "Название заполнено, описание очищено, выбрана категория «Спорт». Перейди на вкладку «Настройки» в VK; затем нажми «Создать эфир в VK» в приложении.";
            }
            else
            {
                VkStreamsStatusText.Text = $"Канал «{channelName}» выбран. Название заполнено, описание пустое, категория «Спорт». Проверь настройки и нажми «Создать эфир в VK».";
            }
        }
        catch (Exception ex)
        {
            VkStreamsStatusText.Text = "Не получилось заполнить форму VK.";
            MessageBox.Show(this, ex.Message, "Заполнение формы", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async void CreateVkStream_Click(object sender, RoutedEventArgs e)
    {
        if (VkStreamsGrid.SelectedItem is not VkStreamRow row || row.Game.Id != _preparedVkGameId || _pendingVkGameId != row.Game.Id)
        {
            MessageBox.Show(this, "Сначала подготовь эфир для выбранной игры.", "Эфир не подготовлен", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!string.Equals(_preparedVkGroupId, Regex.Replace(VkGroupIdBox.Text, @"\D", string.Empty), StringComparison.Ordinal))
        {
            MessageBox.Show(this, "ID сообщества изменился. Подготовь форму заново, чтобы приложение проверило выбранный канал.", "Проверь сообщество", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_createdStreamLinks.ContainsKey(row.Game.Id) || _streamCreationSubmitted) return;

        var confirm = MessageBox.Show(this,
            $"Создать эфир в сообществе «{VkChannelNameBox.Text.Trim()}» (ID {VkGroupIdBox.Text.Trim()})?\n\n{row.Title}\n\nОписание останется пустым. После создания приложение попробует само сохранить ссылку в пост.",
            "Подтверди создание эфира", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        try
        {
            var existingLinks = await GetVisibleStreamLinksAsync();
            var createScript = "(() => {" +
                "const visible=e=>!!(e&&e.getClientRects().length);" +
                "const norm=s=>(s||'').replace(/\\s+/g,' ').trim().toLowerCase();" +
                "const buttons=[...document.querySelectorAll('button,[role=button]')].filter(visible);" +
                "const create=buttons.find(e=>/^(создать трансляцию|создать эфир)$/.test(norm(e.innerText)));" +
                "if(!create)return {ok:false,message:'На странице VK не вижу кнопку «Создать трансляцию». Открой вкладку «Настройки» и повтори.'};" +
                "create.click();return {ok:true};" +
                "})()";
            _streamCreationSubmitted = true;
            var result = await ExecuteVkScriptAsync(createScript);
            if (!result.GetProperty("ok").GetBoolean())
            {
                _streamCreationSubmitted = false;
                VkStreamsStatusText.Text = result.GetProperty("message").GetString() ?? "Не удалось начать создание эфира.";
                return;
            }

            row.Status = "VK создаёт эфир…";
            VkStreamsGrid.Items.Refresh();
            RefreshVkActionButtons(busy: true);
            VkStreamsStatusText.Text = "Запрос отправлен в VK. Жду ссылку на созданный эфир…";

            var link = await WaitForCreatedStreamLinkAsync(existingLinks, TimeSpan.FromSeconds(60));
            if (link is not null)
            {
                SaveStreamLink(row.Game.Id, link);
                return;
            }

            row.Status = "Эфир отправлен — вставь ссылку вручную";
            VkStreamsGrid.Items.Refresh();
            VkStreamsStatusText.Text = "VK не показал ссылку в адресе страницы. Скопируй ссылку на эфир из VK, вставь её в поле ниже и нажми «Сохранить ссылку вручную». Создание не запускай повторно.";
        }
        catch (Exception ex)
        {
            VkStreamsStatusText.Text = _streamCreationSubmitted
                ? "VK мог принять запрос. Проверь список эфиров, прежде чем нажимать создание повторно."
                : "Не удалось создать эфир через VK Видео.";
            MessageBox.Show(this,
                _streamCreationSubmitted
                    ? $"После отправки команды VK вернул ошибку. Чтобы не создать дубликат, проверь VK Видео и не запускай создание повторно, пока не убедишься, что эфир не появился.\n\n{ex.Message}"
                    : ex.Message,
                "Создание эфира", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { SetBusy(false); }
    }

    private async Task<(bool Success, string Message)> EnsureVkTargetChannelAsync(string groupId, string channelName)
    {
        var state = await ExecuteVkScriptAsync(VkChannelStateScript());
        if (!state.GetProperty("found").GetBoolean())
            return (false, "Открой в VK Видео первый шаг формы создания трансляции. В нём должен быть указан канал.");

        var currentText = state.GetProperty("text").GetString() ?? string.Empty;
        var currentMarkup = state.GetProperty("html").GetString() ?? string.Empty;
        if (IsVkTargetSelected(currentText, currentMarkup, groupId, channelName))
            return (true, currentText);

        await ExecuteVkScriptAsync(VkClickChannelDropdownScript());
        await Task.Delay(350);
        var idJson = JsonSerializer.Serialize(groupId);
        var nameJson = JsonSerializer.Serialize(channelName);
        var selectScript = "(() => {" +
            "const visible=e=>!!(e&&e.getClientRects().length);" +
            "const norm=s=>(s||'').replace(/\\s+/g,' ').trim().toLowerCase();" +
            $"const id={idJson},name=norm({nameJson});" +
            "const items=[...document.querySelectorAll('a[href],[role=option],[role=menuitem],button,[role=button]')].filter(visible);" +
            "const byId=items.find(e=>((e.getAttribute('href')||'')+' '+e.outerHTML).toLowerCase().includes('club'+id));" +
            "const byName=items.find(e=>{const t=norm(e.innerText);return t===name||t.startsWith(name+' ')||t.startsWith(name+'\\n')});" +
            "const target=byId||byName;if(!target)return {ok:false,message:'В списке VK не нашёл этот канал. Выбери его вручную и снова нажми «Подготовить выбранную игру».',text:''};" +
            "target.click();return {ok:true,message:'Канал выбран.',text:target.innerText||''};" +
            "})()";
        var selection = await ExecuteVkScriptAsync(selectScript);
        if (!selection.GetProperty("ok").GetBoolean())
            return (false, selection.GetProperty("message").GetString() ?? "Не удалось выбрать канал VK.");

        await Task.Delay(450);
        state = await ExecuteVkScriptAsync(VkChannelStateScript());
        currentText = state.TryGetProperty("text", out var textValue) ? textValue.GetString() ?? string.Empty : string.Empty;
        currentMarkup = state.TryGetProperty("html", out var htmlValue) ? htmlValue.GetString() ?? string.Empty : string.Empty;
        return IsVkTargetSelected(currentText, currentMarkup, groupId, channelName)
            ? (true, currentText)
            : (false, $"Не получилось подтвердить выбранный канал. Проверь, что в форме VK выбрано «{channelName}» (ID {groupId}).");
    }

    private static bool IsVkTargetSelected(string currentText, string markup, string groupId, string channelName) =>
        markup.Contains($"club{groupId}", StringComparison.OrdinalIgnoreCase) ||
        markup.Contains($"club-{groupId}", StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(channelName) && currentText.Contains(channelName, StringComparison.CurrentCultureIgnoreCase));

    private static string VkChannelStateScript() => "(() => {" +
        "const visible=e=>!!(e&&e.getClientRects().length);" +
        "const norm=s=>(s||'').replace(/\\s+/g,' ').trim().toLowerCase();" +
        "const label=[...document.querySelectorAll('body *')].find(e=>e.children.length===0&&norm(e.innerText||e.textContent)==='канал');" +
        "if(!label)return {found:false,text:'',html:''};" +
        "let p=label;for(let i=0;i<5&&p;i++,p=p.parentElement){const c=[...p.querySelectorAll('button,[role=combobox],[aria-haspopup],input')].find(visible);if(c)return {found:true,text:c.innerText||c.value||'',html:c.outerHTML||''};}" +
        "return {found:false,text:'',html:''};" +
        "})()";

    private static string VkClickChannelDropdownScript() => "(() => {" +
        "const visible=e=>!!(e&&e.getClientRects().length);" +
        "const norm=s=>(s||'').replace(/\\s+/g,' ').trim().toLowerCase();" +
        "const label=[...document.querySelectorAll('body *')].find(e=>e.children.length===0&&norm(e.innerText||e.textContent)==='канал');" +
        "if(!label)return {ok:false};let p=label;for(let i=0;i<5&&p;i++,p=p.parentElement){const c=[...p.querySelectorAll('button,[role=combobox],[aria-haspopup],input')].find(visible);if(c){c.click();return {ok:true};}}return {ok:false};" +
        "})()";

    private async Task<bool> WaitForVkCreateButtonAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        const string script = "(() => [...document.querySelectorAll('button,[role=button]')].some(e=>e.getClientRects().length&&/^(создать трансляцию|создать эфир)$/i.test((e.innerText||'').replace(/\\s+/g,' ').trim())))()";
        while (DateTime.UtcNow < until)
        {
            var result = await ExecuteVkScriptAsync(script);
            if (result.ValueKind == JsonValueKind.True) return true;
            await Task.Delay(500);
        }
        return false;
    }

    private async Task<HashSet<string>> GetVisibleStreamLinksAsync()
    {
        const string script = "(() => {const values=[location.href,...[...document.querySelectorAll('a[href]')].map(a=>a.href),...[...document.querySelectorAll('input,textarea,[contenteditable=true]')].map(e=>e.value||e.innerText||e.textContent),...(document.body?.innerText||'').match(/https?:\\/\\/(?:live\\.)?(?:vkvideo\\.ru|vk\\.ru|vk\\.com)\\/live-[\\w-]+/gi)||[])];return values.filter(Boolean);})()";
        try
        {
            var result = await ExecuteVkScriptAsync(script);
            if (result.ValueKind == JsonValueKind.Array)
                return result.EnumerateArray()
                    .Select(value => value.GetString())
                    .Where(value => TryNormalizeVkStreamLink(value, out _))
                    .Select(value => { TryNormalizeVkStreamLink(value, out var link); return link; })
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string?> WaitForCreatedStreamLinkAsync(HashSet<string> existingLinks, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        const string script = "(() => {const values=[location.href,...[...document.querySelectorAll('a[href]')].map(a=>a.href),...[...document.querySelectorAll('input,textarea,[contenteditable=true]')].map(e=>e.value||e.innerText||e.textContent),...(document.body?.innerText||'').match(/https?:\\/\\/(?:live\\.)?(?:vkvideo\\.ru|vk\\.ru|vk\\.com)\\/live-[\\w-]+/gi)||[])];return values.filter(Boolean);})()";
        while (DateTime.UtcNow < until)
        {
            var source = await VkVideoUrlAsync();
            if (TryNormalizeVkStreamLink(source, out var normalized) && !existingLinks.Contains(normalized)) return normalized;
            try
            {
                var values = await ExecuteVkScriptAsync(script);
                if (values.ValueKind == JsonValueKind.Array)
                {
                    foreach (var value in values.EnumerateArray())
                        if (TryNormalizeVkStreamLink(value.GetString(), out normalized) && !existingLinks.Contains(normalized)) return normalized;
                }
            }
            catch { /* VK can navigate while publishing the newly created stream. */ }
            await Task.Delay(1000);
        }
        return null;
    }

    private async Task<JsonElement> ExecuteVkScriptAsync(string script)
    {
        return await VkVideoEvaluateAsync(script);
    }

    private async Task<JsonElement> VkVideoEvaluateAsync(string expression) =>
        await (_vkCdp ?? throw new InvalidOperationException("Edge не подключён.")).EvaluateAsync(expression);

    private async Task<string> VkVideoUrlAsync() =>
        (await VkVideoEvaluateAsync("location.href")).GetString() ?? string.Empty;

    private async Task VkVideoNavigateAsync(string url) =>
        await (_vkCdp ?? throw new InvalidOperationException("Edge не подключён.")).NavigateAsync(url);

    private static bool TryNormalizeVkStreamLink(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !(uri.Host.Equals("vkvideo.ru", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("live.vkvideo.ru", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("vk.ru", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.Equals("vk.com", StringComparison.OrdinalIgnoreCase)) ||
            !uri.AbsolutePath.StartsWith("/live-", StringComparison.OrdinalIgnoreCase))
            return false;

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        if (builder.Host.Equals("live.vkvideo.ru", StringComparison.OrdinalIgnoreCase)) builder.Host = "vkvideo.ru";
        return PostBuilder.TryExpandStreamLink(builder.Uri.AbsoluteUri, 0, out normalized);
    }

    private void SaveStreamLink(string gameId, string link)
    {
        _createdStreamLinks[gameId] = link;
        PersistCreatedStreamLinks();
        var row = _vkStreamRows.FirstOrDefault(item => item.Game.Id == gameId);
        if (row is not null)
        {
            row.Link = link;
            row.Status = "Создан · ссылка сохранена";
            VkStreamsGrid.Items.Refresh();
        }
        VkCurrentLinkBox.Clear();
        _pendingVkGameId = null;
        _preparedVkGameId = null;
        _preparedVkGroupId = null;
        _streamCreationSubmitted = false;
        UpdatePostPreview();
        VkStreamsStatusText.Text = "Эфир создан, ссылка сохранена в пост и пакет. Можно переходить к следующей игре.";
        RefreshVkActionButtons();
    }

    private async void SaveCurrentStreamLink_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingVkGameId))
        {
            MessageBox.Show(this, "Сначала заполни форму для игры и создай эфир в VK.", "Нет выбранной игры", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var value = string.IsNullOrWhiteSpace(VkCurrentLinkBox.Text)
            ? await VkVideoUrlAsync()
            : VkCurrentLinkBox.Text.Trim();
        if (!TryNormalizeVkStreamLink(value, out var link))
        {
            MessageBox.Show(this, "Вставь ссылку на созданный эфир вида https://vkvideo.ru/live-... .",
                "Ссылка на эфир не найдена", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveStreamLink(_pendingVkGameId, link);
    }

    private sealed record VkTargetSettings(string GroupId, string ChannelName);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")]
        internal static extern bool SetForegroundWindow(IntPtr hWnd);
    }

    private sealed class VkCdpSession
    {
        private readonly ClientWebSocket _socket = new();
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _nextId;

        private VkCdpSession() { }

        internal static async Task<VkCdpSession> ConnectAsync(string webSocketUrl)
        {
            var session = new VkCdpSession();
            await session._socket.ConnectAsync(new Uri(webSocketUrl), CancellationToken.None);
            return session;
        }

        internal async Task<JsonElement> EvaluateAsync(string expression)
        {
            var result = await SendAsync("Runtime.evaluate", new { expression, returnByValue = true, awaitPromise = true });
            if (result.TryGetProperty("exceptionDetails", out var exception))
                throw new InvalidOperationException(exception.ToString());
            if (!result.TryGetProperty("result", out var remote) || !remote.TryGetProperty("value", out var value))
                return JsonDocument.Parse("null").RootElement.Clone();
            return value.Clone();
        }

        internal async Task NavigateAsync(string url) => await SendAsync("Page.navigate", new { url });

        internal async Task ClearCookiesAsync() => await SendAsync("Network.clearBrowserCookies", new { });

        private async Task<JsonElement> SendAsync(string method, object parameters)
        {
            await _gate.WaitAsync();
            try
            {
                var id = Interlocked.Increment(ref _nextId);
                var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
                var buffer = new byte[65536];
                while (true)
                {
                    using var stream = new MemoryStream();
                    WebSocketReceiveResult received;
                    do
                    {
                        received = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                        if (received.MessageType == WebSocketMessageType.Close)
                            throw new IOException("Edge закрыл соединение управления.");
                        stream.Write(buffer, 0, received.Count);
                    } while (!received.EndOfMessage);

                    using var document = JsonDocument.Parse(stream.ToArray());
                    var root = document.RootElement;
                    if (!root.TryGetProperty("id", out var responseId) || responseId.GetInt32() != id) continue;
                    if (root.TryGetProperty("error", out var error)) throw new InvalidOperationException(error.ToString());
                    return root.TryGetProperty("result", out var result) ? result.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
                }
            }
            finally { _gate.Release(); }
        }
    }

    private static string VkTargetSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChelbasketTV", "vk-target.json");

    private static VkTargetSettings LoadVkTargetSettings()
    {
        try
        {
            return File.Exists(VkTargetSettingsPath)
                ? JsonSerializer.Deserialize<VkTargetSettings>(File.ReadAllText(VkTargetSettingsPath)) ?? new("216863675", "Проба")
                : new("216863675", "Проба");
        }
        catch { return new("216863675", "Проба"); }
    }

    private void SaveVkTargetSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VkTargetSettingsPath)!);
            File.WriteAllText(VkTargetSettingsPath, JsonSerializer.Serialize(new VkTargetSettings(
                Regex.Replace(VkGroupIdBox.Text, @"\D", string.Empty), VkChannelNameBox.Text.Trim())));
        }
        catch { }
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
            Title = "Папка для пакетов Атласа",
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
