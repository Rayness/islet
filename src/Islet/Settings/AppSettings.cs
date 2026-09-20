using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Islet.Native;

namespace Islet.Settings;

public sealed record Hotkey(uint Modifiers, uint Key)
{
    public static readonly Hotkey Default = new(Win32.MOD_CONTROL | Win32.MOD_ALT, Win32.VK_SPACE);

    [JsonIgnore]
    public string Label
    {
        get
        {
            var parts = new List<string>();
            if ((Modifiers & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
            if ((Modifiers & Win32.MOD_ALT) != 0) parts.Add("Alt");
            if ((Modifiers & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
            if ((Modifiers & Win32.MOD_WIN) != 0) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }
    }

    private static string KeyName(uint vk) => vk switch
    {
        0x20 => "Пробел",
        0x0D => "Enter",
        0x09 => "Tab",
        0xC0 => "Ё",
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => ((Windows.System.VirtualKey)vk).ToString(),
    };
}

public sealed record SearchEngine(string Id, string Title, string UrlPrefix)
{
    public static readonly IReadOnlyList<SearchEngine> All =
    [
        new("yandex", "Яндекс", "https://yandex.ru/search/?text="),
        new("google", "Google", "https://www.google.com/search?q="),
        new("bing", "Bing", "https://www.bing.com/search?q="),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q="),
    ];

    public static SearchEngine Find(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}

/// <summary>Все настройки островка. Хранятся в %LOCALAPPDATA%\Islet\settings.json.</summary>
public sealed class AppSettings
{
    public Hotkey Hotkey { get; set; } = Hotkey.Default;
    public string SearchEngine { get; set; } = "yandex";

    public bool Glass { get; set; } = true;
    /// <summary>Плотность заливки пилюли, 0..1. Со стеклом — насколько тонирован фон.</summary>
    public double SurfaceOpacity { get; set; } = 0.62;
    public double IslandWidth { get; set; } = 680;
    public bool ShowClock { get; set; } = true;

    public bool DriveIndexEnabled { get; set; } = true;
    /// <summary>null — все несистемные диски; иначе ровно этот список папок.</summary>
    public List<string>? IndexRoots { get; set; }
    public List<string> IndexExcludes { get; set; } = [.. DefaultExcludes];

    public static readonly string[] DefaultExcludes =
    [
        "node_modules", ".git", ".svn", ".hg", ".vs", ".idea", "__pycache__", ".venv",
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Windows", "WindowsApps",
    ];

    public static List<string> DefaultRoots()
    {
        var system = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .Where(r => !string.Equals(r, system, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public List<string> EffectiveRoots() => IndexRoots ?? DefaultRoots();
}

internal static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(Pins.PinStore.Directory, "settings.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static AppSettings Current { get; private set; } = new();

    /// <summary>Любое изменение. Вызывается на потоке, который сохранял (UI).</summary>
    public static event Action? Changed;

    public static void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new();
        }
        catch (Exception e)
        {
            Log.Write($"settings.json unreadable, using defaults: {e.Message}");
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { }
            Current = new();
        }
        Current.IslandWidth = Math.Clamp(Current.IslandWidth, 560, 960);
        Current.SurfaceOpacity = Math.Clamp(Current.SurfaceOpacity, 0.2, 1);
    }

    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        try
        {
            Directory.CreateDirectory(Pins.PinStore.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception e)
        {
            Log.Write($"settings.json not saved: {e.Message}");
        }
        Changed?.Invoke();
    }
}
