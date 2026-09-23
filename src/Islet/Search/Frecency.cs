using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Islet.Search;

/// <summary>
/// Что человек запускает часто и недавно. Поднимает это в выдаче и
/// показывает «Недавние» на пустом запросе.
///
/// Вес — число запусков, затухающее с возрастом: вчерашнее ежедневное важнее
/// того, что открывали сто раз полгода назад. Хранится в %APPDATA%\Islet\history.json;
/// запоминание выключается в настройках.
/// </summary>
internal static class Frecency
{
    public sealed class Entry
    {
        public string Title { get; set; } = "";
        public string Subtitle { get; set; } = "";
        public ResultKind Kind { get; set; }
        public string Target { get; set; } = "";
        public string? IconSource { get; set; }
        public string? IconUrl { get; set; }
        public string? Glyph { get; set; }
        public int Count { get; set; }
        public DateTime Last { get; set; }
    }

    private const int MaxEntries = 300;
    private static readonly string FilePath = Path.Combine(Paths.Config, "history.json");
    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static Dictionary<string, Entry> _entries = [];
    private static bool _loaded;
    private static bool _dirty;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
                _entries = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath), Json) ?? [];
        }
        catch (Exception e)
        {
            Log.Write($"history.json unreadable: {e.Message}");
        }
    }

    public static void Record(ResultItem item)
    {
        if (!item.Remember || !Settings.SettingsStore.Current.RememberLaunches) return;
        if (item.Kind is ResultKind.Calc or ResultKind.Hint or ResultKind.Clipboard or ResultKind.Timer or ResultKind.Web) return;
        EnsureLoaded();

        var key = item.HistoryKey;
        if (!_entries.TryGetValue(key, out var entry))
            _entries[key] = entry = new Entry();
        entry.Title = item.Title;
        entry.Subtitle = item.Subtitle;
        entry.Kind = item.Kind;
        entry.Target = item.Target;
        entry.IconSource = item.IconSource;
        entry.IconUrl = item.IconUrl;
        entry.Glyph = item.GlyphOverride;
        entry.Count++;
        entry.Last = DateTime.Now;

        if (_entries.Count > MaxEntries)
        {
            foreach (var old in _entries.OrderBy(e => Weight(e.Value)).Take(_entries.Count - MaxEntries).ToList())
                _entries.Remove(old.Key);
        }
        _dirty = true;
        Save();
    }

    /// <summary>Прибавка к оценке строки: 0 — не запускали.</summary>
    public static int Boost(ResultItem item)
    {
        EnsureLoaded();
        return _entries.TryGetValue(item.HistoryKey, out var entry) ? (int)Math.Min(60, Weight(entry) * 12) : 0;
    }

    public static IEnumerable<Entry> Top(int max)
    {
        EnsureLoaded();
        return _entries.Values.OrderByDescending(Weight).Take(max);
    }

    public static void Forget(ResultItem item)
    {
        EnsureLoaded();
        if (_entries.Remove(item.HistoryKey))
        {
            _dirty = true;
            Save();
        }
    }

    public static void Clear()
    {
        _entries.Clear();
        _loaded = true;
        _dirty = true;
        Save();
    }

    private static double Weight(Entry e)
    {
        var days = (DateTime.Now - e.Last).TotalDays;
        // Полураспад — неделя.
        return e.Count * Math.Pow(0.5, days / 7);
    }

    private static void Save()
    {
        if (!_dirty) return;
        _dirty = false;
        var snapshot = JsonSerializer.Serialize(_entries, Json);
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(Paths.Config);
                File.WriteAllText(FilePath, snapshot);
            }
            catch (Exception e)
            {
                Log.Write($"history.json not saved: {e.Message}");
            }
        });
    }
}
