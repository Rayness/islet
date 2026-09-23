using Microsoft.UI.Dispatching;

namespace Islet.Core;

/// <summary>
/// Живая активность — то, что идёт прямо сейчас и видно в свёрнутой капсуле:
/// таймер, прогресс сборки из плагина, загрузка. Капсула в этот момент
/// становится чуть шире полоски и показывает иконку, текст и прогресс.
/// </summary>
public sealed class LiveActivity
{
    public required string Id { get; init; }
    public string Source { get; init; } = "islet";
    public string Text { get; set; } = "";
    /// <summary>0..1 — полоска прогресса по низу капсулы; null — без неё.</summary>
    public double? Progress { get; set; }
    public string? Glyph { get; set; }
    public string? Icon { get; set; }
    /// <summary>Акцентный цвет текста и прогресса, #RRGGBB.</summary>
    public string? Color { get; set; }
    /// <summary>Выше — важнее: таймер 100, внешние 50, музыка показывается отдельно ниже всех.</summary>
    public int Priority { get; init; } = 50;
    public IsletAction? Action { get; set; }
    public DateTime Updated { get; set; } = DateTime.Now;
}

/// <summary>Активности по id. Изменения приходят с любого потока, событие — на UI-потоке.</summary>
internal sealed class ActivityHub
{
    private readonly Dictionary<string, LiveActivity> _items = [];
    private DispatcherQueue? _queue;

    public event Action? Changed;

    public void Attach(DispatcherQueue queue) => _queue = queue;

    /// <summary>Самая важная, при равенстве — самая свежая.</summary>
    public LiveActivity? Current => _items.Values
        .OrderByDescending(a => a.Priority)
        .ThenByDescending(a => a.Updated)
        .FirstOrDefault();

    public IReadOnlyCollection<LiveActivity> All => _items.Values;

    public void Set(LiveActivity activity) => OnUi(() =>
    {
        activity.Updated = DateTime.Now;
        _items[activity.Id] = activity;
        Changed?.Invoke();
    });

    public void Clear(string id) => OnUi(() =>
    {
        if (_items.Remove(id))
            Changed?.Invoke();
    });

    /// <summary>Убрать всё, что прислал источник (плагин выключили или он упал).</summary>
    public void ClearSource(string source) => OnUi(() =>
    {
        var ids = _items.Values.Where(a => a.Source == source).Select(a => a.Id).ToList();
        foreach (var id in ids) _items.Remove(id);
        if (ids.Count > 0) Changed?.Invoke();
    });

    private void OnUi(Action action)
    {
        if (_queue is null || _queue.HasThreadAccess) action();
        else _queue.TryEnqueue(() => Guard.Run(action));
    }
}
