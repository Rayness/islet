using Islet.Settings;
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

    /// <summary>
    /// Строка по ключу. Ключи свойств из x:Uid («SearchBox.PlaceholderText») в MRT
    /// адресуются через «/» — переводим сами. Нет строки — ключ, а не исключение:
    /// брошенное в обработчике XAML оно роняет процесс целиком (stowed exception).
    /// </summary>
    public static string T(string key)
    {
        try
        {
            var value = Loader.GetString(key.Replace('.', '/'));
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch
        {
            return key;
        }
    }

    /// <summary>Язык интерфейса двумя буквами: ru, en. Передаётся плагинам.</summary>
    public static string CurrentLanguage
    {
        get
        {
            var language = SettingsStore.Current.Language is { Length: > 0 } chosen ? chosen : SystemLanguage();
            return language.Split('-')[0].ToLowerInvariant();
        }
    }

    public static string T(string key, params object?[] args)
    {
        try { return string.Format(T(key), args); }
        catch (FormatException) { return T(key); }
    }

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
