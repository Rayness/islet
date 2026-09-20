using Microsoft.Windows.ApplicationModel.Resources;

namespace Islet;

/// <summary>
/// Строки интерфейса. Лежат в Strings/&lt;язык&gt;/Resources.resw, язык берётся от Windows;
/// в настройках его можно переопределить — тогда приложение перезапускается,
/// потому что XAML разбирает x:Uid один раз при загрузке окна.
/// </summary>
internal static class Loc
{
    private static readonly ResourceLoader Loader = new();

    public static string T(string key) => Loader.GetString(key);

    public static string T(string key, params object?[] args) =>
        string.Format(Loader.GetString(key), args);

    /// <summary>
    /// Пусто — язык системы. Сбросить переопределение пустой строкой нельзя: вне пакета
    /// MRT отвечает «параметр неверен», поэтому «как в Windows» — это язык, который
    /// Windows называет первым в списке предпочтений.
    /// </summary>
    public static void Apply(string? language)
    {
        var target = string.IsNullOrEmpty(language) ? SystemLanguage() : language;
        if (string.IsNullOrEmpty(target)) return;
        try
        {
            Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = target;
        }
        catch (Exception e)
        {
            Log.Write($"language override '{target}' failed: {e.Message}");
        }
    }

    private static string SystemLanguage()
    {
        try
        {
            var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            if (languages.Count > 0) return languages[0];
        }
        catch { /* ниже есть запасной путь */ }
        return System.Globalization.CultureInfo.InstalledUICulture.Name;
    }
}
