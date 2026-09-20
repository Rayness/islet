using Velopack;
using Velopack.Sources;

namespace Islet.Shell;

internal enum UpdateState
{
    /// <summary>Приложение распаковано из zip или запущено из папки сборки — обновлять нечего.</summary>
    NotInstalled,
    Idle,
    Checking,
    Downloading,
    UpToDate,
    Ready,
    Failed,
}

/// <summary>
/// Обновления с GitHub Releases через Velopack. Проверка идёт в фоне при запуске;
/// скачанное ставится не молча, а по кнопке — островок перезапускать без спроса не будем.
/// </summary>
internal static class Updater
{
    private const string Repository = "https://github.com/Rayness/islet";

    private static UpdateManager? _manager;
    private static UpdateInfo? _pending;

    public static UpdateState State { get; private set; } = UpdateState.Idle;

    /// <summary>Версия, которая скачана и ждёт перезапуска.</summary>
    public static string? ReadyVersion { get; private set; }

    public static string? Error { get; private set; }

    /// <summary>Любая смена состояния. Вызывается не на UI-потоке.</summary>
    public static event Action? Changed;

    private static UpdateManager Manager => _manager ??= new UpdateManager(new GithubSource(Repository, null, false));

    /// <summary>Установлено ли приложение установщиком: у портативной копии обновлять нечего.</summary>
    public static bool IsInstalled
    {
        get
        {
            try { return Manager.IsInstalled; }
            catch { return false; }
        }
    }

    public static async Task CheckAsync(bool download = true)
    {
        if (!IsInstalled)
        {
            Set(UpdateState.NotInstalled);
            return;
        }
        if (State is UpdateState.Checking or UpdateState.Downloading) return;

        try
        {
            Set(UpdateState.Checking);
            var info = await Manager.CheckForUpdatesAsync();
            if (info is null)
            {
                Set(UpdateState.UpToDate);
                return;
            }
            if (!download)
            {
                _pending = info;
                ReadyVersion = info.TargetFullRelease.Version.ToString();
                Set(UpdateState.Idle);
                return;
            }

            Set(UpdateState.Downloading);
            await Manager.DownloadUpdatesAsync(info);
            _pending = info;
            ReadyVersion = info.TargetFullRelease.Version.ToString();
            Set(UpdateState.Ready);
            Log.Write($"update {ReadyVersion} downloaded");
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Write($"update check failed: {e.Message}");
            Set(UpdateState.Failed);
        }
    }

    /// <summary>Поставить скачанное обновление и вернуться на место.</summary>
    public static void ApplyAndRestart()
    {
        if (_pending is null) return;
        try
        {
            Manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception e)
        {
            Error = e.Message;
            Log.Write($"apply update failed: {e.Message}");
            Set(UpdateState.Failed);
        }
    }

    private static void Set(UpdateState state)
    {
        State = state;
        Changed?.Invoke();
    }
}
