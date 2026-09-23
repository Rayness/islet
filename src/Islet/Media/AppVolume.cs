using System.Diagnostics;
using System.Runtime.InteropServices;
using Islet.Native;

namespace Islet.Media;

/// <summary>
/// Громкость того приложения, которое сейчас играет, — как ползунок в микшере
/// громкости Windows, только прямо в островке.
///
/// Медиасеанс знает приложение по AUMID («Spotify.exe», «Chrome», «MSEdge»,
/// «Microsoft.ZuneMusic_…!Microsoft.ZuneMusic»), аудиосеанс — по процессу.
/// Связываем их по имени: слова из AUMID против имени процесса и пути в
/// идентификаторе сеанса. У браузера аудиосеансов бывает несколько — громкость
/// ставится всем сразу, как это делает микшер.
///
/// Всё на пуле потоков: Core Audio свободнопоточный, а UI ждать не должен.
/// </summary>
internal static class AppVolume
{
    private static Guid _context = Guid.NewGuid();

    /// <summary>Громкость 0…1 или null, если аудиосеанс приложения не нашёлся.</summary>
    public static Task<float?> GetAsync(string appId) => Task.Run(() =>
    {
        float? result = null;
        Visit(appId, volume =>
        {
            if (result is null && volume.GetMasterVolume(out var level) == 0)
                result = level;
        });
        return result;
    });

    public static Task SetAsync(string appId, float level) => Task.Run(() =>
    {
        level = Math.Clamp(level, 0, 1);
        Visit(appId, volume => volume.SetMasterVolume(level, ref _context));
    });

    private static void Visit(string appId, Action<ISimpleAudioVolume> action)
    {
        if (string.IsNullOrWhiteSpace(appId)) return;
        var tokens = Tokens(appId);
        if (tokens.Count == 0) return;

        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            device = CoreAudio.DefaultRenderDevice();
            manager = CoreAudio.Activate<IAudioSessionManager2>(device, CoreAudio.IID_IAudioSessionManager2);
            if (manager.GetSessionEnumerator(out sessions) != 0) return;
            sessions.GetCount(out var count);
            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var raw) != 0) continue;
                try
                {
                    if (raw is not IAudioSessionControl2 control) continue;
                    if (!Matches(control, tokens)) continue;
                    if (raw is ISimpleAudioVolume volume) action(volume);
                }
                finally
                {
                    Marshal.ReleaseComObject(raw);
                }
            }
        }
        catch (Exception e)
        {
            Log.Write($"app volume: {e.Message}");
        }
        finally
        {
            if (sessions is not null) Marshal.ReleaseComObject(sessions);
            if (manager is not null) Marshal.ReleaseComObject(manager);
            if (device is not null) Marshal.ReleaseComObject(device);
        }
    }

    private static bool Matches(IAudioSessionControl2 control, List<string> tokens)
    {
        var haystack = new List<string>();
        if (control.GetProcessId(out var pid) == 0 && pid != 0)
        {
            try
            {
                using var process = Process.GetProcessById((int)pid);
                haystack.Add(process.ProcessName.ToLowerInvariant());
            }
            catch { /* процесс уже закрылся */ }
        }
        if (control.GetSessionIdentifier(out var id) == 0 && id is not null)
            haystack.Add(id.ToLowerInvariant());

        foreach (var hay in haystack)
        {
            foreach (var token in tokens)
            {
                if (hay == token || hay.Contains(token, StringComparison.Ordinal)) return true;
            }
        }
        return false;
    }

    /// <summary>«Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic» → zunemusic; «Spotify.exe» → spotify.</summary>
    private static List<string> Tokens(string appId)
    {
        var id = appId.ToLowerInvariant();
        var bang = id.IndexOf('!');
        var head = bang >= 0 ? id[..bang] : id;
        var underscore = head.IndexOf('_');
        if (underscore > 0) head = head[..underscore];
        if (head.EndsWith(".exe")) head = head[..^4];

        var tokens = new List<string>();
        foreach (var part in head.Split(['.', ' ', '-'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length >= 4 && part is not ("microsoft" or "windows" or "com" or "app"))
                tokens.Add(part);
        }
        // Браузеры сообщают о себе не так, как называется процесс.
        if (id is "msedge" || id.Contains("edge")) tokens.Add("msedge");
        if (id.Contains("chrome")) tokens.Add("chrome");
        if (id.Contains("yandex")) tokens.Add("browser");
        if (tokens.Count == 0 && head.Length >= 3) tokens.Add(head);
        return tokens;
    }
}
