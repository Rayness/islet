using Islet.Settings;

namespace Islet.Search;

internal sealed class SearchService
{
    private const int MaxApps = 4;
    private const int MaxFiles = 5;

    private readonly AppIndex _apps = new();
    private readonly DriveIndex _drives;

    public SearchService(DriveIndex drives)
    {
        _drives = drives;
    }

    public void WarmUp() => _apps.RefreshIfStale();

    /// <summary>Мгновенная часть: приложения из памяти и строка «в интернете».</summary>
    public List<ResultItem> SearchInstant(string query)
    {
        _apps.RefreshIfStale();
        var results = _apps.Search(query, MaxApps);
        results.Add(WebItem(query));
        return results;
    }

    /// <summary>Полная выдача: приложения, затем файлы (индекс Windows + свой индекс дисков), затем интернет.</summary>
    public async Task<List<ResultItem>> SearchAsync(string query)
    {
        var windows = FileSearch.SearchAsync(query, MaxFiles);
        var drives = Task.Run(() => _drives.Search(query, MaxFiles));
        await Task.WhenAll(windows, drives);

        // Поровну из обоих источников, чтобы второй диск не терялся за первым.
        var files = Interleave(windows.Result, drives.Result)
            .DistinctBy(f => f.Target, StringComparer.OrdinalIgnoreCase)
            .Take(MaxFiles);

        var results = _apps.Search(query, MaxApps);
        results.AddRange(files);
        results.Add(WebItem(query));
        return results;
    }

    private static IEnumerable<ResultItem> Interleave(List<ResultItem> a, List<ResultItem> b)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count) yield return a[i];
            if (i < b.Count) yield return b[i];
        }
    }

    private static ResultItem WebItem(string query)
    {
        var engine = SearchEngine.Find(SettingsStore.Current.SearchEngine);
        return new()
        {
            Title = $"Найти «{query}» в интернете",
            Subtitle = engine.Title,
            Kind = ResultKind.Web,
            Target = engine.UrlPrefix + Uri.EscapeDataString(query),
        };
    }
}
