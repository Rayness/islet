using Microsoft.UI.Xaml;

namespace KawakiIsland;

public partial class App : Application
{
    private IslandWindow? _window;

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

        _window = new IslandWindow();
        _window.Activate();
    }
}
