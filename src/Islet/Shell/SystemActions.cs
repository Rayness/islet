using System.Diagnostics;
using Islet.Native;
using Microsoft.Win32;

namespace Islet.Shell;

/// <summary>Системные действия для команд островка.</summary>
internal static class SystemActions
{
    public static bool Lock() => Win32.LockWorkStation();

    public static bool Sleep() => Win32.SetSuspendState(false, false, false);

    public static bool Shutdown() => RunHidden("shutdown", "/s /t 0");

    public static bool Restart() => RunHidden("shutdown", "/r /t 0");

    public static bool SignOut() => RunHidden("shutdown", "/l");

    public static bool EmptyRecycleBin()
    {
        // Флаги: без подтверждения, без анимации, без звука.
        var hr = Win32.SHEmptyRecycleBin(0, null, 0x1 | 0x2 | 0x4);
        // Пустая корзина отвечает ошибкой — это не повод оставлять островок открытым.
        return hr >= 0 || hr == unchecked((int)0x8000FFFF);
    }

    public static bool IsLightTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
    }

    /// <summary>Светлая ↔ тёмная тема для приложений и системы разом.</summary>
    public static bool ToggleTheme()
    {
        try
        {
            var light = IsLightTheme() ? 0 : 1;
            using (var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                key.SetValue("AppsUseLightTheme", light, RegistryValueKind.DWord);
                key.SetValue("SystemUsesLightTheme", light, RegistryValueKind.DWord);
            }
            Win32.BroadcastSettingChange("ImmersiveColorSet");
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"theme toggle failed: {e.Message}");
            return false;
        }
    }

    private static bool RunHidden(string file, string args)
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false });
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"{file} {args} failed: {e.Message}");
            return false;
        }
    }
}
