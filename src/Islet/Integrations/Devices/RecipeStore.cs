using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Islet.Integrations.Devices;

/// <summary>
/// Откуда берутся рецепты. Три слоя, каждый следующий главнее:
///
///  1. встроенная копия devices.json — рядом с Islet.exe, работает без сети;
///  2. общая база из репозитория (<see cref="RemoteUrl"/>) — раз в сутки, с ETag, кешируется
///     в %LOCALAPPDATA%\Islet.cache; берётся, только если её revision не ниже встроенной;
///  3. свой файл %APPDATA%\Islet\devices.json — для тех, кто пишет рецепт для своего
///     устройства: правки подхватываются сразу, рецепт с тем же id заменяет общий.
///
/// Битый файл или битый рецепт не ломают остальное: негодное отбрасывается, причина — в отчёте.
/// </summary>
internal sealed class RecipeStore : IDisposable
{
    public const string RemoteUrl = "https://raw.githubusercontent.com/Rayness/islet/master/devices/devices.json";
    private const long MaxBytes = 1024 * 1024;
    private static readonly TimeSpan FetchEvery = TimeSpan.FromHours(24);

    public static readonly string BundledPath = Path.Combine(AppContext.BaseDirectory, "devices.json");
    public static readonly string CachePath = Path.Combine(Paths.Cache, "devices.json");
    public static readonly string LocalPath = Path.Combine(Paths.Config, "devices.json");
    private static readonly string EtagPath = Path.Combine(Paths.Cache, "devices.etag");

    private readonly Func<bool> _onlineEnabled;
    private FileSystemWatcher? _watcher;
    private Timer? _reloadDebounce;
    private Timer? _fetchTimer;
    private HttpClient? _http;

    public RecipeStore(Func<bool> onlineEnabled) => _onlineEnabled = onlineEnabled;

    public IReadOnlyList<Recipe> Recipes { get; private set; } = [];

    /// <summary>Что загружено и что отброшено — для отчёта диагностики.</summary>
    public IReadOnlyList<string> Summary { get; private set; } = [];

    /// <summary>Рецепты поменялись. С любого потока.</summary>
    public event Action? Changed;

    public void Start()
    {
        Load();
        try
        {
            Directory.CreateDirectory(Paths.Config);
            _watcher = new FileSystemWatcher(Paths.Config, "devices.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            FileSystemEventHandler reload = (_, _) => ScheduleReload();
            _watcher.Changed += reload;
            _watcher.Created += reload;
            _watcher.Deleted += reload;
            _watcher.Renamed += (_, _) => ScheduleReload();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception e)
        {
            Log.Write($"devices: local recipes watcher failed: {e.Message}");
        }
        // Первый раз — не на старте: островку сначала надо появиться.
        _fetchTimer = new Timer(_ => _ = FetchAsync(), null, TimeSpan.FromSeconds(30), FetchEvery);
    }

    private void ScheduleReload()
    {
        _reloadDebounce?.Dispose();
        _reloadDebounce = new Timer(_ => Guard.Run(() =>
        {
            Load();
            Changed?.Invoke();
        }), null, 300, Timeout.Infinite);
    }

    public void Load()
    {
        var summary = new List<string>();
        var bundled = ReadFile(BundledPath, "встроенная", summary);
        var cached = ReadFile(CachePath, "из сети", summary);
        var local = ReadFile(LocalPath, "свой файл", summary);

        // Сетевая база главнее встроенной, только если не старше её: после обновления
        // островка встроенная копия может оказаться свежее давнего кеша.
        var useCached = cached is not null && (bundled is null || cached.Value.Revision >= bundled.Value.Revision);
        var baseLayer = useCached ? cached : bundled;
        var byId = new Dictionary<string, Recipe>();
        foreach (var r in baseLayer?.Recipes ?? []) byId[r.Id] = r;
        foreach (var r in local?.Recipes ?? []) byId[r.Id] = r;

        Recipes = [.. byId.Values];
        summary.Insert(0, $"рецептов: {Recipes.Count}, база — {(useCached ? "из сети" : "встроенная")}{(local is not null ? " + свой файл" : "")}");
        Summary = summary;
        Log.Write($"devices: {summary[0]}");
    }

    private static (int Revision, List<Recipe> Recipes)? ReadFile(string path, string label, List<string> summary)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes)
            {
                summary.Add($"{label}: файл больше 1 МБ — пропущен");
                return null;
            }
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var errors = new List<string>();
            var recipes = RecipeParser.Parse(doc.RootElement, errors);
            var revision = doc.RootElement.TryGetProperty("revision", out var rv) && rv.ValueKind == JsonValueKind.Number ? rv.GetInt32() : 0;
            summary.Add($"{label} (revision {revision}): {recipes.Count} рецептов, {path}");
            summary.AddRange(errors.Select(e => $"  ✕ {e}"));
            return (revision, recipes);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            summary.Add($"{label}: не читается — {e.Message}");
            return null;
        }
    }

    /// <summary>Скачать общую базу, если она поменялась. Ошибки сети — не повод что-то ломать.</summary>
    public async Task FetchAsync()
    {
        if (!_onlineEnabled()) return;
        try
        {
            _http ??= CreateHttp();
            using var request = new HttpRequestMessage(HttpMethod.Get, RemoteUrl);
            if (File.Exists(CachePath) && File.Exists(EtagPath) && File.ReadAllText(EtagPath).Trim() is { Length: > 0 } etag)
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (response.StatusCode == HttpStatusCode.NotModified) return;
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return;

            var bytes = await response.Content.ReadAsByteArrayAsync();
            if (bytes.Length > MaxBytes) return;
            // Сначала проверить, что это вообще наша база, и только потом класть в кеш.
            using (var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }))
            {
                var errors = new List<string>();
                if (RecipeParser.Parse(doc.RootElement, errors).Count == 0) return;
            }
            Directory.CreateDirectory(Paths.Cache);
            var temp = CachePath + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, CachePath, overwrite: true);
            if (response.Headers.ETag is { } tag) await File.WriteAllTextAsync(EtagPath, tag.ToString());

            Load();
            Changed?.Invoke();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException or FormatException or UnauthorizedAccessException)
        {
            Log.Write($"devices: base update failed: {e.Message}");
        }
    }

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        var version = typeof(RecipeStore).Assembly.GetName().Version?.ToString(3) ?? "0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Islet/{version} (Windows)");
        return http;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadDebounce?.Dispose();
        _fetchTimer?.Dispose();
        _http?.Dispose();
    }
}
