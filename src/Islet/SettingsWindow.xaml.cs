using System.Reflection;
using Islet.Native;
using Islet.Pins;
using Islet.Settings;
using Islet.Shell;
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

namespace Islet;

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

        Title = Loc.T("Settings_Title");
        ToolTipService.SetToolTip(HotkeyResetButton, Loc.T("Hotkey_DefaultTip"));

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "islet.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = 640;
            presenter.PreferredMinimumHeight = 480;
        }
        PlaceWindow();

        LoadValues();
        BuildSearchSources();
        BuildNotificationsPage();
        BuildIntegrationsPage();
        RenderPlugins();
        Nav.SelectedItem = Nav.MenuItems[0];

        _pins.Changed += RenderPins;
        App.Current.Kawaki.StateChanged += OnKawakiChanged;
        App.Current.Plugins.Changed += OnPluginsChanged;
        App.Current.DriveIndex.StatusChanged += OnIndexStatusChanged;
        Updater.Changed += OnUpdateStateChanged;
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
        WidthValue.Text = Loc.T("Px_Format", s.IslandWidth.ToString("0"));
        CollapsedWidthSlider.Value = s.CollapsedWidth;
        CollapsedWidthValue.Text = Loc.T("Px_Format", s.CollapsedWidth.ToString("0"));
        CollapsedHeightSlider.Value = s.CollapsedHeight;
        CollapsedHeightValue.Text = Loc.T("Px_Format", s.CollapsedHeight.ToString("0"));
        MaxRowsSlider.Value = s.MaxRows;
        MaxRowsValue.Text = s.MaxRows.ToString();
        ClockToggle.IsOn = s.ShowClock;

        HoverOpenToggle.IsOn = s.HoverOpen;
        OpenDelaySlider.Value = s.HoverOpenDelayMs;
        OpenDelayValue.Text = Loc.T("Ms_Format", s.HoverOpenDelayMs);
        CloseDelaySlider.Value = s.HoverCloseDelayMs;
        CloseDelayValue.Text = Loc.T("Ms_Format", s.HoverCloseDelayMs);
        HideCollapsedToggle.IsOn = s.HideCollapsed;
        FullscreenToggle.IsOn = s.HideOnFullscreen;
        MonitorCombo.ItemsSource = new[] { Loc.T("Monitor_Primary"), Loc.T("Monitor_Cursor") };
        MonitorCombo.SelectedIndex = s.MonitorMode == "cursor" ? 1 : 0;

        LanguageCombo.ItemsSource = LanguageOptions.Select(o => o.Label).ToList();
        LanguageCombo.SelectedIndex = Math.Max(0, Array.FindIndex(LanguageOptions, o => o.Id == s.Language));

        IndexToggle.IsOn = s.DriveIndexEnabled;
        ExcludesBox.Text = string.Join(Environment.NewLine, s.IndexExcludes);
        RenderRoots();
        UpdateIndexStatus();

        RenderPins();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = Loc.T("About_Version", version?.ToString(3));
        DataFolderText.Text = Paths.Config;
        RenderUpdateState();
    }

    /// <summary>Открыть раздел по тегу: general, behavior, notifications, look, search, pins, integrations, plugins, about.</summary>
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
        BehaviorPage.Visibility = tag == "behavior" ? Visibility.Visible : Visibility.Collapsed;
        LookPage.Visibility = tag == "look" ? Visibility.Visible : Visibility.Collapsed;
        SearchPage.Visibility = tag == "search" ? Visibility.Visible : Visibility.Collapsed;
        PinsPage.Visibility = tag == "pins" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPage.Visibility = tag == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        IntegrationsPage.Visibility = tag == "integrations" ? Visibility.Visible : Visibility.Collapsed;
        PluginsPage.Visibility = tag == "plugins" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------
    // Общие
    // ------------------------------------------------------------------

    private void UpdateHotkeyStatus()
    {
        var taken = App.Current.Island?.HotkeyRegistered == false;
        HotkeyStatusText.Text = taken ? Loc.T("Hotkey_TakenHint") : Loc.T("HotkeyHint");
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
        HotkeyText.Text = Loc.T("Hotkey_Press");
        HotkeyButton.Content = Loc.T("Cancel");
        HotkeyStatusText.Text = Loc.T("Hotkey_Help");
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
            HotkeyStatusText.Text = Loc.T("Hotkey_NeedModifier");
            return;
        }
        // Одинокий Shift с буквой — это просто заглавная буква при наборе.
        if (mods == Win32.MOD_SHIFT && !isFunctionKey)
        {
            HotkeyStatusText.Text = Loc.T("Hotkey_ShiftOnly");
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
        HotkeyButton.Content = Loc.T("Change");

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

    private void CollapsedWidthSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CollapsedWidthValue is null) return;
        CollapsedWidthValue.Text = Loc.T("Px_Format", e.NewValue.ToString("0"));
        if (_loading) return;
        SettingsStore.Update(s => s.CollapsedWidth = e.NewValue);
    }

    private void CollapsedHeightSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CollapsedHeightValue is null) return;
        CollapsedHeightValue.Text = Loc.T("Px_Format", e.NewValue.ToString("0"));
        if (_loading) return;
        SettingsStore.Update(s => s.CollapsedHeight = e.NewValue);
    }

    private void MaxRowsSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (MaxRowsValue is null) return;
        MaxRowsValue.Text = e.NewValue.ToString("0");
        if (_loading) return;
        SettingsStore.Update(s => s.MaxRows = (int)e.NewValue);
    }

    // ------------------------------------------------------------------
    // Поведение
    // ------------------------------------------------------------------

    private void HoverOpenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.HoverOpen = HoverOpenToggle.IsOn);
    }

    private void OpenDelaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (OpenDelayValue is null) return;
        OpenDelayValue.Text = Loc.T("Ms_Format", (int)e.NewValue);
        if (_loading) return;
        SettingsStore.Update(s => s.HoverOpenDelayMs = (int)e.NewValue);
    }

    private void CloseDelaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (CloseDelayValue is null) return;
        CloseDelayValue.Text = Loc.T("Ms_Format", (int)e.NewValue);
        if (_loading) return;
        SettingsStore.Update(s => s.HoverCloseDelayMs = (int)e.NewValue);
    }

    private void HideCollapsedToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.HideCollapsed = HideCollapsedToggle.IsOn);
    }

    private void FullscreenToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        SettingsStore.Update(s => s.HideOnFullscreen = FullscreenToggle.IsOn);
    }

    private void MonitorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var mode = MonitorCombo.SelectedIndex == 1 ? "cursor" : "primary";
        SettingsStore.Update(s => s.MonitorMode = mode);
    }

    // ------------------------------------------------------------------
    // Язык
    // ------------------------------------------------------------------

    /// <summary>Пустой Id — язык системы. Названия языков не переводятся.</summary>
    private static readonly (string Id, string Label)[] LanguageOptions =
    [
        ("", Loc.T("Language_System")),
        ("ru", "Русский"),
        ("en", "English"),
    ];

    private void LanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || LanguageCombo.SelectedIndex < 0) return;
        var id = LanguageOptions[LanguageCombo.SelectedIndex].Id;
        if (id == SettingsStore.Current.Language) return;

        SettingsStore.Update(s => s.Language = id);
        Loc.Apply(id);

        // x:Uid разбирается один раз при загрузке окна, поэтому язык меняем перезапуском.
        try
        {
            Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
        }
        catch (Exception ex)
        {
            Log.Write($"restart after language change failed: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Обновления
    // ------------------------------------------------------------------

    private void OnUpdateStateChanged() =>
        DispatcherQueue.TryEnqueue(() => Guard.Run(RenderUpdateState));

    private void RenderUpdateState()
    {
        if (UpdateStatusText is null) return;

        UpdateStatusText.Text = Updater.State switch
        {
            UpdateState.NotInstalled => Loc.T("Update_Portable"),
            UpdateState.Checking => Loc.T("Update_Checking"),
            UpdateState.Downloading => Loc.T("Update_Downloading"),
            UpdateState.UpToDate => Loc.T("Update_UpToDate"),
            UpdateState.Ready => Loc.T("Update_Ready", Updater.ReadyVersion),
            UpdateState.Failed => Loc.T("Update_Failed", Updater.Error),
            _ => "",
        };

        var busy = Updater.State is UpdateState.Checking or UpdateState.Downloading;
        UpdateCheckButton.IsEnabled = !busy && Updater.State != UpdateState.NotInstalled;

        var ready = Updater.State == UpdateState.Ready;
        UpdateApplyButton.Visibility = ready ? Visibility.Visible : Visibility.Collapsed;
        if (ready)
            UpdateApplyButton.Content = Loc.T("Update_Restart");
    }

    private void UpdateCheck_Click(object sender, RoutedEventArgs e) => _ = Updater.CheckAsync();

    private void UpdateApply_Click(object sender, RoutedEventArgs e) => Updater.ApplyAndRestart();

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

    private void OnIndexStatusChanged() => DispatcherQueue.TryEnqueue(() => Guard.Run(UpdateIndexStatus));

    private void UpdateIndexStatus()
    {
        var index = App.Current.DriveIndex;
        var s = SettingsStore.Current;
        RescanButton.IsEnabled = s.DriveIndexEnabled && !index.IsScanning;

        if (!s.DriveIndexEnabled)
            IndexStatusText.Text = Loc.T("Index_Off");
        else if (index.IsScanning)
            IndexStatusText.Text = Loc.T("Index_Building", index.Count.ToString("N0"));
        else if (index.LastError is { } error)
            IndexStatusText.Text = Loc.T("Index_Error", error);
        else if (index.BuiltAt == default)
            IndexStatusText.Text = Loc.T("Index_NotBuilt");
        else
            IndexStatusText.Text = Loc.T("Index_Ready",
                index.Count.ToString("N0"),
                index.BuiltAt.ToLocalTime().ToString("dd.MM HH:mm"));
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
                Text = Loc.T("Roots_Empty"),
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
            ToolTipService.SetToolTip(remove, Loc.T("Roots_Remove"));
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
            buttons.Children.Add(IconButton("", Loc.T("Pin_Up"), i > 0, () => _pins.Move(pin, -1)));
            buttons.Children.Add(IconButton("", Loc.T("Pin_Down"), i < items.Count - 1, () => _pins.Move(pin, +1)));
            buttons.Children.Add(IconButton("", Loc.T("Pin_Remove"), true, () => _pins.Remove(pin)));
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
        var title = new TextBox { Header = Loc.T("PinDialog_Name"), PlaceholderText = "GitHub" };
        var url = new TextBox { Header = Loc.T("PinDialog_Url"), PlaceholderText = "https://github.com" };
        var content = new StackPanel { Spacing = 12, MinWidth = 360 };
        content.Children.Add(title);
        content.Children.Add(url);

        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
            Title = Loc.T("PinDialog_Title"),
            Content = content,
            PrimaryButtonText = Loc.T("Add"),
            CloseButtonText = Loc.T("Cancel"),
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

    private void OpenDataFolder_Click(object sender, RoutedEventArgs e) => Launcher.Open(Paths.Config);

    private void Exit_Click(object sender, RoutedEventArgs e) => App.Current.Shutdown();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (_recordingHotkey)
            StopRecording(null);
        _pins.Changed -= RenderPins;
        App.Current.DriveIndex.StatusChanged -= OnIndexStatusChanged;
        Updater.Changed -= OnUpdateStateChanged;
        App.Current.Kawaki.StateChanged -= OnKawakiChanged;
        App.Current.Plugins.Changed -= OnPluginsChanged;
        _kawakiLogin?.Cancel();
    }
}
