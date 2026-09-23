using System.Text.Json;
using Islet.Core;
using Islet.Search;
using Islet.Settings;

namespace Islet.Integrations;

/// <summary>
/// ClipTide (загрузчик видео того же автора) и островок.
///
/// ClipTide пишет свои уведомления в %LOCALAPPDATA%\ClipTide\notifications.json —
/// «Готово: &lt;название&gt;» с превью и папкой загрузки. Островок следит за этим
/// файлом и показывает каждое новое пиком: превью, название, щелчок открывает
/// папку. Правок в ClipTide не нужно, опроса нет — только FileSystemWatcher.
///
/// «ct » в строке поиска — последние загрузки и сам ClipTide.
/// </summary>
internal sealed class ClipTideBridge : IDisposable
{
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipTide");
    private static readonly string NotificationsPath = Path.Combine(DataDir, "notifications.json");

    public sealed record Download(string Id, string Title, string Message, string? Folder, string? Thumbnail, string Time, string Source);

    private FileSystemWatcher? _watcher;
    private readonly HashSet<string> _known = [];
    private Timer? _debounce;

    public bool IsInstalled => Directory.Exists(DataDir);

    public void Start()
    {
        Stop();
        if (!IsInstalled || !SettingsStore.Current.ClipTideNotifications) return;

        // Всё, что лежит в файле сейчас, — уже старое: пиком только новое.
        foreach (var d in Read()) _known.Add(d.Id);

        try
        {
            _watcher = new FileSystemWatcher(DataDir, "notifications.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
            Log.Write("cliptide: watching notifications");
        }
        catch (Exception e)
        {
            Log.Write($"cliptide watcher failed: {e.Message}");
        }
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }

    // ClipTide переписывает файл целиком; ждём, пока запись закончится.
    private void Schedule()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => Check(), null, 400, Timeout.Infinite);
    }

    private void Check()
    {
        foreach (var d in Read())
        {
            if (!_known.Add(d.Id)) continue;
            App.Current.Notifications.Post(new IsletNotification
            {
                Source = "cliptide",
                SourceName = "ClipTide",
                ExternalId = d.Id,
                Title = d.Title,
                Body = d.Message,
                Icon = d.Thumbnail is { Length: > 0 } t && t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? t : null,
                Glyph = "",
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
                string? S(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                var payload = n.TryGetProperty("payload", out var p) ? p : default;
                list.Add(new Download(
                    S(n, "id") ?? "",
                    S(n, "title") ?? "ClipTide",
                    S(n, "message") ?? "",
                    S(payload, "folder"),
                    S(payload, "thumbnail"),
                    S(n, "timestamp") ?? "",
                    S(n, "source") ?? ""));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException)
        {
            // Файл в середине записи — прочтём при следующем изменении.
        }
        return list;
    }

    public void Dispose() => Stop();
}

/// <summary>«ct » — последние загрузки ClipTide и запуск самого ClipTide.</summary>
internal sealed class ClipTideProvider(AppIndex apps) : SearchProvider
{
    public override string Id => "cliptide";
    public override string Name => "ClipTide";
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => ["ct", "cliptide"];
    public override string Description => Loc.T("Provider_ClipTideHint");
    public override bool IsGlobal => false;
    public override bool AnswersEmptyScoped => true;
    public override int Order => 45;
    public override bool IsEnabled => Directory.Exists(ClipTideBridge.DataDir);

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var words = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var results = new List<ResultItem>();

        if (apps.FindIdByName("ClipTide") is { } appId && (words.Length == 0 || "cliptide".Contains(words[0], StringComparison.OrdinalIgnoreCase)))
        {
            results.Add(new ResultItem
            {
                Title = Loc.T("ClipTide_Open"),
                Subtitle = Loc.T("Result_App"),
                Kind = ResultKind.App,
                Target = appId,
                ProviderId = Id,
                IconSource = $"shell:AppsFolder\\{appId}",
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
                IconUrl = d.Thumbnail is { Length: > 0 } t && t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? t : null,
                GlyphOverride = "",
                Trailing = folderExists ? Loc.T("Trailing_OpenFolder") : "",
                Remember = false,
            });
        }
        return results;
    }
}

/// <summary>«k » — поиск аниме в каталоге Kawaki.</summary>
internal sealed class KawakiProvider(KawakiClient client) : SearchProvider
{
    public override string Id => "kawaki";
    public override string Name => "Kawaki";
    public override string Glyph => "";
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
