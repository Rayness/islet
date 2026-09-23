using System.Diagnostics;
using System.Text.Json;
using Islet.Core;
using Islet.Search;
using Islet.Settings;

namespace Islet.Integrations;

/// <summary>
/// Wireless Device Connect (программа того же автора: заряд мыши, клавиатуры,
/// Stream Dock) и островок.
///
/// WDC при каждом изменении пишет снимок устройств в
/// %LOCALAPPDATA%\com.rayness.wirelessdeviceconnect\status.json (src-tauri/src/export.rs
/// в его репозитории) и удаляет файл при выходе. Островок следит за файлом:
/// садится заряд — пик, почти сел — капсула, пока не поставят на зарядку;
/// «bat » и имя устройства в поиске — заряд и подключение. Опроса нет — только
/// FileSystemWatcher, а WDC пишет файл лишь когда что-то поменялось.
/// </summary>
internal sealed class WirelessDevicesBridge : IDisposable
{
    private const string Identifier = "com.rayness.wirelessdeviceconnect";
    public static readonly string DataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Identifier);
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Identifier);
    private static readonly string StatusPath = Path.Combine(DataDir, "status.json");

    public const string AppName = "Wireless Device Connect";
    private const string ActivityId = "wdc-battery";
    private const string Danger = "#FF6B6B";

    public sealed record Device(string Id, string Name, string Kind, bool Connected, string? Transport, int? Battery, bool? Charging);

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    /// <summary>До какого порога уже предупредили по устройству — чтобы пик был один на порог.</summary>
    private readonly Dictionary<string, int> _warned = [];

    /// <summary>Устройства из последнего снимка; пусто — WDC не запущен.</summary>
    public IReadOnlyList<Device> Devices { get; private set; } = [];

    /// <summary>exe из последнего снимка — им же открываем окно WDC (второй запуск его покажет).</summary>
    public string? ExePath { get; private set; }

    /// <summary>WDC хоть раз запускался: его папки создаются при первом старте.</summary>
    public bool IsInstalled => Directory.Exists(ConfigDir) || Directory.Exists(DataDir);

    public void Start()
    {
        Stop();
        if (!IsInstalled) return;
        try
        {
            // Папку WDC создаёт при первой записи — заводим сами, иначе следить не за чем.
            Directory.CreateDirectory(DataDir);
            _watcher = new FileSystemWatcher(DataDir, "status.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => Schedule();
            _watcher.Created += (_, _) => Schedule();
            _watcher.Renamed += (_, _) => Schedule();
            _watcher.Deleted += (_, _) => Schedule();
            _watcher.EnableRaisingEvents = true;
            Log.Write("wdc: watching");
        }
        catch (Exception e)
        {
            Log.Write($"wdc watcher failed: {e.Message}");
        }
        Refresh();
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _debounce?.Dispose();
        _debounce = null;
    }

    private void Schedule()
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => Guard.Run(Refresh), null, 300, Timeout.Infinite);
    }

    /// <summary>Перечитать снимок и решить, о чём предупредить. Звать после смены настроек.</summary>
    public void Refresh()
    {
        var devices = Read(out var exe);
        if (exe is not null) ExePath = exe;
        Devices = devices;
        // Звать могут и таймер, и страница настроек.
        lock (_warned) Evaluate(devices);
    }

    private static List<Device> Read(out string? exe)
    {
        exe = null;
        var list = new List<Device>();
        try
        {
            if (!File.Exists(StatusPath)) return list;
            using var stream = new FileStream(StatusPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            exe = Protocol.Str(root, "exe");

            // Файл остаётся, если WDC упал: pid подскажет, что снимок устарел.
            if (!root.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number || !IsAlive(pid.GetInt32()))
                return list;

            if (!root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array) return list;
            foreach (var d in devices.EnumerateArray())
            {
                list.Add(new Device(
                    Protocol.Str(d, "id") ?? "",
                    Protocol.Str(d, "name") ?? "",
                    Protocol.Str(d, "kind") ?? "",
                    Protocol.Str(d, "connection") == "connected",
                    Protocol.Str(d, "transport"),
                    d.TryGetProperty("battery", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt32() : null,
                    d.TryGetProperty("charging", out var c) && c.ValueKind is JsonValueKind.True or JsonValueKind.False ? c.GetBoolean() : null));
            }
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            // Середина записи — прочтём при следующем изменении.
        }
        return list;
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void Evaluate(List<Device> devices)
    {
        var settings = SettingsStore.Current;
        var low = Math.Clamp(settings.WirelessLowBattery, 5, 50);
        var critical = Math.Max(5, low / 2);
        Device? worst = null;

        foreach (var d in devices)
        {
            if (!d.Connected || d.Battery is not { } battery || d.Charging == true || battery > low + 5)
            {
                // Зарядили (с запасом, чтобы 20↔21 не мигали) — следующий раз снова предупредим.
                if (d.Charging == true || d.Battery > low + 5) _warned.Remove(d.Id);
                continue;
            }
            if (battery <= critical && d.Charging != true && (worst is null || battery < worst.Battery))
                worst = d;

            var level = battery <= critical ? critical : battery <= low ? low : 0;
            if (level == 0 || (_warned.TryGetValue(d.Id, out var warned) && warned <= level)) continue;
            _warned[d.Id] = level;
            if (!settings.WirelessBatteryPeeks) continue;

            App.Current.Notifications.Post(new IsletNotification
            {
                Source = "wdc",
                SourceName = AppName,
                Title = Loc.T("Wdc_LowTitle", d.Name, battery),
                Body = Loc.T(level == critical ? "Wdc_CriticalBody" : "Wdc_LowBody"),
                Glyph = BatteryGlyph(battery, false),
                Action = OpenAction(),
            });
        }

        // Почти сел — держим в капсуле, пока не поставят на зарядку.
        if (worst is not null && settings.WirelessBatteryPeeks)
        {
            App.Current.Activities.Set(new LiveActivity
            {
                Id = ActivityId,
                Source = "wdc",
                Text = Loc.T("Wdc_Activity", worst.Name, worst.Battery),
                Glyph = BatteryGlyph(worst.Battery ?? 0, false),
                Color = Danger,
                Action = OpenAction(),
            });
        }
        else
        {
            App.Current.Activities.Clear(ActivityId);
        }
    }

    public IsletAction? OpenAction() => ExePath is { Length: > 0 } exe && File.Exists(exe) ? IsletAction.OpenTarget(exe) : null;

    /// <summary>Глиф батареи Segoe Fluent Icons по уровню: Battery0…Battery10 и они же с зарядкой.</summary>
    public static string BatteryGlyph(int percent, bool charging)
    {
        var step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        if (charging) return step == 10 ? "" : ((char)(0xE85A + step)).ToString();
        return step == 10 ? "" : ((char)(0xE850 + step)).ToString();
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>«bat » — заряд и подключение устройств из Wireless Device Connect; без ключевого слова — по имени устройства.</summary>
internal sealed class WirelessDevicesProvider(WirelessDevicesBridge bridge, AppIndex apps) : SearchProvider
{
    public override string Id => "wdc";
    public override string Name => WirelessDevicesBridge.AppName;
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => ["bat", "заряд", "wdc"];
    public override string Description => Loc.T("Provider_WdcHint");
    public override bool AnswersEmptyScoped => true;
    public override int Order => 30;
    public override int MaxGlobal => 3;
    public override bool IsEnabled => SettingsStore.Current.WirelessSearch && bridge.IsInstalled;

    // Слова, на которые общий поиск показывает все устройства разом.
    private static readonly string[] BatteryWords = ["заряд", "батар", "battery", "charge", "мыш", "клав", "mouse", "keyboard"];

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var text = query.Text;
        var devices = bridge.Devices;

        if (!query.IsScoped)
        {
            if (text.Length < 3 || devices.Count == 0) return [];
            var all = BatteryWords.Any(w => w.StartsWith(text, StringComparison.OrdinalIgnoreCase) || text.StartsWith(w, StringComparison.OrdinalIgnoreCase));
            var named = devices.Where(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            return (all ? devices : named).Take(max).Select(Row).ToList();
        }

        if (devices.Count == 0)
        {
            // WDC не запущен — предложить запустить.
            var action = bridge.OpenAction() ?? (apps.FindIdByName(WirelessDevicesBridge.AppName) is { } id ? new IsletAction { App = id } : null);
            return
            [
                new ResultItem
                {
                    Title = Loc.T("Wdc_NotRunning"),
                    Subtitle = Loc.T("Wdc_NotRunningHint"),
                    Kind = ResultKind.Command,
                    Target = "wdc:start",
                    ProviderId = Id,
                    GlyphOverride = "",
                    Action = action,
                    Remember = false,
                },
            ];
        }

        return devices
            .Where(d => text.Length == 0 || d.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            .Take(max)
            .Select(Row)
            .ToList();
    }

    private ResultItem Row(WirelessDevicesBridge.Device d)
    {
        var transport = d.Transport switch
        {
            "bluetooth" => "Bluetooth",
            "dongle24" => Loc.T("Wdc_Dongle"),
            "usbWired" => "USB",
            _ => "",
        };
        string subtitle;
        var trailing = "";
        if (!d.Connected)
        {
            subtitle = Loc.T("Wdc_Disconnected");
        }
        else if (d.Battery is { } battery)
        {
            trailing = d.Charging == true ? $"⚡ {battery}%" : $"{battery}%";
            subtitle = transport;
        }
        else
        {
            // У Stream Dock батареи нет; у донглов заряд пока не читается.
            subtitle = d.Kind == "streamDock" ? transport : $"{transport} · {Loc.T("Wdc_NoBattery")}";
        }

        return new ResultItem
        {
            Title = d.Name,
            Subtitle = subtitle.Trim(' ', '·'),
            Kind = ResultKind.Command,
            Target = $"wdc:{d.Id}",
            ProviderId = Id,
            GlyphOverride = d.Battery is { } level && d.Connected
                ? WirelessDevicesBridge.BatteryGlyph(level, d.Charging == true)
                : d.Kind switch
                {
                    "mouse" => "",
                    "keyboard" => "",
                    _ => "",
                },
            Trailing = trailing,
            // Без exe (снимок ещё не читали) строке нечего открывать — пусть просто остаётся.
            Action = bridge.OpenAction() ?? IsletAction.Run(() => ActionOutcome.Stay),
            Remember = false,
        };
    }
}
