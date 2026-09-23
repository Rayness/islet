using Islet.Ipc;
using Microsoft.UI.Dispatching;

namespace Islet;

/// <summary>
/// Точка входа вместо сгенерированной WinUI.
///
/// Зачем своя: второй запуск Islet.exe не должен поднимать WinUI вовсе. Он
/// превращает свои ключи в сообщение, отправляет его работающему островку по
/// именованному каналу и выходит — за десятки миллисекунд, а не за время
/// загрузки XAML. Поэтому `Islet.exe --notify "Готово"` годится для скриптов.
/// </summary>
public static class Program
{
    /// <summary>Сообщение из ключей первого запуска: выполняется, когда окно готово.</summary>
    internal static string? StartupMessage { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        // Установщик Velopack запускает exe со своими ключами (--veloapp-install и т. п.) —
        // такие запуски обслуживаются первыми и мимо проверки второго экземпляра.
        var velopackHook = args.Length > 0 && args[0].StartsWith("--veloapp", StringComparison.OrdinalIgnoreCase);
        if (velopackHook)
            Velopack.VelopackApp.Build().Run();

        var message = Cli.ToMessage(args);
        if (!SingleInstance.TryAcquire())
        {
            // Без ключей — это просто «открой островок»: запуск из Пуска, когда он уже работает.
            if (IpcClient.TrySend(message ?? Cli.OpenMessage(), timeoutMs: 1500))
                return 0;
            // Никто не принял — прежний экземпляр как раз завершается (перезапуск). Ждём его.
            if (!SingleInstance.WaitForRelease(TimeSpan.FromSeconds(5)))
                return 1;
        }

        // Второй запуск сюда не доходит: ему Velopack не нужен, а инициализация стоит сотни миллисекунд.
        if (!velopackHook)
            Velopack.VelopackApp.Build().Run();

        StartupMessage = message;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Microsoft.UI.Xaml.Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
