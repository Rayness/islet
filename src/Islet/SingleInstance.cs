namespace Islet;

internal static class SingleInstance
{
    // Держим ссылку до конца процесса, иначе сборщик мусора отпустит мьютекс.
    private static Mutex? _mutex;

    public static bool TryAcquire()
    {
        _mutex = new Mutex(initiallyOwned: true, @"Local\Islet.SingleInstance", out var createdNew);
        return createdNew;
    }

    /// <summary>
    /// Подождать, пока прежний экземпляр отпустит мьютекс. Нужно при перезапуске
    /// (смена языка, обновление): новый процесс стартует, пока старый ещё завершается,
    /// и без ожидания он решил бы, что островок уже есть, и закрылся бы.
    /// </summary>
    public static bool WaitForRelease(TimeSpan timeout)
    {
        if (_mutex is null) return false;
        try
        {
            return _mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            // Прежний процесс завершился, не отпустив мьютекс, — теперь он наш.
            return true;
        }
    }
}
