using System.Text.Json;
using Islet.Core;
using Islet.Integrations;
using Islet.Ipc;
using Islet.Media;
using Islet.Pins;
using Islet.Plugins;
using Islet.Search;
using Islet.Settings;
using Islet.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Islet;

public partial class App : Application
{
    private IslandWindow? _island;
    private SettingsWindow? _settings;
    private DispatcherQueue? _queue;
    private readonly IpcServer _ipc = new();
    // Одноразовые таймеры держим полем: иначе сборщик мусора заберёт их раньше срабатывания.
    private DispatcherQueueTimer? _welcomeTimer;

    internal static new App Current => (App)Application.Current;

    internal PinStore Pins { get; } = new();
    internal DriveIndex DriveIndex { get; } = new();
    internal NotificationCenter Notifications { get; } = new();
    internal ActivityHub Activities { get; } = new();
    internal TimerService Timers { get; } = new();
    internal MediaService Media { get; } = new();
    internal ClipboardHistory Clipboard { get; } = new();
    internal KawakiClient Kawaki { get; } = new();
    internal ClipTideBridge ClipTide { get; } = new();
    internal DeviceMonitor Devices { get; } = new();
    internal PluginManager Plugins { get; } = new();
    internal SearchService Search { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            // Островок живёт весь день: ошибка в одном обработчике не должна его ронять.
            Log.Write($"unhandled: {e.Exception}");
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _queue = DispatcherQueue.GetForCurrentThread();

        SettingsStore.Load();
        Loc.Apply(SettingsStore.Current.Language);
        Notifications.Attach(_queue);
        Activities.Attach(_queue);
        Timers.Attach(_queue);
        Pins.Load();
        DriveIndex.Start();

        Search = new SearchService(DriveIndex, [new KawakiProvider(Kawaki)]);
        var clipTide = new ClipTideProvider(Search.Apps, ClipTide);
        var devices = new DevicesProvider(Devices);
        Search.Extra = () => Plugins.Providers.Append(clipTide).Append(devices);
        ActionRunner.PluginInvoker = Plugins.Invoke;
        Plugins.Load();

        _island = new IslandWindow();
        _island.Activate();

        _ipc.MessageReceived += msg => Protocol.Handle(msg, "ipc", Loc.T("Source_External"));
        _ipc.Start();

        _ = Media.StartAsync(_queue);
        Kawaki.Start();
        ClipTide.Start();
        Devices.Start();

        // Проверка обновлений не должна задерживать запуск островка.
        _ = Updater.CheckAsync();

        if (Program.StartupMessage is { } message)
        {
            using var doc = JsonDocument.Parse(message);
            Protocol.Handle(doc.RootElement.Clone(), "cli", Loc.T("Source_External"));
        }

        if (!SettingsStore.Current.Onboarded)
            Welcome();
    }

    /// <summary>
    /// Первый запуск: полоска у края экрана ни о чём не говорит, поэтому островок
    /// сам раскрывается пиком и рассказывает, как им пользоваться.
    /// </summary>
    private void Welcome()
    {
        SettingsStore.Update(s => s.Onboarded = true);
        var hotkey = SettingsStore.Current.Hotkey.Label;
        var delay = _welcomeTimer = _queue!.CreateTimer();
        delay.Interval = TimeSpan.FromSeconds(1.2);
        delay.IsRepeating = false;
        delay.Tick += (_, _) => Notifications.Post(new IsletNotification
        {
            Source = "islet",
            SourceName = "Islet",
            Title = Loc.T("Welcome_Title"),
            Body = Loc.T("Welcome_Body", hotkey),
            Icon = "ms-appx:///Assets/islet.ico",
            Action = new IsletAction { Query = "?" },
        });
        delay.Start();
    }

    internal IslandWindow? Island => _island;

    /// <summary>Выход по-настоящему — окну островка можно закрыться.</summary>
    internal bool IsShuttingDown { get; private set; }

    /// <summary>Выполнить на UI-потоке (из обработчиков канала, плагинов, таймеров).</summary>
    internal void Dispatch(Action action)
    {
        if (_queue is null) return;
        if (_queue.HasThreadAccess) Guard.Run(action);
        else _queue.TryEnqueue(() => Guard.Run(action));
    }

    internal void OpenSettings(string? page = null)
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow();
            _settings.Closed += (_, _) => _settings = null;
        }
        if (page is not null)
            _settings.ShowPage(page);
        _settings.Activate();
    }

    internal void Shutdown()
    {
        IsShuttingDown = true;
        _settings?.Close();
        _ipc.Dispose();
        Plugins.Dispose();
        Kawaki.Stop();
        ClipTide.Dispose();
        Devices.Dispose();
        Media.Stop();
        Notifications.Save();
        DriveIndex.Dispose();
        _island?.Close();
        Exit();
    }
}
