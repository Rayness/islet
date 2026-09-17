using System.Collections.Concurrent;

namespace KawakiIsland.Shell;

/// <summary>
/// Отдельный STA-поток для COM-объектов оболочки. На UI-потоке они тормозят
/// ввод, а в пуле потоков (MTA) часть из них не работает.
/// </summary>
internal sealed class StaWorker
{
    public static readonly StaWorker Shell = new("KawakiIsland.Shell");

    private readonly BlockingCollection<Action> _queue = [];

    private StaWorker(string name)
    {
        var thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable())
                action();
        })
        {
            IsBackground = true,
            Name = name,
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }
}
