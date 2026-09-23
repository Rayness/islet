using System.Runtime.InteropServices;

namespace Islet.Native;

internal static class ShellFolders
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    public static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, nint hToken, out string pszPath);
}
