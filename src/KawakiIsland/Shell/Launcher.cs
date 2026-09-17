using System.Diagnostics;

namespace KawakiIsland.Shell;

internal static class Launcher
{
    /// <summary>
    /// Приложение из списка «Все приложения». Через shell:AppsFolder запускаются
    /// одинаково и обычные программы, и приложения из Microsoft Store.
    /// </summary>
    public static bool OpenApp(string appId) =>
        Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{appId}"));

    /// <summary>Файл, папка, ссылка или протокол (ms-settings:) — тем, чем их открывает Windows.</summary>
    public static bool Open(string target, string? arguments = null) =>
        Start(new ProcessStartInfo(Environment.ExpandEnvironmentVariables(target))
        {
            Arguments = arguments ?? "",
            UseShellExecute = true,
        });

    /// <summary>Открыть папку с выделенным файлом.</summary>
    public static bool Reveal(string path) =>
        Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));

    private static bool Start(ProcessStartInfo info)
    {
        try
        {
            using var _ = Process.Start(info);
            return true;
        }
        catch (Exception e)
        {
            Debug.WriteLine($"Launch failed: {info.FileName} {info.Arguments}: {e.Message}");
            return false;
        }
    }
}
