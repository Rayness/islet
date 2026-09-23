using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Islet.Core;
using Islet.Ipc;
using Islet.Native;
using Islet.Search;
using Islet.Search.Providers;
using Islet.Settings;
using Islet.Shell;

namespace Islet.Integrations;

/// <summary>
/// ClipTide (загрузчик видео того же автора) и островок.
///
/// Уведомления. ClipTide пишет их в %LOCALAPPDATA%\ClipTide\notifications.json —
/// «Готово: &lt;название&gt;» с превью и папкой загрузки. Островок следит за
/// файлом и показывает каждое новое пиком: превью, название, щелчок открывает папку.
///
/// Загрузки. У ClipTide свой канал \\.\pipe\ClipTide.&lt;пользователь&gt;
/// (app/core/remote.py в его репозитории): «ct &lt;ссылка&gt;» отправляет туда
/// ссылку, и ClipTide сразу её качает. Если ClipTide закрыт, островок запускает
/// его командой из launch.json — её переписывает каждый запуск ClipTide.
///
/// Прогресс. Островок подписывается на тот же канал, и ClipTide сам присылает
/// состояние загрузок — капсула показывает название, проценты и превью, щелчок
/// выводит окно ClipTide вперёд. Опроса нет: подписка открывается, когда
/// ClipTide запускается (переписал launch.json), и закрывается вместе с ним.
/// </summary>
internal sealed class ClipTideBridge : IDisposable
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipTide");
    private static readonly string NotificationsPath = Path.Combine(DataDir, "notifications.json");
    private static readonly string LaunchPath = Path.Combine(DataDir, "launch.json");
    private static string PipeName => $"ClipTide.{Environment.UserName}";

    public const string ReleasesUrl = "https://github.com/Rayness/YouTube-Downloader/releases/latest";
    private const string ActivityId = "cliptide";
    private const string Accent = "#3BE5CE";
    private const string DownloadGlyph = "\uE896";

    public sealed record Download(string Id, string Title, string Message, string? Folder, string? Thumbnail, string Time, string Source);

    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _known = [];
    private Timer? _notificationsDebounce;
    private Timer? _launchDebounce;
    private Timer? _pendingTimeout;

    private CancellationTokenSource? _subscription;
    private string? _lastActivity;

    public bool IsInstalled => Directory.Exists(DataDir);

    /// <summary>Принимает ли этот ClipTide ссылки: старые версии канала и launch.json не знают.</summary>
    public bool CanDownload => File.Exists(LaunchPath);

    public void Start()
    {
        Stop();
        if (!IsInstalled) return;

        // Всё, что лежит в файле сейчас, — уже старое: пиком только новое.
        foreach (var d in Read()) _known.Add(d.Id);

        try
        {
            _watcher = new FileSystemWatcher(DataDir, "*.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, e) => OnFileChanged(e.Name);
            _watcher.Created += (_, e) => OnFileChanged(e.Name);
            _watcher.Renamed += (_, e) => OnFileChanged(e.Name);
            _watcher.EnableRaisingEvents = true;
            Log.Write("cliptide: watching");
        }
        catch (Exception e)
        {
            Log.Write($"cliptide watcher failed: {e.Message}");
        }

        // ClipTide мог быть запущен раньше островка.
        if (CanDownload) Subscribe();
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _notificationsDebounce?.Dispose();
        _notificationsDebounce = null;
        _launchDebounce?.Dispose();
        _launchDebounce = null;
    }

    private void OnFileChanged(string? name)
    {
        // ClipTide переписывает файлы целиком; ждём, пока запись закончится.
        if (string.Equals(name, "notifications.json", StringComparison.OrdinalIgnoreCase))
        {
            _notificationsDebounce?.Dispose();
            _notificationsDebounce = new Timer(_ => Guard.Run(CheckNotifications), null, 400, Timeout.Infinite);
        }
        else if (string.Equals(name, "launch.json", StringComparison.OrdinalIgnoreCase))
        {
            // launch.json переписывается при каждом запуске ClipTide — значит, канал уже есть.
            _launchDebounce?.Dispose();
            _launchDebounce = new Timer(_ => Guard.Run(Subscribe), null, 300, Timeout.Infinite);
        }
    }

    private void CheckNotifications()
    {
        var show = SettingsStore.Current.ClipTideNotifications;
        foreach (var d in Read())
        {
            if (!_known.Add(d.Id) || !show) continue;
            App.Current.Notifications.Post(new IsletNotification
            {
                Source = "cliptide",
                SourceName = "ClipTide",
                ExternalId = d.Id,
                Title = d.Title,
                Body = d.Message,
                Icon = IsWebImage(d.Thumbnail) ? d.Thumbnail : null,
                Glyph = "\uE896",
                Action = d.Folder is { Length: > 0 } folder && Directory.Exists(folder) ? IsletAction.OpenTarget(folder) : null,
            });
        }
    }

    /// <summary>Уведомления ClipTide, новые в конце (так их пишет ClipTide).</summary>
    public static List<Download> Read()
    {
        var list = new List<Download>();
        try
        {
            if (!File.Exists(NotificationsPath)) return list;
            // ClipTide может держать файл открытым на запись — читаем с общим доступом.
            using var stream = new FileStream(NotificationsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            foreach (var n in doc.RootElement.EnumerateArray())
            {
                var payload = n.TryGetProperty("payload", out var p) ? p : default;
                list.Add(new Download(
                    Protocol.Str(n, "id") ?? "",
                    Protocol.Str(n, "title") ?? "ClipTide",
                    Protocol.Str(n, "message") ?? "",
                    Protocol.Str(payload, "folder"),
                    Protocol.Str(payload, "thumbnail"),
                    Protocol.Str(n, "timestamp") ?? "",
                    Protocol.Str(n, "source") ?? ""));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            // Файл в середине записи — прочтём при следующем изменении.
        }
        return list;
    }

    // ------------------------------------------------------------------
    // Загрузка по ссылке
    // ------------------------------------------------------------------

    /// <summary>
    /// Отдать ссылку ClipTide. Запущен — по каналу, мгновенно; нет — запустить
    /// свёрнутым с ключом --download. Вызывается с UI-потока, не блокирует его.
    /// </summary>
    public void DownloadLink(string url, string format, string quality)
    {
        var message = new JsonObject
        {
            ["type"] = "download",
            ["url"] = url,
            ["format"] = format,
            ["quality"] = quality,
        }.ToJsonString();

        // Капсула отвечает сразу, не дожидаясь ClipTide: иначе после Enter
        // ничего не видно секунду (канал) или дольше (запуск, окно UAC).
        ShowPending(Loc.T("ClipTide_Analyzing"));

        _ = Task.Run(() => Guard.Run(() =>
        {
            if (IpcClient.TrySend(message, 500, PipeName))
            {
                Subscribe();
                return;
            }
            ShowPending(Loc.T("ClipTide_Starting"));
            if (!Launch(["--download", url, "--format", format, "--quality", quality, "--minimized"]))
            {
                ClearPending();
                PostError(Loc.T("ClipTide_LaunchFailed"), "", null);
            }
            // Дальше ClipTide перепишет launch.json — по нему откроется подписка.
        }));
    }

    /// <summary>Окно ClipTide — вперёд; закрыт — запустить.</summary>
    public ActionOutcome ShowWindow()
    {
        // Островок сейчас на переднем плане и может отдать это право ClipTide.
        Win32.AllowSetForegroundWindow(Win32.ASFW_ANY);
        if (IpcClient.TrySend("""{"type":"show"}""", 300, PipeName)) return ActionOutcome.Done;
        return Launch([]) ? ActionOutcome.Done : ActionOutcome.Failed;
    }

    /// <summary>Запуск командой из launch.json. ClipTide просит права администратора — нужен ShellExecute.</summary>
    private static bool Launch(IReadOnlyList<string> args)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(LaunchPath));
            if (!doc.RootElement.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Array)
                return false;
            var parts = command.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
            if (parts.Count == 0 || !File.Exists(parts[0])) return false;

            using var process = Process.Start(new ProcessStartInfo(parts[0])
            {
                Arguments = string.Join(' ', parts.Skip(1).Concat(args).Select(Quote)),
                WorkingDirectory = Path.GetDirectoryName(parts[0]) ?? "",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception e)
        {
            // Отказ в окне UAC — тоже сюда (Win32Exception 1223).
            Log.Write($"cliptide launch failed: {e.Message}");
            return false;
        }
    }

    private static string Quote(string arg) => $"\"{arg.Replace("\"", "\\\"")}\"";

    // ------------------------------------------------------------------
    // Прогресс в капсуле
    // ------------------------------------------------------------------

    public void Subscribe()
    {
        if (!SettingsStore.Current.ClipTideProgress) return;
        var cts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _subscription, cts, null) is not null)
        {
            cts.Dispose();
            return;
        }
        _ = Task.Run(() => SubscribeLoopAsync(cts));
    }

    public void Unsubscribe()
    {
        Interlocked.Exchange(ref _subscription, null)?.Cancel();
        ClearActivity();
    }

    private async Task SubscribeLoopAsync(CancellationTokenSource cts)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(1500, cts.Token);
            await pipe.WriteAsync("{\"type\":\"subscribe\"}\n"u8.ToArray(), cts.Token);
            await pipe.FlushAsync(cts.Token);
            Log.Write("cliptide: subscribed");

            using var reader = new StreamReader(pipe, Encoding.UTF8);
            while (await reader.ReadLineAsync(cts.Token) is { } line)
                Guard.Run(() => HandleEvent(line));
        }
        catch (Exception e) when (e is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            // ClipTide закрыт или закрылся — подписка откроется при его следующем запуске.
            if (e is UnauthorizedAccessException) Log.Write($"cliptide pipe: {e.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _subscription, null, cts);
            cts.Dispose();
            ClearActivity();
        }
    }

    private void HandleEvent(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        switch (Protocol.Str(root, "type"))
        {
            case "state":
                ApplyState(root);
                break;
            case "error":
                ClearPending();
                var title = Protocol.Str(root, "title") ?? "";
                var reason = Protocol.Str(root, "message") ?? "";
                PostError(Loc.T("ClipTide_Failed"),
                    title.Length > 0 && reason.Length > 0 ? $"{title} — {reason}" : title + reason,
                    Protocol.Str(root, "url"));
                break;
        }
    }

    private void ApplyState(JsonElement state)
    {
        static int Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        var analyzing = Int(state, "analyzing");
        var queued = Int(state, "queued");
        var downloads = state.TryGetProperty("downloads", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(d => (
                Title: Protocol.Str(d, "title") ?? "",
                Thumbnail: Protocol.Str(d, "thumbnail"),
                Percent: d.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0)).ToList()
            : [];

        if (downloads.Count == 0 && analyzing == 0)
        {
            // Пустое состояние приходит и сразу после подписки — ожидание от
            // только что отправленной ссылки оно не гасит, это сделает таймаут.
            if (_pendingTimeout is null) ClearActivity();
            return;
        }
        ClearPending(keepActivity: true);

        string text;
        double? progress = null;
        string? icon = null;
        if (downloads.Count == 0)
        {
            text = Loc.T("ClipTide_Analyzing");
        }
        else
        {
            var percent = downloads.Average(d => d.Percent);
            progress = percent / 100;
            icon = downloads.Select(d => d.Thumbnail).FirstOrDefault(IsWebImage);
            text = downloads.Count > 1
                ? Loc.T("ClipTide_Many", downloads.Count, (int)percent)
                : percent >= 99
                    ? Loc.T("ClipTide_Finishing", Short(downloads[0].Title))
                    : $"{Short(downloads[0].Title)} · {(int)percent}%";
        }
        if (queued > 0) text += $" +{queued}";

        // ClipTide шлёт состояние до трёх раз в секунду; капсулу трогаем, только
        // когда видно разницу — целый процент или текст.
        var key = $"{text}|{icon}|{(progress is { } v ? Math.Round(v, 2) : -1)}";
        if (key == _lastActivity) return;
        _lastActivity = key;
        SetActivity(text, progress, icon);
    }

    private void SetActivity(string text, double? progress, string? icon) =>
        App.Current.Activities.Set(new LiveActivity
        {
            Id = ActivityId,
            Source = "cliptide",
            Text = text,
            Progress = progress,
            Glyph = DownloadGlyph,
            Icon = icon,
            Color = Accent,
            Action = IsletAction.Run(ShowWindow),
        });

    private void ClearActivity()
    {
        _lastActivity = null;
        App.Current.Activities.Clear(ActivityId);
    }

    /// <summary>«Отправляю…», пока ClipTide не ответил сам. Не ответил за минуту — убрать.</summary>
    private void ShowPending(string text)
    {
        _lastActivity = null;
        SetActivity(text, null, null);
        var timer = new Timer(_ => Guard.Run(() => ClearPending()), null, TimeSpan.FromMinutes(1), Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _pendingTimeout, timer)?.Dispose();
    }

    private void ClearPending(bool keepActivity = false)
    {
        if (Interlocked.Exchange(ref _pendingTimeout, null) is not { } timer) return;
        timer.Dispose();
        if (!keepActivity) ClearActivity();
    }

    private void PostError(string title, string message, string? url) =>
        App.Current.Notifications.Post(new IsletNotification
        {
            Source = "cliptide",
            SourceName = "ClipTide",
            Title = title,
            Body = message.Length > 0 ? message : url ?? "",
            Glyph = "\uE783",
            Action = IsletAction.Run(ShowWindow),
        });

    private static string Short(string title) => title.Length > 30 ? title[..29].TrimEnd() + "…" : title;

    public static bool IsWebImage(string? url) => url is { Length: > 0 } && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        Stop();
        Interlocked.Exchange(ref _subscription, null)?.Cancel();
        Interlocked.Exchange(ref _pendingTimeout, null)?.Dispose();
    }
}

/// <summary>
/// «ct » — скачать по ссылке через ClipTide, последние загрузки и сам ClipTide.
/// В общей выдаче отвечает только на ссылку с видеосайта: строкой «Скачать»
/// сразу под «Открыть».
/// </summary>
internal sealed class ClipTideProvider(AppIndex apps, ClipTideBridge bridge) : SearchProvider
{
    public override string Id => "cliptide";
    public override string Name => "ClipTide";
    public override string Glyph => "\uE896";
    public override IReadOnlyList<string> Keywords => ["ct", "cliptide"];
    public override string Description => Loc.T("Provider_ClipTideHint");
    public override bool AnswersEmptyScoped => true;
    // Сразу после строки адреса (у неё 1).
    public override int Order => 2;
    public override int MaxGlobal => 2;
    public override bool IsEnabled => bridge.IsInstalled;

    public static readonly string[] VideoFormats = ["mp4", "mkv", "webm"];
    public static readonly string[] AudioFormats = ["mp3", "m4a", "opus", "flac"];
    public static readonly string[] Qualities = ["2160", "1440", "1080", "720", "480", "360"];

    // Сайты, откуда люди качают видео и музыку; поддомены (m., music.) — тоже.
    private static readonly string[] VideoHosts =
    [
        "youtube.com", "youtu.be", "youtube-nocookie.com", "tiktok.com", "vk.com", "vkvideo.ru", "vk.ru",
        "rutube.ru", "dzen.ru", "ok.ru", "twitch.tv", "kick.com", "instagram.com", "x.com", "twitter.com",
        "reddit.com", "vimeo.com", "dailymotion.com", "soundcloud.com", "bilibili.com", "coub.com",
        "facebook.com", "fb.watch", "bandcamp.com", "rumble.com", "pinterest.com", "threads.net",
    ];

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var settings = SettingsStore.Current;
        var words = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (link, format, quality) = Parse(words);

        if (!query.IsScoped)
        {
            if (!settings.ClipTideLinks || words.Length != 1 || link is null || !IsVideoLink(link)) return [];
            return DownloadRows(link, null, null, fromClipboard: false);
        }

        if (link is not null) return DownloadRows(link, format, quality, fromClipboard: false);

        var results = new List<ResultItem>();
        // «ct» без ссылки — предложить ту, что лежит в буфере.
        if (words.Length == 0 && settings.ClipTideLinks && LatestClipboardLink() is { } copied)
            results.AddRange(DownloadRows(copied, null, null, fromClipboard: true));

        if (apps.FindIdByName("ClipTide") is not null && (words.Length == 0 || "cliptide".Contains(words[0], StringComparison.OrdinalIgnoreCase)))
        {
            results.Add(new ResultItem
            {
                Title = Loc.T("ClipTide_Open"),
                Subtitle = Loc.T("Result_App"),
                Kind = ResultKind.Command,
                Target = "cliptide:open",
                ProviderId = Id,
                IconSource = apps.FindIdByName("ClipTide") is { } appId ? $"shell:AppsFolder\\{appId}" : null,
                // Через канал: второй запуск ClipTide ради «покажи окно» спросил бы права администратора.
                Action = IsletAction.Run(bridge.ShowWindow),
                Remember = false,
            });
        }

        foreach (var d in Enumerable.Reverse(ClipTideBridge.Read()))
        {
            if (results.Count >= max) break;
            if (words.Length > 0 && !words.All(w => d.Message.Contains(w, StringComparison.OrdinalIgnoreCase))) continue;
            var folderExists = d.Folder is { Length: > 0 } f && Directory.Exists(f);
            results.Add(new ResultItem
            {
                Title = d.Message.Length > 0 ? d.Message : d.Title,
                Subtitle = $"{d.Title} · {d.Time}",
                Kind = ResultKind.Folder,
                Target = folderExists ? d.Folder! : ClipTideBridge.DataDir,
                ProviderId = Id,
                IconUrl = ClipTideBridge.IsWebImage(d.Thumbnail) ? d.Thumbnail : null,
                GlyphOverride = "\uE896",
                Trailing = folderExists ? Loc.T("Trailing_OpenFolder") : "",
                Remember = false,
            });
        }
        return results;
    }

    /// <summary>«ct https://… mp3 720» — ссылка и, по желанию, формат и качество в любом порядке.</summary>
    private static (Uri? Link, string? Format, string? Quality) Parse(string[] words)
    {
        Uri? link = null;
        string? format = null, quality = null;
        foreach (var word in words)
        {
            var w = word.ToLowerInvariant();
            if (VideoFormats.Contains(w) || AudioFormats.Contains(w))
                format = w;
            else if (ParseQuality(w) is { } q)
                quality = q;
            else if (link is null && UrlProvider.TryGetUrl(word, out var url) && url.Scheme is "http" or "https")
                link = url;
        }
        return (link, format, quality);
    }

    private static string? ParseQuality(string word)
    {
        var w = word.TrimEnd('p', 'р');
        return w switch
        {
            "4k" or "uhd" => "2160",
            "2k" => "1440",
            "fullhd" or "fhd" => "1080",
            "hd" => "720",
            _ => Qualities.Contains(w) ? w : null,
        };
    }

    private static bool IsVideoLink(Uri url) =>
        VideoHosts.Any(h => url.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
            || url.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    private static Uri? LatestClipboardLink()
    {
        var text = App.Current.Clipboard.Items.FirstOrDefault()?.Text.Trim();
        return text is { Length: > 0 } && UrlProvider.TryGetUrl(text, out var url) && url.Scheme is "http" or "https"
            ? url
            : null;
    }

    private List<ResultItem> DownloadRows(Uri link, string? format, string? quality, bool fromClipboard)
    {
        if (!bridge.CanDownload)
        {
            return
            [
                new ResultItem
                {
                    Title = Loc.T("ClipTide_UpdateNeeded"),
                    Subtitle = Loc.T("ClipTide_UpdateNeededHint"),
                    Kind = ResultKind.Command,
                    Target = "cliptide:update",
                    ProviderId = Id,
                    GlyphOverride = "\uE896",
                    Trailing = "ClipTide",
                    Action = IsletAction.OpenTarget(ClipTideBridge.ReleasesUrl),
                    Remember = false,
                },
            ];
        }

        var settings = SettingsStore.Current;
        var explicitFormat = format is not null;
        format ??= settings.ClipTideFormat;
        quality ??= settings.ClipTideQuality;

        var rows = new List<ResultItem> { Row(link, format, quality, fromClipboard) };
        // Формат не назван — вторая строка с противоположным: видео ↔ звук.
        if (!explicitFormat)
        {
            var other = AudioFormats.Contains(format) ? "mp4" : "mp3";
            rows.Add(Row(link, other, quality, fromClipboard));
        }
        return rows;
    }

    private ResultItem Row(Uri link, string format, string quality, bool fromClipboard)
    {
        var audio = AudioFormats.Contains(format);
        var what = audio ? format.ToUpperInvariant() : $"{format.ToUpperInvariant()} · {quality}p";
        var where = ShortLink(link);
        var url = link.ToString();
        return new ResultItem
        {
            Title = Loc.T(audio ? "ClipTide_DownloadAudio" : "ClipTide_DownloadVideo"),
            Subtitle = fromClipboard ? Loc.T("ClipTide_FromClipboard", where, what) : $"{where} · {what}",
            Kind = ResultKind.Command,
            Target = $"cliptide:{format}:{url}",
            ProviderId = Id,
            GlyphOverride = audio ? "\uE8D6" : "\uE896",
            Trailing = "ClipTide",
            Remember = false,
            Action = IsletAction.Run(() =>
            {
                bridge.DownloadLink(url, format, quality);
                return ActionOutcome.DoneRestoreFocus;
            }),
        };
    }

    private static string ShortLink(Uri link)
    {
        var text = (link.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? link.Host[4..] : link.Host)
            + link.PathAndQuery.TrimEnd('/');
        return text.Length > 48 ? text[..47] + "…" : text;
    }
}

/// <summary>«k » — поиск аниме в каталоге Kawaki.</summary>
internal sealed class KawakiProvider(KawakiClient client) : SearchProvider
{
    public override string Id => "kawaki";
    public override string Name => "Kawaki";
    public override string Glyph => "\uE8B2";
    public override IReadOnlyList<string> Keywords => ["k", "к", "kawaki"];
    public override string Description => Loc.T("Provider_KawakiHint");
    // Без ключевого слова каждый запрос уходил бы на сайт — только если человек сам так решил.
    public override bool IsGlobal => SettingsStore.Current.KawakiGlobalSearch;
    public override bool IsInstant => false;
    public override bool IsEnabled => SettingsStore.Current.KawakiSearch;
    public override int Order => 50;
    public override int MaxGlobal => 3;
    public override int MaxScoped => 8;

    public override async Task<List<ResultItem>> QueryAsync(SearchQuery query, int max, CancellationToken ct)
    {
        if (query.Text.Length < 2) return [];
        try
        {
            var results = await client.SearchAnimeAsync(query.Text, max, ct);
            if (results.Count == 0 && query.AltText is { } alt && !ct.IsCancellationRequested)
                results = await client.SearchAnimeAsync(alt, max, ct);
            return results;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return [];
        }
    }
}
