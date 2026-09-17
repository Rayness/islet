namespace KawakiIsland.Search;

internal sealed class SearchService
{
    private const int MaxApps = 4;
    private const int MaxFiles = 5;

    /// <summary>Куда уходит «найти в интернете». Запрос подставляется в конец.</summary>
    private const string WebSearchUrl = "https://yandex.ru/search/?text=";

    private readonly AppIndex _apps = new();

    public void WarmUp() => _apps.RefreshIfStale();

    /// <summary>Мгновенная часть: приложения из памяти и строка «в интернете».</summary>
    public List<ResultItem> SearchInstant(string query)
    {
        _apps.RefreshIfStale();
        var results = _apps.Search(query, MaxApps);
        results.Add(WebItem(query));
        return results;
    }

    /// <summary>Полная выдача: приложения, затем файлы, затем интернет.</summary>
    public async Task<List<ResultItem>> SearchAsync(string query)
    {
        var files = await FileSearch.SearchAsync(query, MaxFiles);
        var results = _apps.Search(query, MaxApps);
        results.AddRange(files);
        results.Add(WebItem(query));
        return results;
    }

    private static ResultItem WebItem(string query) => new()
    {
        Title = $"Найти «{query}» в интернете",
        Subtitle = "Браузер",
        Kind = ResultKind.Web,
        Target = WebSearchUrl + Uri.EscapeDataString(query),
    };
}
