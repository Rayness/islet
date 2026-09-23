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
        0x20 => Loc.T("Key_Space"),
        0x0D => "Enter",
        0x09 => "Tab",
        0xC0 => Loc.T("Key_Oem3"),
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x70 and <= 0x87 => $"F{vk - 0x6F}",
        _ => ((Windows.System.VirtualKey)vk).ToString(),
    };
}

public sealed record SearchEngine(string Id, string Title, string UrlPrefix)
{
    public static readonly IReadOnlyList<SearchEngine> All =
    [
        new("yandex", Loc.T("Engine_Yandex"), "https://yandex.ru/search/?text="),
        new("google", "Google", "https://www.google.com/search?q="),
        new("bing", "Bing", "https://www.bing.com/search?q="),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q="),
    ];

    public static SearchEngine Find(string? id) => All.FirstOrDefault(e => e.Id == id) ?? All[0];
}

/// <summary>Все настройки островка. Хранятся в %APPDATA%\Islet\settings.json.</summary>
public sealed class AppSettings
{
    public Hotkey Hotkey { get; set; } = Hotkey.Default;
    public string SearchEngine { get; set; } = "yandex";
    /// <summary>Пусто — язык Windows; иначе "ru" или "en".</summary>
    public string Language { get; set; } = "";

    public bool HoverOpen { get; set; } = true;
    /// <summary>Сколько курсор должен побыть на капсуле до раскрытия, мс.</summary>
    public int HoverOpenDelayMs { get; set; } = 0;
    /// <summary>Сколько островок ждёт после ухода курсора, мс.</summary>
    public int HoverCloseDelayMs { get; set; } = 350;
    public bool HideOnFullscreen { get; set; } = true;
    /// <summary>Свёрнутая капсула невидима, но на неё всё так же можно навести.</summary>
    public bool HideCollapsed { get; set; } = false;
    /// <summary>"primary" — всегда основной монитор, "cursor" — тот, где курсор.</summary>
    public string MonitorMode { get; set; } = "primary";

    public bool Glass { get; set; } = true;
    /// <summary>Плотность заливки пилюли, 0..1. Со стеклом — насколько тонирован фон.</summary>
    public double SurfaceOpacity { get; set; } = 0.62;
    public double IslandWidth { get; set; } = 680;
    public bool ShowClock { get; set; } = true;
    public double CollapsedWidth { get; set; } = 160;
    public double CollapsedHeight { get; set; } = 8;
    /// <summary>Сколько строк выдачи помещается на островке.</summary>
    public int MaxRows { get; set; } = 8;

    // --- Уведомления и живые активности ---

    /// <summary>"peek" — островок раскрывается сам и показывает текст; "badge" — только метка на капсуле; "off" — тихо в колокол.</summary>
    public string NotifyMode { get; set; } = "peek";
    /// <summary>Сколько пик держится на экране, секунд (без учёта бегущей строки).</summary>
    public double PeekSeconds { get; set; } = 4.5;
    public bool NotifySound { get; set; } = false;
    /// <summary>Таймеры и прогресс плагинов в свёрнутой капсуле.</summary>
    public bool LiveActivities { get; set; } = true;
    /// <summary>«Сейчас играет» карточкой в раскрытом островке.</summary>
    public bool MediaCard { get; set; } = true;
    /// <summary>Играющая музыка — обложка и эквалайзер в свёрнутой капсуле.</summary>
    public bool MediaInCapsule { get; set; } = true;
    /// <summary>Первый запуск уже был: знакомство показано.</summary>
    public bool Onboarded { get; set; } = false;

    // --- Поиск: источники ---

    public bool RememberLaunches { get; set; } = true;
    /// <summary>Недавнее и частое на пустой запрос.</summary>
    public bool ShowRecent { get; set; } = true;
    public bool CalculatorEnabled { get; set; } = true;
    public bool CommandsEnabled { get; set; } = true;
    /// <summary>История буфера обмена — только текст, только в памяти.</summary>
    public bool ClipboardHistory { get; set; } = true;
    public int ClipboardMax { get; set; } = 30;

    // --- Интеграции ---

    public bool KawakiSearch { get; set; } = true;
    /// <summary>Искать на Kawaki без «k » — каждый запрос уходит на сайт, поэтому по умолчанию выключено.</summary>
    public bool KawakiGlobalSearch { get; set; } = false;
    public bool KawakiNotifications { get; set; } = true;
    public bool ClipTideNotifications { get; set; } = true;

    /// <summary>Выключенные плагины по id.</summary>
    public List<string> DisabledPlugins { get; set; } = [];

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
    private static readonly string FilePath = Path.Combine(Paths.Config, "settings.json");

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
        Current.CollapsedWidth = Math.Clamp(Current.CollapsedWidth, 60, 400);
        Current.CollapsedHeight = Math.Clamp(Current.CollapsedHeight, 3, 24);
        Current.MaxRows = Math.Clamp(Current.MaxRows, 3, 12);
        Current.HoverOpenDelayMs = Math.Clamp(Current.HoverOpenDelayMs, 0, 1000);
        Current.HoverCloseDelayMs = Math.Clamp(Current.HoverCloseDelayMs, 0, 2000);
        if (Current.MonitorMode is not ("primary" or "cursor"))
            Current.MonitorMode = "primary";
        if (Current.NotifyMode is not ("peek" or "badge" or "off"))
            Current.NotifyMode = "peek";
        Current.PeekSeconds = Math.Clamp(Current.PeekSeconds, 2, 15);
        Current.ClipboardMax = Math.Clamp(Current.ClipboardMax, 5, 100);
        Current.DisabledPlugins ??= [];
        Current.IndexExcludes ??= [.. AppSettings.DefaultExcludes];
    }

    public static void Update(Action<AppSettings> change)
    {
        change(Current);
        try
        {
            Directory.CreateDirectory(Paths.Config);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current, Json));
        }
        catch (Exception e)
        {
            Log.Write($"settings.json not saved: {e.Message}");
        }
        Changed?.Invoke();
    }
}
