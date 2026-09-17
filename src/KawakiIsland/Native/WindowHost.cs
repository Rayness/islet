namespace KawakiIsland.Native;

/// <summary>
/// Перехват оконных сообщений, которых WinUI наружу не отдаёт: глобальная
/// горячая клавиша и минимальный размер окна.
/// </summary>
internal sealed unsafe class WindowHost : IDisposable
{
    private const nuint SubclassId = 1;

    private readonly nint _hwnd;
    // Делегат обязан жить столько же, сколько подкласс, иначе сборщик мусора
    // освободит его, и следующее сообщение окна уронит процесс.
    private readonly Win32.SUBCLASSPROC _proc;
    private readonly HashSet<int> _hotkeys = [];

    public event Action<int>? HotkeyPressed;

    public WindowHost(nint hwnd)
    {
        _hwnd = hwnd;
        _proc = WndProc;
        Win32.SetWindowSubclass(hwnd, _proc, SubclassId, 0);
    }

    public bool RegisterHotkey(int id, uint modifiers, uint vk)
    {
        if (!Win32.RegisterHotKey(_hwnd, id, modifiers | Win32.MOD_NOREPEAT, vk))
            return false;
        _hotkeys.Add(id);
        return true;
    }

    private nint WndProc(nint hWnd, uint msg, nint wParam, nint lParam, nuint id, nuint data)
    {
        switch (msg)
        {
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
        foreach (var id in _hotkeys)
            Win32.UnregisterHotKey(_hwnd, id);
        _hotkeys.Clear();
        Win32.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
    }
}
