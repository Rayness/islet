using System.Runtime.InteropServices;

namespace Islet.Native;

internal static unsafe partial class Win32
{
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_ERASEBKGND = 0x0014;
    public const int WM_STYLECHANGING = 0x007C;
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_HOTKEY = 0x0312;

    public const uint MOD_ALT = 0x1;
    public const uint MOD_CONTROL = 0x2;
    public const uint MOD_SHIFT = 0x4;
    public const uint MOD_WIN = 0x8;
    public const uint MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_BORDER_COLOR = 34;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Covers(RECT other) =>
            Left <= other.Left && Top <= other.Top && Right >= other.Right && Bottom >= other.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate nint SUBCLASSPROC(nint hWnd, uint uMsg, nint wParam, nint lParam, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    public static extern bool SetWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass, nuint dwRefData);

    [DllImport("comctl32.dll")]
    public static extern bool RemoveWindowSubclass(nint hWnd, SUBCLASSPROC pfnSubclass, nuint uIdSubclass);

    [DllImport("comctl32.dll")]
    public static extern nint DefSubclassProc(nint hWnd, uint uMsg, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(nint hWnd, int id);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hWnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int attrValue, int attrSize);

    public static double GetScale(nint hWnd) => GetDpiForWindow(hWnd) / 96.0;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const long WS_EX_DLGMODALFRAME = 0x00000001L;
    private const long WS_EX_WINDOWEDGE = 0x00000100L;

    private static readonly nint HWND_TOPMOST = -1;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    private const int DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public int fEnable;
        public nint hRgnBlur;
        public int fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(nint hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [DllImport("gdi32.dll")]
    private static extern nint CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    private static extern nint GetStockObject(int i);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern int FillRect(nint hDC, ref RECT lprc, nint hbr);

    /// <summary>
    /// Голое всплывающее окно: без заголовка и системной рамки (она и давала
    /// белую обводку), не в панели задач и не в Alt+Tab, поверх всех окон.
    /// Скругление DWM выключаем — форму рисует сам островок.
    /// </summary>
    public static void MakeBareTopmostPopup(nint hWnd)
    {
        var style = (long)GetWindowLongPtr(hWnd, GWL_STYLE);
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        style |= WS_POPUP;
        SetWindowLongPtr(hWnd, GWL_STYLE, (nint)style);

        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        ex &= ~(WS_EX_APPWINDOW | WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE);
        ex |= WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)ex);

        var corner = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hWnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        var border = unchecked((int)DWMWA_COLOR_NONE);
        DwmSetWindowAttribute(hWnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));

        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_FRAMECHANGED);
    }

    private const long FrameStyles = WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
    private const long FrameExStyles = WS_EX_APPWINDOW | WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE;

    [StructLayout(LayoutKind.Sequential)]
    private struct STYLESTRUCT
    {
        public uint styleOld;
        public uint styleNew;
    }

    /// <summary>Обработчик WM_STYLECHANGING: срезает рамочные стили из нового значения.</summary>
    public static void SanitizeStyleChange(int which, nint lParam)
    {
        var ss = (STYLESTRUCT*)lParam;
        if (which == GWL_STYLE)
            ss->styleNew = (uint)((ss->styleNew & ~FrameStyles) | WS_POPUP);
        else if (which == GWL_EXSTYLE)
            ss->styleNew = (uint)((ss->styleNew & ~FrameExStyles) | WS_EX_TOOLWINDOW);
    }

    private const long WS_VISIBLE = 0x10000000L;

    /// <summary>
    /// Выполнить с временно снятым WS_VISIBLE: DefWindowProc тогда не рисует
    /// заголовок (так делает и WinForms/WPF для WM_SETTEXT без рамки).
    /// </summary>
    public static nint WithoutVisibleStyle(nint hWnd, Func<nint> action)
    {
        var style = (long)GetWindowLongPtr(hWnd, GWL_STYLE);
        if ((style & WS_VISIBLE) == 0) return action();
        SetWindowLongPtr(hWnd, GWL_STYLE, (nint)(style & ~WS_VISIBLE));
        try { return action(); }
        finally { SetWindowLongPtr(hWnd, GWL_STYLE, (nint)((long)GetWindowLongPtr(hWnd, GWL_STYLE) | WS_VISIBLE)); }
    }

    /// <summary>Вернуть окно наверх, если кто-то снял с него «поверх всех».</summary>
    public static bool EnsureTopmost(nint hWnd)
    {
        if ((GetWindowLongPtr(hWnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0)
            return false;
        SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        return true;
    }

    /// <summary>
    /// Прозрачность по пикселям для окна без WS_EX_LAYERED: размытие «позади»
    /// с регионом за пределами окна включает у DWM альфа-канал поверхности,
    /// а само размытие никуда не попадает.
    /// </summary>
    public static void EnablePerPixelTransparency(nint hWnd)
    {
        var region = CreateRectRgn(-2, -2, -1, -1);
        var bb = new DWM_BLURBEHIND { dwFlags = 0x1 | 0x2, fEnable = 1, hRgnBlur = region };
        DwmEnableBlurBehindWindow(hWnd, ref bb);
        DeleteObject(region);
    }

    /// <summary>Фон для WM_ERASEBKGND: чёрный GDI в альфа-поверхности = прозрачный, без вспышки при ресайзе.</summary>
    public static void EraseTransparent(nint hWnd, nint hdc)
    {
        const int BLACK_BRUSH = 4;
        if (GetClientRect(hWnd, out var rect))
            FillRect(hdc, ref rect, GetStockObject(BLACK_BRUSH));
    }

    /// <summary>
    /// Окно на весь монитор, на котором стоит островок: игра, видео во весь экран, презентация.
    /// Рабочий стол тоже занимает весь монитор, поэтому его классы исключены.
    /// </summary>
    public static bool IsFullscreenOnSameMonitor(nint candidate, nint island)
    {
        if (candidate == 0 || candidate == island || !IsWindow(candidate))
            return false;

        var cls = GetClassName(candidate);
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            return false;

        // Развёрнутое окно при автоскрытии панели задач тоже закрывает весь
        // монитор, но это обычная работа, а не игра. У настоящего полноэкранного
        // окна нет заголовка.
        if (IsZoomed(candidate) || (GetWindowLongPtr(candidate, GWL_STYLE) & WS_CAPTION) == WS_CAPTION)
            return false;

        var monitor = MonitorFromWindow(candidate, MONITOR_DEFAULTTONEAREST);
        if (monitor != MonitorFromWindow(island, MONITOR_DEFAULTTONEAREST))
            return false;

        var info = new MONITORINFO { cbSize = sizeof(MONITORINFO) };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(candidate, out var rect))
            return false;

        return rect.Covers(info.rcMonitor);
    }

    private static string GetClassName(nint hWnd)
    {
        var buffer = stackalloc char[256];
        var len = GetClassName(hWnd, buffer, 256);
        return len > 0 ? new string(buffer, 0, len) : "";
    }

    // --- Картинки ---

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public nint bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll")]
    public static extern int GetObject(nint h, int c, out BITMAP pv);

    [DllImport("gdi32.dll")]
    public static extern int GetDIBits(nint hdc, nint hbm, uint start, uint cLines, byte* lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint ho);

    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    // --- Буфер обмена, ввод, система ---

    public const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")]
    public static extern bool MessageBeep(uint uType);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint RegisterClipboardFormat(string lpszFormat);

    [DllImport("user32.dll")]
    public static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint hWndNewOwner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint hMem);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint hMem);

    /// <summary>DWORD из формата буфера; -1 — не прочитался.</summary>
    public static long ReadClipboardDword(uint format)
    {
        if (!OpenClipboard(0)) return -1;
        try
        {
            var handle = GetClipboardData(format);
            if (handle == 0 || GlobalSize(handle) < 4) return -1;
            var ptr = GlobalLock(handle);
            if (ptr == 0) return -1;
            try { return *(uint*)ptr; }
            finally { GlobalUnlock(handle); }
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll")]
    public static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    public static extern bool LockWorkStation();

    [DllImport("powrprof.dll")]
    public static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHEmptyRecycleBin(nint hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);

    /// <summary>Сообщить всем окнам, что поменялась системная настройка (тема).</summary>
    public static void BroadcastSettingChange(string area)
    {
        const uint WM_SETTINGCHANGE = 0x001A;
        const uint SMTO_ABORTIFHUNG = 0x0002;
        SendMessageTimeout(0xFFFF, WM_SETTINGCHANGE, 0, area, SMTO_ABORTIFHUNG, 200, out _);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public nint dwExtraInfo;
    }

    // INPUT с союзом: берём размер самого большого члена (MOUSEINPUT), иначе SendInput отвергнет cbSize.
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public KEYBDINPUT ki;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT* pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>Ctrl+V в окно с фокусом. Зажатые человеком модификаторы сначала отпускаем.</summary>
    public static void SendCtrlV()
    {
        const uint INPUT_KEYBOARD = 1, KEYUP = 0x2;
        const ushort VK_CONTROL = 0x11, VK_V = 0x56, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_LWIN = 0x5B;
        var inputs = stackalloc INPUT[8];
        var n = 0;
        foreach (var held in new[] { VK_SHIFT, VK_MENU, VK_LWIN })
        {
            if (GetAsyncKeyState(held) < 0)
                inputs[n++] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = held, dwFlags = KEYUP } };
        }
        inputs[n++] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL } };
        inputs[n++] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V } };
        inputs[n++] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYUP } };
        inputs[n++] = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYUP } };
        SendInput((uint)n, inputs, sizeof(INPUT));
    }

    [DllImport("user32.dll")]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    private const long WS_EX_NOACTIVATE = 0x08000000L;

    /// <summary>
    /// Щелчок по окну не забирает фокус. Свёрнутому островку (полоска, капсула,
    /// пик уведомления) фокус не нужен: крестик на пике не должен уводить
    /// клавиатуру из окна, где человек печатал.
    /// </summary>
    public static void SetNoActivate(nint hWnd, bool noActivate)
    {
        var ex = (long)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
        var next = noActivate ? ex | WS_EX_NOACTIVATE : ex & ~WS_EX_NOACTIVATE;
        if (next != ex)
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, (nint)next);
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    /// <summary>Окно этого же процесса (всплывающие меню островка, окно настроек).</summary>
    public static bool IsOwnWindow(nint hWnd) =>
        GetWindowThreadProcessId(hWnd, out var pid) != 0 && pid == (uint)Environment.ProcessId;
}
