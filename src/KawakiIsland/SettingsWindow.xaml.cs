using System.Reflection;
using KawakiIsland.Native;
using KawakiIsland.Pins;
using KawakiIsland.Settings;
using KawakiIsland.Shell;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace KawakiIsland;

/// <summary>
/// Окно настроек. Каждое изменение сразу сохраняется в SettingsStore —
/// островок подхватывает его по событию, кнопки «Применить» нет.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly nint _hwnd;
    private readonly PinStore _pins = App.Current.Pins;
    private bool _loading = true;
    private bool _recordingHotkey;

    public SettingsWindow()
    {
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "kawaki.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 480;
        }
        PlaceWindow();

        LoadValues();
        Nav.SelectedItem = Nav.MenuItems[0];

        _pins.Changed += RenderPins;
        App.Current.DriveIndex.StatusChanged += OnIndexStatusChanged;
        Closed += OnClosed;
        _loading = false;
    }

    private void PlaceWindow()
    {
        var scale = Win32.GetScale(_hwnd);
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var width = (int)(900 * scale);
        var height = (int)(680 * scale);
        AppWindow.MoveAndResize(new RectInt32(
            area.X + (area.Width - width) / 2,
            area.Y + (area.Height - height) / 2,
            width,
            height));
    }

    private void LoadValues()
    {
        var s = SettingsStore.Current;

        HotkeyText.Text = s.Hotkey.Label;
        UpdateHotkeyStatus();
        AutoStartToggle.IsOn = AutoStart.IsEnabled;
        EngineCombo.ItemsSource = SearchEngine.All;
        EngineCombo.SelectedItem = SearchEngine.Find(s.SearchEngine);

        GlassToggle.IsOn = s.Glass;
        OpacitySlider.Value = Math.Round(s.SurfaceOpacity * 100);
        OpacityValue.Text = $"{OpacitySlider.Value:0}%";
        WidthSlider.Value = s.IslandWidth;
        WidthValue.Text = $"{s.IslandWidth:0} px";
        ClockToggle.IsOn = s.ShowClock;

        IndexToggle.IsOn = s.DriveIndexEnabled;
        ExcludesBox.Text = string.Join(Environment.NewLine, s.IndexExcludes);
        RenderRoots();
        UpdateIndexStatus();

        RenderPins();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Версия {version?.ToString(3)} · WinUI 3";
        DataFolderText.Text = PinStore.Directory;
    }

    /// <summary>Открыть раздел по тегу: general, look, search, pins, about.</summary>
    public void ShowPage(string tag)
    {
        var item = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == tag);
        if (item is not null)
            Nav.SelectedItem = item;
    }

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        // Иначе первый фокус достаётся пункту меню, и вокруг него висит рамка фокуса.
        Root.Focus(FocusState.Pointer);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        LookPage.Visibility = tag == "look" ? Visibility.Visible : Visibility.Collapsed;
        SearchPage.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        PinsPage.Visibility = tag == "pins" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Общие
    // ------------------------------------------------------------------

    private void UpdateHotkeyStatus()
    {
        var status = App.Current.Island?.HotkeyStatus ?? "";
        var taken = status.Contains("занято", StringComparison.Ordinal);
        HotkeyStatusText.Text = taken
            ? "Это сочетание уже занято другой программой — выберите другое"
            : "Раскрывает островок с курсором в поиске";
        _hintBrush ??= HotkeyStatusText.Foreground;
        HotkeyStatusText.Foreground = taken
            ? new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE5, 0x64, 0x5A))
            : _hintBrush;
    }

    private Brush? _hintBrush;

    private void HotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordingHotkey)
        {
            StopRecording(null);
            return;
        }
        _recordingHotkey = true;
        App.Current.Island?.SuspendHotkey();
        HotkeyText.Text = "Нажмите сочетание…";
        HotkeyButton.Content = "Отмена";
        HotkeyStatusText.Text = "Ctrl, Alt, Shift или Win плюс клавиша. Esc — отмена.";
        Root.Focus(FocusState.Programmatic);
    }

    private void HotkeyResetButton_Click(object sender, RoutedEventArgs e)
    {
        _recordingHotkey = true;
        StopRecording(Hotkey.Default);
    }

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_recordingHotkey) return;
        e.Handled = true;

        if (e.Key == VirtualKey.Escape)
        {
            StopRecording(null);
            return;
        }
        if (e.Key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows
            or VirtualKey.LeftControl or VirtualKey.RightControl
            or VirtualKey.LeftShift or VirtualKey.RightShift
            or VirtualKey.LeftMenu or VirtualKey.RightMenu)
            return;

        uint mods = 0;
        if (IsDown(VirtualKey.Control)) mods |= Win32.MOD_CONTROL;
        if (IsDown(VirtualKey.Menu)) mods |= Win32.MOD_ALT;
        if (IsDown(VirtualKey.Shift)) mods |= Win32.MOD_SHIFT;
        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) mods |= Win32.MOD_WIN;

        var isFunctionKey = e.Key is >= VirtualKey.F1 and <= VirtualKey.F24;
        if (mods == 0 && !isFunctionKey)
        {
            HotkeyStatusText.Text = "Нужен хотя бы один модификатор: Ctrl, Alt, Shift или Win.";
            return;
        }
        // Одинокий Shift с буквой — это просто заглавная буква при наборе.
        if (mods == Win32.MOD_SHIFT && !isFunctionKey)
        {
            HotkeyStatusText.Text = "Shift с клавишей мешал бы печатать — добавьте Ctrl, Alt или Win.";
            return;
        }

        StopRecording(new Hotkey(mods, (uint)e.Key));
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void StopRecording(Hotkey? chosen)
    {
        if (!_recordingHotkey) return;
        _recordingHotkey = false;
        HotkeyButton.Content = "Изменить";

        if (chosen is not null)
            SettingsStore.Update(s => s.Hotkey = chosen);
        // Регистрируем заново в любом случае: на время записи старая была снята.
        App.Current.Island?.ApplyHotkey();

        HotkeyText.Text = SettingsStore.Current.Hotkey.Label;
        UpdateHotkeyStatus();
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try { AutoStart.Set(AutoStartToggle.IsOn); }
        catch (Exception ex) { Log.Write($"autostart: {ex.Message}"); }
    }

    private void EngineCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || EngineCombo.SelectedItem is not SearchEngine engine) return;
        SettingsStore.Update(s => s.SearchEngine = engine.Id);
    }

    // ------------------------------------------------------------------
    // Внешний вид
    // ------------------------------------------------------------------

    private void GlassToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.Glass = GlassToggle.IsOn);
    }

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (OpacityValue is null) return;
        OpacityValue.Text = $"{e.NewValue:0}%";
        if (_loading) return;
        SettingsStore.Update(s => s.SurfaceOpacity = e.NewValue / 100);
    }

    private void WidthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (WidthValue is null) return;
        WidthValue.Text = $"{e.NewValue:0} px";
        if (_loading) return;
        SettingsStore.Update(s => s.IslandWidth = e.NewValue);
    }

    private void ClockToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.ShowClock = ClockToggle.IsOn);
    }

    // ------------------------------------------------------------------
    // Поиск
    // ------------------------------------------------------------------

    private void IndexToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.DriveIndexEnabled = IndexToggle.IsOn);
        App.Current.DriveIndex.Start();
        UpdateIndexStatus();
    }

    private void OnIndexStatusChanged() => DispatcherQueue.TryEnqueue(UpdateIndexStatus);

    private void UpdateIndexStatus()
    {
        var index = App.Current.DriveIndex;
        var s = SettingsStore.Current;
        RescanButton.IsEnabled = s.DriveIndexEnabled && !index.IsScanning;

        if (!s.DriveIndexEnabled)
            IndexStatusText.Text = "Выключен — ищем только через индекс Windows";
        else if (index.IsScanning)
            IndexStatusText.Text = $"Индексирую… Уже в индексе: {index.Count:N0}";
        else if (index.LastError is { } error)
            IndexStatusText.Text = $"Ошибка: {error}";
        else if (index.BuiltAt == default)
            IndexStatusText.Text = "Ещё не построен";
        else
            IndexStatusText.Text = $"{index.Count:N0} файлов и папок · обновлён {index.BuiltAt.ToLocalTime():dd.MM HH:mm}";
    }

    private void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.DriveIndex.Rescan();
        UpdateIndexStatus();
    }

    private void RenderRoots()
    {
        RootsPanel.Children.Clear();
        var roots = SettingsStore.Current.EffectiveRoots();
        if (roots.Count == 0)
        {
            RootsPanel.Children.Add(new TextBlock
            {
                Text = "Других дисков нет — добавьте папку вручную.",
                Style = (Style)Root.Resources["CardHint"],
            });
            return;
        }

        foreach (var root in roots)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var isDrive = Path.GetPathRoot(root) == root;
            grid.Children.Add(new FontIcon { Glyph = isDrive ? "" : "", FontSize = 18 });

            var text = new TextBlock { Text = root, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 } };
            ToolTipService.SetToolTip(remove, "Не индексировать");
            remove.Click += (_, _) => UpdateRoots(list => list.RemoveAll(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase)));
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            RootsPanel.Children.Add(new Border { Style = (Style)Root.Resources["Card"], Padding = new Thickness(16, 8, 8, 8), Child = grid });
        }
    }

    private void UpdateRoots(Action<List<string>> change)
    {
        var roots = SettingsStore.Current.EffectiveRoots();
        change(roots);
        SettingsStore.Update(s => s.IndexRoots = roots);
        RenderRoots();
        App.Current.DriveIndex.Start();
        UpdateIndexStatus();
    }

    private async void AddRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        UpdateRoots(list =>
        {
            if (!list.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
                list.Add(folder.Path);
        });
    }

    private void ResetRoots_Click(object sender, RoutedEventArgs e)
    {
        SettingsStore.Update(s => s.IndexRoots = null);
        RenderRoots();
        App.Current.DriveIndex.Start();
        UpdateIndexStatus();
    }

    private void SaveExcludes_Click(object sender, RoutedEventArgs e)
    {
        var excludes = ExcludesBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SettingsStore.Update(s => s.IndexExcludes = excludes);
        ExcludesBox.Text = string.Join(Environment.NewLine, excludes);
        App.Current.DriveIndex.Start();
        App.Current.DriveIndex.Rescan();
        UpdateIndexStatus();
    }

    private void OpenWindowsSearch_Click(object sender, RoutedEventArgs e) =>
        Launcher.Open("ms-settings:cortana-windowssearch");

    // ------------------------------------------------------------------
    // Кнопки
    // ------------------------------------------------------------------

    private void RenderPins()
    {
        PinsList.Children.Clear();
        var items = _pins.Items;
        AddPinButton.IsEnabled = items.Count < PinStore.MaxPins;

        for (var i = 0; i < items.Count; i++)
        {
            var pin = items[i];
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new FontIcon { Glyph = "", FontSize = 18 };
            var image = new Image { Width = 24, Height = 24 };
            grid.Children.Add(glyph);
            grid.Children.Add(image);
            if (pin.IconSource is { } source)
                _ = LoadIconAsync(source, image, glyph);

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = pin.Title });
            texts.Children.Add(new TextBlock
            {
                Text = pin.Target,
                Style = (Style)Root.Resources["CardHint"],
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
            });
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            buttons.Children.Add(IconButton("", "Выше", i > 0, () => _pins.Move(pin, -1)));
            buttons.Children.Add(IconButton("", "Ниже", i < items.Count - 1, () => _pins.Move(pin, +1)));
            buttons.Children.Add(IconButton("", "Убрать", true, () => _pins.Remove(pin)));
            Grid.SetColumn(buttons, 2);
            grid.Children.Add(buttons);

            PinsList.Children.Add(new Border { Style = (Style)Root.Resources["Card"], Padding = new Thickness(16, 8, 8, 8), Child = grid });
        }
    }

    private static Button IconButton(string glyph, string tip, bool enabled, Action action)
    {
        var button = new Button { Content = new FontIcon { Glyph = glyph, FontSize = 14 }, IsEnabled = enabled };
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) => action();
        return button;
    }

    private static async Task LoadIconAsync(string source, Image image, FontIcon glyph)
    {
        var icon = await ShellIcons.GetAsync(source, 32);
        if (icon is null) return;
        image.Source = icon;
        glyph.Visibility = Visibility.Collapsed;
    }

    private async void AddPinFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add("*");
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var title = Path.GetFileNameWithoutExtension(file.Path);
        _pins.Add(new Pin(title, PinKind.Path, file.Path));
    }

    private async void AddPinFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, _hwnd);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        _pins.Add(new Pin(folder.Name, PinKind.Path, folder.Path));
    }

    private async void AddPinUrl_Click(object sender, RoutedEventArgs e)
    {
        var title = new TextBox { Header = "Название", PlaceholderText = "Kawaki" };
        var url = new TextBox { Header = "Адрес", PlaceholderText = "https://kawaki.ru" };
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(title);
        content.Children.Add(url);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = "Кнопка-ссылка",
            Content = content,
            PrimaryButtonText = "Добавить",
            CloseButtonText = "Отмена",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var address = url.Text.Trim();
        if (address.Length == 0) return;
        if (!address.Contains(':'))
            address = "https://" + address;
        var name = title.Text.Trim();
        if (name.Length == 0)
            name = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : address;
        _pins.Add(new Pin(name, PinKind.Url, address));
    }

    private void ResetPins_Click(object sender, RoutedEventArgs e) => _pins.ResetToDefaults();

    // ------------------------------------------------------------------
    // О программе
    // ------------------------------------------------------------------

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Launcher.Open(PinStore.Directory);

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Current.Shutdown();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_recordingHotkey)
            StopRecording(null);
        _pins.Changed -= RenderPins;
        App.Current.DriveIndex.StatusChanged -= OnIndexStatusChanged;
    }
}
