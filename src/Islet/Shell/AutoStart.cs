using Microsoft.Win32;

namespace Islet.Shell;

/// <summary>Запуск вместе с Windows через HKCU\...\Run — без прав администратора.</summary>
internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Islet";
    private const string LegacyValueName = "KawakiIsland";

    static AutoStart()
    {
        // Приложение раньше звалось Kawaki Island. Переносим запись на новое имя, иначе
        // в автозапуске останется строка со старым путём, которая уже никуда не ведёт.
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is not string) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        }
        catch { /* автозапуск не должен мешать запуску */ }
    }

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled)
            key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
