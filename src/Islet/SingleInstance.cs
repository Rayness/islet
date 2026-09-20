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
}
