using System.Diagnostics;

namespace Islet;

/// <summary>Короткий лог в %LOCALAPPDATA%\Islet\island.log — чтобы разбирать поведение без отладчика.</summary>
internal static class Log
{
    private const long MaxBytes = 512 * 1024;
    private static readonly Lock Gate = new();
    private static readonly string FilePath = Path.Combine(Pins.PinStore.Directory, "island.log");

    public static void Write(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Pins.PinStore.Directory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch { /* лог не должен ронять островок */ }
        }
    }
}
