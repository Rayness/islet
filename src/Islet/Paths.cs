namespace Islet;

/// <summary>
/// Где лежат данные. Настройки и кнопки — в Roaming, индекс и лог — в Local:
/// установщик Velopack ставит приложение в %LOCALAPPDATA%\Islet и при каждой установке
/// вычищает эту папку, поэтому ничего своего там держать нельзя.
/// </summary>
internal static class Paths
{
    /// <summary>%APPDATA%\Islet — settings.json и pins.json.</summary>
    public static string Config { get; } = Resolve();

    /// <summary>%LOCALAPPDATA%\Islet.cache — индекс дисков и лог, их не жалко потерять.</summary>
    public static string Cache { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Islet.cache");

    private static string Resolve()
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var config = Path.Combine(roaming, "Islet");

        // Старые места: %LOCALAPPDATA%\Islet (до установщика) и \KawakiIsland (до переименования).
        foreach (var legacy in new[] { Path.Combine(local, "Islet"), Path.Combine(local, "KawakiIsland") })
            Migrate(legacy, config);

        return config;
    }

    /// <summary>Переносит настройки и кнопки из старой папки; кеш и лог просто бросаем.</summary>
    private static void Migrate(string from, string to)
    {
        try
        {
            if (!Directory.Exists(from)) return;
            Directory.CreateDirectory(to);
            foreach (var name in new[] { "settings.json", "pins.json" })
            {
                var source = Path.Combine(from, name);
                var target = Path.Combine(to, name);
                if (File.Exists(source) && !File.Exists(target))
                    File.Copy(source, target);
            }
        }
        catch
        {
            // Не вышло — начнём с настроек по умолчанию, это не повод не запускаться.
        }
    }
}
