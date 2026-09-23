namespace Islet.Native;

/// <summary>
/// Перехват оконных сообщений, которых WinUI наружу не отдаёт: глобальная
/// горячая клавиша, минимальный размер окна и всё, что касается системной рамки.
/// </summary>
internal sealed unsafe class WindowHost : IDisposable
{
    private const nuint SubclassId = 1;
    private const int WM_SETTEXT = 0x000C;
    private const int WM_NCPAINT = 0x0085;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const nint MA_NOACTIVATE = 3;

    private readonly nint _hwnd;
    // Делегат обязан жить столько же, сколько подкласс, иначе сборщик мусора
    // освободит его, и следующее сообщение окна уронит процесс.
    private readonly Win32.SUBCLASSPROC _proc;
    private readonly HashSet<int> _hotkeys = [];

    public event Action<int>? HotkeyPressed;
    /// <summary>Содержимое буфера обмена поменялось (после <see cref="ListenClipboard"/>).</summary>
    public event Action? ClipboardChanged;

    private bool _clipboardListening;

    /// <summary>true — щелчок по окну не активирует его (WM_MOUSEACTIVATE → MA_NOACTIVATE).</summary>
    public Func<bool>? NoActivate { get; set; }

    public void ListenClipboard(bool listen)
    {
        if (listen == _clipboardListening) return;
        _clipboardListening = listen;
        if (listen) Win32.AddClipboardFormatListener(_hwnd);
        else Win32.RemoveClipboardFormatListener(_hwnd);
    }

    public WindowHost(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        Win32.SetWindowSubclass(hwnd, _proc, SubclassId, 0);
    }

    /// <summary>
    /// Переставить наш обработчик в начало цепочки. WinUI добавляет свои
    /// подклассы позже нашего и, будучи снаружи, возвращает рамочные стили
    /// после нашей чистки. Вызывать после первой активации окна.
    /// </summary>
    public void BecomeOutermost()
    {
        Win32.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        Win32.SetWindowSubclass(_hwnd, _proc, SubclassId, 0);
    }

    public bool RegisterHotkey(int id, uint modifiers, uint vk)
    {
        if (!Win32.RegisterHotKey(_hwnd, id, modifiers | Win32.MOD_NOREPEAT, vk))
            return false;
        _hotkeys.Add(id);
        return true;
    }

    public void UnregisterHotkey(int id)
    {
        if (_hotkeys.Remove(id))
            Win32.UnregisterHotKey(_hwnd, id);
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        switch (msg)
        {
            case Win32.WM_NCCALCSIZE when wParam != 0:
                // Вся площадь окна — клиентская: системной рамке негде рисоваться.
                return 0;

            // Эти три сообщения DefWindowProc обрабатывает, рисуя классический
            // заголовок прямо поверх окна, — та самая белая полоса с крестиком.
            case WM_NCPAINT:
                return 0;
            case WM_NCACTIVATE:
                // lParam = -1: состояние активности меняется, но заголовок не перерисовывается.
                return Win32.DefSubclassProc(hWnd, msg, wParam, -1);
            case WM_SETTEXT:
                return Win32.WithoutVisibleStyle(hWnd, () => Win32.DefSubclassProc(hWnd, msg, wParam, lParam));

            case Win32.WM_STYLECHANGING:
            {
                // WinUI при показе окна возвращает WS_DLGFRAME/WS_SYSMENU — срезаем
                // уже после всех внутренних обработчиков.
                var result = Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
                Win32.SanitizeStyleChange((int)wParam, lParam);
                return result;
            }

            case Win32.WM_ERASEBKGND:
                Win32.EraseTransparent(hWnd, wParam);
                return 1;

            case WM_MOUSEACTIVATE when NoActivate?.Invoke() == true:
                return MA_NOACTIVATE;

            case Win32.WM_CLIPBOARDUPDATE:
                ClipboardChanged?.Invoke();
                return 0;

            case Win32.WM_HOTKEY:
                HotkeyPressed?.Invoke((int)wParam);
                return 0;

            case Win32.WM_GETMINMAXINFO:
            {
                // Windows не даёт окну быть ниже высоты заголовка, а свёрнутый
                // островок — полоска в несколько пикселей.
                var result = Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
                var mmi = (Win32.MINMAXINFO*)lParam;
                mmi->ptMinTrackSize.X = 1;
                mmi->ptMinTrackSize.Y = 1;
                return result;
            }
        }

        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        ListenClipboard(false);
        foreach (var id in _hotkeys)
            Win32.UnregisterHotKey(_hwnd, id);
        _hotkeys.Clear();
        Win32.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }
}
