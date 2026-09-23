namespace Islet;

/// <summary>
/// Исключение внутри колбэка диспетчера или таймера WinUI не доходит до
/// Application.UnhandledException: оно «складывается» (stowed exception) и
/// роняет процесс целиком. Островок живёт весь день — такие места
/// оборачиваются здесь, а ошибка уходит в лог.
/// </summary>
internal static class Guard
{
    public static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            Log.Write($"guarded: {e}");
        }
    }
}
