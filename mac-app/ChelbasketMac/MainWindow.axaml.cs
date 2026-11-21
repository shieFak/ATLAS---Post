using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System.Diagnostics;
using System.Text.Json;
using System.Web;
using ChelbasketDesktop;
using NativeWebView = Avalonia.Controls.NativeWebView;

namespace ChelbasketMac;

public partial class MainWindow : Window
{
    private readonly ChelbasketClient _client = new();
    private readonly Dictionary<string, string> _createdLinks = LoadLinks();
    private List<GameRecord> _games = [];
    private string? _lastOutputFolder;
    private string? _pendingVkGameId;
    private bool _vkOpened;
    private TaskCompletionSource<byte[]>? _pendingPng;
    private TaskCompletionSource? _navigation;

    public MainWindow()
    {
        InitializeComponent();
        GameDatePicker.SelectedDate = DateTimeOffset.Now.Date;
        OutputFolderBox.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ChelbasketPackages");
        SiteBrowser.NavigationCompleted += (_, _) => _navigation?.TrySetResult();
        SiteBrowser.WebMessageReceived += (_, args) => HandleWebMessage(args.Body ?? "");
        OpenFolderButton.IsEnabled = false;
        _ = LoadTournamentsAsync();
    }

    private async Task LoadTournamentsAsync()
    {
        try
        {
            StatusText.Text = "Загружаю список турниров…";
            var tournaments = await _client.GetTournamentsAsync();
            TournamentCombo.ItemsSource = tournaments;
            TournamentCombo.SelectedIndex = tournaments.Count > 0 ? 0 : -1;
            RefreshAutoFields();
            StatusText.Text = $"Турниров в списке: {tournaments.Count}. Выбери дату и нажми «Найти игры».";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось загрузить список турниров с сайта Федерации.";
            await ShowMessageAsync("Не удалось загрузить турниры", ex.Message);
        }
        await CheckForUpdateAsync();
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update is null) return;
            var dialog = new Window
            {
                Title = "Доступно обновление",
                Width = 480,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var open = false;
            var laterButton = new Button { Content = "Позже", MinWidth = 90 };
            var openButton = new Button { Content = "Открыть страницу загрузки", MinWidth = 190, Classes = { "primary" } };
            laterButton.Click += (_, _) => dialog.Close();
            openButton.Click += (_, _) => { open = true; dialog.Close(); };
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(22),
                Spacing = 18,
                Children =
                {
                    new TextBlock { Text = $"Доступно обновление Челбаскет ТВ-пакета {update.Version}. Текущая версия: {UpdateChecker.CurrentVersion}.", TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Spacing = 8, Children = { laterButton, openButton } }
                }
            };
            await dialog.ShowDialog(this);
            if (open) Process.Start(new ProcessStartInfo("open", update.ReleasePage.AbsoluteUri) { UseShellExecute = false });
        }
        catch { }
    }

    private TournamentInfo? Tournament => TournamentCombo.SelectedItem as TournamentInfo;
    private DateTime SelectedDate => (GameDatePicker.SelectedDate ?? DateTimeOffset.Now).Date;
    private IReadOnlyList<GameRecord> SelectedGames => _games.Where(game => game.IsSelected).ToArray();

    private void RefreshAutoFields()
    {
        if (Tournament is not { } tournament) return;
        if (string.IsNullOrWhiteSpace(IntroBox.Text) || _lastAutoIntro == IntroBox.Text)
        {
            IntroBox.Text = PostBuilder.DefaultIntro(tournament.Name, SelectedDate);
            _lastAutoIntro = IntroBox.Text;
        }
        if (string.IsNullOrWhiteSpace(HashtagsBox.Text) || _lastAutoHashtags == HashtagsBox.Text)
        {
            HashtagsBox.Text = PostBuilder.DefaultHashtags(tournament.Name);
            _lastAutoHashtags = HashtagsBox.Text;
        }
        UpdatePostPreview();
    }

    private void Tournament_Changed(object? sender, SelectionChangedEventArgs e) => RefreshAutoFields();
    private void Date_Changed(object? sender, DatePickerSelectedValueChangedEventArgs e) => RefreshAutoFields();
    private void PostFields_Changed(object? sender, Avalonia.Controls.TextChangedEventArgs e) => UpdatePostPreview();
    private void GameSelection_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is CheckBox check && check.DataContext is GameRecord game) game.IsSelected = check.IsChecked == true;
        UpdatePostPreview();
    }
    private void GameRow_Selected(object? sender, SelectionChangedEventArgs e) { }

    private string? _lastAutoIntro;
    private string? _lastAutoHashtags;

    private async void FindGames_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Tournament is not { } tournament)
        {
            await ShowMessageAsync("Нужен турнир", "Выбери турнир.");
            return;
        }
        SetBusy(true);
        try
        {
            RefreshAutoFields();
            StatusText.Text = $"Ищу игры: {PostBuilder.DisplayTournament(tournament.Name)}, {SelectedDate:dd.MM.yyyy}…";
            _games = (await _client.GetGamesForDateAsync(tournament, SelectedDate)).ToList();
            GamesGrid.ItemsSource = _games;
            var halls = _games.Select(g => g.Hall).Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(h => h).ToList();
            HallCombo.ItemsSource = new[] { "Все залы" }.Concat(halls).ToList();
            HallCombo.SelectedIndex = 0;
            BuildButton.IsEnabled = CopyPostButton.IsEnabled = _games.Count > 0;
            CopyPostInnerButton.IsEnabled = _games.Count > 0;
            SummaryText.Text = _games.Count == 0 ? "На эту дату игры не найдены." : $"{PostBuilder.DisplayTournament(tournament.Name)} · {SelectedDate:dd.MM.yyyy} · игр: {_games.Count}";
            SelectionText.Text = $"Выбрано игр: {_games.Count} из {_games.Count}";
            StatusText.Text = _games.Count == 0 ? "Проверь дату или турнир." : "Матчи загружены. Проверь пост и собери пакет.";
            UpdatePostPreview();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось загрузить список игр.";
            await ShowMessageAsync("Не удалось загрузить игры", ex.Message);
        }
        finally { SetBusy(false); }
    }

    private async void BuildPackage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (Tournament is not { } tournament || _games.Count == 0) return;
        var firstLink = FirstLinkBox.Text?.Trim() ?? "";
        if (firstLink.Length > 0 && !PostBuilder.TryExpandStreamLink(firstLink, 0, out _))
        {
            await ShowMessageAsync("Проверь ссылку", "Вставь ссылку вида https://vkvideo.ru/live-123456789_456239001 или оставь поле пустым.");
            return;
        }
        var outputRoot = OutputFolderBox.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            await ShowMessageAsync("Нужна папка", "Выбери папку для результата.");
            return;
        }
        var selected = SelectedGames.ToList();
        var errors = new List<string>();
        var reused = 0;
        var downloaded = 0;
        SetBusy(true);
        Progress.IsVisible = true;
        _lastOutputFolder = null;
        string? folder = null;
        try
        {
            folder = PackageBuilder.CreatePackageFolder(outputRoot, tournament.Name, SelectedDate);
            _lastOutputFolder = folder;
            if (selected.Count > 0)
            {
                Tabs.SelectedIndex = 2;
                for (var i = 0; i < selected.Count; i++)
                {
                    var game = selected[i];
                    Progress.Value = (i * 75d) / selected.Count;
                    StatusText.Text = $"Обложка {i + 1} из {selected.Count}: {game.Time} · {game.HomeTeam} — {game.GuestTeam}";
                    game.Status = "Загружается…";
                    RefreshGamesList();
                    var destination = Path.Combine(folder, PackageBuilder.CoverFileName(game));
                    try
                    {
                        var old = FindCachedCover(outputRoot, folder, game.Id);
                        if (old is not null)
                        {
                            await VerifyPngAsync(old);
                            File.Copy(old, destination, overwrite: false);
                            reused++;
                        }
                        else
                        {
                            await NavigateAsync(new Uri(game.CoverUrl));
                            await WaitUntilAsync("(() => { const e=document.querySelector('#downloadpic'); return !!e && !!document.querySelector('.button-download-infographic') && Array.from(e.querySelectorAll('img')).every(i=>i.complete && i.naturalWidth>0); })()", "Не дождался готовой обложки.");
                            await ReadTeamsAsync(game);
                            destination = Path.Combine(folder, PackageBuilder.CoverFileName(game));
                            await DownloadPngAsync(destination);
                            downloaded++;
                        }
                        game.CoverPath = destination; game.Status = old is null ? "Скачана" : "Взята из папки";
                    }
                    catch (Exception ex) { game.Status = "Ошибка"; errors.Add($"{game.Number:00}. {game.HomeTeam} — {game.GuestTeam}: {ex.Message}"); }
                    RefreshGamesList();
                    UpdatePostPreview();
                }
            }
            var scheduleReady = false;
            try
            {
                StatusText.Text = "Формирую расписание на день…";
                await DownloadScheduleAsync(tournament, folder);
                scheduleReady = true;
            }
            catch (Exception ex) { errors.Add($"Расписание: {ex.Message}"); }
            Progress.Value = 92;
            var result = await PackageBuilder.WritePackageAsync(folder, tournament.Name, SelectedDate, _games,
                IntroBox.Text ?? "", VenueBox.Text ?? "", HashtagsBox.Text ?? "", firstLink,
                streamLinksByGameId: _createdLinks);
            _lastOutputFolder = result.FolderPath;
            Progress.Value = 100;
            OpenFolderButton.IsEnabled = true;
            BottomStatus.Text = $"Готово · обложки: скачано {downloaded}, взято из папки {reused} · {(scheduleReady ? "расписание скачано" : "без расписания")}";
            SummaryText.Text = $"Готово · {result.GameCount} игр · выбрано {selected.Count} · {(scheduleReady ? "расписание скачано" : "расписание не удалось скачать")}";
            StatusText.Text = "Папка с результатами и ZIP-архив готовы.";
            if (errors.Count > 0) await ShowMessageAsync("Пакет собран с ошибками", string.Join(Environment.NewLine, errors.Take(10)));
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось собрать пакет.";
            await ShowMessageAsync("Ошибка сборки", ex.Message);
        }
        finally { Progress.IsVisible = false; SetBusy(false); }
    }

    private async Task DownloadScheduleAsync(TournamentInfo tournament, string folder)
    {
        var uri = new Uri(tournament.Url);
        var query = HttpUtility.ParseQueryString(uri.Query);
        query["datefrom"] = SelectedDate.ToString("dd.MM.yyyy");
        query["dateto"] = SelectedDate.ToString("dd.MM.yyyy");
        query["tab"] = "0";
        var scheduleUri = new UriBuilder(uri) { Path = "/games-schedule/schedule2022/", Query = query.ToString() ?? "" }.Uri;
        await NavigateAsync(scheduleUri);
        await WaitUntilAsync("document.readyState==='complete' && !!document.querySelector('#form-schedule') && !!document.querySelector('#leaguename') && !!document.querySelector('#categorytitle')", "Не нашёл форму расписания на сайте.");
        var league = JsonSerializer.Serialize(PostBuilder.DisplayTournament(tournament.Name));
        await EvalAsync($"(() => {{const e=document.querySelector('#leaguename'); if(!e)return false; e.value={league}; e.dispatchEvent(new Event('input',{{bubbles:true}})); e.dispatchEvent(new Event('change',{{bubbles:true}})); return true;}})()");
        var category = (CategoryCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Автоматически (из сайта)";
        if (!category.StartsWith("Автоматически", StringComparison.CurrentCultureIgnoreCase))
        {
            var cat = JsonSerializer.Serialize(category);
            await EvalAsync($"(() => {{const e=document.querySelector('#categorytitle'); if(!e)return false; e.value={cat}; e.dispatchEvent(new Event('input',{{bubbles:true}})); e.dispatchEvent(new Event('change',{{bubbles:true}})); return true;}})()");
        }
        await EvalAsync("(() => {const f=document.querySelector('#form-schedule'); if(!f)return false; const b=f.querySelector('#submit'); if(b)b.click(); else f.requestSubmit(); return true;})()");
        await WaitUntilAsync("(() => {const e=document.querySelector('#downloadpic'); return !!e && !!document.querySelector('.button-download-infographic') && document.querySelectorAll('#schedule-loader .ig__sched-item').length>0 && Array.from(e.querySelectorAll('img')).every(i=>i.complete&&i.naturalWidth>0);})()", "Сайт не создал расписание на эту дату.");
        var raw = await EvalAsync("document.querySelector('#categorytitle')?.value || ''");
        var finalCategory = DecodeScriptString(raw).Trim();
        var path = Path.Combine(folder, PackageBuilder.ScheduleFileName(tournament.Name, SelectedDate, finalCategory));
        await DownloadPngAsync(path);
    }

    private async Task DownloadPngAsync(string path)
    {
        if (File.Exists(path)) throw new IOException("PNG с таким именем уже существует.");
        _pendingPng = new(TaskCreationOptions.RunContinuationsAsynchronously);
        const string hook = "(() => { if(window.__cbDownloadHook)return true; window.__cbDownloadHook=true; const old=HTMLAnchorElement.prototype.click; HTMLAnchorElement.prototype.click=function(){const a=this;if(!a.hasAttribute('download'))return old.apply(a,arguments); fetch(a.href).then(r=>r.blob()).then(b=>new Promise((resolve,reject)=>{const fr=new FileReader();fr.onload=()=>resolve(fr.result);fr.onerror=reject;fr.readAsDataURL(b)})).then(data=>invokeCSharpAction(JSON.stringify({type:'png',data:data.split(',')[1]}))).catch(e=>invokeCSharpAction(JSON.stringify({type:'error',message:String(e)})));}; return true;})()";
        await EvalAsync(hook);
        var click = await EvalAsync("(() => {const b=document.querySelector('.button-download-infographic'); if(!b)return false; document.querySelector('#downloadpic')?.scrollIntoView({block:'center'}); b.click(); return true;})()");
        if (!IsTrue(click)) throw new InvalidOperationException("Не нашёл кнопку скачать PNG на странице.");
        var bytes = await _pendingPng.Task.WaitAsync(TimeSpan.FromSeconds(90));
        await File.WriteAllBytesAsync(path, bytes);
        await VerifyPngAsync(path);
        _pendingPng = null;
    }

    private void HandleWebMessage(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            if (root.TryGetProperty("type", out var type) && type.GetString() == "png")
                _pendingPng?.TrySetResult(Convert.FromBase64String(root.GetProperty("data").GetString() ?? ""));
            else if (type.GetString() == "error")
                _pendingPng?.TrySetException(new InvalidOperationException(root.GetProperty("message").GetString()));
        }
        catch (Exception ex) { _pendingPng?.TrySetException(ex); }
    }

    private async Task NavigateAsync(Uri uri)
    {
        _navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SiteBrowser.Navigate(uri);
        await _navigation.Task.WaitAsync(TimeSpan.FromSeconds(70));
        await Task.Delay(200);
    }

    private async Task<string> EvalAsync(string script) => await SiteBrowser.InvokeScript(script) ?? "";
    private async Task WaitUntilAsync(string predicate, string error)
    {
        for (var i = 0; i < 120; i++)
        {
            if (IsTrue(await EvalAsync(predicate))) return;
            await Task.Delay(250);
        }
        throw new TimeoutException(error);
    }

    private async Task ReadTeamsAsync(GameRecord game)
    {
        var raw = await EvalAsync("JSON.stringify({home:document.querySelector('#teamnameA')?.value||'',guest:document.querySelector('#teamnameB')?.value||''})");
        var value = DecodeScriptString(raw);
        using var json = JsonDocument.Parse(value);
        var root = json.RootElement;
        var home = root.TryGetProperty("home", out var h) ? h.GetString() : null;
        var guest = root.TryGetProperty("guest", out var g) ? g.GetString() : null;
        if (!string.IsNullOrWhiteSpace(home)) game.HomeTeam = home;
        if (!string.IsNullOrWhiteSpace(guest)) game.GuestTeam = guest;
        GamesGrid.ItemsSource = null; GamesGrid.ItemsSource = _games;
    }

    private static string DecodeScriptString(string raw)
    {
        try { return JsonSerializer.Deserialize<string>(raw) ?? raw; }
        catch { return raw; }
    }
    private static bool IsTrue(string raw) => raw.Trim().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase);

    private void HallFilter_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (GamesGrid is null || HallCombo.SelectedItem is not string hall) return;
        RefreshGamesList();
    }

    private void RefreshGamesList()
    {
        var hall = HallCombo.SelectedItem as string;
        GamesGrid.ItemsSource = hall is null or "Все залы" ? _games : _games.Where(g => g.Hall.Equals(hall, StringComparison.CurrentCultureIgnoreCase)).ToList();
    }

    private void UpdatePostPreview()
    {
        if (PostPreviewBox is null || Tournament is not { } tournament) return;
        PostPreviewBox.Text = PostBuilder.BuildPost(tournament.Name, SelectedDate, _games,
            IntroBox.Text ?? "", VenueBox.Text ?? "", HashtagsBox.Text ?? "", FirstLinkBox.Text ?? "", _createdLinks);
        var selectedCount = _games.Count(g => g.IsSelected);
        if (SelectionText is not null && _games.Count > 0) SelectionText.Text = $"Выбрано игр: {selectedCount} из {_games.Count}";
    }

    private async void CopyPost_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        UpdatePostPreview();
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) { await ShowMessageAsync("Буфер обмена", "Не удалось получить доступ к буферу обмена."); return; }
        await clipboard.SetTextAsync(PostPreviewBox.Text ?? "");
        StatusText.Text = "Пост скопирован в буфер обмена.";
    }

    private async void ChooseFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Папка для пакетов Челбаскета", AllowMultiple = false });
        if (selected.Count > 0) OutputFolderBox.Text = selected[0].Path.LocalPath;
    }

    private async void OpenFolder_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_lastOutputFolder is null || !Directory.Exists(_lastOutputFolder)) return;
        Process.Start(new ProcessStartInfo("open", _lastOutputFolder) { UseShellExecute = false });
    }

    private void OpenVk_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Tabs.SelectedIndex = 3;
        VkBrowser.IsVisible = true;
        VkBrowser.Source = new Uri("https://vkvideo.ru/");
        _vkOpened = true;
        VkStatus.Text = "Войди в VK и создай эфир вручную; описание оставляй пустым.";
    }

    private async void FillVkForm_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (GamesGrid.SelectedItem is not GameRecord game || Tournament is not { } tournament)
        {
            await ShowMessageAsync("Выбери игру", "Сначала выбери игру во вкладке «Игры».");
            return;
        }
        if (!_vkOpened)
        {
            await ShowMessageAsync("VK Видео не открыто", "Нажми «Открыть VK Видео», войди в аккаунт и открой первый шаг формы создания эфира.");
            return;
        }
        if (_createdLinks.ContainsKey(game.Id))
        {
            await ShowMessageAsync("Ссылка уже сохранена", "Для этой игры уже сохранена ссылка на эфир.");
            return;
        }
        try
        {
            var title = JsonSerializer.Serialize(PackageBuilder.GameTitle(tournament.Name, SelectedDate, game));
            var script = "(() => {" +
                "const visible=e=>!!(e&&e.getClientRects().length);" +
                "const nodes=[...document.querySelectorAll('input,textarea,[contenteditable=true]')].filter(visible);" +
                "const meta=e=>[(e.getAttribute('placeholder')||''),(e.getAttribute('aria-label')||''),(e.getAttribute('name')||''),(e.labels?[...e.labels].map(x=>x.innerText).join(' '):''),(e.parentElement?.innerText||'')].join(' ').toLowerCase();" +
                "const field=nodes.find(e=>/(назван|название эфира)/i.test(meta(e)));" +
                "const desc=nodes.find(e=>/(описание|расскажите, о чём)/i.test(meta(e)));" +
                "if(!field)return JSON.stringify({ok:false,message:'Не вижу поля названия. Открой первый шаг создания трансляции.'});" +
                "if(!desc)return JSON.stringify({ok:false,message:'Не вижу поле описания. Открой первый шаг формы; описание останется пустым.'});" +
                "const set=(e,v)=>{const p=e instanceof HTMLTextAreaElement?HTMLTextAreaElement.prototype:HTMLInputElement.prototype;const d=Object.getOwnPropertyDescriptor(p,'value');if(d?.set)d.set.call(e,v);else e.textContent=v;e.dispatchEvent(new InputEvent('input',{bubbles:true,inputType:'insertText',data:v}));e.dispatchEvent(new Event('change',{bubbles:true}));};" +
                $"set(field,{title});set(desc,'');" +
                "const sport=[...document.querySelectorAll('button,[role=button]')].find(e=>visible(e)&&e.innerText.trim()==='Спорт');if(sport)sport.click();" +
                "const next=[...document.querySelectorAll('button,[role=button]')].find(e=>visible(e)&&e.innerText.trim()==='Далее');if(next)next.click();" +
                "return JSON.stringify({ok:true,sportSelected:!!sport,nextClicked:!!next});})()";
            var raw = await VkBrowser.InvokeScript(script) ?? "";
            var response = DecodeScriptString(raw);
            using var result = JsonDocument.Parse(response);
            var root = result.RootElement;
            if (!root.GetProperty("ok").GetBoolean())
            {
                VkStatus.Text = root.GetProperty("message").GetString() ?? "Открой форму создания трансляции.";
                return;
            }
            _pendingVkGameId = game.Id;
            var sportChosen = root.GetProperty("sportSelected").GetBoolean();
            var nextClicked = root.GetProperty("nextClicked").GetBoolean();
            VkStatus.Text = $"Название вставлено, описание пустое. {(sportChosen ? "Категория «Спорт» выбрана." : "Выбери категорию «Спорт» вручную.")} {(nextClicked ? "Проверь настройки и создай эфир вручную." : "Нажми «Далее», проверь настройки и создай эфир вручную.")}";
        }
        catch (Exception ex) { VkStatus.Text = $"Не получилось заполнить форму: {ex.Message}"; }
    }

    private async void SaveVkLink_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_pendingVkGameId))
        {
            await ShowMessageAsync("Нет выбранной игры", "Сначала выбери игру, нажми «Заполнить эфир для выбранной игры», а затем создай эфир в VK."); return;
        }
        var inputLink = string.IsNullOrWhiteSpace(VkLinkBox.Text) ? VkBrowser.Source?.AbsoluteUri : VkLinkBox.Text?.Trim();
        if (!Uri.TryCreate(inputLink, UriKind.Absolute, out var link) ||
            !(link.Host.Equals("vkvideo.ru", StringComparison.OrdinalIgnoreCase) || link.Host.Equals("vk.ru", StringComparison.OrdinalIgnoreCase)) ||
            !link.AbsolutePath.StartsWith("/live", StringComparison.OrdinalIgnoreCase))
        {
            await ShowMessageAsync("Ссылка не распознана", "Вставь ссылку на созданный эфир вида https://vkvideo.ru/live-… ."); return;
        }
        _createdLinks[_pendingVkGameId] = link.AbsoluteUri;
        Directory.CreateDirectory(Path.GetDirectoryName(LinkStorePath)!);
        await File.WriteAllTextAsync(LinkStorePath, JsonSerializer.Serialize(_createdLinks));
        VkLinkBox.Text = "";
        _pendingVkGameId = null;
        UpdatePostPreview();
        VkStatus.Text = "Ссылка сохранена и попадёт в post.txt. Выбери следующую игру.";
    }

    private string? FindCachedCover(string root, string current, string id)
    {
        foreach (var path in Directory.EnumerateFiles(root, $"*_{id}.png", SearchOption.AllDirectories))
        {
            if (path.StartsWith(current + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var stream = File.OpenRead(path);
                byte[] header = new byte[8];
                if (stream.Read(header) == 8 && header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return path;
            }
            catch { }
        }
        return null;
    }

    private static async Task VerifyPngAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        byte[] signature = new byte[8];
        var read = await stream.ReadAsync(signature);
        if (read != 8 || !signature.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) throw new InvalidDataException("Файл не является PNG-изображением.");
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new Window { Title = title, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel { Margin = new Thickness(20), Spacing = 16, Children = { new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new Button { Content = "ОК", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 90 } } } };
        if (dialog.Content is StackPanel panel && panel.Children.Last() is Button button) button.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private void SetBusy(bool busy) { FindButton.IsEnabled = !busy; BuildButton.IsEnabled = !busy && _games.Count > 0; }

    private static string LinkStorePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChelbasketTV", "vk-stream-links.json");
    private static Dictionary<string, string> LoadLinks()
    {
        try { return File.Exists(LinkStorePath) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(LinkStorePath)) ?? new(StringComparer.Ordinal) : new(StringComparer.Ordinal); }
        catch { return new(StringComparer.Ordinal); }
    }

    protected override void OnClosed(EventArgs e) { _client.Dispose(); base.OnClosed(e); }
}
