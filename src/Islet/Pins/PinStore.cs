using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Islet.Search;
using Islet.Shell;

namespace Islet.Pins;

public enum PinKind
{
    /// <summary>Приложение из «Все приложения» (shell:AppsFolder).</summary>
    App,
    /// <summary>Файл, папка или exe; переменные окружения раскрываются.</summary>
    Path,
    /// <summary>Ссылка или протокол: https://, ms-settings: и т. п.</summary>
    Url,
}

public sealed record Pin(string Title, PinKind Kind, string Target, string? Arguments = null, string? Icon = null)
{
    [JsonIgnore]
    public string? IconSource => Icon ?? Kind switch
    {
        PinKind.App => $"shell:AppsFolder\\{Target}",
        PinKind.Path => Environment.ExpandEnvironmentVariables(Target),
        _ => null,
    };

    public bool Launch() => Kind == PinKind.App
        ? Launcher.OpenApp(Target)
        : Launcher.Open(Target, Arguments);

    public static Pin? FromResult(ResultItem item) => item.Kind switch
    {
        ResultKind.App => new Pin(item.Title, PinKind.App, item.Target),
        ResultKind.File or ResultKind.Folder => new Pin(item.Title, PinKind.Path, item.Target),
        ResultKind.Url or ResultKind.Kawaki => new Pin(item.Title, PinKind.Url, item.Target, Icon: item.IconUrl),
        _ => null,
    };
}

/// <summary>
/// Кнопки на островке. Лежат в %APPDATA%\Islet\pins.json —
/// файл можно править руками, островок перечитывает его при запуске.
/// </summary>
internal sealed class PinStore
{
    public const int MaxPins = 6;

    /// <summary>Папка настроек: %APPDATA%\Islet, см. Paths.</summary>
    public static readonly string Directory = Paths.Config;

    public static readonly string FilePath = System.IO.Path.Combine(Directory, "pins.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Кириллица буквами, а не escape-последовательностями: файл правят руками.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public List<Pin> Items { get; private set; } = [];

    public event Action? Changed;

    private DateTime _loadedWriteTime;

    /// <summary>Файл поправили руками — подхватить без перезапуска.</summary>
    public void ReloadIfChanged()
    {
        try
        {
            if (File.Exists(FilePath) && File.GetLastWriteTimeUtc(FilePath) != _loadedWriteTime)
            {
                Load();
                Changed?.Invoke();
            }
        }
        catch { /* файл занят редактором — попробуем в следующий раз */ }
    }

    /// <summary>Названия берутся на языке интерфейса и сразу попадают в pins.json — дальше их правит пользователь.</summary>
    private static List<Pin> Defaults() =>
    [
        new(Loc.T("Pin_Explorer"), PinKind.Path, @"%WINDIR%\explorer.exe"),
        new(Loc.T("Pin_Terminal"), PinKind.App, "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App"),
        new(Loc.T("Pin_WinSettings"), PinKind.App, "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel"),
        new(Loc.T("Pin_TaskManager"), PinKind.Path, @"%WINDIR%\System32\Taskmgr.exe"),
        new("Kawaki", PinKind.Url, "https://kawaki.ru", Icon: "ms-appx:///Assets/kawaki.ico"),
    ];

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                _loadedWriteTime = File.GetLastWriteTimeUtc(FilePath);
                Items = JsonSerializer.Deserialize<List<Pin>>(File.ReadAllText(FilePath), Json) ?? [];
                return;
            }
        }
        catch (Exception e)
        {
            // Битый файл не затираем молча: откладываем рядом, чтобы правку руками можно было вернуть.
            System.Diagnostics.Debug.WriteLine($"pins.json unreadable: {e.Message}");
            TryBackup();
        }

        Items = Defaults();
        Save();
    }

    public bool Add(Pin pin)
    {
        if (Items.Count >= MaxPins || Items.Any(p => p.Kind == pin.Kind && string.Equals(p.Target, pin.Target, StringComparison.OrdinalIgnoreCase)))
            return false;
        Items.Add(pin);
        Save();
        return true;
    }

    public void Remove(Pin pin)
    {
        if (Items.Remove(pin))
            Save();
    }

    public void Move(Pin pin, int delta)
    {
        var from = Items.IndexOf(pin);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Items.Count) return;
        Items.RemoveAt(from);
        Items.Insert(to, pin);
        Save();
    }

    public void ResetToDefaults()
    {
        Items = Defaults();
        Save();
    }

    private void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, Json));
            _loadedWriteTime = File.GetLastWriteTimeUtc(FilePath);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"pins.json not saved: {e.Message}");
        }
        Changed?.Invoke();
    }

    private static void TryBackup()
    {
        try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); }
        catch { /* нечего спасать */ }
    }
}
