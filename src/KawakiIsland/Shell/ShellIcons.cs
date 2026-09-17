using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using KawakiIsland.Native;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace KawakiIsland.Shell;

/// <summary>
/// Иконки из оболочки Windows: те же, что показывают Пуск и Проводник.
/// Принимает любое «имя для разбора»: путь к файлу или папке,
/// <c>shell:AppsFolder\&lt;id&gt;</c> для приложения.
/// </summary>
internal static class ShellIcons
{
    // Кешируем и промахи: иначе битый путь дёргал бы оболочку на каждую букву запроса.
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Вызывать с UI-потока: WriteableBitmap создаётся на нём.</summary>
    public static async Task<ImageSource?> GetAsync(string source, int size)
    {
        var key = $"{size}|{source}";
        if (Cache.TryGetValue(key, out var cached))
            return cached;

        ImageSource? image = null;
        if (source.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
        {
            image = new BitmapImage(new Uri(source)) { DecodePixelWidth = size, DecodePixelHeight = size };
        }
        else
        {
            var pixels = await StaWorker.Shell.Run(() => Extract(source, size));
            if (pixels is { } px)
            {
                var bitmap = new WriteableBitmap(px.Width, px.Height);
                using (var stream = bitmap.PixelBuffer.AsStream())
                    stream.Write(px.Bgra);
                bitmap.Invalidate();
                image = bitmap;
            }
        }

        Cache[key] = image;
        return image;
    }

    private readonly record struct Pixels(int Width, int Height, byte[] Bgra);

    private static Pixels? Extract(string parsingName, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(parsingName, 0, ref iid, out var factory) < 0 || factory is null)
            return null;

        nint hbm = 0;
        try
        {
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_ICONONLY, out hbm) < 0 || hbm == 0)
                return null;
            return ReadBitmap(hbm);
        }
        finally
        {
            if (hbm != 0) Win32.DeleteObject(hbm);
            Marshal.ReleaseComObject(factory);
        }
    }

    private static unsafe Pixels? ReadBitmap(nint hbm)
    {
        if (Win32.GetObject(hbm, sizeof(Win32.BITMAP), out var bm) == 0)
            return null;

        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
        var header = new Win32.BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(Win32.BITMAPINFOHEADER),
            biWidth = w,
            biHeight = -h, // отрицательная высота = строки сверху вниз
            biPlanes = 1,
            biBitCount = 32,
        };

        var data = new byte[w * h * 4];
        var hdc = Win32.GetDC(0);
        try
        {
            fixed (byte* p = data)
            {
                if (Win32.GetDIBits(hdc, hbm, 0, (uint)h, p, ref header, 0) == 0)
                    return null;
            }
        }
        finally
        {
            Win32.ReleaseDC(0, hdc);
        }

        // Старые иконки без альфа-канала приходят с нулевой альфой — это не
        // прозрачность, а её отсутствие.
        var hasAlpha = false;
        for (var i = 3; i < data.Length; i += 4)
        {
            if (data[i] != 0) { hasAlpha = true; break; }
        }
        if (!hasAlpha)
        {
            for (var i = 3; i < data.Length; i += 4)
                data[i] = 255;
        }

        return new Pixels(w, h, data);
    }

    private const int SIIGBF_ICONONLY = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out nint phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        nint pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? ppv);
}
