using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Islet.Integrations.Devices;

/// <summary>
/// Рецепт — как спросить заряд у одного семейства устройств, без кода: какую HID-коллекцию
/// открыть, какие байты отправить и где в ответе заряд. Рецепты лежат в devices.json
/// (встроенная копия, общая база из репозитория, свой файл для отладки) — формат описан
/// в docs/devices.md.
///
/// Рецепт шлёт байты прямо в устройство, поэтому разбор строгий: только вендорские
/// коллекции (и Consumer Control — там живут гарнитуры), ограничения на число шагов,
/// длину пакетов, паузы и общее время. Что не прошло проверку — отбрасывается целиком.
/// </summary>
internal sealed record Recipe(
    string Id,
    string Name,
    string Kind,
    string Connection,
    ushort Vid,
    ushort[] Pids,
    ushort? Page,
    ushort? Usage,
    int? Interface,
    IReadOnlyList<RecipeStep> Steps,
    string? Source);

internal enum StepKind { Write, Read, SetFeature, GetFeature, Wait, Flush }

internal sealed record RecipeStep(
    StepKind Kind,
    byte[] Data,
    int Ms,
    int Attempts,
    Checksum? Checksum,
    IReadOnlyList<ByteMatch> Match,
    IReadOnlyList<ByteMatch> Require,
    IReadOnlyList<ByteMatch> Offline,
    BatteryField? Battery,
    ChargingField? Charging);

internal sealed record Checksum(int From, int To, int At);

/// <summary>Условие на байт ответа: (b[At] &amp; Mask) — одно из Values.</summary>
internal sealed record ByteMatch(int At, byte Mask, byte[] Values)
{
    public bool Test(byte[] data) => At < data.Length && Values.Contains((byte)(data[At] & Mask));
}

internal sealed record BatteryField(int At, byte Mask, string? Word, int[]? Range, int[][]? Curve, int[]? Valid);

internal sealed record ChargingField(int At, byte Mask, byte[]? Values);

/// <summary>Итог опроса: заряд, заряжается ли, или устройство сейчас не на связи.</summary>
internal sealed record BatteryReading(int? Level, bool? Charging, bool Offline)
{
    public static readonly BatteryReading Unknown = new(null, null, false);
    public static readonly BatteryReading Away = new(null, null, true);
}

internal static class RecipeParser
{
    public const int MaxSteps = 16;
    public const int MaxPayload = 64;
    public const int MaxWaitMs = 500;
    public const int MaxReadMs = 1500;
    public const int MaxAttempts = 32;

    public static readonly string[] Kinds = ["mouse", "keyboard", "headset", "gamepad", "other"];
    public static readonly string[] Connections = ["dongle", "wired", "bluetooth"];

    /// <summary>Все годные рецепты из документа; негодные — в <paramref name="errors"/>, остальным не мешают.</summary>
    public static List<Recipe> Parse(JsonElement root, List<string> errors)
    {
        var list = new List<Recipe>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("devices", out var devices) || devices.ValueKind != JsonValueKind.Array)
        {
            errors.Add("нет массива devices");
            return list;
        }
        foreach (var d in devices.EnumerateArray())
        {
            var id = Str(d, "id") ?? "?";
            try
            {
                list.Add(ParseRecipe(d));
            }
            catch (FormatException e)
            {
                errors.Add($"{id}: {e.Message}");
            }
        }
        return list;
    }

    private static Recipe ParseRecipe(JsonElement d)
    {
        var id = Str(d, "id") is { Length: > 0 and <= 80 } s && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')
            ? s : throw new FormatException("id: латиница, цифры, - _ . до 80 символов");
        var name = Str(d, "name") is { Length: > 0 and <= 80 } n ? n : throw new FormatException("name обязателен");
        var kind = Str(d, "kind") ?? "other";
        if (!Kinds.Contains(kind)) throw new FormatException($"kind: одно из {string.Join(", ", Kinds)}");
        var connection = Str(d, "connection") ?? "dongle";
        if (!Connections.Contains(connection)) throw new FormatException($"connection: одно из {string.Join(", ", Connections)}");

        if (!d.TryGetProperty("match", out var m) || m.ValueKind != JsonValueKind.Object) throw new FormatException("нет match");
        var vid = Hex16(m, "vid") ?? throw new FormatException("match.vid обязателен");
        var pids = m.TryGetProperty("pid", out var p) && p.ValueKind == JsonValueKind.Array
            ? p.EnumerateArray().Select(e => ParseHex16(e.GetString())).ToArray()
            : [Hex16(m, "pid") ?? throw new FormatException("match.pid обязателен")];
        if (pids.Length is 0 or > 64) throw new FormatException("match.pid: от 1 до 64");
        var page = Hex16(m, "page");
        var usage = Hex16(m, "usage");
        int? iface = m.TryGetProperty("interface", out var i) && i.ValueKind == JsonValueKind.Number ? i.GetInt32() : null;
        // Клавиатуру и мышь (Generic Desktop) Windows не отдаёт, а писать в них и нечего.
        if (page is { } pg && pg < 0xFF00 && pg != 0x000C) throw new FormatException("match.page: только вендорские (FF00+) или 000C");

        // Без steps рецепт только отмечает, что устройство подключено (Stream Dock, клавиатура по проводу).
        var steps = d.TryGetProperty("steps", out var st) && st.ValueKind == JsonValueKind.Array
            ? st.EnumerateArray().Select(ParseStep).ToList()
            : [];
        if (steps.Count > MaxSteps) throw new FormatException($"steps: не больше {MaxSteps}");
        if (steps.Count > 0 && !steps.Any(s => s.Battery is not null)) throw new FormatException("ни один шаг не читает battery");
        // С устройством разговаривают — значит, надо точно знать, с какой коллекцией.
        if (steps.Count > 0 && page is null && iface is null) throw new FormatException("match: нужен page или interface");

        return new Recipe(id, name, kind, connection, vid, pids, page, usage, iface, steps, Str(d, "source"));
    }

    private static RecipeStep ParseStep(JsonElement s)
    {
        if (s.ValueKind != JsonValueKind.Object) throw new FormatException("шаг — объект");
        StepKind kind;
        byte[] data = [];
        var ms = 0;
        var attempts = 1;

        if (Str(s, "write") is { } w) { kind = StepKind.Write; data = Bytes(w); }
        else if (Str(s, "setFeature") is { } sf) { kind = StepKind.SetFeature; data = Bytes(sf); }
        else if (Str(s, "getFeature") is { } gf) { kind = StepKind.GetFeature; data = Bytes(gf); if (data.Length != 1) throw new FormatException("getFeature — один байт, номер отчёта"); }
        else if (s.TryGetProperty("wait", out var wt) && wt.ValueKind == JsonValueKind.Number)
        {
            kind = StepKind.Wait;
            ms = wt.GetInt32();
            if (ms is < 1 or > MaxWaitMs) throw new FormatException($"wait: 1…{MaxWaitMs} мс");
        }
        else if (s.TryGetProperty("flush", out var fl) && fl.ValueKind == JsonValueKind.True) kind = StepKind.Flush;
        else if (s.TryGetProperty("read", out var rd) && rd.ValueKind is JsonValueKind.Object or JsonValueKind.True)
        {
            kind = StepKind.Read;
            ms = 1000;
            attempts = 8;
            if (rd.ValueKind == JsonValueKind.Object)
            {
                if (rd.TryGetProperty("timeout", out var t) && t.ValueKind == JsonValueKind.Number) ms = t.GetInt32();
                if (rd.TryGetProperty("attempts", out var a) && a.ValueKind == JsonValueKind.Number) attempts = a.GetInt32();
            }
            if (ms is < 10 or > MaxReadMs) throw new FormatException($"read.timeout: 10…{MaxReadMs} мс");
            if (attempts is < 1 or > MaxAttempts) throw new FormatException($"read.attempts: 1…{MaxAttempts}");
        }
        else throw new FormatException("шаг: write, read, setFeature, getFeature, wait или flush");

        if (data.Length > MaxPayload) throw new FormatException($"пакет длиннее {MaxPayload} байт");

        var len = s.TryGetProperty("length", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;
        if (len > 0)
        {
            if (len > MaxPayload || len < data.Length) throw new FormatException("length меньше пакета или длиннее 64");
            Array.Resize(ref data, len);
        }

        Checksum? checksum = null;
        if (s.TryGetProperty("checksum", out var c) && c.ValueKind == JsonValueKind.Object)
        {
            checksum = new Checksum(Int(c, "from"), Int(c, "to"), Int(c, "at"));
            if (checksum.From < 0 || checksum.To < checksum.From || checksum.At >= data.Length || checksum.To >= data.Length)
                throw new FormatException("checksum вне пакета");
        }

        var reads = kind is StepKind.Read or StepKind.GetFeature;
        var match = Matches(s, "match");
        var require = Matches(s, "require");
        var offline = Matches(s, "offline");
        var battery = s.TryGetProperty("battery", out var b) ? ParseBattery(b) : null;
        var charging = s.TryGetProperty("charging", out var ch) ? ParseCharging(ch) : null;
        if (!reads && (match.Count + require.Count + offline.Count > 0 || battery is not null || charging is not null))
            throw new FormatException("match, require, offline, battery, charging — только у read и getFeature");
        if (kind != StepKind.Read && match.Count > 0) throw new FormatException("match — только у read");

        return new RecipeStep(kind, data, ms, attempts, checksum, match, require, offline, battery, charging);
    }

    private static BatteryField ParseBattery(JsonElement b)
    {
        var at = Int(b, "at");
        var word = Str(b, "word");
        if (word is not (null or "be" or "le")) throw new FormatException("battery.word: be или le");
        var range = IntArray(b, "range");
        if (range is not null && (range.Length != 2 || range[0] == range[1])) throw new FormatException("battery.range: [мин, макс]");
        int[][]? curve = null;
        if (b.TryGetProperty("curve", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            curve = c.EnumerateArray().Select(pt => pt.EnumerateArray().Select(x => x.GetInt32()).ToArray()).ToArray();
            if (curve.Length < 2 || curve.Any(pt => pt.Length != 2)) throw new FormatException("battery.curve: [[сырое, %], …] от двух точек");
            curve = [.. curve.OrderBy(pt => pt[0])];
        }
        var valid = IntArray(b, "valid");
        if (valid is not null && valid.Length != 2) throw new FormatException("battery.valid: [мин, макс]");
        return new BatteryField(at, Mask(b), word, range, curve, valid);
    }

    private static ChargingField ParseCharging(JsonElement c) =>
        new(Int(c, "at"), Mask(c), c.TryGetProperty("equals", out var e) ? Values(e) : null);

    private static List<ByteMatch> Matches(JsonElement s, string name)
    {
        var list = new List<ByteMatch>();
        if (!s.TryGetProperty(name, out var m)) return list;
        if (m.ValueKind != JsonValueKind.Object) throw new FormatException($"{name}: {{\"байт\": \"значение\"}}");
        foreach (var p in m.EnumerateObject())
        {
            if (!int.TryParse(p.Name, out var at) || at is < 0 or > 255) throw new FormatException($"{name}: номер байта");
            // «4A/7F» — значение под маской; список — любое из значений.
            byte mask = 0xFF;
            byte[] values;
            if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString()!.Split('/') is [var v, var mk])
            {
                mask = ParseHex8(mk);
                values = [ParseHex8(v)];
            }
            else values = Values(p.Value);
            list.Add(new ByteMatch(at, mask, values));
        }
        return list;
    }

    private static byte[] Values(JsonElement e) => e.ValueKind == JsonValueKind.Array
        ? e.EnumerateArray().Select(x => ParseHex8(x.GetString())).ToArray()
        : [ParseHex8(e.GetString())];

    private static byte Mask(JsonElement e) => Str(e, "mask") is { } m ? ParseHex8(m) : (byte)0xFF;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() is >= 0 and <= 255
            ? v.GetInt32()
            : throw new FormatException($"{name}: число 0…255");

    private static int[]? IntArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ushort? Hex16(JsonElement e, string name) => Str(e, name) is { } s ? ParseHex16(s) : null;

    private static ushort ParseHex16(string? s) =>
        ushort.TryParse(s?.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, null, out var v)
            ? v : throw new FormatException($"«{s}» — не hex-число");

    private static byte ParseHex8(string? s) =>
        byte.TryParse(s?.Replace("0x", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, null, out var v)
            ? v : throw new FormatException($"«{s}» — не hex-байт");

    /// <summary>«13 4A 01» или «134A01».</summary>
    public static byte[] Bytes(string hex)
    {
        var clean = new string(hex.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray());
        if (clean.Length % 2 != 0) throw new FormatException($"«{hex}» — нечётное число hex-цифр");
        try { return Convert.FromHexString(clean); }
        catch (FormatException) { throw new FormatException($"«{hex}» — не hex"); }
    }
}

/// <summary>Выполняет рецепт на открытой коллекции. Весь рецепт укладывается в <see cref="Budget"/>.</summary>
internal static class RecipeRunner
{
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    public static BatteryReading Run(Recipe recipe, IHidChannel channel, List<string>? trace = null)
    {
        var clock = Stopwatch.StartNew();
        int? level = null;
        bool? charging = null;

        foreach (var step in recipe.Steps)
        {
            if (clock.Elapsed > Budget)
            {
                trace?.Add("бюджет времени исчерпан");
                return BatteryReading.Unknown;
            }
            byte[]? response = null;
            switch (step.Kind)
            {
                case StepKind.Wait:
                    Thread.Sleep(step.Ms);
                    continue;
                case StepKind.Flush:
                    channel.Flush();
                    continue;
                case StepKind.Write:
                case StepKind.SetFeature:
                {
                    var packet = Build(step);
                    var ok = step.Kind == StepKind.Write ? channel.Write(packet) : channel.SetFeature(packet);
                    trace?.Add($"{(step.Kind == StepKind.Write ? "write" : "setFeature")} {Hex(packet)} → {(ok ? "ok" : "ошибка")}");
                    if (!ok) return BatteryReading.Unknown;
                    continue;
                }
                case StepKind.GetFeature:
                    response = channel.GetFeature(step.Data[0]);
                    trace?.Add($"getFeature {step.Data[0]:X2} → {(response is null ? "ошибка" : Hex(response))}");
                    break;
                case StepKind.Read:
                    response = ReadMatching(channel, step, clock, trace);
                    break;
            }

            if (response is null) return BatteryReading.Unknown;
            if (step.Offline.Count > 0 && step.Offline.All(m => m.Test(response)))
            {
                trace?.Add("устройство не на связи");
                return BatteryReading.Away;
            }
            if (!step.Require.All(m => m.Test(response)))
            {
                trace?.Add("ответ не прошёл require");
                return BatteryReading.Unknown;
            }
            if (step.Charging is { } ch && ch.At < response.Length)
            {
                var v = (byte)(response[ch.At] & ch.Mask);
                charging = ch.Values is null ? v != 0 : ch.Values.Contains(v);
            }
            if (step.Battery is { } b)
            {
                level = Extract(b, response);
                trace?.Add($"заряд: {(level is null ? "не прочитан" : $"{level}%")}");
                if (level is null) return BatteryReading.Unknown;
            }
        }
        return new BatteryReading(level, charging, false);
    }

    private static byte[]? ReadMatching(IHidChannel channel, RecipeStep step, Stopwatch clock, List<string>? trace)
    {
        var deadline = clock.Elapsed + TimeSpan.FromMilliseconds(step.Ms);
        for (var i = 0; i < step.Attempts; i++)
        {
            var left = deadline - clock.Elapsed;
            if (left <= TimeSpan.Zero) break;
            var report = channel.Read(left);
            if (report is null) break;
            var ok = step.Match.All(m => m.Test(report));
            trace?.Add($"read {Hex(report)}{(ok ? "" : " — не тот, дальше")}");
            if (ok) return report;
        }
        trace?.Add("read: ответа нет");
        return null;
    }

    private static byte[] Build(RecipeStep step)
    {
        var packet = (byte[])step.Data.Clone();
        if (step.Checksum is { } c)
        {
            var sum = 0;
            for (var i = c.From; i <= c.To; i++) sum += packet[i];
            packet[c.At] = (byte)sum;
        }
        return packet;
    }

    public static int? Extract(BatteryField b, byte[] data)
    {
        int raw;
        if (b.Word is null)
        {
            if (b.At >= data.Length) return null;
            raw = data[b.At] & b.Mask;
        }
        else
        {
            if (b.At + 1 >= data.Length) return null;
            raw = b.Word == "be" ? data[b.At] << 8 | data[b.At + 1] : data[b.At + 1] << 8 | data[b.At];
        }
        if (b.Valid is [var lo, var hi] && (raw < lo || raw > hi)) return null;

        double percent;
        if (b.Curve is { } curve) percent = Interpolate(curve, raw);
        else if (b.Range is [var min, var max]) percent = (raw - min) * 100.0 / (max - min);
        else percent = raw;
        return (int)Math.Round(Math.Clamp(percent, 0, 100));
    }

    /// <summary>Кусочно-линейная кривая: напряжение (или другое сырое значение) → проценты.</summary>
    public static double Interpolate(int[][] curve, int raw)
    {
        if (raw <= curve[0][0]) return curve[0][1];
        for (var i = 1; i < curve.Length; i++)
        {
            if (raw > curve[i][0]) continue;
            var (x0, y0, x1, y1) = (curve[i - 1][0], curve[i - 1][1], curve[i][0], curve[i][1]);
            return y0 + (y1 - y0) * (raw - x0) / (double)(x1 - x0);
        }
        return curve[^1][1];
    }

    public static string Hex(byte[] data) =>
        string.Join(' ', (data.Length > 32 ? data[..32] : data).Select(b => b.ToString("X2"))) + (data.Length > 32 ? " …" : "");
}
