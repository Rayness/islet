using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Islet.Native;

/// <summary>
/// HID-коллекция по пути интерфейса (\\?\HID#VID_…): вендорские feature-, output- и
/// input-отчёты. Островку это нужно только чтобы спросить заряд у приёмников
/// мыши, клавиатуры и гарнитуры — см. Integrations/Devices/.
/// </summary>
internal static unsafe class Hid
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Caps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        public fixed ushort Reserved[17];
        public fixed ushort Counts[10];
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ_WRITE = 0x3;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetFeature(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(SafeFileHandle device, byte[] buffer, int length);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle device, out nint data);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(nint data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(nint data, out Caps caps);

    /// <summary>Открыть коллекцию на чтение и запись. null — нет доступа (клавиатуры и мыши Windows держит сама).</summary>
    public static SafeFileHandle? Open(string path, bool overlapped = false)
    {
        var handle = CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ_WRITE, 0, OPEN_EXISTING,
            overlapped ? FILE_FLAG_OVERLAPPED : 0, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>Открыть без чтения и записи — только спросить имя и размеры отчётов. Так открываются даже мышь и клавиатура.</summary>
    public static SafeFileHandle? OpenQuery(string path)
    {
        var handle = CreateFileW(path, 0, FILE_SHARE_READ_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    public static Caps? GetCaps(SafeFileHandle device)
    {
        if (!HidD_GetPreparsedData(device, out var data)) return null;
        try
        {
            return HidP_GetCaps(data, out var caps) == 0x00110000 /* HIDP_STATUS_SUCCESS */ ? caps : null;
        }
        finally
        {
            HidD_FreePreparsedData(data);
        }
    }

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(SafeFileHandle device, char[] buffer, int length);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(SafeFileHandle device, char[] buffer, int length);

    /// <summary>Название устройства из его дескриптора — для отчёта диагностики.</summary>
    public static string ProductString(SafeFileHandle device) => ReadString(device, HidD_GetProductString);

    public static string ManufacturerString(SafeFileHandle device) => ReadString(device, HidD_GetManufacturerString);

    private static string ReadString(SafeFileHandle device, Func<SafeFileHandle, char[], int, bool> read)
    {
        var buffer = new char[128];
        if (!read(device, buffer, buffer.Length * 2)) return "";
        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }

    public static bool SetFeature(SafeFileHandle device, byte[] report) => HidD_SetFeature(device, report, report.Length);

    public static bool GetFeature(SafeFileHandle device, byte[] report) => HidD_GetFeature(device, report, report.Length);
}
