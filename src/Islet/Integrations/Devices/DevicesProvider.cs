using Islet.Core;
using Islet.Search;
using Islet.Settings;

namespace Islet.Integrations.Devices;

/// <summary>«bat » — заряд мышей, клавиатур, гарнитур и геймпадов; без ключевого слова — по имени устройства и «заряд».</summary>
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
    private static readonly string[] BatteryWords =
        ["заряд", "батар", "battery", "charge", "мыш", "клав", "наушн", "гарнит", "геймпад", "mouse", "keyboard", "headset", "gamepad"];

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var text = query.Text;
        var devices = monitor.Devices;

        if (!query.IsScoped)
        {
            if (text.Length < 3 || devices.Count == 0) return [];
            var all = BatteryWords.Any(w => w.StartsWith(text, StringComparison.OrdinalIgnoreCase) || text.StartsWith(w, StringComparison.OrdinalIgnoreCase));
            var found = all ? devices : devices.Where(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();
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
        var parts = new List<string>
        {
            d.Connection switch
            {
                "bluetooth" => "Bluetooth",
                "dongle" => Loc.T("Wdc_Dongle"),
                _ => "USB",
            },
        };
        if (d.Offline) parts.Add(Loc.T("Wdc_Offline"));
        else if (d.Charging == true) parts.Add(Loc.T("Wdc_Charging"));
        else if (d.Battery is null && d.Via != "hid++" && d.Connection != "wired") parts.Add(Loc.T("Wdc_NoBattery"));

        return new ResultItem
        {
            Title = d.Name,
            Subtitle = string.Join(" · ", parts),
            Kind = ResultKind.Command,
            Target = $"devices:{d.Key}",
            ProviderId = Id,
            GlyphOverride = d.Battery is { } level && !d.Offline
                ? BatteryGlyph(level, d.Charging == true)
                : KindGlyph(d.Kind),
            Trailing = d.Battery is { } battery && !d.Offline ? $"{battery}%" : "",
            // Enter — спросить заряд ещё раз, островок остаётся открытым.
            Action = IsletAction.Run(() =>
            {
                monitor.RefreshIfStale();
                return ActionOutcome.Stay;
            }),
            Remember = false,
        };
    }

    public static string KindGlyph(string kind) => kind switch
    {
        "mouse" => "",
        "keyboard" => "",
        "headset" => "",
        "gamepad" => "",
        _ => "",
    };

    /// <summary>Глиф батареи Segoe Fluent Icons по уровню: Battery0…Battery10 и они же с зарядкой.</summary>
    public static string BatteryGlyph(int percent, bool charging)
    {
        var step = Math.Clamp((int)Math.Round(percent / 10.0), 0, 10);
        if (charging) return step == 10 ? "" : ((char)(0xE85A + step)).ToString();
        return step == 10 ? "" : ((char)(0xE850 + step)).ToString();
    }
}
