using System.Diagnostics;
using System.Text;

namespace Islet.Integrations.Devices;

/// <summary>
/// Logitech HID++ 2.0 — заряд мышей, клавиатур и гарнитур Logitech: через приёмник
/// (Unifying, Lightspeed, Bolt — до шести устройств) и напрямую (провод, Bluetooth).
///
/// Протокол открытый и одинаковый у всего семейства, поэтому он в коде, а не в рецептах:
/// номера нужных фич устройство сообщает само через корневую фичу 0x0000. Заряд — из
/// первой найденной: 0x1004 Unified Battery (проценты или уровень), 0x1000 Battery Status
/// (проценты), 0x1001 Battery Voltage и 0x1F20 ADC (напряжение → проценты по таблице
/// Solaar). Имя — 0x0005 Device Name. Всё только читается.
///
/// Длинные пакеты (отчёт 0x11, 20 байт) идут в «длинную» коллекцию, но приёмник отвечает
/// на запрос к пустому слоту коротким HID++ 1.0 пакетом ошибки (0x10 … 8F) в «короткую»
/// коллекцию — её слушаем тоже, иначе каждый пустой слот стоил бы целый таймаут.
/// </summary>
internal sealed class HidppSession(IHidChannel longChannel, IHidChannel? shortChannel)
{
    public const byte Direct = 0xFF;
    private const byte SoftwareId = 0x0E;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(500);

    private const ushort FeatureRoot = 0x0000;
    private const ushort FeatureName = 0x0005;
    private const ushort FeatureBatteryStatus = 0x1000;
    private const ushort FeatureBatteryVoltage = 0x1001;
    private const ushort FeatureUnifiedBattery = 0x1004;
    private const ushort FeatureAdc = 0x1F20;

    /// <summary>Таблица Solaar: напряжение Li-ion, мВ → проценты.</summary>
    public static readonly int[][] VoltageCurve =
    [
        [3500, 0], [3579, 2], [3646, 5], [3671, 10], [3717, 20], [3751, 30], [3778, 40],
        [3811, 50], [3859, 60], [3922, 70], [3989, 80], [4067, 90], [4186, 100],
    ];

    public enum Outcome { Ok, Error, Absent, Timeout }

    public sealed record Response(Outcome Outcome, byte[] Params);

    /// <summary>Что знаем об устройстве за один раз — номера фич и имя не меняются.</summary>
    public sealed class DeviceInfo
    {
        public string Name { get; set; } = "Logitech";
        public string Kind { get; set; } = "other";
        public ushort BatteryFeature { get; set; }
        public byte BatteryIndex { get; set; }
    }

    public List<string>? Trace { get; set; }

    /// <summary>Запрос к фиче по её номеру у устройства. null — канал сломан.</summary>
    public Response Request(byte device, byte featureIndex, byte function, params byte[] args)
    {
        var packet = new byte[20];
        packet[0] = 0x11;
        packet[1] = device;
        packet[2] = featureIndex;
        packet[3] = (byte)(function << 4 | SoftwareId);
        args.CopyTo(packet, 4);

        longChannel.Flush();
        shortChannel?.Flush();
        if (!longChannel.Write(packet))
        {
            Trace?.Add($"hid++ write {RecipeRunner.Hex(packet[..7])} → ошибка");
            return new Response(Outcome.Error, []);
        }

        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < Timeout)
        {
            // Слушаем обе коллекции по очереди короткими окнами.
            foreach (var channel in shortChannel is null ? [longChannel] : new[] { longChannel, shortChannel })
            {
                if (channel.Read(TimeSpan.FromMilliseconds(25)) is not { Length: >= 4 } r || r[1] != device) continue;
                // HID++ 1.0 ошибка (короткий пакет): слот пуст, устройство спит или это не HID++ 2.0.
                if (r[0] == 0x10 && r[2] == 0x8F)
                {
                    Trace?.Add($"hid++ {device:X2}: нет ответа (8F, ошибка {(r.Length > 5 ? r[5] : 0):X2})");
                    return new Response(Outcome.Absent, []);
                }
                // HID++ 2.0 ошибка: FF, номер фичи, функция, код.
                if (r[0] == 0x11 && r[2] == 0xFF && r.Length > 5 && r[3] == featureIndex && r[4] == packet[3])
                {
                    Trace?.Add($"hid++ {device:X2}/{featureIndex:X2}: ошибка {r[5]:X2}");
                    return new Response(Outcome.Error, []);
                }
                if (r[0] is 0x11 or 0x10 && r[2] == featureIndex && r[3] == packet[3])
                    return new Response(Outcome.Ok, r[4..]);
                // Иначе — уведомление или ответ другой программе (G HUB): дальше.
            }
        }
        Trace?.Add($"hid++ {device:X2}/{featureIndex:X2}: таймаут");
        return new Response(Outcome.Timeout, []);
    }

    /// <summary>Номер фичи у устройства; 0 — нет такой. null — устройство не отвечает.</summary>
    public byte? FeatureIndex(byte device, ushort feature)
    {
        var r = Request(device, 0x00, 0, (byte)(feature >> 8), (byte)feature);
        return r.Outcome == Outcome.Ok && r.Params.Length > 0 ? r.Params[0] : r.Outcome == Outcome.Error ? (byte)0 : null;
    }

    /// <summary>Есть ли на этом номере устройство с HID++ 2.0: корневой пинг возвращает версию протокола.</summary>
    public bool Ping(byte device) => Probe(device) == Outcome.Ok;

    /// <summary>Корневой пинг: Ok — HID++ 2.0, Absent/Error — нет его тут, Timeout — непонятно.</summary>
    public Outcome Probe(byte device)
    {
        var r = Request(device, 0x00, 1, 0, 0, 0xAA);
        if (r.Outcome != Outcome.Ok) return r.Outcome;
        return r.Params.Length >= 3 && r.Params[0] >= 2 && r.Params[2] == 0xAA ? Outcome.Ok : Outcome.Error;
    }

    /// <summary>Узнать имя, тип и фичу заряда. null — устройства нет или у него нет заряда.</summary>
    public DeviceInfo? Discover(byte device)
    {
        if (!Ping(device)) return null;
        var info = new DeviceInfo();

        foreach (var feature in new[] { FeatureUnifiedBattery, FeatureBatteryStatus, FeatureBatteryVoltage, FeatureAdc })
        {
            if (FeatureIndex(device, feature) is { } index and > 0)
            {
                info.BatteryFeature = feature;
                info.BatteryIndex = index;
                break;
            }
        }
        if (info.BatteryIndex == 0) return null;

        if (FeatureIndex(device, FeatureName) is { } nameIndex and > 0)
        {
            var count = Request(device, nameIndex, 0);
            if (count.Outcome == Outcome.Ok && count.Params.Length > 0 && count.Params[0] is > 0 and <= 64)
            {
                var name = new StringBuilder();
                while (name.Length < count.Params[0])
                {
                    var part = Request(device, nameIndex, 1, (byte)name.Length);
                    if (part.Outcome != Outcome.Ok) break;
                    var chunk = Encoding.UTF8.GetString(part.Params).TrimEnd('\0');
                    if (chunk.Length == 0) break;
                    name.Append(chunk[..Math.Min(chunk.Length, count.Params[0] - name.Length)]);
                }
                if (name.Length > 0) info.Name = name.ToString().Trim();
            }
            var type = Request(device, nameIndex, 2);
            if (type.Outcome == Outcome.Ok && type.Params.Length > 0)
            {
                info.Kind = type.Params[0] switch
                {
                    0 or 2 => "keyboard",
                    3 or 4 or 5 => "mouse",
                    8 => "headset",
                    11 or 12 => "gamepad",
                    _ => "other",
                };
            }
        }
        // Гарнитуры не всегда называют свой тип, но ADC-фича бывает только у них.
        if (info.BatteryFeature == FeatureAdc && info.Kind == "other") info.Kind = "headset";
        return info;
    }

    public BatteryReading ReadBattery(byte device, DeviceInfo info)
    {
        var function = info.BatteryFeature == FeatureUnifiedBattery ? (byte)1 : (byte)0;
        var r = Request(device, info.BatteryIndex, function);
        if (r.Outcome == Outcome.Absent) return BatteryReading.Away;
        if (r.Outcome != Outcome.Ok || r.Params.Length < 3) return BatteryReading.Unknown;
        var p = r.Params;

        switch (info.BatteryFeature)
        {
            case FeatureUnifiedBattery:
            {
                // Проценты, а если их нет — уровень: 8 полный, 4 хороший, 2 низкий, 1 критический.
                int? level = p[0] is > 0 and <= 100 ? p[0] : p[1] switch { 8 => 90, 4 => 50, 2 => 20, 1 => 5, _ => null };
                return new BatteryReading(level, p[2] is 1 or 2 or 4, false);
            }
            case FeatureBatteryStatus:
                return new BatteryReading(p[0] is > 0 and <= 100 ? p[0] : null, p[2] is 1 or 2 or 4, false);
            case FeatureBatteryVoltage:
            {
                var mv = p[0] << 8 | p[1];
                return new BatteryReading(mv > 0 ? (int)Math.Round(RecipeRunner.Interpolate(VoltageCurve, mv)) : null, (p[2] & 0x80) != 0, false);
            }
            case FeatureAdc:
            {
                // Флаг 1 — гарнитура на связи, 2 — заряжается.
                if ((p[2] & 0x01) == 0) return BatteryReading.Away;
                var mv = p[0] << 8 | p[1];
                return new BatteryReading((int)Math.Round(RecipeRunner.Interpolate(VoltageCurve, mv)), (p[2] & 0x02) != 0, false);
            }
        }
        return BatteryReading.Unknown;
    }
}
