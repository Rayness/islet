using Islet.Core;
using Islet.Settings;

namespace Islet.Integrations.Devices;

/// <summary>
/// Садится заряд — пик; почти сел — красная капсула, пока устройство не поставят на зарядку.
/// Один пик на порог; снова — только после зарядки (с запасом, чтобы 20↔21 не мигали).
/// </summary>
internal sealed class BatteryAlerts
{
    private const string ActivityId = "devices-battery";
    private const string Danger = "#FF6B6B";

    private readonly DeviceMonitor _monitor;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, int> _warned = [];

    public BatteryAlerts(DeviceMonitor monitor)
    {
        _monitor = monitor;
        monitor.Changed += () => Guard.Run(Evaluate);
    }

    public void Evaluate()
    {
        var settings = SettingsStore.Current;
        var low = Math.Clamp(settings.WirelessLowBattery, 5, 50);
        var critical = Math.Max(5, low / 2);
        DeviceMonitor.Device? worst = null;

        lock (_gate)
        {
            foreach (var d in _monitor.Devices)
            {
                // 0 % у живого устройства не бывает — это «не знаю», тревожить им нельзя.
                if (d.Offline || d.Battery is not (> 0 and var battery)) continue;
                if (d.Charging == true || battery > low + 5)
                {
                    _warned.Remove(d.Key);
                    continue;
                }
                if (battery <= critical && (worst is null || battery < worst.Battery))
                    worst = d;

                var level = battery <= critical ? critical : battery <= low ? low : 0;
                if (level == 0 || (_warned.TryGetValue(d.Key, out var warned) && warned <= level)) continue;
                _warned[d.Key] = level;
                if (!settings.WirelessBatteryPeeks) continue;

                App.Current.Notifications.Post(new IsletNotification
                {
                    Source = "devices",
                    SourceName = Loc.T("S_DevicesSection"),
                    Title = Loc.T("Wdc_LowTitle", d.Name, battery),
                    Body = Loc.T(level == critical ? "Wdc_CriticalBody" : "Wdc_LowBody"),
                    Glyph = DevicesProvider.BatteryGlyph(battery, false),
                    Action = new IsletAction { Query = "bat " },
                });
            }
        }

        if (worst is not null && settings.WirelessBatteryPeeks)
        {
            App.Current.Activities.Set(new LiveActivity
            {
                Id = ActivityId,
                Source = "devices",
                Text = Loc.T("Wdc_Activity", worst.Name, worst.Battery),
                Glyph = DevicesProvider.BatteryGlyph(worst.Battery ?? 0, false),
                Color = Danger,
                Action = new IsletAction { Query = "bat " },
            });
        }
        else
        {
            App.Current.Activities.Clear(ActivityId);
        }
    }
}
