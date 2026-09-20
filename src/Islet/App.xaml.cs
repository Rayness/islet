using Islet.Pins;
using Islet.Search;
using Islet.Settings;
using Microsoft.UI.Xaml;

namespace Islet;

public partial class App : Application
{
    private IslandWindow? _island;
    private SettingsWindow? _settings;

    internal static new App Current => (App)Application.Current;

    internal PinStore Pins { get; } = new();
    internal DriveIndex DriveIndex { get; } = new();

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Второй экземпляр не нужен: островок один на рабочий стол.
        if (!SingleInstance.TryAcquire())
        {
            Exit();
            return;
        }

        SettingsStore.Load();
        Pins.Load();
        DriveIndex.Start();

        _island = new IslandWindow();
        _island.Activate();

        // Ключи для проверки вида без клавиатуры и мыши.
        var cli = Environment.GetCommandLineArgs();
        var settings = Array.IndexOf(cli, "--settings");
        if (settings >= 0)
            OpenSettings(settings + 1 < cli.Length ? cli[settings + 1] : null);
        var demo = Array.IndexOf(cli, "--demo");
        if (demo >= 0 && demo + 1 < cli.Length)
            _island.ShowDemo(cli[demo + 1]);
    }

    internal IslandWindow? Island => _island;

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
        _settings?.Close();
        DriveIndex.Dispose();
        _island?.Close();
        Exit();
    }
}
