using System.Text.RegularExpressions;
using Islet.Core;
using Islet.Settings;
using Islet.Shell;

namespace Islet.Search.Providers;

/// <summary>Приложения из «Все приложения» — из памяти, на каждую букву.</summary>
internal sealed class AppsProvider(AppIndex index) : SearchProvider
{
    public override string Id => "apps";
    public override string Name => Loc.T("Provider_Apps");
    public override string Glyph => "";
    public override int Order => 10;
    public override int MaxGlobal => 4;

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        index.RefreshIfStale();
        if (query.IsEmpty) return [];
        var results = index.Search(query.Text, max);
        // Своих находок мало — пробуем тот же запрос в другой раскладке.
        if (results.Count < 2 && query.AltText is { } alt)
        {
            foreach (var extra in index.Search(alt, max))
            {
                if (results.Count >= max) break;
                if (results.All(r => r.Target != extra.Target))
                    results.Add(extra);
            }
        }
        return results;
    }
}

/// <summary>Файлы и папки: индекс Windows Search и свой индекс дисков.</summary>
internal sealed class FilesProvider(DriveIndex drives) : SearchProvider
{
    public override string Id => "files";
    public override string Name => Loc.T("Provider_Files");
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => ["f", "ф"];
    public override string Description => Loc.T("Provider_FilesHint");
    public override bool IsInstant => false;
    public override int Order => 30;
    public override int MaxGlobal => 5;

    public override async Task<List<ResultItem>> QueryAsync(SearchQuery query, int max, CancellationToken ct)
    {
        if (query.IsEmpty) return [];
        var files = await SearchFiles(query.Text, max, ct);
        if (files.Count < 2 && query.AltText is { } alt && !ct.IsCancellationRequested)
            files.AddRange((await SearchFiles(alt, max, ct)).Where(f => files.All(x => x.Target != f.Target)));

        // Открываемое часто — выше, остальное в порядке источников.
        return files
            .Select((f, i) => (File: f, Rank: Frecency.Boost(f) * 10 - i))
            .OrderByDescending(x => x.Rank)
            .Select(x => x.File)
            .Take(max)
            .ToList();
    }

    private async Task<List<ResultItem>> SearchFiles(string text, int max, CancellationToken ct)
    {
        var windows = FileSearch.SearchAsync(text, max);
        var local = Task.Run(() => drives.Search(text, max), ct);
        await Task.WhenAll(windows, local);
        // Поровну из обоих источников, чтобы второй диск не терялся за первым.
        return Interleave(windows.Result, local.Result)
            .DistinctBy(f => f.Target, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static IEnumerable<ResultItem> Interleave(List<ResultItem> a, List<ResultItem> b)
    {
        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            if (i < a.Count) yield return a[i];
            if (i < b.Count) yield return b[i];
        }
    }
}

/// <summary>«= 2+2», «sqrt(2)*3», «0xFF + 1» — ответ первой строкой, Enter копирует.</summary>
internal sealed class CalculatorProvider : SearchProvider
{
    public override string Id => "calc";
    public override string Name => Loc.T("Provider_Calc");
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => ["="];
    public override string Description => Loc.T("Provider_CalcHint");
    public override int Order => 0;
    public override bool IsEnabled => SettingsStore.Current.CalculatorEnabled;

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var text = query.Text;
        if (text.Length == 0 || (!query.IsScoped && !Calculator.LooksLikeMath(text))) return [];
        if (!Calculator.TryEvaluate(text, out var value)) return [];
        // Просто число — считать нечего.
        if (double.TryParse(text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out _))
            return [];

        var (display, copy) = Calculator.Format(value);
        return
        [
            new ResultItem
            {
                Title = "= " + display,
                Subtitle = text.Trim(),
                Kind = ResultKind.Calc,
                Target = copy,
                ProviderId = Id,
                Trailing = Loc.T("Trailing_Copy"),
                Action = new IsletAction { Copy = copy },
                Actions = [new ResultAction(Loc.T("Ctx_Paste"), "", new IsletAction { Paste = copy })],
                Remember = false,
            },
        ];
    }
}

/// <summary>Адрес сайта или путь — открыть напрямую, без поисковика.</summary>
internal sealed partial class UrlProvider : SearchProvider
{
    public override string Id => "url";
    public override string Name => Loc.T("Provider_Url");
    public override int Order => 1;

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var text = query.Text.Trim();
        if (text.Length < 3 || text.Contains(' ') && !LooksLikePath(text)) return [];

        if (LooksLikePath(text))
        {
            var path = Environment.ExpandEnvironmentVariables(text.StartsWith('~')
                ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), text[1..])
                : text);
            var isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return [];
            return
            [
                new ResultItem
                {
                    Title = Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path,
                    Subtitle = path,
                    Kind = isDir ? ResultKind.Folder : ResultKind.File,
                    Target = path,
                    IconSource = path,
                    ProviderId = Id,
                    Trailing = Loc.T("Trailing_Open"),
                },
            ];
        }

        if (!TryGetUrl(text, out var url)) return [];
        return
        [
            new ResultItem
            {
                Title = Loc.T("Result_OpenUrl", url.Host.Length > 0 ? url.Host + url.PathAndQuery.TrimEnd('/') : text),
                Subtitle = url.ToString(),
                Kind = ResultKind.Url,
                Target = url.ToString(),
                ProviderId = Id,
                Trailing = Loc.T("Trailing_Open"),
            },
        ];
    }

    private static bool LooksLikePath(string text) =>
        DrivePathRegex().IsMatch(text) || text.StartsWith(@"\\") || text.StartsWith('%') || text.StartsWith(@"~\") || text.StartsWith("~/");

    public static bool TryGetUrl(string text, out Uri url)
    {
        url = null!;
        if (text.Contains(' ')) return false;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https" or "ftp")
        {
            url = absolute;
            return true;
        }
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase) || DomainRegex().IsMatch(text))
            return Uri.TryCreate("https://" + text, UriKind.Absolute, out url!);
        return false;
    }

    [GeneratedRegex(@"^[a-zA-Z]:[\\/]")]
    private static partial Regex DrivePathRegex();

    // Домен с настоящей зоной из букв: «kawaki.ru», «github.com/rayness». Не «file.txt» — у файлов
    // зоны вроде txt/docx не бывает сайтами, поэтому зоны ограничены распространёнными.
    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)+(?:ru|com|org|net|io|dev|app|me|рф|su|ua|by|kz|de|uk|co|tv|gg|xyz|info|ai|so|to|site|online|tech|pro|cc|fm|ly|link|page|moe)(?::\d+)?(?:[/?#]\S*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex DomainRegex();
}

/// <summary>Поиск в интернете — последней строкой, и «g запрос», «yt запрос» через плагин web-shortcuts.</summary>
internal sealed class WebProvider : SearchProvider
{
    public override string Id => "web";
    public override string Name => Loc.T("Provider_Web");
    public override int Order => 100;
    public override int MaxGlobal => 1;

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        if (query.IsEmpty) return [];
        var engine = SearchEngine.Find(SettingsStore.Current.SearchEngine);
        return
        [
            new ResultItem
            {
                Title = Loc.T("Result_Web", query.Text),
                Subtitle = engine.Title,
                Kind = ResultKind.Web,
                Target = engine.UrlPrefix + Uri.EscapeDataString(query.Text),
                ProviderId = Id,
                Remember = false,
            },
        ];
    }
}

/// <summary>«timer 5m», «таймер 25 чай» — обратный отсчёт в капсуле.</summary>
internal sealed class TimerProvider(TimerService timers) : SearchProvider
{
    private static readonly int[] Presets = [1, 3, 5, 10, 15, 25, 45];

    public override string Id => "timer";
    public override string Name => Loc.T("Provider_Timer");
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => ["timer", "таймер", "tm"];
    public override string Description => Loc.T("Provider_TimerHint");
    public override bool IsGlobal => false;
    public override bool AnswersEmptyScoped => true;
    public override int Order => 2;

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var results = new List<ResultItem>();
        if (TimerService.TryParse(query.Text, out var duration, out var label))
        {
            results.Add(StartRow(duration, label));
        }
        else if (query.IsEmpty)
        {
            foreach (var minutes in Presets)
                results.Add(StartRow(TimeSpan.FromMinutes(minutes), null));
        }

        foreach (var running in timers.Timers)
        {
            var left = running.EndsAt - DateTime.Now;
            results.Add(new ResultItem
            {
                Title = Loc.T("Timer_Running", TimerService.Format(left)) + (running.Label.Length > 0 ? " · " + running.Label : ""),
                Subtitle = Loc.T("Timer_CancelHint"),
                Kind = ResultKind.Timer,
                Target = "timer:" + running.Id,
                ProviderId = Id,
                GlyphOverride = "",
                Remember = false,
                Action = IsletAction.Run(() =>
                {
                    timers.Cancel(running.Id);
                    return ActionOutcome.DoneRestoreFocus;
                }),
            });
        }
        return results.Take(max).ToList();
    }

    private ResultItem StartRow(TimeSpan duration, string? label) => new()
    {
        Title = Loc.T("Timer_Start", TimerService.Format(duration)) + (label is null ? "" : " · " + label),
        Subtitle = Loc.T("Timer_StartHint"),
        Kind = ResultKind.Timer,
        Target = "timer",
        ProviderId = Id,
        Remember = false,
        Action = IsletAction.Run(() =>
        {
            timers.Start(duration, label);
            return ActionOutcome.DoneRestoreFocus;
        }),
    };
}

/// <summary>История буфера обмена: «cb », Enter — вставить в окно, где были до островка.</summary>
internal sealed class ClipboardProvider(ClipboardHistory history) : SearchProvider
{
    public override string Id => "clipboard";
    public override string Name => Loc.T("Provider_Clipboard");
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => ["cb", "буфер", "clip"];
    public override string Description => Loc.T("Provider_ClipboardHint");
    public override bool IsGlobal => false;
    public override bool AnswersEmptyScoped => true;
    public override int Order => 40;
    public override bool IsEnabled => SettingsStore.Current.ClipboardHistory;

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var words = query.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return history.Items
            .Where(e => words.All(w => e.Text.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Take(max)
            .Select(e => new ResultItem
            {
                Title = OneLine(e.Text),
                Subtitle = Loc.T("Clipboard_Meta", e.Time.ToString("HH:mm"), e.Text.Length),
                Kind = ResultKind.Clipboard,
                Target = e.Text,
                ProviderId = Id,
                Trailing = Loc.T("Trailing_Paste"),
                Remember = false,
                Action = new IsletAction { Paste = e.Text },
                Actions =
                [
                    new ResultAction(Loc.T("Ctx_Copy"), "", new IsletAction { Copy = e.Text }),
                    new ResultAction(Loc.T("Ctx_RemoveFromHistory"), "", IsletAction.Run(() =>
                    {
                        history.Remove(e);
                        return ActionOutcome.Stay;
                    })),
                ],
            })
            .ToList();
    }

    private static string OneLine(string text)
    {
        var line = string.Join(' ', text.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return line.Length > 140 ? line[..140] + "…" : line;
    }
}

/// <summary>«?» — все ключевые слова: что можно набрать и что оно найдёт.</summary>
internal sealed class HelpProvider(Func<IEnumerable<SearchProvider>> providers) : SearchProvider
{
    public override string Id => "help";
    public override string Name => Loc.T("Provider_Help");
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => ["?"];
    public override bool IsGlobal => false;
    public override bool AnswersEmptyScoped => true;
    public override int Order => 0;
    public override int MaxScoped => 30;

    public override List<ResultItem> Query(SearchQuery query, int max) =>
        providers()
            .Where(p => p.IsEnabled && p.Keywords.Count > 0 && p != this)
            .Where(p => query.IsEmpty || p.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                || p.Keywords.Any(k => k.StartsWith(query.Text, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(p => p.Order)
            .Take(max)
            .Select(p => new ResultItem
            {
                Title = $"{p.Keywords[0]}  —  {p.Name}",
                Subtitle = p.Description,
                Kind = ResultKind.Hint,
                Target = p.Keywords[0],
                ProviderId = Id,
                GlyphOverride = p.Glyph,
                Trailing = string.Join("  ", p.Keywords.Skip(1)),
                Remember = false,
                Action = new IsletAction { Query = p.Keywords[0] + " " },
            })
            .ToList();
}
