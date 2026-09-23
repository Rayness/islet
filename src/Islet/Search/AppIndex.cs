using System.Runtime.InteropServices;
using Islet.Shell;

namespace Islet.Search;

/// <summary>
/// Список «Все приложения» из меню Пуск: и обычные программы, и приложения из Store.
/// Держится в памяти и перечитывается изредка — искать по нему нужно на каждую букву.
/// </summary>
internal sealed class AppIndex
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    private IReadOnlyList<Entry> _entries = [];
    private DateTime _loadedAt = DateTime.MinValue;
    private Task? _refresh;

    private sealed record Entry(string Name, string Id, string Normalized, string Initials);

    public void RefreshIfStale()
    {
        if (DateTime.UtcNow - _loadedAt < MaxAge || _refresh is { IsCompleted: false })
            return;
        _refresh = RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            _entries = await StaWorker.Shell.Run(Enumerate);
            _loadedAt = DateTime.UtcNow;
            Log.Write($"app index: {_entries.Count} apps");
        }
        catch (Exception e)
        {
            Log.Write($"app index failed: {e}");
        }
    }

    public List<ResultItem> Search(string query, int max)
    {
        var q = Normalize(query);
        if (q.Length == 0) return [];

        // Частые запуски поднимаются над просто совпавшими: «te» ведёт в Telegram,
        // если его открывают каждый день, а не в «Техническую поддержку».
        return _entries
            .Select(e => (Entry: e, Score: Score(e, q)))
            .Where(x => x.Score > 0)
            .Select(x =>
            {
                var item = ToItem(x.Entry);
                item.Score = x.Score + Frecency.Boost(item);
                return item;
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Title.Length)
            .DistinctBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    private static ResultItem ToItem(Entry e) => new()
    {
        Title = e.Name,
        Subtitle = Loc.T("Result_App"),
        Kind = ResultKind.App,
        Target = e.Id,
        ProviderId = "apps",
        IconSource = $"shell:AppsFolder\\{e.Id}",
    };

    /// <summary>Есть ли уже список — до первой загрузки «Недавние» не фильтруем.</summary>
    public bool IsLoaded => _entries.Count > 0;

    /// <summary>Приложение по точному идентификатору — для «Недавних».</summary>
    public bool Contains(string id) => _entries.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Установленное приложение по имени — для интеграций (ClipTide и др.).</summary>
    public string? FindIdByName(string name)
    {
        var q = Normalize(name);
        return _entries.FirstOrDefault(e => e.Normalized == q)?.Id
            ?? _entries.FirstOrDefault(e => e.Normalized.StartsWith(q, StringComparison.Ordinal))?.Id;
    }

    private static int Score(Entry e, string q)
    {
        var n = e.Normalized;
        if (n == q) return 100;
        if (n.StartsWith(q, StringComparison.Ordinal)) return 80;
        if (n.Contains(' ' + q, StringComparison.Ordinal)) return 60;
        if (e.Initials.StartsWith(q, StringComparison.Ordinal) && q.Length >= 2) return 50;
        if (n.Contains(q, StringComparison.Ordinal)) return 40;
        return 0;
    }

    private static string Normalize(string s)
    {
        var chars = s.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
        return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static List<Entry> Enumerate()
    {
        var type = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Shell.Application is unavailable");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            dynamic items = folder.Items();
            int count = items.Count;

            var list = new List<Entry>(count);
            for (var i = 0; i < count; i++)
            {
                dynamic item = items.Item(i);
                string? name = item.Name;
                string? id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                    continue;

                var normalized = Normalize(name);
                var initials = new string(normalized.Split(' ').Where(w => w.Length > 0).Select(w => w[0]).ToArray());
                list.Add(new Entry(name, id, normalized, initials));
            }
            return list;
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
