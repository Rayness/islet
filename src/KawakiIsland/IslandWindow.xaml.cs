using System.Collections.ObjectModel;
using KawakiIsland.Native;
using KawakiIsland.Pins;
using KawakiIsland.Search;
using KawakiIsland.Shell;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using VirtualKey = Windows.System.VirtualKey;

namespace KawakiIsland;

/// <summary>
/// Островок у верхнего края экрана.
///
/// ТРИ СОСТОЯНИЯ:
/// • Свёрнут — полоска. Ничего не закрывает и фокус не берёт.
/// • Раскрыт наведением — курсор у полоски. Фокус по-прежнему у того окна,
///   где человек работал; увёл курсор — островок свернулся.
/// • Закреплён — горячей клавишей или кликом в поиск. Держит фокус и
///   сворачивается, только когда фокус ушёл: Esc, запуск, клик мимо.
///
/// ФОРМА. Окно прозрачное, островок — пилюля (Border) внутри него. Анимируется
/// пилюля, а не окно: изменение размера окна на каждом кадре заставляет DWM
/// перерисовывать всё и даёт мигание. Окно меняет размер только на границах
/// анимации: при росте — сразу до конечного, при сжатии — после неё.
/// </summary>
public sealed partial class IslandWindow : Window
{
    private const int HotkeyId = 1;
    private const uint HotkeyModifiers = Win32.MOD_CONTROL | Win32.MOD_ALT;
    private const uint HotkeyKey = Win32.VK_SPACE;
    private const string HotkeyLabel = "Ctrl + Alt + Пробел";

    private const double CollapsedWidth = 160;
    private const double CollapsedHeight = 8;
    private const double ExpandedWidth = 680;
    private const double BarHeight = 56;
    private const double RowHeight = 52;
    private const int MaxRows = 8;
    private const double ListBottomPadding = 10;
    private const double MaxCornerRadius = 28;
    private const double TopMargin = 8;
    private const int AnimationMs = 220;
    private const int HoverLeaveDelayMs = 350;
    private const int TopmostCheckEveryTicks = 10;

    private enum Mode { Collapsed, Hover, Pinned }

    private readonly record struct SizeD(double Width, double Height);

    private readonly nint _hwnd;
    private readonly WindowHost _host;
    private readonly SearchService _search = new();
    private readonly PinStore _pins = new();
    private readonly ObservableCollection<ResultItem> _results = [];

    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _debounceTimer;

    private Mode _mode = Mode.Collapsed;
    private int _pollTicks;
    private SizeD _windowSize;
    private int _openFlyouts;
    private long _pointerLeftAt;
    private bool _hiddenForFullscreen;
    private int _searchGeneration;
    private nint _previousForeground;

    private SizeD _current = new(CollapsedWidth, CollapsedHeight);
    private SizeD _animationFrom;
    private SizeD _animationTo;
    private long _animationStart;

    public IslandWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Перехват сообщений — раньше настройки окна: он чистит стиль, который та меняет.
        _host = new WindowHost(_hwnd);
        ConfigureAppWindow();

        _host.HotkeyPressed += OnHotkey;
        var hotkeyOk = _host.RegisterHotkey(HotkeyId, HotkeyModifiers, HotkeyKey);
        HotkeyInfo.Text = hotkeyOk ? $"Открыть: {HotkeyLabel}" : $"{HotkeyLabel} занято другой программой";

        ResultsList.ItemsSource = _results;

        var queue = DispatcherQueue;
        _pollTimer = CreateTimer(queue, 100, OnPoll);
        _animationTimer = CreateTimer(queue, 8, OnAnimationFrame);
        _clockTimer = CreateTimer(queue, 1000, UpdateClock);
        _debounceTimer = CreateTimer(queue, 140, () =>
        {
            _debounceTimer!.Stop();
            _ = RunFullSearchAsync();
        });

        MainMenu.Opened += (_, _) => { _openFlyouts++; AutoStartItem.IsChecked = AutoStart.IsEnabled; };
        MainMenu.Closed += (_, _) => _openFlyouts--;

        _pins.Changed += RenderPins;
        _pins.Load();
        RenderPins();

        UpdateClock();
        ApplyPill(_current);
        ResizeWindow(_current);

        Activated += OnActivated;
        Closed += OnClosed;

        _pollTimer.Start();
        _clockTimer.Start();
        _search.WarmUp();
    }

    private static DispatcherQueueTimer CreateTimer(DispatcherQueue queue, int intervalMs, Action tick)
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        timer.Tick += (_, _) => tick();
        return timer;
    }

    // ------------------------------------------------------------------
    // Окно
    // ------------------------------------------------------------------

    private void ConfigureAppWindow()
    {
        AppWindow.Title = "Kawaki Island";
        TrySetIcon();
        try { AppWindow.IsShownInSwitchers = false; }
        catch { /* старые сборки Windows: окно просто будет видно в Alt+Tab */ }

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        // Без этого Windows не даёт окну стать ниже заголовка.
        presenter.PreferredMinimumWidth = 1;
        presenter.PreferredMinimumHeight = 1;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        // После SetPresenter: выставленное до него «поверх всех» теряется.
        presenter.IsAlwaysOnTop = true;

        // Presenter всё равно оставляет WS_CAPTION (отсюда белая обводка и
        // квадратные углы) — дочищаем стиль руками.
        Win32.MakeBareTopmostPopup(_hwnd);
        Win32.EnablePerPixelTransparency(_hwnd);
        SystemBackdrop = new TransparentBackdrop();
    }

    private void TrySetIcon()
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "kawaki.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
    }

    /// <summary>Окно по центру у верхнего края, размер в DIP. Одинаковый размер повторно не применяется.</summary>
    private void ResizeWindow(SizeD size, bool force = false)
    {
        if (!force && size == _windowSize) return;
        _windowSize = size;

        var scale = Win32.GetScale(_hwnd);
        var area = DisplayArea.Primary.WorkArea;
        var width = (int)Math.Ceiling(size.Width * scale);
        var height = (int)Math.Ceiling(size.Height * scale);
        var x = area.X + (area.Width - width) / 2;
        var y = area.Y + (int)Math.Round(TopMargin * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private void ApplyPill(SizeD size)
    {
        Pill.Width = size.Width;
        Pill.Height = size.Height;
        Pill.CornerRadius = new CornerRadius(Math.Min(size.Height / 2, MaxCornerRadius));
    }

    private SizeD TargetSize()
    {
        if (_mode == Mode.Collapsed)
            return new(CollapsedWidth, CollapsedHeight);

        var rows = Math.Min(_results.Count, MaxRows);
        var height = BarHeight + (rows > 0 ? rows * RowHeight + ListBottomPadding : 0);
        return new(ExpandedWidth, height);
    }

    private void AnimateToTarget()
    {
        var target = TargetSize();
        if (target == _animationTo && _animationTimer.IsRunning) return;

        _animationFrom = _current;
        _animationTo = target;
        _animationStart = Environment.TickCount64;

        // Окно сразу вмещает и текущую пилюлю, и конечную — дальше анимация идёт внутри него.
        ResizeWindow(new(
            Math.Max(_current.Width, target.Width),
            Math.Max(_current.Height, target.Height)));

        if (!_animationTimer.IsRunning)
            _animationTimer.Start();
    }

    private void OnAnimationFrame()
    {
        var t = Math.Clamp((Environment.TickCount64 - _animationStart) / (double)AnimationMs, 0, 1);
        // easeOutQuint: быстрый старт, мягкая посадка — как у «острова» в iOS.
        var eased = 1 - Math.Pow(1 - t, 5);
        _current = new(
            _animationFrom.Width + (_animationTo.Width - _animationFrom.Width) * eased,
            _animationFrom.Height + (_animationTo.Height - _animationFrom.Height) * eased);
        ApplyPill(_current);

        if (t >= 1)
        {
            _animationTimer.Stop();
            ResizeWindow(_animationTo);
        }
    }

    // ------------------------------------------------------------------
    // Состояния
    // ------------------------------------------------------------------

    private void SetMode(Mode mode, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        if (_mode == mode) return;
        Log.Write($"mode {_mode} -> {mode} ({caller})");
        var wasCollapsed = _mode == Mode.Collapsed;
        _mode = mode;
        _pointerLeftAt = 0;

        if (mode == Mode.Collapsed)
        {
            _searchGeneration++;
            _debounceTimer.Stop();
            SearchBox.Text = "";
            _results.Clear();
            Panel.IsHitTestVisible = false;
            Panel.Opacity = 0;
        }
        else if (wasCollapsed)
        {
            _pins.ReloadIfChanged();
            Panel.IsHitTestVisible = true;
            Panel.Opacity = 1;
        }

        AnimateToTarget();
    }

    private void OpenPinned()
    {
        if (_hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            AppWindow.Show(activateWindow: false);
        }

        _search.WarmUp();
        SetMode(Mode.Pinned);
        Win32.SetForegroundWindow(_hwnd);
        Activate();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    /// <summary>Свернуть и вернуть клавиатуру тому окну, где человек был до островка.</summary>
    private void Dismiss(bool restoreFocus)
    {
        SetMode(Mode.Collapsed);
        if (restoreFocus && _previousForeground != 0 && Win32.IsWindow(_previousForeground))
            Win32.SetForegroundWindow(_previousForeground);
        _previousForeground = 0;
    }

    private void OnHotkey(int id)
    {
        if (id != HotkeyId) return;

        if (_mode == Mode.Pinned && Win32.GetForegroundWindow() == _hwnd)
        {
            Dismiss(restoreFocus: true);
            return;
        }

        var foreground = Win32.GetForegroundWindow();
        _previousForeground = foreground == _hwnd ? 0 : foreground;
        OpenPinned();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Log.Write($"activation {args.WindowActivationState}, mode {_mode}, flyouts {_openFlyouts}");
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // Меню открыто — фокус ушёл в его всплывающее окно, это не уход с островка.
            if (_mode == Mode.Pinned && _openFlyouts == 0)
                Dismiss(restoreFocus: false);
        }
        else if (_mode == Mode.Hover)
        {
            // Кликнули по раскрытому наведением островку — теперь он в работе.
            SetMode(Mode.Pinned);
        }
    }

    private void OnPoll()
    {
        UpdateFullscreenState();
        if (_hiddenForFullscreen)
            return;

        if (++_pollTicks % TopmostCheckEveryTicks == 0 && Win32.EnsureTopmost(_hwnd))
            Log.Write("topmost was lost, restored");

        if (_mode == Mode.Pinned)
            return;

        if (!Win32.GetCursorPos(out var cursor))
            return;

        var scale = Win32.GetScale(_hwnd);
        var area = DisplayArea.Primary.WorkArea;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;

        if (_mode == Mode.Collapsed)
        {
            // Зона наведения шире полоски и доходит до самого края экрана:
            // курсор, упёртый в верх, должен попадать.
            var pad = (int)(24 * scale);
            var inside = cursor.X >= pos.X - pad && cursor.X <= pos.X + size.Width + pad
                && cursor.Y >= area.Y && cursor.Y <= pos.Y + size.Height + (int)(4 * scale);
            if (inside)
                SetMode(Mode.Hover);
        }
        else if (_mode == Mode.Hover)
        {
            var pad = (int)(8 * scale);
            var inside = cursor.X >= pos.X - pad && cursor.X <= pos.X + size.Width + pad
                && cursor.Y >= area.Y && cursor.Y <= pos.Y + size.Height + pad;

            if (inside || _openFlyouts > 0)
            {
                _pointerLeftAt = 0;
            }
            else if (_pointerLeftAt == 0)
            {
                _pointerLeftAt = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - _pointerLeftAt > HoverLeaveDelayMs)
            {
                SetMode(Mode.Collapsed);
            }
        }
    }

    /// <summary>Поверх игры и видео во весь экран островок не висит.</summary>
    private void UpdateFullscreenState()
    {
        var fullscreen = Win32.IsFullscreenOnSameMonitor(Win32.GetForegroundWindow(), _hwnd);
        if (fullscreen && !_hiddenForFullscreen)
        {
            Log.Write("fullscreen window in front, hiding");
            _hiddenForFullscreen = true;
            SetMode(Mode.Collapsed);
            AppWindow.Hide();
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            Log.Write("fullscreen gone, showing");
            _hiddenForFullscreen = false;
            AppWindow.Show(activateWindow: false);
            Win32.EnsureTopmost(_hwnd);
        }
    }

    private void UpdateClock()
    {
        var text = DateTime.Now.ToString("HH:mm");
        if (ClockText.Text != text)
            ClockText.Text = text;
    }

    // ------------------------------------------------------------------
    // Поиск
    // ------------------------------------------------------------------

    private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_mode != Mode.Collapsed)
            SetMode(Mode.Pinned);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_mode == Mode.Collapsed) return;

        var query = SearchBox.Text.Trim();
        _searchGeneration++;
        _debounceTimer.Stop();

        if (query.Length == 0)
        {
            ShowResults([]);
            return;
        }

        // Приложения — сразу, из памяти; файлы — после паузы в наборе. Пока
        // файлы ищутся, прежние подходящие строки остаются: иначе список на
        // каждую букву сжимался бы и снова вырастал.
        var instant = _search.SearchInstant(query);
        var keptFiles = _results
            .Where(r => r.Kind is ResultKind.File or ResultKind.Folder && MatchesAllWords(r.Title, query))
            .ToList();
        instant.InsertRange(instant.Count - 1, keptFiles);
        ShowResults(instant);
        _debounceTimer.Start();
    }

    private static bool MatchesAllWords(string title, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));

    private async Task RunFullSearchAsync()
    {
        var query = SearchBox.Text.Trim();
        if (query.Length == 0) return;

        var generation = _searchGeneration;
        var results = await _search.SearchAsync(query);
        if (generation != _searchGeneration || _mode == Mode.Collapsed)
            return;

        ShowResults(results);
    }

    /// <summary>
    /// Точечно: совпавшие строки остаются теми же объектами (с уже загруженной
    /// иконкой и тем же контейнером), меняются только отличающиеся позиции.
    /// Clear + Add на каждую букву пересоздавал весь список — это и было миганием.
    /// </summary>
    private void ShowResults(List<ResultItem> items)
    {
        var selected = ResultsList.SelectedItem as ResultItem;
        var keepSelection = selected is not null && ResultsList.SelectedIndex > 0;

        for (var i = 0; i < items.Count; i++)
        {
            var existing = _results.FirstOrDefault(r => SameResult(r, items[i]));
            var item = existing ?? items[i];
            items[i] = item;

            if (i < _results.Count)
            {
                if (!ReferenceEquals(_results[i], item))
                {
                    var from = _results.IndexOf(item);
                    if (from > i) _results.Move(from, i);
                    else _results.Insert(i, item);
                }
            }
            else
            {
                _results.Add(item);
            }
        }
        while (_results.Count > items.Count)
            _results.RemoveAt(_results.Count - 1);

        if (_results.Count > 0)
        {
            var index = keepSelection ? _results.IndexOf(selected!) : 0;
            ResultsList.SelectedIndex = index >= 0 ? index : 0;
        }

        AnimateToTarget();
        _ = LoadIconsAsync(items);
    }

    private static bool SameResult(ResultItem a, ResultItem b) =>
        a.Kind == b.Kind && a.Title == b.Title && string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);

    private static async Task LoadIconsAsync(List<ResultItem> items)
    {
        foreach (var item in items)
        {
            if (item.Icon is null && item.IconSource is { } source)
                item.Icon = await ShellIcons.GetAsync(source, 32);
        }
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Escape:
                Dismiss(restoreFocus: true);
                e.Handled = true;
                break;

            case VirtualKey.Down:
                MoveSelection(+1);
                e.Handled = true;
                break;

            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                if (ResultsList.SelectedItem is ResultItem item)
                {
                    // Ctrl+Enter — показать файл в папке вместо открытия.
                    var ctrl = Microsoft.UI.Input.InputKeyboardSource
                        .GetKeyStateForCurrentThread(VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                    Launch(item, reveal: ctrl && item.CanReveal);
                }
                e.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_results.Count == 0) return;
        var index = Math.Clamp(ResultsList.SelectedIndex + delta, 0, _results.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(_results[index]);
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultItem item)
            Launch(item, reveal: false);
    }

    private void Launch(ResultItem item, bool reveal)
    {
        var ok = reveal ? Launcher.Reveal(item.Target)
            : item.Kind switch
            {
                ResultKind.App => Launcher.OpenApp(item.Target),
                _ => Launcher.Open(item.Target),
            };

        // Запущенное окно само заберёт фокус — возвращать его прежнему не нужно.
        if (ok)
            Dismiss(restoreFocus: false);
    }

    private void Result_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ResultItem item } element)
            return;

        var menu = NewMenu();
        menu.Items.Add(MenuItem("Открыть", "", () => Launch(item, reveal: false)));
        if (item.CanReveal)
            menu.Items.Add(MenuItem("Показать в папке", "", () => Launch(item, reveal: true)));
        if (item.CanPin && Pin.FromResult(item) is { } pin)
        {
            var add = MenuItem("Закрепить на островке", "", () => _pins.Add(pin));
            add.IsEnabled = _pins.Items.Count < PinStore.MaxPins;
            menu.Items.Add(add);
        }

        ShowMenu(menu, element, args);
    }

    // ------------------------------------------------------------------
    // Кнопки
    // ------------------------------------------------------------------

    private void RenderPins()
    {
        PinsPanel.Children.Clear();
        foreach (var pin in _pins.Items)
        {
            var glyph = new FontIcon { Glyph = "", FontSize = 16 };
            var image = new Image { Width = 20, Height = 20 };
            var content = new Grid();
            content.Children.Add(glyph);
            content.Children.Add(image);

            var button = new Button
            {
                Style = (Style)Root.Resources["IslandIconButton"],
                Content = content,
            };
            ToolTipService.SetToolTip(button, pin.Title);
            button.Click += (_, _) =>
            {
                if (pin.Launch())
                    Dismiss(restoreFocus: false);
            };
            button.ContextRequested += (s, args) =>
            {
                var menu = NewMenu();
                menu.Items.Add(MenuItem("Открепить", "", () => _pins.Remove(pin)));
                ShowMenu(menu, (FrameworkElement)s, args);
            };
            PinsPanel.Children.Add(button);

            if (pin.IconSource is { } source)
                _ = LoadPinIconAsync(source, image, glyph);
        }
    }

    private static async Task LoadPinIconAsync(string source, Image image, FontIcon glyph)
    {
        var icon = await ShellIcons.GetAsync(source, 32);
        if (icon is null) return;
        image.Source = icon;
        glyph.Visibility = Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Меню
    // ------------------------------------------------------------------

    private MenuFlyout NewMenu()
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };
        menu.Opened += (_, _) => _openFlyouts++;
        menu.Closed += (_, _) => _openFlyouts--;
        return menu;
    }

    private static MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => action();
        return item;
    }

    private static void ShowMenu(MenuFlyout menu, FrameworkElement target, ContextRequestedEventArgs args)
    {
        var options = new FlyoutShowOptions();
        if (args.TryGetPosition(target, out var point))
            options.Position = point;
        menu.ShowAt(target, options);
        args.Handled = true;
    }

    private void AutoStartItem_Click(object sender, RoutedEventArgs e)
    {
        try { AutoStart.Set(AutoStartItem.IsChecked); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Autostart: {ex.Message}"); }
    }

    private void EditPins_Click(object sender, RoutedEventArgs e)
    {
        if (Launcher.Open("notepad.exe", $"\"{PinStore.FilePath}\""))
            Dismiss(restoreFocus: false);
    }

    private void ResetPins_Click(object sender, RoutedEventArgs e) => _pins.ResetToDefaults();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _pollTimer.Stop();
        _animationTimer.Stop();
        _clockTimer.Stop();
        _debounceTimer.Stop();
        _host.Dispose();
    }
}
