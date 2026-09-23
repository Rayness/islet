using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Islet.Native;
using Windows.Devices.Enumeration;

namespace Islet.Integrations.Devices;

/// <summary>
/// Заряд беспроводных мышей, клавиатур, гарнитур и геймпадов — прямо в островке.
///
/// Три источника:
///  • рецепты (<see cref="RecipeStore"/>) — вендорские протоколы, описанные данными;
///  • Logitech HID++ (<see cref="HidppSession"/>) — всё семейство Logitech через приёмник и напрямую;
///  • Bluetooth — заряд, который Windows уже знает сама (свойство устройства), показываем,
///    пока устройство подключено.
///
/// Что подключено, сообщает Windows (DeviceWatcher по HID-интерфейсам и по устройствам) —
/// опроса наличия нет. Заряд спрашивается на одном фоновом потоке: раз в две минуты, пока
/// устройство подключено, и по просьбе («bat »). Не отвечает — пауза растёт до получаса,
/// чтобы сломанный рецепт или спящее устройство не дёргались впустую. Любой сбой одного
/// источника остаётся внутри него.
/// </summary>
internal sealed partial class DeviceMonitor : IDisposable
{
    public sealed record Device(
        string Key, string Name, string Kind, string Connection,
        int? Battery, bool? Charging, bool Offline, DateTime? At, string Via);

    private sealed record HidInterface(string Path, ushort Vid, ushort Pid, ushort Page, ushort Usage, int Interface, string Container);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan OnDemandAge = TimeSpan.FromSeconds(30);
    /// <summary>Устройство дремлет и не отвечает — последний заряд держим столько.</summary>
    private static readonly TimeSpan BatteryKeep = TimeSpan.FromMinutes(30);

    private const string HidInterfaces =
        "System.Devices.InterfaceClassGuid:=\"{4D1E55B2-F16F-11CF-88CB-001111000030}\" AND " +
        "System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True";
    private const string VendorKey = "System.DeviceInterface.Hid.VendorId";
    private const string ProductKey = "System.DeviceInterface.Hid.ProductId";
    private const string PageKey = "System.DeviceInterface.Hid.UsagePage";
    private const string UsageKey = "System.DeviceInterface.Hid.UsageId";
    private const string ContainerKey = "System.Devices.ContainerId";
    /// <summary>DEVPKEY_Bluetooth_Battery — заряд, который Windows показывает в «Параметрах».</summary>
    private const string BluetoothBatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    /// <summary>DEVPKEY_Device_IsConnected — у сопряжённого, но выключенного устройства заряд остаётся старым.</summary>
    private const string ConnectedKey = "{83DA6326-97A6-4088-9453-A1923F573B29} 15";

    private readonly RecipeStore _store;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, HidInterface> _hid = [];
    private readonly Dictionary<string, Source> _sources = [];
    private readonly Dictionary<string, (string Name, int Battery, bool Connected)> _bluetooth = [];
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _worker;
    private volatile bool _disposed;

    private DeviceWatcher? _hidWatcher;
    private DeviceWatcher? _btWatcher;

    public DeviceMonitor(RecipeStore store)
    {
        _store = store;
        _store.Changed += RebuildSources;
        _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "islet-devices", Priority = ThreadPriority.BelowNormal };
    }

    /// <summary>Снимок изменился. С фонового потока.</summary>
    public event Action? Changed;

    public IReadOnlyList<Device> Devices { get; private set; } = [];

    public RecipeStore Store => _store;

    public void Start()
    {
        _worker.Start();
        try
        {
            _hidWatcher = DeviceInformation.CreateWatcher(HidInterfaces, [VendorKey, ProductKey, PageKey, UsageKey, ContainerKey],
                DeviceInformationKind.DeviceInterface);
            _hidWatcher.Added += (_, info) => Guard.Run(() => OnHidAdded(info));
            _hidWatcher.Removed += (_, update) => Guard.Run(() => OnHidRemoved(update.Id));
            // Без подписки на Updated перечисление не доходит до конца.
            _hidWatcher.Updated += (_, update) => Guard.Run(() =>
            {
                if (update.Properties.TryGetValue("System.Devices.InterfaceEnabled", out var enabled) && enabled is false)
                    OnHidRemoved(update.Id);
            });
            _hidWatcher.EnumerationCompleted += (_, _) => Guard.Run(RebuildSources);
            _hidWatcher.Start();

            _btWatcher = DeviceInformation.CreateWatcher("System.Devices.Present:=System.StructuredQueryType.Boolean#True",
                [BluetoothBatteryKey, ConnectedKey], DeviceInformationKind.Device);
            _btWatcher.Added += (_, info) => Guard.Run(() => OnBluetooth(info.Id, info.Name, info.Properties));
            _btWatcher.Updated += (_, update) => Guard.Run(() => OnBluetooth(update.Id, null, update.Properties));
            _btWatcher.Removed += (_, update) => Guard.Run(() =>
            {
                bool removed;
                lock (_gate) removed = _bluetooth.Remove(update.Id);
                if (removed) Publish();
            });
            _btWatcher.Start();
            Log.Write("devices: watching");
        }
        catch (Exception e)
        {
            Log.Write($"devices: watcher failed: {e.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Что подключено
    // ------------------------------------------------------------------

    private void OnHidAdded(DeviceInformation info)
    {
        var p = info.Properties;
        var hid = new HidInterface(
            info.Id,
            Prop(p, VendorKey), Prop(p, ProductKey), Prop(p, PageKey), Prop(p, UsageKey),
            InterfaceRegex().Match(info.Id) is { Success: true } m ? Convert.ToInt32(m.Groups[1].Value, 16) : 0,
            p.TryGetValue(ContainerKey, out var c) && c is not null ? c.ToString()! : info.Id);
        lock (_gate) _hid[info.Id] = hid;
        // Во время первого перечисления источники соберёт EnumerationCompleted.
        if (_hidWatcher?.Status == DeviceWatcherStatus.EnumerationCompleted) RebuildSources();
    }

    private void OnHidRemoved(string id)
    {
        bool removed;
        lock (_gate) removed = _hid.Remove(id);
        if (removed) RebuildSources();
    }

    private void OnBluetooth(string id, string? name, IReadOnlyDictionary<string, object> props)
    {
        bool changed;
        lock (_gate)
        {
            var known = _bluetooth.TryGetValue(id, out var old);
            var battery = props.TryGetValue(BluetoothBatteryKey, out var b) && b is not null ? Convert.ToInt32(b) : known ? old.Battery : -1;
            var connected = props.TryGetValue(ConnectedKey, out var cn) && cn is bool on ? on : known && old.Connected;
            if (battery is < 0 or > 100)
            {
                changed = known && _bluetooth.Remove(id);
            }
            else
            {
                var entry = (name ?? (known ? old.Name : "Bluetooth"), battery, connected);
                changed = !known || entry != old;
                _bluetooth[id] = entry;
            }
        }
        if (changed) Publish();
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

    [GeneratedRegex(@"&MI_([0-9A-Fa-f]{2})")]
    private static partial Regex InterfaceRegex();

    /// <summary>Сопоставить подключённое с рецептами и HID++. Источники, что не поменялись, сохраняются вместе с кешем.</summary>
    private void RebuildSources()
    {
        lock (_gate)
        {
            var wanted = new Dictionary<string, Source>();
            var claimed = new HashSet<string>();
            var recipes = _store.Recipes;

            foreach (var recipe in recipes)
            {
                var candidates = _hid.Values.Where(h => h.Vid == recipe.Vid && recipe.Pids.Contains(h.Pid)
                    && (recipe.Interface is null || h.Interface == recipe.Interface)
                    && (recipe.Page is null || h.Page == recipe.Page)
                    && (recipe.Usage is null || h.Usage == recipe.Usage));
                foreach (var group in candidates.GroupBy(h => h.Container))
                {
                    // Рецепт только по номеру интерфейса — берём вендорскую коллекцию, клавиатуру и мышь не трогаем.
                    var target = group.OrderBy(h => h.Page >= 0xFF00 ? 0 : h.Page == 0x000C ? 1 : 2).First();
                    if (target.Page < 0xFF00 && target.Page != 0x000C && recipe.Steps.Count > 0) continue;
                    var key = $"recipe:{recipe.Id}:{group.Key}";
                    wanted[key] = _sources.TryGetValue(key, out var old) && old is RecipeSource rs && ReferenceEquals(rs.Recipe, recipe) && rs.Path == target.Path
                        ? old
                        : recipe.Steps.Count == 0
                            ? new PresenceSource(key, recipe)
                            : new RecipeSource(key, recipe, target.Path);
                    claimed.Add(group.Key);
                }
            }

            foreach (var group in _hid.Values.Where(h => h.Vid == 0x046D && !claimed.Contains(h.Container)).GroupBy(h => h.Container))
            {
                var longHid = group.FirstOrDefault(h => (h.Page, h.Usage) is (0xFF00, 0x0002) or (0xFF43, 0x0202));
                if (longHid is null) continue;
                var shortHid = group.FirstOrDefault(h => (h.Page, h.Usage) is (0xFF00, 0x0001) or (0xFF43, 0x0201));
                var key = $"hidpp:{group.Key}";
                wanted[key] = _sources.TryGetValue(key, out var old) && old is HidppSource hs && hs.LongPath == longHid.Path
                    ? old
                    : new HidppSource(key, longHid.Path, shortHid?.Path, Connection(longHid));
            }

            _sources.Clear();
            foreach (var (key, source) in wanted) _sources[key] = source;
        }
        _wake.Set();
        Publish();
    }

    private static string Connection(HidInterface hid) =>
        // HID поверх Bluetooth: {00001124…} — классический, {00001812…} — BLE.
        hid.Path.Contains("{00001812", StringComparison.OrdinalIgnoreCase) || hid.Path.Contains("{00001124", StringComparison.OrdinalIgnoreCase)
            ? "bluetooth"
            : hid.Pid >= 0xC500 && hid.Pid <= 0xC5FF ? "dongle" : "wired";

    // ------------------------------------------------------------------
    // Опрос
    // ------------------------------------------------------------------

    /// <summary>«bat » открыли — что давно не спрашивали, спросить сейчас.</summary>
    public void RefreshIfStale()
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            foreach (var s in _sources.Values)
                if (s.Due != DateTime.MaxValue && now - s.LastPoll > OnDemandAge) s.Due = now;
        }
        _wake.Set();
    }

    /// <summary>Опросить всё сейчас и дождаться — для отчёта диагностики.</summary>
    public void PollAllAndWait(TimeSpan timeout)
    {
        var start = DateTime.Now;
        lock (_gate)
            foreach (var s in _sources.Values)
                if (s.Due != DateTime.MaxValue) s.Due = start;
        _wake.Set();
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            lock (_gate)
                if (_sources.Values.All(s => s.Due == DateTime.MaxValue || s.LastPoll >= start)) return;
            Thread.Sleep(100);
        }
    }

    private void WorkerLoop()
    {
        while (!_disposed)
        {
            Source? next;
            TimeSpan wait;
            lock (_gate)
            {
                next = _sources.Values.Where(s => s.Due != DateTime.MaxValue).MinBy(s => s.Due);
                wait = next is null ? Timeout.InfiniteTimeSpan : next.Due - DateTime.Now;
            }
            if (next is null || wait > TimeSpan.Zero)
            {
                _wake.WaitOne(next is null ? Timeout.InfiniteTimeSpan : wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait);
                continue;
            }

            var trace = new List<string>();
            List<Device>? result = null;
            try
            {
                result = next.Poll(trace);
            }
            catch (Exception e)
            {
                trace.Add($"сбой: {e.GetType().Name}: {e.Message}");
                Log.Write($"devices: {next.Key}: {e.Message}");
            }

            lock (_gate)
            {
                var now = DateTime.Now;
                next.LastPoll = now;
                next.Trace = trace;
                if (result is not null)
                {
                    next.Failures = 0;
                    next.Devices = result;
                }
                else
                {
                    // Не ответило: старый заряд держим, пока не устарел; пауза растёт.
                    next.Failures++;
                    next.Devices = next.Devices
                        .Select(d => d.At is { } at && now - at > BatteryKeep ? d with { Battery = null, Charging = null, At = null } : d)
                        .ToList();
                }
                if (next.Due != DateTime.MaxValue)
                {
                    var delay = next.Failures == 0 ? PollInterval : TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, PollInterval.Ticks << Math.Min(next.Failures - 1, 4)));
                    next.Due = now + delay;
                }
            }
            Publish();
        }
    }

    private void Publish()
    {
        List<Device> devices;
        lock (_gate)
        {
            devices = _sources.Values.SelectMany(s => s.Devices).ToList();
            foreach (var (id, bt) in _bluetooth)
            {
                // Сопряжённое, но выключенное: у Windows остаётся заряд с прошлого раза — его не показываем.
                if (!bt.Connected) continue;
                // At = null: Windows обновляет заряд сама, «устаревания» у него нет — и снимок не меняется зря.
                devices.Add(new Device($"bt:{id}", bt.Name, GuessKind(bt.Name), "bluetooth", bt.Battery, null, false, null, "bluetooth"));
            }
            devices = devices
                .OrderBy(d => Array.IndexOf(RecipeParser.Kinds, d.Kind))
                .ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (devices.SequenceEqual(Devices)) return;
            Devices = devices;
        }
        Changed?.Invoke();
    }

    private static string GuessKind(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("dualsense") || n.Contains("joy-con")) return "gamepad";
        if (n.Contains("keyboard") || n.Contains("клавиат") || n.Contains("keys")) return "keyboard";
        if (n.Contains("mouse") || n.Contains("мыш") || n.Contains("mx ") || n.Contains("apex")) return "mouse";
        if (n.Contains("buds") || n.Contains("head") || n.Contains("pods") || n.Contains("наушн") || n.Contains("wh-") || n.Contains("wf-")) return "headset";
        return "other";
    }

    // ------------------------------------------------------------------
    // Источники
    // ------------------------------------------------------------------

    private abstract class Source(string key)
    {
        public string Key { get; } = key;
        /// <summary>Когда спросить; MaxValue — спрашивать нечего.</summary>
        public DateTime Due { get; set; } = DateTime.Now.AddSeconds(2);
        public DateTime LastPoll { get; set; } = DateTime.MinValue;
        public int Failures { get; set; }
        public List<Device> Devices { get; set; } = [];
        public List<string> Trace { get; set; } = [];

        /// <summary>Устройства с зарядом; null — не ответило (заряд держим старый).</summary>
        public abstract List<Device>? Poll(List<string> trace);
    }

    /// <summary>Рецепт без шагов: только «подключено».</summary>
    private sealed class PresenceSource : Source
    {
        public PresenceSource(string key, Recipe recipe) : base(key)
        {
            Due = DateTime.MaxValue;
            Devices = [new Device(key, recipe.Name, recipe.Kind, recipe.Connection, null, null, false, null, recipe.Id)];
        }

        public override List<Device>? Poll(List<string> trace) => Devices;
    }

    private sealed class RecipeSource(string key, Recipe recipe, string path) : Source(key)
    {
        public Recipe Recipe { get; } = recipe;
        public string Path { get; } = path;

        public override List<Device>? Poll(List<string> trace)
        {
            using var channel = HidChannel.Open(Path);
            if (channel is null)
            {
                trace.Add("коллекция не открывается (занята или нет доступа)");
                return null;
            }
            trace.Add($"рецепт {Recipe.Id}: вход {channel.InputLength}, выход {channel.OutputLength}, feature {channel.FeatureLength} байт");
            var reading = RecipeRunner.Run(Recipe, channel, trace);
            if (reading.Offline)
                return [new Device(Key, Recipe.Name, Recipe.Kind, Recipe.Connection, null, null, true, null, Recipe.Id)];
            if (reading.Level is not { } level) return null;
            return [new Device(Key, Recipe.Name, Recipe.Kind, Recipe.Connection, level, reading.Charging, false, DateTime.Now, Recipe.Id)];
        }
    }

    private sealed class HidppSource(string key, string longPath, string? shortPath, string connection) : Source(key)
    {
        private const int Slots = 6;
        public string LongPath { get; } = longPath;
        private bool? _direct;
        private readonly Dictionary<byte, HidppSession.DeviceInfo> _known = [];

        public override List<Device>? Poll(List<string> trace)
        {
            using var longChannel = HidChannel.Open(LongPath);
            if (longChannel is null)
            {
                trace.Add("HID++: длинная коллекция не открывается");
                return null;
            }
            using var shortChannel = shortPath is null ? null : HidChannel.Open(shortPath);
            var session = new HidppSession(longChannel, shortChannel) { Trace = trace };

            // Прямое устройство отвечает на 0xFF как HID++ 2.0, приёмник — ошибкой HID++ 1.0.
            // Режим запоминаем только по ясному ответу: таймаут — просто попробовать позже.
            if (_direct is null)
            {
                switch (session.Probe(HidppSession.Direct))
                {
                    case HidppSession.Outcome.Ok: _direct = true; break;
                    case HidppSession.Outcome.Absent or HidppSession.Outcome.Error: _direct = false; break;
                    default: return null;
                }
            }
            var slots = _direct == true ? [HidppSession.Direct] : Enumerable.Range(1, Slots).Select(i => (byte)i).ToArray();

            var devices = new List<Device>();
            foreach (var slot in slots)
            {
                if (!_known.TryGetValue(slot, out var info))
                {
                    // Слот ещё не знаем — спросить; пустой слот отвечает сразу.
                    if (session.Discover(slot) is not { } found) continue;
                    _known[slot] = info = found;
                    trace.Add($"HID++ {slot:X2}: {found.Name}, фича заряда {found.BatteryFeature:X4}");
                }
                var reading = session.ReadBattery(slot, info);
                var key = $"{Key}:{slot}";
                var conn = _direct == true ? connection : "dongle";
                if (reading.Offline || reading.Level is null)
                {
                    // Спит или выключено: старый заряд держим, если он был.
                    var old = Devices.FirstOrDefault(d => d.Key == key);
                    devices.Add(old is not null ? old with { Offline = reading.Offline } : new Device(key, info.Name, info.Kind, conn, null, null, true, null, "hid++"));
                    continue;
                }
                devices.Add(new Device(key, info.Name, info.Kind, conn, reading.Level, reading.Charging, false, DateTime.Now, "hid++"));
            }
            trace.Add($"HID++: {devices.Count} устройств с зарядом");
            // Приёмник без устройств с зарядом — не ошибка, просто показывать нечего.
            return devices;
        }
    }

    // ------------------------------------------------------------------
    // Отчёт для тех, кто пишет рецепт
    // ------------------------------------------------------------------

    /// <summary>Всё, что нужно, чтобы написать рецепт: коллекции, база, ответы устройств.</summary>
    public string BuildReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Islet — устройства, {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("Как писать рецепт: https://github.com/Rayness/islet/blob/master/docs/devices.md");
        sb.AppendLine();
        sb.AppendLine("== База рецептов");
        foreach (var line in _store.Summary) sb.AppendLine(line);

        List<HidInterface> hid;
        List<Source> sources;
        List<KeyValuePair<string, (string Name, int Battery, bool Connected)>> bluetooth;
        lock (_gate)
        {
            hid = [.. _hid.Values];
            sources = [.. _sources.Values];
            bluetooth = [.. _bluetooth];
        }

        sb.AppendLine();
        sb.AppendLine("== HID-интерфейсы (VID:PID, интерфейс, страница/usage, длины вход/выход/feature, имя)");
        foreach (var h in hid.OrderBy(h => h.Vid).ThenBy(h => h.Pid).ThenBy(h => h.Interface).ThenBy(h => h.Page))
        {
            string name = "", lengths = "";
            using (var handle = Hid.OpenQuery(h.Path))
            {
                if (handle is not null)
                {
                    name = $"{Hid.ManufacturerString(handle)} {Hid.ProductString(handle)}".Trim();
                    if (Hid.GetCaps(handle) is { } caps)
                        lengths = $"{caps.InputReportByteLength}/{caps.OutputReportByteLength}/{caps.FeatureReportByteLength}";
                }
            }
            var mark = h.Page >= 0xFF00 ? "  ← вендорская" : "";
            sb.AppendLine($"{h.Vid:X4}:{h.Pid:X4}  MI {h.Interface:D2}  {h.Page:X4}/{h.Usage:X4}  {lengths,-11} {name}{mark}");
        }

        sb.AppendLine();
        sb.AppendLine("== Источники заряда и последний разговор с устройством");
        if (sources.Count == 0) sb.AppendLine("(ни одно подключённое устройство не совпало с рецептами и не Logitech)");
        foreach (var s in sources)
        {
            sb.AppendLine($"-- {s.Key}  (неудач подряд: {s.Failures}, следующий опрос: {(s.Due == DateTime.MaxValue ? "не нужен" : s.Due.ToString("HH:mm:ss"))})");
            foreach (var d in s.Devices)
                sb.AppendLine($"   {d.Name}: {(d.Offline ? "не на связи" : d.Battery is { } b ? $"{b}%{(d.Charging == true ? ", заряжается" : "")}" : "заряд неизвестен")}");
            foreach (var t in s.Trace) sb.AppendLine($"   · {t}");
        }

        sb.AppendLine();
        sb.AppendLine("== Bluetooth: заряд, который знает Windows");
        foreach (var (_, bt) in bluetooth) sb.AppendLine($"{bt.Name}: {bt.Battery}% {(bt.Connected ? "(подключено)" : "(не подключено — не показываем)")}");
        if (bluetooth.Count == 0) sb.AppendLine("(нет)");
        return sb.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        _store.Changed -= RebuildSources;
        foreach (var watcher in new[] { _hidWatcher, _btWatcher })
        {
            try
            {
                if (watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
                    watcher.Stop();
            }
            catch
            {
                // Островок закрывается — неважно.
            }
        }
    }
}
