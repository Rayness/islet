using Islet.Core;
using Islet.Native;
using Islet.Search;
using Islet.Settings;
using Windows.Devices.Enumeration;

namespace Islet.Integrations;

/// <summary>
/// Заряд беспроводной мыши и клавиатуры — прямо в островке, без отдельной программы.
///
/// Какие приёмники подключены, сообщает Windows (DeviceWatcher по HID-интерфейсам):
/// опроса наличия нет, события приходят сами. Заряд спрашиваем у самого приёмника
/// вендорским отчётом — раз в пару минут, только пока он подключён, и когда человек
/// открывает «bat ». Протоколы разобраны сообществом и проверены на этих устройствах:
///
///  • Ajazz AJ159 APEX, приёмник «AJAZZ 2.4G 8K» 3151:5007 — SET_FEATURE 0xF7 на
///    коллекции FFFF/0002 (тот же пульс, что шлёт фирменный драйвер), через ~40 мс
///    GET_FEATURE отчёта 0x05: «05 00 00 4C …» → 76 %. Aiacos/ajazz-control-center.
///  • Aula F75, приёмник Compx (Beken BK3632) 3554:fa09 — output-отчёт 0x13 с командой
///    0x4A на коллекции FF02, ответ во входном 0x13, заряд в байте 5. Флаги в байте 6
///    не расшифрованы — «заряжается» из них не выводим. deepan-alve/womier-l65-linux.
///  • Aula F75 по проводу (SinoWealth 258a:010c) и Stream Dock AKP153 — только наличие.
///
/// Садится заряд — пик, почти сел — капсула, пока не поставят на зарядку.
/// </summary>
internal sealed class DeviceMonitor : IDisposable
{
    public sealed record Device(string Id, string Name, string Kind, string Transport, int? Battery, DateTime? BatteryAt);

    private sealed record Profile(
        ushort Vid, ushort Pid, string Id, string Name, string Kind, string Transport,
        ushort UsagePage = 0, ushort Usage = 0, Func<string, int?>? ReadBattery = null);

    private static readonly Profile[] Profiles =
    [
        new(0x3151, 0x5007, "mouse-aj159", "Ajazz AJ159 APEX", "mouse", "dongle24", 0xFFFF, 0x0002, ReadAjazzMouse),
        new(0x3554, 0xFA09, "keyboard-f75", "Aula F75", "keyboard", "dongle24", 0xFF02, 0x0002, ReadCompxKeyboard),
        new(0x258A, 0x010C, "keyboard-f75", "Aula F75", "keyboard", "usbWired"),
        new(0x0300, 0x1020, "stream-dock", "Ajazz AKP153 (Stream Dock)", "streamDock", "usbWired"),
        new(0x0300, 0x1010, "stream-dock", "Ajazz AKP153 (Stream Dock)", "streamDock", "usbWired"),
    ];

    private const string HidInterfaces =
        "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\" AND " +
        "System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";
    private const string VendorKey = "System.DeviceInterface.Hid.VendorId";
    private const string ProductKey = "System.DeviceInterface.Hid.ProductId";
    private const string PageKey = "System.DeviceInterface.Hid.UsagePage";
    private const string UsageKey = "System.DeviceInterface.Hid.UsageId";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan OnDemandAge = TimeSpan.FromSeconds(30);
    /// <summary>Клавиатура дремлет и не отвечает — последнее значение держим столько.</summary>
    private static readonly TimeSpan BatteryKeep = TimeSpan.FromMinutes(30);

    private const string ActivityId = "devices-battery";
    private const string Danger = "#FF6B6B";

    private readonly Lock _gate = new();
    /// <summary>Подключённые HID-интерфейсы известных устройств: id интерфейса → профиль и коллекция.</summary>
    private readonly Dictionary<string, (Profile Profile, ushort Page, ushort Usage)> _interfaces = [];
    private readonly Dictionary<string, (int Level, DateTime At)> _battery = [];
    /// <summary>До какого порога уже предупредили по устройству — пик один на порог.</summary>
    private readonly Dictionary<string, int> _warned = [];

    private DeviceWatcher? _watcher;
    private Timer? _poll;
    private int _polling;
    private DateTime _lastPoll = DateTime.MinValue;

    public event Action? Changed;

    /// <summary>Подключённые устройства — по одному на устройство, даже если у него несколько интерфейсов.</summary>
    public IReadOnlyList<Device> Devices { get; private set; } = [];

    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(HidInterfaces, [VendorKey, ProductKey, PageKey, UsageKey],
                DeviceInformationKind.DeviceInterface);
            _watcher.Added += (_, info) => Guard.Run(() => OnAdded(info));
            _watcher.Removed += (_, update) => Guard.Run(() => OnRemoved(update.Id));
            // Без подписки на Updated DeviceWatcher не доводит перечисление до конца.
            _watcher.Updated += (_, update) => Guard.Run(() =>
            {
                if (update.Properties.TryGetValue("System.Devices.InterfaceEnabled", out var enabled) && enabled is false)
                    OnRemoved(update.Id);
            });
            _watcher.EnumerationCompleted += (_, _) => Guard.Run(() => SchedulePoll(TimeSpan.FromSeconds(1)));
            _watcher.Start();
            _poll = new Timer(_ => Guard.Run(Poll), null, Timeout.Infinite, Timeout.Infinite);
            Log.Write("devices: watching");
        }
        catch (Exception e)
        {
            Log.Write($"devices watcher failed: {e.Message}");
        }
    }

    private void OnAdded(DeviceInformation info)
    {
        var vid = Prop(info.Properties, VendorKey);
        var pid = Prop(info.Properties, ProductKey);
        var profile = Profiles.FirstOrDefault(p => p.Vid == vid && p.Pid == pid);
        if (profile is null) return;

        lock (_gate)
            _interfaces[info.Id] = (profile, Prop(info.Properties, PageKey), Prop(info.Properties, UsageKey));
        Rebuild();
        // Только что подключённому приёмнику нужна пара секунд, чтобы поднять радиоканал.
        if (profile.ReadBattery is not null) SchedulePoll(TimeSpan.FromSeconds(3));
    }

    private void OnRemoved(string id)
    {
        bool removed;
        lock (_gate) removed = _interfaces.Remove(id);
        if (removed) Rebuild();
    }

    private static ushort Prop(IReadOnlyDictionary<string, object> props, string key)
    {
        try
        {
            return props.TryGetValue(key, out var value) && value is not null ? Convert.ToUInt16(value) : (ushort)0;
        }
        catch
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------
    // Заряд
    // ------------------------------------------------------------------

    /// <summary>«bat » открыли — если заряд давно не спрашивали, спросить сейчас.</summary>
    public void RefreshIfStale()
    {
        if (DateTime.Now - _lastPoll > OnDemandAge) SchedulePoll(TimeSpan.Zero);
    }

    private void SchedulePoll(TimeSpan delay) => _poll?.Change(delay, PollInterval);

    private void Poll()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;
        try
        {
            List<(string Path, Profile Profile)> readable;
            lock (_gate)
            {
                readable = _interfaces
                    .Where(i => i.Value.Profile.ReadBattery is not null
                        && i.Value.Page == i.Value.Profile.UsagePage && i.Value.Usage == i.Value.Profile.Usage)
                    .Select(i => (i.Key, i.Value.Profile))
                    .ToList();
            }
            // Нечего спрашивать — таймер не крутим, его разбудит следующее подключение.
            if (readable.Count == 0)
            {
                _poll?.Change(Timeout.Infinite, Timeout.Infinite);
                return;
            }

            _lastPoll = DateTime.Now;
            foreach (var (path, profile) in readable)
            {
                int? level = null;
                try
                {
                    level = profile.ReadBattery!(path);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // Устройство уснуло или отключилось посреди запроса — спросим в следующий раз.
                }
                if (level is { } value)
                    lock (_gate) _battery[profile.Id] = (value, DateTime.Now);
            }
            Rebuild();
        }
        finally
        {
            Interlocked.Exchange(ref _polling, 0);
        }
    }

    private static int? ReadAjazzMouse(string path)
    {
        using var device = Hid.Open(path);
        if (device is null || Hid.GetCaps(device) is not { FeatureReportByteLength: >= 4 } caps) return null;

        // Пульс статуса: без него приёмник не поднимает телеметрию и отдаёт нули.
        var poll = new byte[caps.FeatureReportByteLength];
        poll[1] = 0xF7;
        if (!Hid.SetFeature(device, poll)) return null;
        Thread.Sleep(40);

        var report = new byte[caps.FeatureReportByteLength];
        report[0] = 0x05;
        if (!Hid.GetFeature(device, report)) return null;
        // «05 00 00 LL …»: ненулевые байты 1–2 — мусор сразу после переподключения, 0 — ещё не знает.
        return report[1] == 0 && report[2] == 0 && report[3] is > 0 and <= 100 ? report[3] : null;
    }

    private static int? ReadCompxKeyboard(string path)
    {
        using var device = Hid.Open(path, overlapped: true);
        if (device is null || Hid.GetCaps(device) is not { OutputReportByteLength: >= 20, InputReportByteLength: >= 20 } caps)
            return null;

        using var stream = new FileStream(device, FileAccess.ReadWrite, 0, isAsync: true);
        var packet = new byte[caps.OutputReportByteLength];
        packet[0] = 0x13;   // отчёт
        packet[1] = 0x4A;   // заряд
        packet[2] = 1;      // пакетов в сообщении
        packet[19] = (byte)packet.Take(19).Sum(b => b);
        stream.Write(packet);

        // Ответ — входной отчёт 0x13 с той же командой; между ними могут идти другие.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
        var input = new byte[caps.InputReportByteLength];
        for (var i = 0; i < 25; i++)
        {
            var read = stream.ReadAsync(input, timeout.Token).AsTask().GetAwaiter().GetResult();
            if (read >= 7 && input[0] == 0x13 && (input[1] & 0x7F) == 0x4A)
                return input[5] is > 0 and <= 100 ? input[5] : null;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Снимок и предупреждения
    // ------------------------------------------------------------------

    private void Rebuild()
    {
        List<Device> devices;
        lock (_gate)
        {
            var now = DateTime.Now;
            devices = _interfaces.Values
                .Select(i => i.Profile)
                // Один профиль на устройство; у клавиатуры по проводу и по радио — разные.
                .GroupBy(p => p.Id)
                .Select(g => g.OrderBy(p => p.ReadBattery is null ? 1 : 0).First())
                .OrderBy(p => Array.FindIndex(Profiles, x => x.Id == p.Id))
                .Select(p =>
                {
                    var known = p.Transport != "usbWired" && _battery.TryGetValue(p.Id, out var b) && now - b.At < BatteryKeep;
                    return new Device(p.Id, p.Name, p.Kind, p.Transport,
                        known ? _battery[p.Id].Level : null, known ? _battery[p.Id].At : null);
                })
                .ToList();
            Devices = devices;
            Evaluate(devices);
        }
        Changed?.Invoke();
    }

    /// <summary>Перечитать настройки порогов (звать со страницы настроек).</summary>
    public void Reevaluate()
    {
        lock (_gate) Evaluate(Devices.ToList());
    }

    private void Evaluate(List<Device> devices)
    {
        var settings = SettingsStore.Current;
        var low = Math.Clamp(settings.WirelessLowBattery, 5, 50);
        var critical = Math.Max(5, low / 2);
        Device? worst = null;

        foreach (var d in devices)
        {
            if (d.Battery is not { } battery || battery > low + 5)
            {
                // Зарядили (с запасом, чтобы 20↔21 не мигали) или провод — в следующий раз предупредим снова.
                if (d.Battery > low + 5 || d.Transport == "usbWired") _warned.Remove(d.Id);
                continue;
            }
            if (battery <= critical && (worst is null || battery < worst.Battery))
                worst = d;

            var level = battery <= critical ? critical : battery <= low ? low : 0;
            if (level == 0 || (_warned.TryGetValue(d.Id, out var warned) && warned <= level)) continue;
            _warned[d.Id] = level;
            if (!settings.WirelessBatteryPeeks) continue;

            App.Current.Notifications.Post(new IsletNotification
            {
                Source = "devices",
                SourceName = Loc.T("S_DevicesSection"),
                Title = Loc.T("Wdc_LowTitle", d.Name, battery),
                Body = Loc.T(level == critical ? "Wdc_CriticalBody" : "Wdc_LowBody"),
                Glyph = BatteryGlyph(battery, false),
                Action = new IsletAction { Query = "bat " },
            });
        }

        // Почти сел — держим в капсуле, пока заряд не поднимется или устройство не отключат.
        if (worst is not null && settings.WirelessBatteryPeeks)
        {
            App.Current.Activities.Set(new LiveActivity
            {
                Id = ActivityId,
                Source = "devices",
                Text = Loc.T("Wdc_Activity", worst.Name, worst.Battery),
                Glyph = BatteryGlyph(worst.Battery ?? 0, false),
                Color = Danger,
                Action = new IsletAction { Query = "bat " },
            });
        }
        else
        {
            App.Current.Activities.Clear(ActivityId);
        }
    }

    /// <summary>Глиф батареи Segoe Fluent Icons по уровню: Battery0…Battery10 и они же с зарядкой.</summary>
    public static string BatteryGlyph(int percent, bool charging)
    {
        var step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        if (charging) return step == 10 ? "" : ((char)(0xE85A + step)).ToString();
        return step == 10 ? "" : ((char)(0xE850 + step)).ToString();
    }

    public void Dispose()
    {
        try
        {
            if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
                _watcher.Stop();
        }
        catch
        {
            // Островок закрывается — неважно.
        }
        _poll?.Dispose();
    }
}

/// <summary>«bat » — заряд и подключение мыши, клавиатуры; без ключевого слова — по имени устройства и «заряд».</summary>
internal sealed class DevicesProvider(DeviceMonitor monitor) : SearchProvider
{
    public override string Id => "devices";
    public override string Name => Loc.T("S_DevicesSection");
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => ["bat", "заряд"];
    public override string Description => Loc.T("Provider_WdcHint");
    public override bool AnswersEmptyScoped => true;
    public override int Order => 30;
    public override int MaxGlobal => 3;
    public override bool IsEnabled => SettingsStore.Current.WirelessSearch;

    // Слова, на которые общий поиск показывает все устройства разом.
    private static readonly string[] BatteryWords = ["заряд", "батар", "battery", "charge", "мыш", "клав", "mouse", "keyboard"];

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var text = query.Text;
        var devices = monitor.Devices;

        if (!query.IsScoped)
        {
            if (text.Length < 3 || devices.Count == 0) return [];
            var all = BatteryWords.Any(w => w.StartsWith(text, StringComparison.OrdinalIgnoreCase) || text.StartsWith(w, StringComparison.OrdinalIgnoreCase));
            var named = devices.Where(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
            var found = all ? devices : named;
            if (found.Count > 0) monitor.RefreshIfStale();
            return found.Take(max).Select(Row).ToList();
        }

        monitor.RefreshIfStale();
        if (devices.Count == 0)
        {
            return
            [
                new ResultItem
                {
                    Title = Loc.T("Wdc_NotRunning"),
                    Subtitle = Loc.T("Wdc_NotRunningHint"),
                    Kind = ResultKind.Hint,
                    Target = "devices:none",
                    ProviderId = Id,
                    GlyphOverride = "",
                    Action = IsletAction.Run(() => ActionOutcome.Stay),
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

    private ResultItem Row(DeviceMonitor.Device d)
    {
        var transport = d.Transport switch
        {
            "bluetooth" => "Bluetooth",
            "dongle24" => Loc.T("Wdc_Dongle"),
            _ => "USB",
        };
        var subtitle = d.Battery is not null || d.Kind == "streamDock" || d.Transport == "usbWired"
            ? transport
            : $"{transport} · {Loc.T("Wdc_NoBattery")}";

        return new ResultItem
        {
            Title = d.Name,
            Subtitle = subtitle,
            Kind = ResultKind.Command,
            Target = $"devices:{d.Id}",
            ProviderId = Id,
            GlyphOverride = d.Battery is { } level
                ? DeviceMonitor.BatteryGlyph(level, false)
                : d.Kind switch
                {
                    "mouse" => "",
                    "keyboard" => "",
                    _ => "",
                },
            Trailing = d.Battery is { } battery ? $"{battery}%" : "",
            // Enter — спросить заряд ещё раз, островок остаётся открытым.
            Action = IsletAction.Run(() =>
            {
                monitor.RefreshIfStale();
                return ActionOutcome.Stay;
            }),
            Remember = false,
        };
    }
}
