using Islet.Core;
using Islet.Search.Providers;

namespace Islet.Search;

/// <summary>
/// Собирает выдачу из провайдеров.
///
/// Без ключевого слова отвечают «глобальные» провайдеры, каждый своей группой
/// в своём порядке: ответ калькулятора и адрес → приложения → команды →
/// файлы → Kawaki и плагины → поиск в интернете последней строкой. С ключевым
/// словом («k », «cb », «>») — только его провайдер, и строк от него больше.
///
/// Быстрые провайдеры отвечают на каждую букву; медленные (файлы, сеть,
/// процессы плагинов) — после паузы в наборе, параллельно, и каждый со своим
/// потолком ожидания: один зависший плагин не держит всю выдачу.
/// </summary>
internal sealed class SearchService
{
    private const int MaxTotal = 40;
    private static readonly TimeSpan SlowProviderTimeout = TimeSpan.FromSeconds(4);

    private readonly List<SearchProvider> _builtIns;
    private readonly AppIndex _apps = new();

    public CommandsProvider Commands { get; } = new();
    public AppIndex Apps => _apps;

    /// <summary>Провайдеры плагинов — меняются при перезагрузке плагинов.</summary>
    public Func<IEnumerable<SearchProvider>>? Extra { get; set; }

    public SearchService(DriveIndex drives, IEnumerable<SearchProvider> integrations)
    {
        _builtIns =
        [
            new CalculatorProvider(),
            new UrlProvider(),
            new AppsProvider(_apps),
            Commands,
            new FilesProvider(drives),
            new TimerProvider(App.Current.Timers),
            new ClipboardProvider(App.Current.Clipboard),
            new WebProvider(),
            new HelpProvider(() => All),
            .. integrations,
        ];
    }

    public IEnumerable<SearchProvider> All =>
        _builtIns.Concat(Extra?.Invoke() ?? []).Where(p => p.IsEnabled);

    public void WarmUp() => _apps.RefreshIfStale();

    public SearchProvider? Find(string? id) => id is null ? null : All.FirstOrDefault(p => p.Id == id);

    public SearchProvider? FindByKeyword(string keyword) =>
        All.FirstOrDefault(p => p.Keywords.Any(k => string.Equals(k, keyword, StringComparison.OrdinalIgnoreCase)));

    public static SearchQuery MakeQuery(string text, string? scope) =>
        new(text.Trim(), scope, KeyboardLayout.Alternate(text));

    private IEnumerable<SearchProvider> Participants(SearchQuery q) =>
        q.IsScoped
            ? Find(q.Scope) is { } scoped ? [scoped] : []
            : All.Where(p => p.IsGlobal);

    private static int Max(SearchProvider p, SearchQuery q) => q.IsScoped ? p.MaxScoped : p.MaxGlobal;

    private static bool ShouldAsk(SearchProvider p, SearchQuery q) => !q.IsEmpty || (q.IsScoped && p.AnswersEmptyScoped);

    /// <summary>Быстрая часть — на каждую букву, на UI-потоке.</summary>
    public List<ResultItem> SearchInstant(SearchQuery q)
    {
        var groups = new List<(SearchProvider Provider, List<ResultItem> Items)>();
        foreach (var p in Participants(q).Where(p => p.IsInstant && ShouldAsk(p, q)))
        {
            try
            {
                groups.Add((p, p.Query(q, Max(p, q))));
            }
            catch (Exception e)
            {
                Log.Write($"provider {p.Id} failed: {e.Message}");
            }
        }
        return Merge(q, groups);
    }

    /// <summary>Полная выдача: быстрые и медленные вместе.</summary>
    public async Task<List<ResultItem>> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var participants = Participants(q).Where(p => ShouldAsk(p, q)).ToList();
        var groups = new List<(SearchProvider Provider, List<ResultItem> Items)>();

        foreach (var p in participants.Where(p => p.IsInstant))
        {
            try { groups.Add((p, p.Query(q, Max(p, q)))); }
            catch (Exception e) { Log.Write($"provider {p.Id} failed: {e.Message}"); }
        }

        var slow = participants.Where(p => !p.IsInstant)
            .Select(async p =>
            {
                try
                {
                    var items = await p.QueryAsync(q, Max(p, q), ct).WaitAsync(SlowProviderTimeout, ct);
                    return (Provider: p, Items: items);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    Log.Write($"provider {p.Id} failed: {e.Message}");
                    return (Provider: p, Items: new List<ResultItem>());
                }
            })
            .ToList();
        foreach (var result in await Task.WhenAll(slow))
            groups.Add(result);

        ct.ThrowIfCancellationRequested();
        return Merge(q, groups);
    }

    /// <summary>Провайдер медленный — его строки, найденные раньше, можно подержать до нового ответа.</summary>
    public bool IsSlow(string providerId) => Find(providerId) is { IsInstant: false };

    private static List<ResultItem> Merge(SearchQuery q, List<(SearchProvider Provider, List<ResultItem> Items)> groups)
    {
        var results = groups
            .OrderBy(g => g.Provider.Order)
            .SelectMany(g => g.Items)
            .DistinctBy(r => r.HistoryKey)
            .Take(MaxTotal)
            .ToList();

        // Нашлось по запросу в другой раскладке — первой строкой объясняем почему,
        // и Enter по ней переписывает строку поиска.
        if (q.AltText is { } alt && results.Any(r => r.ProviderId is "apps" or "files")
            && results.Where(r => r.ProviderId is "apps" or "files").All(r => !Matches(r.Title, q.Text)))
        {
            results.Insert(0, new ResultItem
            {
                Title = Loc.T("Hint_Layout", alt),
                Subtitle = Loc.T("Hint_LayoutSub"),
                Kind = ResultKind.Hint,
                Target = alt,
                ProviderId = "layout",
                GlyphOverride = "",
                Trailing = "Tab",
                Remember = false,
                Action = new IsletAction { Query = alt },
            });
        }
        return results;
    }

    private static bool Matches(string title, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>Недавнее и частое — на пустой запрос. Несуществующее уже отсеивается.</summary>
    public Task<List<ResultItem>> RecentAsync(int max)
    {
        var entries = Frecency.Top(max * 2).ToList();
        var appsLoaded = _apps.IsLoaded;
        return Task.Run(() =>
        {
            var list = new List<ResultItem>();
            foreach (var e in entries)
            {
                if (list.Count >= max) break;
                ResultItem? item = e.Kind switch
                {
                    ResultKind.App when !appsLoaded || _apps.Contains(e.Target) => Rebuild(e),
                    ResultKind.File when File.Exists(e.Target) => Rebuild(e),
                    ResultKind.Folder when Directory.Exists(e.Target) => Rebuild(e),
                    ResultKind.Url or ResultKind.Kawaki => Rebuild(e),
                    ResultKind.Command => Commands.Find(e.Target),
                    _ => null,
                };
                if (item is null) continue;
                item.IsRecent = true;
                list.Add(item);
            }
            return list;
        });
    }

    private static ResultItem Rebuild(Frecency.Entry e) => new()
    {
        Title = e.Title,
        Subtitle = e.Subtitle,
        Kind = e.Kind,
        Target = e.Target,
        ProviderId = "recent",
        IconSource = e.IconSource,
        IconUrl = e.IconUrl,
        GlyphOverride = e.Glyph,
    };
}
