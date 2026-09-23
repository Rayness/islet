using Islet.Core;
using Islet.Integrations;
using Islet.Plugins;
using Islet.Search;
using Islet.Settings;
using Islet.Shell;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Islet;

/// <summary>
/// Страницы настроек, которые собираются в коде: уведомления, интеграции,
/// плагины и источники поиска. Карточки те же, что в XAML (стиль Card), —
/// код лишь избавляет от сотни одинаковых разметочных блоков.
/// </summary>
public sealed partial class SettingsWindow
{
    private const string RepoUrl = "https://github.com/Rayness/islet";
    private CancellationTokenSource? _kawakiLogin;
    private StackPanel? _kawakiAccount;
    private KawakiClient.DeviceCode? _pendingCode;
    private string? _kawakiMessage;

    // ------------------------------------------------------------------
    // Кирпичики
    // ------------------------------------------------------------------

    private Style StyleOf(string key) => (Style)Root.Resources[key];

    private Border Card(UIElement child, Thickness? padding = null)
    {
        var card = new Border { Style = StyleOf("Card"), Child = child };
        if (padding is { } p) card.Padding = p;
        return card;
    }

    private TextBlock PageHeading(string text) => new() { Text = text, Style = StyleOf("PageTitle") };
    private TextBlock Section(string text) => new() { Text = text, Style = StyleOf("Section") };
    private TextBlock Hint(string text) => new() { Text = text, Style = StyleOf("CardHint") };

    private StackPanel Texts(string title, string? hint)
    {
        var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = title, Style = StyleOf("CardTitle") });
        if (!string.IsNullOrEmpty(hint)) texts.Children.Add(Hint(hint));
        return texts;
    }

    /// <summary>Заголовок с подсказкой слева, элемент управления справа.</summary>
    private Border Row(string title, string? hint, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Texts(title, hint));
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return Card(grid);
    }

    private Border Toggle(string title, string? hint, bool value, Action<bool> changed)
    {
        var toggle = new ToggleSwitch { IsOn = value, OnContent = "", OffContent = "", MinWidth = 0 };
        toggle.Toggled += (_, _) => changed(toggle.IsOn);
        return Row(title, hint, toggle);
    }

    private static Button ActionButton(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += (_, _) => click();
        return button;
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var b in buttons) panel.Children.Add(b);
        return panel;
    }

    /// <summary>Выбор из списка: значения в настройках, подписи — на экране.</summary>
    private Border Choice(string title, string? hint, string[] values, string[] labels, string current, Action<string> changed)
    {
        var combo = new ComboBox { MinWidth = 180, ItemsSource = labels, SelectedIndex = Math.Max(0, Array.IndexOf(values, current)) };
        combo.SelectionChanged += (_, _) => changed(values[Math.Max(0, combo.SelectedIndex)]);
        return Row(title, hint, combo);
    }

    /// <summary>Ползунок с подписью значения справа от заголовка.</summary>
    private Border SliderCard(string title, string? hint, double min, double max, double step, double value,
        Func<double, string> format, Action<double> changed)
    {
        var valueText = Hint(format(value));
        valueText.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = title, Style = StyleOf("CardTitle") });
        header.Children.Add(valueText);

        var slider = new Slider { Minimum = min, Maximum = max, StepFrequency = step, Value = value };
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = format(e.NewValue);
            changed(e.NewValue);
        };

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(header);
        if (hint is not null) panel.Children.Add(Hint(hint));
        panel.Children.Add(slider);
        return Card(panel);
    }

    // ------------------------------------------------------------------
    // Внешний вид: положение островка
    // ------------------------------------------------------------------

    private void BuildLookExtras()
    {
        var s = SettingsStore.Current;
        static string Label(double percent) => percent switch
        {
            <= 0 => Loc.T("S_PosLeft"),
            >= 100 => Loc.T("S_PosRight"),
            50 => Loc.T("S_PosCenter"),
            _ => $"{percent:0}%",
        };

        var valueText = Hint(Label(Math.Round(s.IslandPosition * 100)));
        valueText.HorizontalAlignment = HorizontalAlignment.Right;
        var header = new Grid();
        header.Children.Add(new TextBlock { Text = Loc.T("S_Position"), Style = StyleOf("CardTitle") });
        header.Children.Add(valueText);

        // Ступень 5%: у центра и краёв ползунок встаёт ровно, а не в 49 или 1 процент.
        var slider = new Slider { Minimum = 0, Maximum = 100, StepFrequency = 5, Value = Math.Round(s.IslandPosition * 100) };
        slider.ValueChanged += (_, e) =>
        {
            valueText.Text = Label(e.NewValue);
            SettingsStore.Update(x => x.IslandPosition = e.NewValue / 100);
        };

        var presets = Buttons(
            ActionButton(Loc.T("S_PosLeft"), () => slider.Value = 0),
            ActionButton(Loc.T("S_PosCenter"), () => slider.Value = 50),
            ActionButton(Loc.T("S_PosRight"), () => slider.Value = 100));
        presets.Margin = new Thickness(0, 4, 0, 0);

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(header);
        panel.Children.Add(Hint(Loc.T("S_PositionHint")));
        panel.Children.Add(slider);
        panel.Children.Add(presets);
        LookExtraStack.Children.Add(Card(panel));
    }

    // ------------------------------------------------------------------
    // Поиск: источники
    // ------------------------------------------------------------------

    private void BuildSearchSources()
    {
        var s = SettingsStore.Current;
        var stack = SearchSourcesStack;
        stack.Children.Add(Toggle(Loc.T("S_Calculator"), Loc.T("S_CalculatorHint"), s.CalculatorEnabled,
            on => SettingsStore.Update(x => x.CalculatorEnabled = on)));
        stack.Children.Add(Toggle(Loc.T("S_Commands"), Loc.T("S_CommandsHint"), s.CommandsEnabled,
            on => SettingsStore.Update(x => x.CommandsEnabled = on)));
        stack.Children.Add(Toggle(Loc.T("S_Clipboard"), Loc.T("S_ClipboardHint"), s.ClipboardHistory, on =>
        {
            SettingsStore.Update(x => x.ClipboardHistory = on);
            if (!on) App.Current.Clipboard.Clear();
        }));
        stack.Children.Add(Toggle(Loc.T("S_Recent"), Loc.T("S_RecentHint"), s.ShowRecent,
            on => SettingsStore.Update(x => x.ShowRecent = on)));

        var remember = new ToggleSwitch { IsOn = s.RememberLaunches, OnContent = "", OffContent = "", MinWidth = 0 };
        remember.Toggled += (_, _) => SettingsStore.Update(x => x.RememberLaunches = remember.IsOn);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        controls.Children.Add(ActionButton(Loc.T("S_Forget"), Frecency.Clear));
        controls.Children.Add(remember);
        stack.Children.Add(Row(Loc.T("S_Remember"), Loc.T("S_RememberHint"), controls));
    }

    // ------------------------------------------------------------------
    // Уведомления и живые активности
    // ------------------------------------------------------------------

    private void BuildNotificationsPage()
    {
        var s = SettingsStore.Current;
        var stack = NotificationsStack;
        stack.Children.Add(PageHeading(Loc.T("S_NotifyTitle")));

        var modes = new[] { "peek", "badge", "off" };
        var mode = new ComboBox
        {
            MinWidth = 180,
            ItemsSource = new[] { Loc.T("S_NotifyPeek"), Loc.T("S_NotifyBadge"), Loc.T("S_NotifyOff") },
            SelectedIndex = Math.Max(0, Array.IndexOf(modes, s.NotifyMode)),
        };
        mode.SelectionChanged += (_, _) => SettingsStore.Update(x => x.NotifyMode = modes[Math.Max(0, mode.SelectedIndex)]);
        stack.Children.Add(Row(Loc.T("S_NotifyMode"), Loc.T("S_NotifyModeHint"), mode));

        var secondsValue = Hint(Loc.T("S_Seconds", s.PeekSeconds.ToString("0.#")));
        secondsValue.HorizontalAlignment = HorizontalAlignment.Right;
        var seconds = new Slider { Minimum = 2, Maximum = 15, StepFrequency = 0.5, Value = s.PeekSeconds };
        seconds.ValueChanged += (_, e) =>
        {
            secondsValue.Text = Loc.T("S_Seconds", e.NewValue.ToString("0.#"));
            SettingsStore.Update(x => x.PeekSeconds = e.NewValue);
        };
        var secondsPanel = new StackPanel { Spacing = 4 };
        var secondsHeader = new Grid();
        secondsHeader.Children.Add(new TextBlock { Text = Loc.T("S_PeekSeconds"), Style = StyleOf("CardTitle") });
        secondsHeader.Children.Add(secondsValue);
        secondsPanel.Children.Add(secondsHeader);
        secondsPanel.Children.Add(Hint(Loc.T("S_PeekSecondsHint")));
        secondsPanel.Children.Add(seconds);
        stack.Children.Add(Card(secondsPanel));

        stack.Children.Add(Toggle(Loc.T("S_Sound"), Loc.T("S_SoundHint"), s.NotifySound,
            on => SettingsStore.Update(x => x.NotifySound = on)));

        stack.Children.Add(Row(Loc.T("S_Test"), Loc.T("S_TestHint"), ActionButton(Loc.T("S_TestButton"), () =>
            App.Current.Notifications.Post(new IsletNotification
            {
                Source = "islet",
                SourceName = "Islet",
                Title = Loc.T("S_TestTitle"),
                Body = Loc.T("S_TestBody"),
                Icon = "ms-appx:///Assets/islet.ico",
            }), accent: true)));

        stack.Children.Add(Section(Loc.T("S_LiveSection")));
        stack.Children.Add(Toggle(Loc.T("S_LiveActivities"), Loc.T("S_LiveActivitiesHint"), s.LiveActivities,
            on => SettingsStore.Update(x => x.LiveActivities = on)));

        stack.Children.Add(Section(Loc.T("S_MusicSection")));
        stack.Children.Add(Toggle(Loc.T("S_MediaCapsule"), Loc.T("S_MediaCapsuleHint"), s.MediaInCapsule,
            on => SettingsStore.Update(x => x.MediaInCapsule = on)));
        stack.Children.Add(Toggle(Loc.T("S_MediaCard"), Loc.T("S_MediaCardHint"), s.MediaCard,
            on => SettingsStore.Update(x => x.MediaCard = on)));
        stack.Children.Add(Toggle(Loc.T("S_MediaVolume"), Loc.T("S_MediaVolumeHint"), s.MediaVolume,
            on => SettingsStore.Update(x => x.MediaVolume = on)));

        stack.Children.Add(Section(Loc.T("S_VisualizerSection")));
        stack.Children.Add(Choice(Loc.T("S_VisMode"), Loc.T("S_VisModeHint"),
            ["reactive", "animated", "off"], [Loc.T("S_VisReactive"), Loc.T("S_VisAnimated"), Loc.T("S_VisOff")],
            s.VisualizerMode, v => SettingsStore.Update(x => x.VisualizerMode = v)));
        stack.Children.Add(Choice(Loc.T("S_VisColor"), Loc.T("S_VisColorHint"),
            ["album", "accent", "white"], [Loc.T("S_VisAlbum"), Loc.T("S_VisAccent"), Loc.T("S_VisWhite")],
            s.VisualizerColor, v => SettingsStore.Update(x => x.VisualizerColor = v)));
        stack.Children.Add(SliderCard(Loc.T("S_VisBars"), null, 3, 8, 1, s.VisualizerBars,
            v => v.ToString("0"), v => SettingsStore.Update(x => x.VisualizerBars = (int)v)));
        stack.Children.Add(SliderCard(Loc.T("S_VisSensitivity"), Loc.T("S_VisSensitivityHint"), 50, 300, 10, s.VisualizerSensitivity * 100,
            v => $"{v:0}%", v => SettingsStore.Update(x => x.VisualizerSensitivity = v / 100)));
        stack.Children.Add(Choice(Loc.T("S_VisFps"), Loc.T("S_VisFpsHint"),
            ["15", "30", "60"], [Loc.T("S_VisFpsValue", 15), Loc.T("S_VisFpsValue", 30), Loc.T("S_VisFpsValue", 60)],
            s.VisualizerFps.ToString(), v => SettingsStore.Update(x => x.VisualizerFps = int.Parse(v))));
        stack.Children.Add(Toggle(Loc.T("S_VisInCard"), null, s.VisualizerInCard,
            on => SettingsStore.Update(x => x.VisualizerInCard = on)));

        stack.Children.Add(Section(Loc.T("S_BellSection")));
        stack.Children.Add(Buttons(ActionButton(Loc.T("S_ClearAll"), App.Current.Notifications.Clear)));
    }

    // ------------------------------------------------------------------
    // Интеграции: Kawaki, ClipTide, свои программы
    // ------------------------------------------------------------------

    private void BuildIntegrationsPage()
    {
        var s = SettingsStore.Current;
        var stack = IntegrationsStack;
        stack.Children.Clear();
        stack.Children.Add(PageHeading(Loc.T("S_IntegrationsTitle")));

        // --- Kawaki ---
        stack.Children.Add(Section("Kawaki"));
        _kawakiAccount = new StackPanel { Spacing = 10 };
        stack.Children.Add(Card(_kawakiAccount));
        RenderKawakiAccount();
        stack.Children.Add(Toggle(Loc.T("S_KawakiNotifications"), Loc.T("S_KawakiNotificationsHint"), s.KawakiNotifications, on =>
        {
            SettingsStore.Update(x => x.KawakiNotifications = on);
            App.Current.Kawaki.ApplySettings();
        }));
        stack.Children.Add(Toggle(Loc.T("S_KawakiSearch"), Loc.T("S_KawakiSearchHint"), s.KawakiSearch,
            on => SettingsStore.Update(x => x.KawakiSearch = on)));
        stack.Children.Add(Toggle(Loc.T("S_KawakiGlobal"), Loc.T("S_KawakiGlobalHint"), s.KawakiGlobalSearch,
            on => SettingsStore.Update(x => x.KawakiGlobalSearch = on)));

        // --- ClipTide ---
        stack.Children.Add(Section("ClipTide"));
        var clipTide = App.Current.ClipTide;
        if (clipTide.IsInstalled)
        {
            stack.Children.Add(Row(Loc.T("S_ClipTideFound"), Loc.T("S_ClipTideFoundHint"), new FontIcon
            {
                Glyph = "",
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0x3B, 0xE5, 0xCE)),
            }));
            stack.Children.Add(Toggle(Loc.T("S_ClipTideNotifications"), Loc.T("S_ClipTideNotificationsHint"), s.ClipTideNotifications, on =>
            {
                SettingsStore.Update(x => x.ClipTideNotifications = on);
                clipTide.Start();
            }));
        }
        else
        {
            stack.Children.Add(Row(Loc.T("S_ClipTideMissing"), Loc.T("S_ClipTideMissingHint"),
                ActionButton(Loc.T("S_ClipTideDownload"), () => Launcher.Open("https://github.com/Rayness/YouTube-Downloader/releases/latest"))));
        }

        // --- Свои программы ---
        stack.Children.Add(Section(Loc.T("S_ExternalSection")));
        var example = $"Islet.exe --notify \"Build finished\" \"Done in 42 s\"\n" +
                      $"Islet.exe --activity build \"Building…\" 0.4\n" +
                      $"\\\\.\\pipe\\{Ipc.IpcChannel.PipeName}  ←  {{\"type\":\"notify\",\"title\":\"Hi\"}}";
        var external = new StackPanel { Spacing = 10 };
        external.Children.Add(Hint(Loc.T("S_ExternalHint")));
        external.Children.Add(new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Child = new TextBlock
            {
                Text = example,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                IsTextSelectionEnabled = true,
                TextWrapping = TextWrapping.Wrap,
            },
        });
        external.Children.Add(Buttons(
            ActionButton(Loc.T("S_ExternalProtocol"), () => Launcher.Open($"{RepoUrl}/blob/master/docs/protocol.md")),
            ActionButton(Loc.T("S_CopyExample"), () => ClipboardText.Set(example))));
        stack.Children.Add(Card(external));
    }

    private void OnKawakiChanged() => DispatcherQueue.TryEnqueue(() => Guard.Run(RenderKawakiAccount));

    private void RenderKawakiAccount()
    {
        if (_kawakiAccount is null) return;
        var kawaki = App.Current.Kawaki;
        _kawakiAccount.Children.Clear();

        var header = new Grid { ColumnSpacing = 14 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new Border
        {
            Width = 40,
            Height = 40,
            CornerRadius = new CornerRadius(20),
            Background = new ImageBrush
            {
                ImageSource = new BitmapImage(new Uri(kawaki.SignedIn && kawaki.AvatarUrl is { Length: > 0 } url && url.StartsWith("http")
                    ? url
                    : "ms-appx:///Assets/kawaki.ico")) { DecodePixelWidth = 80 },
                Stretch = Stretch.UniformToFill,
            },
        };
        header.Children.Add(avatar);

        // Сессию отозвали на сайте — говорим об этом, а не молча показываем кнопку входа.
        var expired = Loc.T("Kawaki_SessionExpired");
        var problem = _kawakiMessage ?? (kawaki.LastError == expired ? expired : null);
        var texts = kawaki.SignedIn
            ? Texts(Loc.T("S_KawakiSignedIn", kawaki.Username), null)
            : Texts("kawaki.ru", problem ?? Loc.T("S_KawakiHint"));
        if (!kawaki.SignedIn && problem is not null && texts.Children.LastOrDefault() is TextBlock message)
            message.Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE5, 0x64, 0x5A));
        Grid.SetColumn(texts, 1);
        header.Children.Add(texts);

        FrameworkElement action;
        if (kawaki.SignedIn)
        {
            action = ActionButton(Loc.T("S_KawakiSignOut"), () => _ = kawaki.SignOutAsync());
        }
        else if (_pendingCode is null)
        {
            action = ActionButton(Loc.T("S_KawakiSignIn"), () => _ = StartKawakiLoginAsync(), accent: true);
        }
        else
        {
            action = new ProgressRing { IsActive = true, Width = 22, Height = 22 };
        }
        action.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(action, 2);
        header.Children.Add(action);
        _kawakiAccount.Children.Add(header);

        if (!kawaki.SignedIn && _pendingCode is { } code)
        {
            var codePanel = new StackPanel { Spacing = 8, Margin = new Thickness(54, 4, 0, 0) };
            codePanel.Children.Add(new TextBlock
            {
                Text = code.Formatted,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 30,
                FontWeight = FontWeights.SemiBold,
                CharacterSpacing = 120,
                IsTextSelectionEnabled = true,
            });
            codePanel.Children.Add(Hint(Loc.T("S_KawakiCodeHint")));
            codePanel.Children.Add(Buttons(
                ActionButton(Loc.T("S_KawakiOpenPage"), () => Launcher.Open(code.ActivateUrl), accent: true),
                ActionButton(Loc.T("Cancel"), () => _kawakiLogin?.Cancel())));
            _kawakiAccount.Children.Add(codePanel);
        }
    }

    private async Task StartKawakiLoginAsync()
    {
        _kawakiLogin?.Cancel();
        var cts = _kawakiLogin = new CancellationTokenSource();
        _kawakiMessage = null;
        var kawaki = App.Current.Kawaki;

        var code = await kawaki.StartLoginAsync();
        if (code is null)
        {
            _kawakiMessage = Loc.T("S_KawakiFailed");
            RenderKawakiAccount();
            return;
        }

        _pendingCode = code;
        RenderKawakiAccount();
        // Страницу подтверждения открываем сразу: код в адресе, человеку остаётся нажать «Подтвердить».
        Launcher.Open(code.ActivateUrl);

        var ok = await kawaki.WaitForApprovalAsync(code, cts.Token);
        _pendingCode = null;
        if (!ok) _kawakiMessage = Loc.T("S_KawakiExpired");
        RenderKawakiAccount();
    }

    // ------------------------------------------------------------------
    // Плагины
    // ------------------------------------------------------------------

    private void OnPluginsChanged() => DispatcherQueue.TryEnqueue(() => Guard.Run(RenderPlugins));

    private void RenderPlugins()
    {
        var stack = PluginsStack;
        var manager = App.Current.Plugins;
        stack.Children.Clear();
        stack.Children.Add(PageHeading(Loc.T("S_PluginsTitle")));
        stack.Children.Add(Hint(Loc.T("S_PluginsHint", PluginManager.UserDirectory)));

        var buttons = Buttons(
            ActionButton(Loc.T("S_PluginsOpenFolder"), () =>
            {
                Directory.CreateDirectory(PluginManager.UserDirectory);
                Launcher.Open(PluginManager.UserDirectory);
            }),
            ActionButton(Loc.T("S_PluginsReload"), manager.Load),
            ActionButton(Loc.T("S_PluginsDocs"), () => Launcher.Open($"{RepoUrl}/blob/master/docs/plugins.md")));
        buttons.Margin = new Thickness(0, 8, 0, 8);
        stack.Children.Add(buttons);

        if (manager.Plugins.Count == 0)
        {
            stack.Children.Add(Hint(Loc.T("S_PluginsEmpty")));
            return;
        }

        foreach (var plugin in manager.Plugins)
            stack.Children.Add(PluginCard(plugin));
    }

    private Border PluginCard(PluginInfo plugin)
    {
        var m = plugin.Manifest;
        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var (glyph, image) = plugin.ResolveIcon(m.Icon);
        FrameworkElement icon = image is not null
            ? new Image { Width = 28, Height = 28, Source = new BitmapImage(new Uri(image)) }
            : new FontIcon { Glyph = glyph ?? "", FontSize = 22 };
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        grid.Children.Add(icon);

        var texts = new StackPanel { Spacing = 2 };
        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        name.Children.Add(new TextBlock { Text = plugin.Name, Style = StyleOf("CardTitle"), FontWeight = FontWeights.SemiBold });
        if (plugin.IsBuiltIn)
        {
            name.Children.Add(new Border
            {
                Padding = new Thickness(6, 1, 6, 1),
                CornerRadius = new CornerRadius(4),
                VerticalAlignment = VerticalAlignment.Center,
                Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                Child = new TextBlock { Text = Loc.T("S_PluginBuiltIn"), FontSize = 11 },
            });
        }
        texts.Children.Add(name);
        texts.Children.Add(Hint($"v{m.Version}" + (m.Author is { Length: > 0 } author ? $" · {author}" : "")));
        if (m.Description is { } description && description.ToString().Length > 0)
            texts.Children.Add(Hint(description));

        var keywords = m.Keywords.Concat(m.Shortcuts.Select(sc => sc.Keyword)).Distinct().ToList();
        if (keywords.Count > 0)
            texts.Children.Add(Hint(Loc.T("S_PluginKeywords", string.Join(", ", keywords.Select(k => $"«{k}»")))));
        if (plugin.Error is { } error)
        {
            texts.Children.Add(new TextBlock
            {
                Text = error,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(0xFF, 0xE5, 0x64, 0x5A)),
            });
        }
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);

        var toggle = new ToggleSwitch { IsOn = plugin.Enabled, OnContent = "", OffContent = "", MinWidth = 0, VerticalAlignment = VerticalAlignment.Top };
        toggle.Toggled += (_, _) => App.Current.Plugins.SetEnabled(plugin, toggle.IsOn);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(toggle);

        return Card(grid);
    }
}
