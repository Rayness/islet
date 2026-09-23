using System.Collections.ObjectModel;
using System.Numerics;
using System.Text.RegularExpressions;
using Islet.Core;
using Islet.Media;
using Islet.Native;
using Islet.Pins;
using Islet.Search;
using Islet.Settings;
using Islet.Shell;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Rectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.Foundation;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Islet;

/// <summary>
/// Островок у верхнего края экрана.
///
/// РЕЖИМ — чем занят человек:
/// • Свёрнут — ничего не закрывает и фокус не берёт (окно WS_EX_NOACTIVATE).
/// • Раскрыт наведением — курсор у капсулы. Фокус по-прежнему у того окна,
///   где человек работал; увёл курсор — островок свернулся.
/// • Закреплён — горячей клавишей или кликом в поиск. Держит фокус и
///   сворачивается, только когда фокус ушёл: Esc, запуск, клик мимо.
///
/// ФОРМА — что видно, пока островок свёрнут:
/// • Полоска — когда ничего не происходит.
/// • Живая капсула — играет музыка (обложка и эквалайзер) или идёт таймер,
///   прогресс плагина. Чуть шире полоски, в ней иконка, текст и прогресс.
/// • Пик — пришло уведомление: капсула сама раскрывается в плашку с текстом,
///   держит её, пока текст читается, и сворачивается обратно. Всё пришедшее
///   остаётся в колоколе.
///
/// Анимируется пилюля, а не окно: изменение размера окна на каждом кадре
/// заставляет DWM перерисовывать всё и даёт мигание. Окно меняет размер только
/// на границах анимации: при росте — сразу до конечного, при сжатии — после неё.
/// </summary>
public sealed partial class IslandWindow : Window
{
    private const int HotkeyId = 1;

    private const double BarHeight = 56;
    private const double RowHeight = 52;
    private const double NotificationRowHeight = 56;
    private const double NotificationsHeaderHeight = 30;
    private const double RecentHeaderHeight = 26;
    private const double MediaCardHeight = 72;
    private const double ListBottomPadding = 10;
    private const double MaxCornerRadius = 28;
    private const double TopMargin = 8;
    private const double CompactHeight = 30;
    private const double PeekHeight = 58;
    private const int AnimationMs = 240;
    private const int PeekAnimationMs = 320;
    private const int TopmostCheckEveryTicks = 10;
    private const double MarqueeSpeed = 55;
    private const int MarqueePauseMs = 1400;

    private enum Mode { Collapsed, Hover, Pinned }
    private enum Shape { Stripe, Compact, Peek, Expanded }
    private enum View { Results, Notifications }
    private enum CompactKind { None, Activity, Media }

    private readonly record struct SizeD(double Width, double Height);

    private readonly nint _hwnd;
    private readonly WindowHost _host;
    private readonly App _app = App.Current;
    private readonly SearchService _search = App.Current.Search;
    private readonly PinStore _pins = App.Current.Pins;
    private readonly IslandBackdrop _backdrop = new();
    private readonly ObservableCollection<ResultItem> _results = [];
    private readonly ObservableCollection<NotificationRow> _notificationRows = [];
    private bool _activatedOnce;

    private static double ExpandedWidth => SettingsStore.Current.IslandWidth;
    private static double CollapsedWidth => SettingsStore.Current.CollapsedWidth;
    private static double CollapsedHeight => SettingsStore.Current.CollapsedHeight;
    private static double PeekWidth => Math.Min(ExpandedWidth, 500);
    /// <summary>
    /// Ширина окна постоянна — по самой широкой форме. Окно, менявшее ширину на
    /// каждом раскрытии, сдвигалось влево, и кадр, нарисованный ещё по старой
    /// раскладке, показывал капсулу не на месте: это и было мигание при наведении.
    /// Лишнее по бокам отрезает регион окна — клики мимо капсулы проходят насквозь.
    /// </summary>
    private static double WindowWidth => Math.Max(ExpandedWidth, PeekWidth);
    private static double Position => SettingsStore.Current.IslandPosition;
    private static int MaxRows => SettingsStore.Current.MaxRows;

    private readonly DispatcherQueueTimer _pollTimer;
    private readonly DispatcherQueueTimer _animationTimer;
    private readonly DispatcherQueueTimer _clockTimer;
    private readonly DispatcherQueueTimer _debounceTimer;
    private readonly DispatcherQueueTimer _peekTimer;
    private readonly DispatcherQueueTimer _peekGapTimer;
    private readonly DispatcherQueueTimer _mediaTimer;
    private readonly DispatcherQueueTimer _equalizerTimer;

    private Mode _mode = Mode.Collapsed;
    private Shape _shape = Shape.Stripe;
    private View _view = View.Results;
    private int _pollTicks;
    private SizeD _windowSize;
    private int _openFlyouts;
    private long _pointerLeftAt;
    private long _pointerEnteredAt;
    private ulong _windowDisplay;
    private bool _hiddenForFullscreen;
    private int _searchGeneration;
    private CancellationTokenSource? _searchCts;
    private nint _lastExternalForeground;

    // Область поиска: «k » → Kawaki, Backspace на пустой строке снимает.
    private SearchProvider? _scope;
    private bool _suppressTextChanged;

    // Строка, ждущая второго Enter (выключить, очистить корзину).
    private ResultItem? _confirming;
    private string _confirmingSubtitle = "";

    // Живая капсула.
    private CompactKind _compactKind = CompactKind.None;
    /// <summary>Какая картинка активности стоит в капсуле сейчас.</summary>
    private string? _compactIcon;
    private double _compactWidth = 200;
    private bool _equalizerRunning;

    // Визуализатор: столбики в капсуле и в карточке, уровни — от спектра или «анимации».
    private readonly AudioSpectrum _spectrum = new();
    private readonly List<Rectangle> _capsuleBars = [];
    private readonly List<Rectangle> _cardBars = [];
    private readonly float[] _levels = new float[AudioSpectrum.MaxBands];
    private Windows.UI.Color _barColor;

    // Громкость играющего приложения.
    private bool _suppressVolume;
    private int _volumePercent = -1;
    private long _volumeFlashUntil;
    private DispatcherQueueTimer? _volumeFlashTimer;

    // Где капсула в окне (пиксели) — для зоны наведения и региона окна.
    private int _pillLeftPx;
    private int _pillWidthPx;
    private int _pillHeightPx;

    // Пик уведомления.
    private readonly Queue<IsletNotification> _peekQueue = new();
    private IsletNotification? _peek;
    private bool _peekHolding;
    private bool _peekPaused;
    private Storyboard? _marquee;

    private SizeD _current = new(CollapsedWidth, CollapsedHeight);
    private SizeD _animationFrom;
    private SizeD _animationTo;
    private long _animationStart;
    private int _animationDuration = AnimationMs;

    private Visual? _contentVisual;
    private CompositionRoundedRectangleGeometry? _contentClip;

    public IslandWindow()
    {
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // Перехват сообщений — раньше настройки окна: он чистит стиль, который та меняет.
        _host = new WindowHost(_hwnd);
        ConfigureAppWindow();

        _host.HotkeyPressed += OnHotkey;
        // Свёрнутому островку фокус не нужен: щелчок по пику не должен уводить клавиатуру.
        // Отвечаем на WM_MOUSEACTIVATE, а не переключаем WS_EX_NOACTIVATE: смена стиля
        // окна на каждом раскрытии перерисовывала его целиком.
        _host.NoActivate = () => _mode == Mode.Collapsed;
        _host.ClipboardChanged += () => _app.Clipboard.OnClipboardChanged();
        ApplyHotkey();

        ResultsList.ItemsSource = _results;
        SearchBox.SizeChanged += (_, _) => UpdateHotkeyHint();
        PlaceholderProbe.SizeChanged += (_, _) => UpdateHotkeyHint();
        HotkeyProbe.SizeChanged += (_, _) => UpdateHotkeyHint();
        NotificationsList.ItemsSource = _notificationRows;
        ResultsList.SelectionChanged += (_, _) => OnSelectionChanged();

        var queue = DispatcherQueue;
        _pollTimer = CreateTimer(queue, 100, OnPoll);
        _animationTimer = CreateTimer(queue, 8, OnAnimationFrame);
        _clockTimer = CreateTimer(queue, 1000, UpdateClock);
        _debounceTimer = CreateTimer(queue, 140, () =>
        {
            _debounceTimer!.Stop();
            _ = RunFullSearchAsync();
        });
        _peekTimer = CreateTimer(queue, 1000, OnPeekTimer);
        _peekGapTimer = CreateTimer(queue, 500, () =>
        {
            _peekGapTimer!.Stop();
            TryShowNextPeek();
        });
        _mediaTimer = CreateTimer(queue, 500, UpdateMediaProgress);
        _equalizerTimer = CreateTimer(queue, 100, OnEqualizerFrame);

        MainMenu.Opened += (_, _) => _openFlyouts++;
        MainMenu.Closed += (_, _) => _openFlyouts--;

        SetupContentClip();

        _pins.Changed += RenderPins;
        RenderPins();
        SettingsStore.Changed += ApplySettings;
        _app.Notifications.Posted += OnNotificationPosted;
        _app.Notifications.Changed += OnNotificationsChanged;
        _app.Activities.Changed += UpdateCompact;
        _app.Media.Changed += OnMediaChanged;
        ApplySettings();

        UpdateClock();
        UpdateBell();
        UpdateCompact();
        ApplyShape();
        ApplyPill(_current);
        ResizeWindow(_current);

        Activated += OnActivated;
        Closed += OnClosed;
        // Alt+F4 по закреплённому островку закрыл бы окно и оставил процесс без островка.
        AppWindow.Closing += (_, e) =>
        {
            if (_app.IsShuttingDown) return;
            e.Cancel = true;
            Dismiss(restoreFocus: true);
        };

        _pollTimer.Start();
        _search.WarmUp();
    }

    private static DispatcherQueueTimer CreateTimer(DispatcherQueue queue, int intervalMs, Action tick)
    {
        var timer = queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(intervalMs);
        timer.Tick += (_, _) => Guard.Run(tick);
        return timer;
    }

    // ------------------------------------------------------------------
    // Окно
    // ------------------------------------------------------------------

    private void ConfigureAppWindow()
    {
        AppWindow.Title = "Islet";
        TrySetIcon();
        ToolTipService.SetToolTip(MenuButton, Loc.T("Island_MenuTooltip"));
        ToolTipService.SetToolTip(BellButton, Loc.T("Island_BellTooltip"));
        ToolTipService.SetToolTip(PeekClose, Loc.T("Peek_Close"));
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
        SystemBackdrop = _backdrop;
    }

    /// <summary>
    /// Содержимое режется по скруглению капсулы на композиторе: при раскрытии
    /// строка поиска и кнопки появляются изнутри формы, а не поверх её углов.
    /// </summary>
    private void SetupContentClip()
    {
        try
        {
            _contentVisual = ElementCompositionPreview.GetElementVisual(PillContent);
            var compositor = _contentVisual.Compositor;
            _contentClip = compositor.CreateRoundedRectangleGeometry();
            _contentVisual.Clip = compositor.CreateGeometricClip(_contentClip);
        }
        catch (Exception e)
        {
            Log.Write($"content clip unavailable: {e.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Настройки
    // ------------------------------------------------------------------

    /// <summary>false — сочетание занято другой программой. Окно настроек показывает это словами.</summary>
    public bool HotkeyRegistered { get; private set; } = true;

    /// <summary>Перерегистрировать горячую клавишу из настроек. false — занята другой программой.</summary>
    public bool ApplyHotkey()
    {
        var hotkey = SettingsStore.Current.Hotkey;
        _host.UnregisterHotkey(HotkeyId);
        var ok = _host.RegisterHotkey(HotkeyId, hotkey.Modifiers, hotkey.Key);
        HotkeyRegistered = ok;
        HotkeyInfo.Text = ok ? Loc.T("Hotkey_Open", hotkey.Label) : Loc.T("Hotkey_Taken", hotkey.Label);
        HotkeyHintText.Text = hotkey.Label.Replace(" + ", " ");
        UpdateHotkeyHint();
        if (!ok) Log.Write($"hotkey {hotkey.Label} is taken");
        return ok;
    }

    /// <summary>На время записи новой комбинации в настройках старая не должна срабатывать.</summary>
    public void SuspendHotkey() => _host.UnregisterHotkey(HotkeyId);

    private Hotkey? _appliedHotkey;

    private void ApplySettings()
    {
        var s = SettingsStore.Current;
        if (_appliedHotkey is not null && _appliedHotkey != s.Hotkey)
            ApplyHotkey();
        _appliedHotkey = s.Hotkey;

        _backdrop.Glass = s.Glass;
        _surfaceBrush = CreateSurfaceBrush();
        ClockText.Visibility = s.ShowClock ? Visibility.Visible : Visibility.Collapsed;
        Panel.Width = s.IslandWidth - 2;
        PeekLayer.Width = PeekWidth - 2;
        _host.ListenClipboard(s.ClipboardHistory);

        UpdateCompact();
        UpdateBell();
        ApplyShape();
        UpdateVisualizer();
        ResizeWindow(new(Math.Max(_current.Width, TargetSize().Width), Math.Max(_current.Height, TargetSize().Height)), force: true);
        if (_mode != Mode.Collapsed)
            AnimateToTarget();
        else
            ApplyPill(_current);
    }

    private SolidColorBrush _surfaceBrush = new(ColorHelper.FromArgb(0xF5, 0x06, 0x0B, 0x0F));
    private static readonly SolidColorBrush UnreadBrush = new(ColorHelper.FromArgb(0xC8, 0x3B, 0xE5, 0xCE));

    private static SolidColorBrush CreateSurfaceBrush()
    {
        var s = SettingsStore.Current;
        var alpha = (byte)Math.Round(255 * (s.Glass ? s.SurfaceOpacity : Math.Max(s.SurfaceOpacity, 0.9)));
        return new SolidColorBrush(ColorHelper.FromArgb(alpha, 0x06, 0x0B, 0x0F));
    }

    private void TrySetIcon()
    {
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "islet.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);
    }

    /// <summary>Экран, на котором висит островок: основной или тот, где сейчас курсор.</summary>
    private static DisplayArea CurrentDisplay()
    {
        if (SettingsStore.Current.MonitorMode == "cursor" && Win32.GetCursorPos(out var cursor))
        {
            var area = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Primary);
            if (area is not null)
                return area;
        }
        return DisplayArea.Primary;
    }

    /// <summary>Окно по центру у верхнего края, размер в DIP. Одинаковый размер повторно не применяется.</summary>
    private void ResizeWindow(SizeD size, bool force = false)
    {
        var display = CurrentDisplay();
        if (!force && size == _windowSize && display.DisplayId.Value == _windowDisplay) return;
        _windowSize = size;
        _windowDisplay = display.DisplayId.Value;

        var scale = Win32.GetScale(_hwnd);
        var area = display.WorkArea;
        var windowWidth = WindowWidth;
        var width = (int)Math.Ceiling(windowWidth * scale);
        var height = (int)Math.Ceiling(size.Height * scale);
        // Положение — доля свободного места по горизонтали: у края остаётся тот же отступ, что сверху.
        var margin = (int)Math.Round(TopMargin * scale);
        var free = Math.Max(0, area.Width - width - 2 * margin);
        var x = area.X + margin + (int)Math.Round(free * Position);
        var y = area.Y + (int)Math.Round(TopMargin * scale);
        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        // Регион — всё, что капсула займёт за анимацию; точная форма — когда она встанет.
        var left = (int)Math.Floor((windowWidth - size.Width) * Position * scale);
        var right = left + (int)Math.Ceiling(size.Width * scale);
        var radius = (int)Math.Round(Math.Min(size.Height / 2, MaxCornerRadius) * scale * 2);
        Win32.SetRoundRegion(_hwnd, left, 0, right + 1, height + 1, radius);
        ApplyPill(_current);
    }

    private void ApplyPill(SizeD size)
    {
        var radius = Math.Min(size.Height / 2, MaxCornerRadius);
        // Точка капсулы, стоящая на месте при любом её размере, — та же доля ширины, что у окна:
        // по центру капсула растёт в обе стороны, у левого края — вправо, у правого — влево.
        var offset = (WindowWidth - size.Width) * Position;
        Pill.Margin = new Thickness(offset, 0, 0, 0);
        Pill.Width = size.Width;
        Pill.Height = size.Height;
        Pill.CornerRadius = new CornerRadius(radius);

        // Слои шире капсулы стоят по её центру: раскрытие открывает их из середины.
        var inner = Math.Max(0, size.Width - 2);
        CenterLayer(Panel, inner);
        CenterLayer(PeekLayer, inner);
        CenterLayer(CompactLayer, inner);
        if (_contentClip is not null)
        {
            _contentClip.Size = new Vector2((float)inner, (float)Math.Max(0, size.Height - 2));
            var r = (float)Math.Max(0, radius - 1);
            _contentClip.CornerRadius = new Vector2(r, r);
        }

        // Стекло живёт в пикселях окна, пилюля — в DIP по центру сверху.
        var scale = (float)Win32.GetScale(_hwnd);
        var window = new Vector2((float)WindowWidth, (float)_windowSize.Height) * scale;
        var pill = new Vector2((float)size.Width, (float)size.Height) * scale;
        _backdrop.UpdateShape(window, new Vector2((float)(offset * scale), 0), pill, (float)radius * scale);
        _pillLeftPx = (int)(offset * scale);
        _pillWidthPx = (int)pill.X;
        _pillHeightPx = (int)pill.Y;
    }

    private void PeekBodyHost_SizeChanged(object sender, SizeChangedEventArgs e) =>
        PeekBodyHost.Clip = new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };

    private static void CenterLayer(FrameworkElement layer, double inner)
    {
        var offset = (inner - layer.Width) / 2;
        layer.Margin = new Thickness(offset, 0, offset, 0);
    }

    private SizeD TargetSize() => _shape switch
    {
        Shape.Stripe => new(CollapsedWidth, CollapsedHeight),
        Shape.Compact => new(_compactWidth, CompactHeight),
        Shape.Peek => new(PeekWidth, PeekHeight),
        _ => new(ExpandedWidth, ExpandedHeight()),
    };

    private double ExpandedHeight()
    {
        var height = BarHeight;
        if (MediaCard.Visibility == Visibility.Visible)
            height += MediaCardHeight;
        if (RecentHeader.Visibility == Visibility.Visible)
            height += RecentHeaderHeight;

        if (_view == View.Notifications)
        {
            height += NotificationsHeaderHeight;
            var rows = Math.Min(_notificationRows.Count, MaxRows);
            height += rows > 0 ? rows * NotificationRowHeight + ListBottomPadding : 40;
        }
        else
        {
            var rows = Math.Min(_results.Count, MaxRows);
            if (rows > 0) height += rows * RowHeight + ListBottomPadding;
        }
        return height;
    }

    private void AnimateToTarget()
    {
        var target = TargetSize();
        if (target == _animationTo && _animationTimer.IsRunning) return;
        if (target == _current && !_animationTimer.IsRunning)
        {
            ResizeWindow(target);
            return;
        }

        _animationFrom = _current;
        _animationTo = target;
        _animationStart = Environment.TickCount64;
        _animationDuration = _shape == Shape.Peek ? PeekAnimationMs : AnimationMs;

        // Окно сразу вмещает и текущую пилюлю, и конечную — дальше анимация идёт внутри него.
        ResizeWindow(new(
            Math.Max(_current.Width, target.Width),
            Math.Max(_current.Height, target.Height)));

        if (!_animationTimer.IsRunning)
            _animationTimer.Start();
    }

    private void OnAnimationFrame()
    {
        var t = Math.Clamp((Environment.TickCount64 - _animationStart) / (double)_animationDuration, 0, 1);
        // easeOutQuint: быстрый старт, мягкая посадка — как у «острова» в iOS.
        var eased = 1 - Math.Pow(1 - t, 5);
        _current = new(
            _animationFrom.Width + (_animationTo.Width - _animationFrom.Width) * eased,
            _animationFrom.Height + (_animationTo.Height - _animationFrom.Height) * eased);
        ApplyPill(_current);

        if (t >= 1)
        {
            _animationTimer.Stop();
            ResizeWindow(_animationTo, force: true);
        }
    }

    // ------------------------------------------------------------------
    // Форма капсулы
    // ------------------------------------------------------------------

    private Shape DesiredShape() =>
        _mode != Mode.Collapsed ? Shape.Expanded
        : _peek is not null ? Shape.Peek
        : _compactKind != CompactKind.None ? Shape.Compact
        : Shape.Stripe;

    /// <summary>Какой слой виден, цвет полоски, эквалайзер. Размер — отдельно, через AnimateToTarget.</summary>
    private void ApplyShape()
    {
        _shape = DesiredShape();

        SetLayer(Panel, _shape == Shape.Expanded);
        SetLayer(PeekLayer, _shape == Shape.Peek);
        SetLayer(CompactLayer, _shape == Shape.Compact);

        var s = SettingsStore.Current;
        var unread = _app.Notifications.UnreadCount > 0 && s.NotifyMode != "off";
        // Непрочитанное подсвечивает полоску акцентом — как метка на свёрнутом островке Kawaki.
        var background = _shape == Shape.Stripe && unread ? UnreadBrush : _surfaceBrush;
        if (!ReferenceEquals(Pill.Background, background))
            Pill.Background = background;
        Pill.Opacity = _shape == Shape.Stripe && s.HideCollapsed && !unread ? 0 : 1;

        UpdateVisualizer();
    }

    /// <summary>
    /// Появляется слой плавно, уходит — сразу. Два слоя, плавно сменяющие друг друга,
    /// на пару кадров накладывались: текст капсулы просвечивал сквозь строку поиска.
    /// </summary>
    private static void SetLayer(UIElement layer, bool visible)
    {
        if (visible)
        {
            if (layer.Opacity < 1)
            {
                layer.OpacityTransition ??= new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) };
                layer.Opacity = 1;
            }
        }
        else
        {
            layer.OpacityTransition = null;
            layer.Opacity = 0;
        }
        layer.IsHitTestVisible = visible;
    }

    private void Reshape()
    {
        var before = _shape;
        ApplyShape();
        if (before != _shape || _shape is Shape.Compact or Shape.Expanded)
            AnimateToTarget();
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
        _pointerEnteredAt = 0;

        if (mode == Mode.Collapsed)
        {
            _searchGeneration++;
            _searchCts?.Cancel();
            _debounceTimer.Stop();
            _clockTimer.Stop();
            _mediaTimer.Stop();
            ResetConfirm();
            SetScope(null, "");
            _suppressTextChanged = true;
            SearchBox.Text = "";
            _suppressTextChanged = false;
            UpdateHotkeyHint();
            _results.Clear();
            SwitchView(View.Results, animate: false);
            MediaCard.Visibility = Visibility.Collapsed;
            // Пока островок был раскрыт, пришедшее видно в колоколе — пики не догоняют.
            _peekGapTimer.Start();
        }
        else if (wasCollapsed)
        {
            CancelPeek();
            _peekQueue.Clear();
            _pins.ReloadIfChanged();
            UpdateClock();
            UpdateHotkeyHint();
            if (SettingsStore.Current.ShowClock) _clockTimer.Start();
            UpdateMediaCard();
            _app.Kawaki.Nudge();
        }

        if (mode == Mode.Pinned && SearchBox.Text.Length == 0 && _scope is null && _view == View.Results)
            _ = ShowHomeAsync();

        Reshape();
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

    /// <summary>Раскрыть и встать в поиск; с запросом — сразу с ним (второй запуск, канал, «?» из пика).</summary>
    public void OpenWithQuery(string? query)
    {
        RememberForeground();
        OpenPinned();
        if (query is not null)
            ApplyQuery(query);
    }

    /// <summary>Раскрыть с запросом, не забирая фокус (ключ --demo, для скриншотов).</summary>
    public void ShowDemo(string query)
    {
        _search.WarmUp();
        SetMode(Mode.Pinned);
        // До загрузки поля TextChanged не приходит — ждём, пока окно отрисуется.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => Guard.Run(() =>
        {
            SetScope(null, "");
            _suppressTextChanged = true;
            SearchBox.Text = query;
            _suppressTextChanged = false;
            OnSearchTextChanged();
        }));
    }

    private void RememberForeground()
    {
        var foreground = Win32.GetForegroundWindow();
        if (foreground != 0 && foreground != _hwnd && !Win32.IsOwnWindow(foreground))
            _lastExternalForeground = foreground;
    }

    /// <summary>Свернуть и вернуть клавиатуру тому окну, где человек был до островка.</summary>
    private void Dismiss(bool restoreFocus)
    {
        SetMode(Mode.Collapsed);
        if (restoreFocus && _lastExternalForeground != 0 && Win32.IsWindow(_lastExternalForeground))
            Win32.SetForegroundWindow(_lastExternalForeground);
    }

    private void OnHotkey(int id)
    {
        if (id != HotkeyId) return;

        if (_mode == Mode.Pinned && Win32.GetForegroundWindow() == _hwnd)
        {
            Dismiss(restoreFocus: true);
            return;
        }

        RememberForeground();
        OpenPinned();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Log.Write($"activation {args.WindowActivationState}, mode {_mode}, flyouts {_openFlyouts}");

        if (!_activatedOnce)
        {
            _activatedOnce = true;
            // К этому моменту WinUI навесил свои подклассы — встаём перед ними
            // и ещё раз чистим стиль, который они успели вернуть.
            _host.BecomeOutermost();
            Win32.MakeBareTopmostPopup(_hwnd);
        }
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
        if (SettingsStore.Current.HideOnFullscreen)
        {
            UpdateFullscreenState();
            if (_hiddenForFullscreen)
                return;
        }
        else if (_hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            AppWindow.Show(activateWindow: false);
        }

        if (++_pollTicks % TopmostCheckEveryTicks == 0 && Win32.EnsureTopmost(_hwnd))
            Log.Write("topmost was lost, restored");

        // Помним, где человек работал: туда вернётся фокус и туда вставится текст из буфера.
        if (_mode != Mode.Pinned)
            RememberForeground();

        if (_mode == Mode.Pinned)
            return;

        if (!Win32.GetCursorPos(out var cursor))
            return;

        var scale = Win32.GetScale(_hwnd);
        var pos = AppWindow.Position;
        // Окно шире капсулы: зона наведения считается по самой капсуле.
        var pillLeft = pos.X + _pillLeftPx;

        // Один запрос экрана на опрос: DisplayArea — объект WinRT, а опрос идёт десять раз в секунду.
        var display = CurrentDisplay();
        if (_mode == Mode.Collapsed)
        {
            // Курсор ушёл на другой монитор — переезжаем следом.
            if (SettingsStore.Current.MonitorMode == "cursor" && display.DisplayId.Value != _windowDisplay)
                ResizeWindow(_windowSize, force: true);

            var area = display.WorkArea;
            // Зона наведения шире полоски и доходит до самого края экрана:
            // курсор, упёртый в верх, должен попадать.
            var pad = (int)(24 * scale);
            var inside = cursor.X >= pillLeft - pad && cursor.X <= pillLeft + _pillWidthPx + pad
                && cursor.Y >= area.Y && cursor.Y <= pos.Y + _pillHeightPx + (int)(4 * scale);

            // Пик под курсором не уезжает, пока его читают, и не раскрывается в поиск:
            // рука пришла к уведомлению, а не к строке.
            if (_peek is not null)
            {
                SetPeekPaused(inside);
                _pointerEnteredAt = 0;
                return;
            }

            if (!inside || !SettingsStore.Current.HoverOpen)
            {
                _pointerEnteredAt = 0;
                return;
            }
            if (_pointerEnteredAt == 0)
                _pointerEnteredAt = Environment.TickCount64;
            if (Environment.TickCount64 - _pointerEnteredAt >= SettingsStore.Current.HoverOpenDelayMs)
                SetMode(Mode.Hover);
        }
        else if (_mode == Mode.Hover)
        {
            var area = display.WorkArea;
            var pad = (int)(8 * scale);
            var inside = cursor.X >= pillLeft - pad && cursor.X <= pillLeft + _pillWidthPx + pad
                && cursor.Y >= area.Y && cursor.Y <= pos.Y + _pillHeightPx + pad;

            if (inside || _openFlyouts > 0)
            {
                _pointerLeftAt = 0;
            }
            else if (_pointerLeftAt == 0)
            {
                _pointerLeftAt = Environment.TickCount64;
            }
            else if (Environment.TickCount64 - _pointerLeftAt > SettingsStore.Current.HoverCloseDelayMs)
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
            CancelPeek();
            SetMode(Mode.Collapsed);
            AppWindow.Hide();
        }
        else if (!fullscreen && _hiddenForFullscreen)
        {
            Log.Write("fullscreen gone, showing");
            _hiddenForFullscreen = false;
            AppWindow.Show(activateWindow: false);
            Win32.EnsureTopmost(_hwnd);
            TryShowNextPeek();
        }
    }

    private void UpdateClock()
    {
        var text = DateTime.Now.ToString("HH:mm");
        if (ClockText.Text != text)
            ClockText.Text = text;
    }

    // ------------------------------------------------------------------
    // Живая капсула: музыка и активности
    // ------------------------------------------------------------------

    private string _mediaApp = "";

    private void OnMediaChanged()
    {
        if (_app.Media.AppId != _mediaApp)
        {
            _mediaApp = _app.Media.AppId;
            _volumePercent = -1;
            if (MediaCard.Visibility == Visibility.Visible) _ = RefreshVolumeAsync();
        }
        UpdateCompact();
        UpdateMediaCard();
    }

    private void UpdateCompact()
    {
        var s = SettingsStore.Current;
        var activity = s.LiveActivities ? _app.Activities.Current : null;
        var media = _app.Media;
        var kind = activity is not null ? CompactKind.Activity
            : s.MediaInCapsule && media.HasSession && media.IsPlaying ? CompactKind.Media
            : CompactKind.None;

        // Чип рядом с часами — та же активность, когда островок раскрыт.
        if (activity is not null)
        {
            ActivityChip.Visibility = Visibility.Visible;
            ActivityChipText.Text = activity.Text;
            ActivityChipGlyph.Glyph = activity.Glyph ?? "";
        }
        else
        {
            ActivityChip.Visibility = Visibility.Collapsed;
        }

        double textWidth = 0;
        switch (kind)
        {
            case CompactKind.Activity:
                CompactText.Text = activity!.Text;
                CompactText.Foreground = ParseBrush(activity.Color) ?? (Brush)Root.Resources["IslandForegroundBrush"];
                CompactGlyph.Glyph = activity.Glyph ?? "";
                CompactGlyph.Visibility = Visibility.Visible;
                SetCompactImage(activity.Icon);
                Equalizer.Visibility = Visibility.Collapsed;
                CompactProgressTrack.Visibility = activity.Progress is null ? Visibility.Collapsed : Visibility.Visible;
                textWidth = MeasureText(CompactText);
                _compactWidth = Math.Clamp(Math.Ceiling((textWidth + 64) / 8) * 8, 120, 320);
                CompactProgress.Width = Math.Max(0, (_compactWidth - 2 - 16 - 12) * (activity.Progress ?? 0));
                break;

            case CompactKind.Media:
                CompactText.Text = Environment.TickCount64 < _volumeFlashUntil && _volumePercent >= 0
                    ? Loc.T("Volume_Format", _volumePercent)
                    : media.Artist.Length > 0 ? $"{media.Title} · {media.Artist}" : media.Title;
                CompactText.Foreground = (Brush)Root.Resources["IslandForegroundBrush"];
                CompactGlyph.Glyph = "";
                CompactArtBrush.ImageSource = media.Thumbnail;
                _compactIcon = null;
                CompactGlyph.Visibility = media.Thumbnail is null ? Visibility.Visible : Visibility.Collapsed;
                Equalizer.Visibility = Visibility.Visible;
                CompactProgressTrack.Visibility = Visibility.Collapsed;
                _compactWidth = 230;
                break;
        }

        var changed = kind != _compactKind;
        _compactKind = kind;
        CompactLayer.Width = _compactWidth - 2;
        if (changed || _shape == Shape.Compact)
            Reshape();
        else
            ApplyShape();
    }

    /// <summary>
    /// Визуализатор: столбики в капсуле и в карточке «Сейчас играет».
    ///
    /// Кадры — по таймеру с частотой из настроек (15/30/60), а не бесконечной
    /// анимацией: раскадровка «Forever» перерисовывала капсулу с частотой монитора
    /// часами подряд. Звук слушается, только пока столбики видны и выбран режим
    /// «по звуку»: иначе захват даже не запускается.
    /// </summary>
    private void UpdateVisualizer()
    {
        var s = SettingsStore.Current;
        var media = _app.Media;
        var on = s.VisualizerMode != "off";
        var capsule = on && _shape == Shape.Compact && _compactKind == CompactKind.Media;
        var card = on && s.VisualizerInCard && MediaCard.Visibility == Visibility.Visible && _mode != Mode.Collapsed;

        EnsureBars(Equalizer, _capsuleBars, s.VisualizerBars, 14, 3);
        EnsureBars(CardVisualizer, _cardBars, s.VisualizerBars, 26, 4);
        Equalizer.Visibility = on && _compactKind == CompactKind.Media ? Visibility.Visible : Visibility.Collapsed;
        CardVisualizer.Visibility = on && s.VisualizerInCard ? Visibility.Visible : Visibility.Collapsed;

        var color = s.VisualizerColor switch
        {
            "white" => Microsoft.UI.Colors.White,
            "album" when media.ArtColor is { } art => art,
            _ => ColorHelper.FromArgb(255, 0x3B, 0xE5, 0xCE),
        };
        if (color != _barColor || _capsuleBars.Any(b => b.Fill is null))
        {
            _barColor = color;
            var brush = new SolidColorBrush(color);
            foreach (var bar in _capsuleBars.Concat(_cardBars)) bar.Fill = brush;
        }

        var running = (capsule || card) && media.IsPlaying;
        var reactive = running && s.VisualizerMode == "reactive";
        _spectrum.Configure(s.VisualizerBars, s.VisualizerSensitivity);
        if (reactive) _spectrum.Start();
        else _spectrum.Stop();

        _equalizerTimer.Interval = TimeSpan.FromMilliseconds(1000.0 / (reactive ? s.VisualizerFps : Math.Min(s.VisualizerFps, 15)));
        if (running != _equalizerRunning)
        {
            _equalizerRunning = running;
            if (running) _equalizerTimer.Start();
            else
            {
                _equalizerTimer.Stop();
                // На паузе столбики ложатся, а не замирают посреди такта.
                SetBars(_capsuleBars, 0.2f);
                SetBars(_cardBars, 0.2f);
            }
        }
    }

    private static void EnsureBars(StackPanel host, List<Rectangle> bars, int count, double height, double width)
    {
        if (bars.Count == count) return;
        host.Children.Clear();
        bars.Clear();
        for (var i = 0; i < count; i++)
        {
            var bar = new Rectangle
            {
                Width = width,
                Height = height,
                RadiusX = width / 2,
                RadiusY = width / 2,
                RenderTransformOrigin = new Point(0.5, 1),
                RenderTransform = new ScaleTransform { ScaleY = 0.2 },
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            bars.Add(bar);
            host.Children.Add(bar);
        }
    }

    private static void SetBars(List<Rectangle> bars, float level)
    {
        foreach (var bar in bars)
            ((ScaleTransform)bar.RenderTransform).ScaleY = level;
    }

    private void OnEqualizerFrame()
    {
        var count = _capsuleBars.Count;
        if (SettingsStore.Current.VisualizerMode == "reactive")
        {
            _spectrum.Read(_levels);
        }
        else
        {
            // «Анимация»: две синусоиды на столбик — живо, но без захвата звука.
            var t = Environment.TickCount64 / 1000.0;
            for (var i = 0; i < count; i++)
            {
                var speed = 5.3 + i * 1.37 % 3.4;
                var phase = i * 1.7;
                _levels[i] = (float)(Math.Abs(Math.Sin(t * speed + phase)) * 0.6 + Math.Abs(Math.Sin(t * speed * 0.37 + phase)) * 0.4);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var level = 0.15f + 0.85f * Math.Clamp(_levels[i], 0, 1);
            if (i < _capsuleBars.Count) ((ScaleTransform)_capsuleBars[i].RenderTransform).ScaleY = level;
            if (i < _cardBars.Count) ((ScaleTransform)_cardBars[i].RenderTransform).ScaleY = level;
        }
    }

    // ------------------------------------------------------------------
    // Громкость играющего приложения
    // ------------------------------------------------------------------

    private async Task RefreshVolumeAsync()
    {
        if (!SettingsStore.Current.MediaVolume || !_app.Media.HasSession)
        {
            VolumePanel.Visibility = Visibility.Collapsed;
            return;
        }
        var volume = await _app.Media.GetVolumeAsync();
        if (volume is not { } level)
        {
            // Аудиосеанс приложения не нашёлся — ползунок, который ничего не двигает, хуже его отсутствия.
            VolumePanel.Visibility = Visibility.Collapsed;
            _volumePercent = -1;
            return;
        }
        _volumePercent = (int)Math.Round(level * 100);
        _suppressVolume = true;
        VolumeSlider.Value = _volumePercent;
        _suppressVolume = false;
        VolumeGlyph.Glyph = VolumeGlyphFor(_volumePercent);
        VolumePanel.Visibility = Visibility.Visible;
    }

    private static string VolumeGlyphFor(int percent) => percent switch
    {
        0 => "\uE74F",
        < 34 => "\uE993",
        < 67 => "\uE994",
        _ => "\uE995",
    };

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressVolume) return;
        _volumePercent = (int)Math.Round(e.NewValue);
        VolumeGlyph.Glyph = VolumeGlyphFor(_volumePercent);
        _ = _app.Media.SetVolumeAsync(_volumePercent / 100f);
    }

    /// <summary>Колесо над карточкой или живой капсулой — громкость приложения шагом 5%.</summary>
    private async void Media_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!SettingsStore.Current.MediaVolume || !_app.Media.HasSession) return;
        e.Handled = true;
        var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
        if (_volumePercent < 0)
        {
            var current = await _app.Media.GetVolumeAsync();
            if (current is null) return;
            _volumePercent = (int)Math.Round(current.Value * 100);
        }
        _volumePercent = Math.Clamp(_volumePercent + (delta > 0 ? 5 : -5), 0, 100);
        _ = _app.Media.SetVolumeAsync(_volumePercent / 100f);

        _suppressVolume = true;
        VolumeSlider.Value = _volumePercent;
        _suppressVolume = false;
        VolumeGlyph.Glyph = VolumeGlyphFor(_volumePercent);

        // В капсуле на секунду вместо названия трека — громкость.
        _volumeFlashUntil = Environment.TickCount64 + 1200;
        if (_volumeFlashTimer is null)
        {
            _volumeFlashTimer = DispatcherQueue.CreateTimer();
            _volumeFlashTimer.Interval = TimeSpan.FromMilliseconds(1250);
            _volumeFlashTimer.IsRepeating = false;
            _volumeFlashTimer.Tick += (_, _) => Guard.Run(UpdateCompact);
        }
        _volumeFlashTimer.Stop();
        _volumeFlashTimer.Start();
        UpdateCompact();
    }

    private async void SetCompactImage(string? icon)
    {
        // Прогресс загрузки обновляет активность несколько раз в секунду —
        // та же картинка уже стоит, не сбрасываем её, иначе капсула мигает.
        if (icon is not null && icon == _compactIcon && CompactArtBrush.ImageSource is not null)
        {
            CompactGlyph.Visibility = Visibility.Collapsed;
            return;
        }
        _compactIcon = icon;
        CompactArtBrush.ImageSource = null;
        if (icon is null) return;
        var image = await ShellIcons.LoadAsync(icon, 20);
        if (image is null || _compactKind != CompactKind.Activity) return;
        CompactArtBrush.ImageSource = image;
        CompactGlyph.Visibility = Visibility.Collapsed;
    }

    private static double MeasureText(TextBlock text)
    {
        text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return text.DesiredSize.Width;
    }

    private static SolidColorBrush? ParseBrush(string? hex)
    {
        if (hex is not { Length: 7 } || hex[0] != '#') return null;
        try
        {
            var value = Convert.ToUInt32(hex[1..], 16);
            return new SolidColorBrush(ColorHelper.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value));
        }
        catch
        {
            return null;
        }
    }

    private void ActivityChip_Click(object sender, RoutedEventArgs e)
    {
        var activity = _app.Activities.Current;
        if (activity is null) return;
        if (activity.Source == "timer")
        {
            ApplyQuery("timer ");
            return;
        }
        if (activity.Action is { } action)
            HandleOutcome(ActionRunner.Run(action));
    }

    // ------------------------------------------------------------------
    // Сейчас играет
    // ------------------------------------------------------------------

    private void UpdateMediaCard()
    {
        var media = _app.Media;
        var show = _mode != Mode.Collapsed
            && SettingsStore.Current.MediaCard
            && media.HasSession
            && _view == View.Results
            && _scope is null
            && SearchBox.Text.Trim().Length == 0;

        var was = MediaCard.Visibility == Visibility.Visible;
        MediaCard.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show)
        {
            MediaTitle.Text = media.Title;
            MediaArtist.Text = media.Artist;
            MediaArtBrush.ImageSource = media.Thumbnail;
            MediaPlayGlyph.Glyph = media.IsPlaying ? "" : "";
            ToolTipService.SetToolTip(MediaPlay, Loc.T(media.IsPlaying ? "Media_Pause" : "Media_Play"));
            MediaPrev.IsEnabled = media.CanPrevious;
            MediaNext.IsEnabled = media.CanNext;
            MediaProgressTrack.Visibility = media.Duration > TimeSpan.Zero ? Visibility.Visible : Visibility.Collapsed;
            UpdateMediaProgress();
            if (media.IsPlaying && media.Duration > TimeSpan.Zero) _mediaTimer.Start();
            else _mediaTimer.Stop();
            if (!was) _ = RefreshVolumeAsync();
        }
        else
        {
            _mediaTimer.Stop();
        }

        UpdateVisualizer();
        if (show != was && _mode != Mode.Collapsed)
            AnimateToTarget();
    }

    private void UpdateMediaProgress()
    {
        var media = _app.Media;
        if (media.Duration <= TimeSpan.Zero || MediaProgressTrack.ActualWidth <= 0) return;
        var ratio = Math.Clamp(media.EstimatedPosition.TotalSeconds / media.Duration.TotalSeconds, 0, 1);
        MediaProgress.Width = MediaProgressTrack.ActualWidth * ratio;
    }

    private void MediaPrev_Click(object sender, RoutedEventArgs e) => _app.Media.Previous();
    private void MediaPlay_Click(object sender, RoutedEventArgs e) => _app.Media.TogglePlayPause();
    private void MediaNext_Click(object sender, RoutedEventArgs e) => _app.Media.Next();

    // ------------------------------------------------------------------
    // Уведомления: пик и колокол
    // ------------------------------------------------------------------

    private void OnNotificationPosted(IsletNotification n)
    {
        var s = SettingsStore.Current;
        if (s.NotifySound && s.NotifyMode != "off")
            Win32.MessageBeep(0x40);
        // Раскрытый островок показывает пришедшее счётчиком на колоколе — пик не нужен.
        if (s.NotifyMode != "peek" || _mode != Mode.Collapsed) return;

        _peekQueue.Enqueue(n);
        // Пришла пачка — показываем свежие; остальное лежит в колоколе.
        while (_peekQueue.Count > 4) _peekQueue.Dequeue();
        TryShowNextPeek();
    }

    private void OnNotificationsChanged()
    {
        UpdateBell();
        ApplyShape();
        if (_view == View.Notifications && _mode != Mode.Collapsed)
            RenderNotifications();
    }

    private void UpdateBell()
    {
        var unread = _app.Notifications.UnreadCount;
        BellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        BellCount.Text = unread > 9 ? "9+" : unread.ToString();
    }

    private void TryShowNextPeek()
    {
        if (_peek is not null || _mode != Mode.Collapsed || _hiddenForFullscreen || _peekGapTimer.IsRunning) return;
        if (!_peekQueue.TryDequeue(out var next)) return;

        _peek = next;
        _peekHolding = false;
        _peekPaused = false;
        PeekTitle.Text = next.Title.Length > 0 ? next.Title : next.SourceName;
        // Подпись источника — если она что-то говорит: «Kawaki», «ClipTide», имя плагина.
        PeekSource.Text = next.Title.Length > 0 && next.SourceName != next.Title && next.SourceName != Loc.T("Source_External") ? next.SourceName : "";
        PeekBody.Text = next.Body.Replace('\n', ' ');
        PeekBodyShift.X = 0;
        PeekGlyph.Glyph = next.Glyph ?? "";
        PeekImageBrush.ImageSource = null;
        PeekImage.Visibility = Visibility.Collapsed;
        if (next.Icon is { } icon)
            _ = LoadPeekImageAsync(next, icon);

        Reshape();
        // Сначала капсула доезжает до плашки, потом начинается отсчёт чтения.
        _peekTimer.Interval = TimeSpan.FromMilliseconds(PeekAnimationMs + 80);
        _peekTimer.Start();
    }

    private async Task LoadPeekImageAsync(IsletNotification n, string icon)
    {
        var image = await ShellIcons.LoadAsync(icon, 36);
        if (image is null || !ReferenceEquals(_peek, n)) return;
        PeekImageBrush.ImageSource = image;
        PeekImage.Visibility = Visibility.Visible;
    }

    private void OnPeekTimer()
    {
        _peekTimer.Stop();
        if (_peek is null) return;
        if (!_peekHolding)
        {
            _peekHolding = true;
            var marqueeMs = StartMarquee();
            _peekTimer.Interval = TimeSpan.FromMilliseconds(SettingsStore.Current.PeekSeconds * 1000 + marqueeMs);
            if (!_peekPaused) _peekTimer.Start();
            return;
        }
        EndPeek();
    }

    private void SetPeekPaused(bool paused)
    {
        if (paused == _peekPaused || !_peekHolding) return;
        _peekPaused = paused;
        if (paused)
        {
            _peekTimer.Stop();
        }
        else
        {
            // Ушли с пика — даём дочитать ещё полторы секунды и убираем.
            _peekTimer.Interval = TimeSpan.FromMilliseconds(1500);
            _peekTimer.Start();
        }
    }

    /// <summary>Бегущая строка для текста, который не влез. Возвращает длину круга, мс.</summary>
    private int StartMarquee()
    {
        _marquee?.Stop();
        PeekBody.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // Полпикселя — это погрешность вёрстки, а не «не влезло»: гонять из-за неё строку незачем.
        var overflow = Math.Floor(PeekBody.DesiredSize.Width - PeekBodyHost.ActualWidth);
        if (overflow <= 2 || PeekBodyHost.ActualWidth <= 0) return 0;

        var travel = TimeSpan.FromSeconds(overflow / MarqueeSpeed);
        var pause = TimeSpan.FromMilliseconds(MarqueePauseMs);
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = false };
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = TimeSpan.Zero, Value = 0 });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = pause, Value = 0 });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = pause + travel, Value = -overflow });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = pause * 2 + travel, Value = -overflow });
        animation.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = pause * 2 + travel * 2, Value = 0 });
        Storyboard.SetTarget(animation, PeekBodyShift);
        Storyboard.SetTargetProperty(animation, "X");
        _marquee = new Storyboard();
        _marquee.Children.Add(animation);
        _marquee.Begin();
        return (int)(pause * 2 + travel * 2).TotalMilliseconds;
    }

    private void EndPeek()
    {
        if (_peek is null) return;
        _peekTimer.Stop();
        _marquee?.Stop();
        _peek = null;
        Reshape();
        // Пауза между пиками: пачка уведомлений не превращается в одно сплошное движение.
        _peekGapTimer.Start();
    }

    /// <summary>Пик снят без анимации паузы — островок раскрыли или спрятали.</summary>
    private void CancelPeek()
    {
        if (_peek is null) return;
        _peekTimer.Stop();
        _marquee?.Stop();
        _peek = null;
    }

    private void PeekLayer_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInside(source, PeekClose)) return;
        if (_peek is not { } n) return;
        EndPeek();
        ActivateNotification(n);
    }

    private void PeekClose_Click(object sender, RoutedEventArgs e)
    {
        if (_peek is { } n)
            _app.Notifications.MarkRead(n);
        EndPeek();
    }

    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var current = node; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container)) return true;
        }
        return false;
    }

    private void ActivateNotification(IsletNotification n)
    {
        _app.Notifications.MarkRead(n);
        if (n.Source == "kawaki")
            _ = _app.Kawaki.MarkReadAsync(n.ExternalId);
        if (n.Action is not { } action) return;

        if (action.Query is { } query)
        {
            OpenWithQuery(query);
            return;
        }
        var outcome = ActionRunner.Run(action);
        if (_mode != Mode.Collapsed)
            HandleOutcome(outcome);
    }

    private void BellButton_Click(object sender, RoutedEventArgs e) =>
        SwitchView(_view == View.Notifications ? View.Results : View.Notifications);

    private void SwitchView(View view, bool animate = true)
    {
        if (_view == view && view == View.Results) return;
        _view = view;
        var notifications = view == View.Notifications;
        NotificationsHeader.Visibility = notifications ? Visibility.Visible : Visibility.Collapsed;
        if (notifications) RecentHeader.Visibility = Visibility.Collapsed;
        NotificationsList.Visibility = notifications ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = notifications ? Visibility.Collapsed : Visibility.Visible;
        BellGlyph.Glyph = notifications ? "" : "";

        if (notifications)
        {
            RenderNotifications();
            // Открыли колокол — прочитано. Точки на строках остаются до следующего открытия.
            _app.Notifications.MarkAllRead();
        }
        else
        {
            NotificationsEmpty.Visibility = Visibility.Collapsed;
            _notificationRows.Clear();
        }
        UpdateMediaCard();
        if (animate) AnimateToTarget();
    }

    private void RenderNotifications()
    {
        _notificationRows.Clear();
        foreach (var n in _app.Notifications.History)
        {
            var row = new NotificationRow(n);
            _notificationRows.Add(row);
            if (n.Icon is { } icon)
                _ = LoadRowIconAsync(row, icon);
        }
        NotificationsEmpty.Visibility = _notificationRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearNotificationsButton.Visibility = _notificationRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_notificationRows.Count > 0) NotificationsList.SelectedIndex = 0;
        AnimateToTarget();
    }

    private static async Task LoadRowIconAsync(NotificationRow row, string icon) =>
        row.Icon = await ShellIcons.LoadAsync(icon, 32);

    private void NotificationsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NotificationRow row)
            ActivateNotification(row.Notification);
    }

    private void Notification_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: NotificationRow row } element) return;
        var menu = NewMenu();
        if (row.Notification.Action is not null)
            menu.Items.Add(MenuItem(Loc.T("Ctx_Open"), "", () => ActivateNotification(row.Notification)));
        menu.Items.Add(MenuItem(Loc.T("Ctx_Delete"), "", () => _app.Notifications.Remove(row.Notification)));
        ShowMenu(menu, element, args);
    }

    private void ClearNotifications_Click(object sender, RoutedEventArgs e) => _app.Notifications.Clear();

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
        if (_suppressTextChanged || _mode == Mode.Collapsed) return;
        OnSearchTextChanged();
    }

    private void OnSearchTextChanged()
    {
        if (_view == View.Notifications)
            SwitchView(View.Results);
        DetectScope();
        OnQueryChanged();
    }

    [GeneratedRegex(@"^(\S+)\s(.*)$", RegexOptions.Singleline)]
    private static partial Regex KeywordRegex();

    /// <summary>«k наруто» → чип «Kawaki» и строка «наруто».</summary>
    private void DetectScope()
    {
        if (_scope is not null) return;
        var match = KeywordRegex().Match(SearchBox.Text);
        if (!match.Success) return;
        if (_search.FindByKeyword(match.Groups[1].Value) is not { } provider) return;
        SetScope(provider, match.Groups[2].Value);
    }

    private void SetScope(SearchProvider? provider, string text)
    {
        _scope = provider;
        if (provider is null)
        {
            ScopeChip.Visibility = Visibility.Collapsed;
            SearchBox.Padding = new Thickness(14, 6, 10, 6);
            SearchBox.PlaceholderText = Loc.T("SearchBox.PlaceholderText");
            return;
        }

        ScopeText.Text = provider.Name;
        ScopeGlyph.Glyph = provider.Glyph;
        ScopeChip.Visibility = Visibility.Visible;
        ScopeChip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SearchBox.Padding = new Thickness(14 + ScopeChip.DesiredSize.Width, 6, 10, 6);
        SearchBox.PlaceholderText = provider.Description.Length > 0 ? provider.Description : provider.Name;

        _suppressTextChanged = true;
        SearchBox.Text = text;
        SearchBox.SelectionStart = text.Length;
        _suppressTextChanged = false;
    }

    /// <summary>Подставить запрос: из подсказки раскладки, справки «?», пика приветствия.</summary>
    private void ApplyQuery(string query)
    {
        if (_mode == Mode.Collapsed) OpenPinned();
        SetScope(null, "");
        _suppressTextChanged = true;
        SearchBox.Text = query;
        SearchBox.SelectionStart = SearchBox.Text.Length;
        _suppressTextChanged = false;
        SearchBox.Focus(FocusState.Programmatic);
        OnSearchTextChanged();
    }


    /// <summary>
    /// Подсказка клавиши — только пока строка пуста и только если помещается рядом
    /// с плейсхолдером: обрезанный на полуслове плейсхолдер хуже, чем её отсутствие.
    /// </summary>
    private void UpdateHotkeyHint()
    {
        var show = HotkeyRegistered && _scope is null && SearchBox.Text.Length == 0;
        if (show)
        {
            HotkeyHint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            // Ширины ещё нет (островок не раскрывался) — не показываем: проверим при раскрытии.
            var free = SearchBox.ActualWidth - 14 - 10 - PlaceholderProbe.ActualWidth;
            // Ширина подсказки — текст плюс поля и рамка (6 + 6 + 2).
            var hint = HotkeyProbe.ActualWidth + 14;
            show = SearchBox.ActualWidth > 0 && PlaceholderProbe.ActualWidth > 0 && HotkeyProbe.ActualWidth > 0 && free >= hint + 24;
        }
        HotkeyHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnQueryChanged()
    {
        UpdateHotkeyHint();
        _searchGeneration++;
        _debounceTimer.Stop();
        _searchCts?.Cancel();
        ResetConfirm();

        var text = SearchBox.Text.Trim();
        var scope = _scope?.Id;
        if (scope is null && text == "?")
        {
            scope = "help";
            text = "";
        }
        UpdateMediaCard();

        if (scope is null && text.Length == 0)
        {
            _ = ShowHomeAsync();
            return;
        }

        var query = SearchService.MakeQuery(text, scope);
        var instant = _search.SearchInstant(query);

        // Медленные строки (файлы, Kawaki, плагины) держим, пока подходят к запросу:
        // иначе список на каждую букву сжимался бы и снова вырастал.
        var kept = _results
            .Where(r => _search.IsSlow(r.ProviderId) && MatchesAllWords(r.Title, text) && instant.All(i => i.HistoryKey != r.HistoryKey))
            .ToList();
        if (kept.Count > 0)
        {
            var at = instant.FindIndex(r => r.ProviderId == "web");
            instant.InsertRange(at < 0 ? instant.Count : at, kept);
        }
        ShowResults(instant);
        _debounceTimer.Start();
    }

    private static bool MatchesAllWords(string title, string query) =>
        query.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(w => title.Contains(w, StringComparison.OrdinalIgnoreCase));

    private async Task RunFullSearchAsync()
    {
        var text = SearchBox.Text.Trim();
        var scope = _scope?.Id;
        if (scope is null && text == "?") { scope = "help"; text = ""; }
        if (scope is null && text.Length == 0) return;

        var generation = _searchGeneration;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        try
        {
            var results = await _search.SearchAsync(SearchService.MakeQuery(text, scope), cts.Token);
            if (generation != _searchGeneration || _mode == Mode.Collapsed || _view != View.Results)
                return;
            ShowResults(results);
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Пустой запрос: в закреплённом островке — недавнее и частое, наведением — ничего.</summary>
    private async Task ShowHomeAsync()
    {
        var generation = _searchGeneration;
        if (_mode != Mode.Pinned || !SettingsStore.Current.ShowRecent)
        {
            ShowResults([]);
            return;
        }
        var recent = await _search.RecentAsync(6);
        if (generation != _searchGeneration || _mode != Mode.Pinned || SearchBox.Text.Trim().Length > 0 || _scope is not null)
            return;
        ShowResults(recent);
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

        // «Недавнее» подписано: иначе строки на пустом запросе выглядят зависшей выдачей.
        RecentHeader.Visibility = _view == View.Results && _results.Count > 0 && _results.All(r => r.IsRecent)
            ? Visibility.Visible
            : Visibility.Collapsed;

        AnimateToTarget();
        _ = LoadIconsAsync(items);
    }

    private static bool SameResult(ResultItem a, ResultItem b) =>
        a.Kind == b.Kind && a.Title == b.Title && a.ProviderId == b.ProviderId && a.IsRecent == b.IsRecent
        && string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);

    private static async Task LoadIconsAsync(List<ResultItem> items)
    {
        foreach (var item in items)
        {
            if (item.Icon is not null) continue;
            if (item.IconUrl is { } url)
                item.Icon = await ShellIcons.LoadAsync(url, 32);
            else if (item.IconSource is { } source)
                item.Icon = await ShellIcons.GetAsync(source, 32);
        }
    }

    private void OnSelectionChanged()
    {
        if (_confirming is not null && !ReferenceEquals(ResultsList.SelectedItem, _confirming))
            ResetConfirm();
    }

    private void ResetConfirm()
    {
        if (_confirming is null) return;
        _confirming.Subtitle = _confirmingSubtitle;
        _confirming.Confirming = false;
        _confirming = null;
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void SearchBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var notifications = _view == View.Notifications;
        switch (e.Key)
        {
            case VirtualKey.Escape:
                if (notifications)
                    SwitchView(View.Results);
                else if (SearchBox.Text.Length > 0 || _scope is not null)
                    ApplyQuery("");
                else
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

            case VirtualKey.PageDown:
                MoveSelection(+MaxRows);
                e.Handled = true;
                break;

            case VirtualKey.PageUp:
                MoveSelection(-MaxRows);
                e.Handled = true;
                break;

            case VirtualKey.Enter:
                if (notifications)
                {
                    if (NotificationsList.SelectedItem is NotificationRow row)
                        ActivateNotification(row.Notification);
                }
                else if (ResultsList.SelectedItem is ResultItem item)
                {
                    // Ctrl+Enter — показать в папке, Shift+Enter — от имени администратора.
                    Launch(item, reveal: IsDown(VirtualKey.Control), admin: IsDown(VirtualKey.Shift));
                }
                e.Handled = true;
                break;

            case VirtualKey.Tab:
                // Tab дописывает: подсказку раскладки, ключевое слово из справки, имя строки.
                if (!notifications && ResultsList.SelectedItem is ResultItem selected)
                {
                    if (selected.Action?.Query is { } query) ApplyQuery(query);
                    else if (selected.Kind is ResultKind.App or ResultKind.File or ResultKind.Folder or ResultKind.Command)
                    {
                        SearchBox.Text = selected.Title;
                        SearchBox.SelectionStart = SearchBox.Text.Length;
                    }
                }
                e.Handled = true;
                break;

            case VirtualKey.Back:
                // Backspace на пустой строке снимает чип и возвращает ключевое слово текстом.
                if (_scope is not null && SearchBox.Text.Length == 0)
                {
                    var keyword = _scope.Keywords.FirstOrDefault() ?? "";
                    SetScope(null, "");
                    _suppressTextChanged = true;
                    SearchBox.Text = keyword;
                    SearchBox.SelectionStart = keyword.Length;
                    _suppressTextChanged = false;
                    OnQueryChanged();
                    e.Handled = true;
                }
                break;

            case VirtualKey.C when IsDown(VirtualKey.Control) && SearchBox.SelectionLength == 0:
                // Ctrl+C без выделения в строке — скопировать путь или адрес выбранной строки.
                if (!notifications && ResultsList.SelectedItem is ResultItem { CanCopyPath: true } copyItem && ClipboardText.Set(copyItem.Target))
                {
                    copyItem.Subtitle = Loc.T("Copied");
                    e.Handled = true;
                }
                break;

            case VirtualKey.Delete when IsDown(VirtualKey.Shift):
                // Shift+Delete — убрать из «Недавних».
                if (!notifications && ResultsList.SelectedItem is ResultItem { IsRecent: true } recent)
                {
                    Frecency.Forget(recent);
                    _results.Remove(recent);
                    if (_results.Count == 0) RecentHeader.Visibility = Visibility.Collapsed;
                    AnimateToTarget();
                    e.Handled = true;
                }
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        var list = _view == View.Notifications ? NotificationsList : ResultsList;
        var count = list.Items.Count;
        if (count == 0) return;
        var index = Math.Clamp(list.SelectedIndex + delta, 0, count - 1);
        list.SelectedIndex = index;
        list.ScrollIntoView(list.Items[index]);
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ResultItem item)
            Launch(item, reveal: false, admin: false);
    }

    private void Launch(ResultItem item, bool reveal, bool admin)
    {
        if (item.Action?.Query is { } query)
        {
            ApplyQuery(query);
            return;
        }

        // Опасное — только со второго Enter.
        if (item.Confirm is { } confirm && !ReferenceEquals(_confirming, item))
        {
            ResetConfirm();
            _confirming = item;
            _confirmingSubtitle = item.Subtitle;
            item.Confirming = true;
            item.Subtitle = confirm;
            ResultsList.SelectedItem = item;
            return;
        }

        ActionOutcome outcome;
        if (reveal && item.CanReveal)
            outcome = Launcher.Reveal(item.Target) ? ActionOutcome.Done : ActionOutcome.Failed;
        else if (admin && item.Kind == ResultKind.File)
            outcome = Launcher.OpenAsAdmin(item.Target) ? ActionOutcome.Done : ActionOutcome.Failed;
        else if (item.Action is { } action)
            outcome = ActionRunner.Run(action);
        else
            outcome = item.Kind switch
            {
                ResultKind.App => Launcher.OpenApp(item.Target) ? ActionOutcome.Done : ActionOutcome.Failed,
                ResultKind.File or ResultKind.Folder or ResultKind.Url or ResultKind.Web or ResultKind.Kawaki
                    => Launcher.Open(item.Target) ? ActionOutcome.Done : ActionOutcome.Failed,
                _ => ActionOutcome.Failed,
            };

        if (outcome is not (ActionOutcome.Failed or ActionOutcome.Stay))
            Frecency.Record(item);
        _confirming = null;
        HandleOutcome(outcome);
    }

    private void HandleOutcome(ActionOutcome outcome)
    {
        switch (outcome)
        {
            // Запущенное окно само заберёт фокус — возвращать его прежнему не нужно.
            case ActionOutcome.Done:
                Dismiss(restoreFocus: false);
                break;
            case ActionOutcome.DoneRestoreFocus:
                Dismiss(restoreFocus: true);
                break;
            case ActionOutcome.Paste:
                Dismiss(restoreFocus: true);
                SchedulePaste();
                break;
            case ActionOutcome.Stay:
                if (_mode != Mode.Collapsed) OnQueryChanged();
                break;
        }
    }

    /// <summary>Ctrl+V — после того как фокус вернулся в прежнее окно.</summary>
    private void SchedulePaste()
    {
        if (_pasteTimer is null)
        {
            _pasteTimer = DispatcherQueue.CreateTimer();
            _pasteTimer.Interval = TimeSpan.FromMilliseconds(90);
            _pasteTimer.IsRepeating = false;
            _pasteTimer.Tick += (_, _) =>
            {
                if (Win32.GetForegroundWindow() != _hwnd)
                    Win32.SendCtrlV();
            };
        }
        _pasteTimer.Start();
    }

    private DispatcherQueueTimer? _pasteTimer;

    private void Result_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: ResultItem item } element)
            return;

        var menu = NewMenu();
        menu.Items.Add(MenuItem(Loc.T("Ctx_Open"), "", () => Launch(item, reveal: false, admin: false)));
        if (item.CanReveal)
            menu.Items.Add(MenuItem(Loc.T("Ctx_Reveal"), "", () => Launch(item, reveal: true, admin: false)));
        if (item.Kind == ResultKind.File && Path.GetExtension(item.Target).ToLowerInvariant() is ".exe" or ".bat" or ".cmd" or ".msc" or ".lnk")
            menu.Items.Add(MenuItem(Loc.T("Ctx_RunAsAdmin"), "", () => Launch(item, reveal: false, admin: true)));
        if (item.CanCopyPath)
            menu.Items.Add(MenuItem(Loc.T(item.Kind is ResultKind.File or ResultKind.Folder ? "Ctx_CopyPath" : "Ctx_CopyLink"), "", () => ClipboardText.Set(item.Target)));
        if (item.Actions is { } extra)
        {
            foreach (var action in extra)
                menu.Items.Add(MenuItem(action.Title, action.Glyph, () => HandleOutcome(ActionRunner.Run(action.Action))));
        }
        if (item.CanPin && Pin.FromResult(item) is { } pin)
        {
            var add = MenuItem(Loc.T("Ctx_Pin"), "", () => _pins.Add(pin));
            add.IsEnabled = _pins.Items.Count < PinStore.MaxPins;
            menu.Items.Add(add);
        }
        if (item.IsRecent)
        {
            menu.Items.Add(MenuItem(Loc.T("Ctx_Forget"), "", () =>
            {
                Frecency.Forget(item);
                _results.Remove(item);
                if (_results.Count == 0) RecentHeader.Visibility = Visibility.Collapsed;
                AnimateToTarget();
            }));
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
            var glyph = new FontIcon { Glyph = pin.Kind == PinKind.Url ? "" : "", FontSize = 16 };
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
                menu.Items.Add(MenuItem(Loc.T("Ctx_Unpin"), "", () => _pins.Remove(pin)));
                ShowMenu(menu, (FrameworkElement)s, args);
            };
            PinsPanel.Children.Add(button);

            if (pin.IconSource is { } source)
                _ = LoadPinIconAsync(source, image, glyph);
        }
    }

    private static async Task LoadPinIconAsync(string source, Image image, FontIcon glyph)
    {
        var icon = await ShellIcons.LoadAsync(source, 32);
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

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        Dismiss(restoreFocus: false);
        _app.OpenSettings();
    }

    private void Plugins_Click(object sender, RoutedEventArgs e)
    {
        Dismiss(restoreFocus: false);
        _app.OpenSettings("plugins");
    }

    private void Help_Click(object sender, RoutedEventArgs e) => ApplyQuery("?");

    private void Exit_Click(object sender, RoutedEventArgs e) => _app.Shutdown();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        SettingsStore.Changed -= ApplySettings;
        _pins.Changed -= RenderPins;
        _app.Notifications.Posted -= OnNotificationPosted;
        _app.Notifications.Changed -= OnNotificationsChanged;
        _app.Activities.Changed -= UpdateCompact;
        _app.Media.Changed -= OnMediaChanged;
        _pollTimer.Stop();
        _animationTimer.Stop();
        _clockTimer.Stop();
        _debounceTimer.Stop();
        _peekTimer.Stop();
        _peekGapTimer.Stop();
        _mediaTimer.Stop();
        _equalizerTimer.Stop();
        _spectrum.Dispose();
        _host.Dispose();
    }
}
