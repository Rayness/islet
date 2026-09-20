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

        return _entries
            .Select(e => (Entry: e, Score: Score(e, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Entry.Name.Length)
            .DistinctBy(x => x.Entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .Select(x => new ResultItem
            {
                Title = x.Entry.Name,
                Subtitle = "Приложение",
                Kind = ResultKind.App,
                Target = x.Entry.Id,
                IconSource = $"shell:AppsFolder\\{x.Entry.Id}",
            })
            .ToList();
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
            ?? throw new InvalidOperationException("Shell.Application недоступен");
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
