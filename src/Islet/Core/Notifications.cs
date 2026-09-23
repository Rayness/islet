using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Dispatching;

namespace Islet.Core;

/// <summary>
/// Уведомление островка — от Kawaki, ClipTide, таймера, плагина или по каналу.
///
/// Приходит «пиком»: островок сам раскрывается в плашку с текстом, держит её,
/// пока текст читается, и сворачивается. Всё пришедшее остаётся в колоколе —
/// пик лишь показывает, что пришло.
/// </summary>
public sealed class IsletNotification
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    /// <summary>Откуда: kawaki, cliptide, timer, islet, ipc, cli, plugin:&lt;id&gt;.</summary>
    public string Source { get; init; } = "islet";
    /// <summary>Подпись источника в колоколе: «Kawaki», «ClipTide»…</summary>
    public string SourceName { get; init; } = "Islet";
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    /// <summary>Глиф Segoe Fluent Icons, если нет картинки.</summary>
    public string? Glyph { get; init; }
    /// <summary>Картинка: https://, ms-appx:/// или путь к файлу (берётся иконка оболочки).</summary>
    public string? Icon { get; init; }
    public IsletAction? Action { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
    public bool IsRead { get; set; }
    /// <summary>Id у источника — Kawaki по нему отмечает прочитанным.</summary>
    public string? ExternalId { get; init; }
    /// <summary>Только в колокол, без пика (например, старые непрочитанные при входе).</summary>
    [JsonIgnore]
    public bool Silent { get; init; }
}

/// <summary>
/// Колокол: история уведомлений, счётчик непрочитанных, очередь пиков.
/// Принимает уведомления с любого потока; события приходят на UI-потоке.
/// </summary>
internal sealed class NotificationCenter
{
    private const int MaxHistory = 60;
    private static readonly string FilePath = Path.Combine(Paths.Config, "notifications.json");

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly List<IsletNotification> _history = [];
    private DispatcherQueue? _queue;
    private DispatcherQueueTimer? _saveTimer;

    public IReadOnlyList<IsletNotification> History => _history;
    public int UnreadCount { get; private set; }

    /// <summary>История или счётчик поменялись.</summary>
    public event Action? Changed;
    /// <summary>Пришло новое — островку решать, показывать ли пик.</summary>
    public event Action<IsletNotification>? Posted;

    public void Attach(DispatcherQueue queue)
    {
        _queue = queue;
        _saveTimer = queue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromSeconds(2);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
        Load();
    }

    public void Post(IsletNotification n)
    {
        if (_queue is null) return;
        if (!_queue.HasThreadAccess)
        {
            _queue.TryEnqueue(() => Guard.Run(() => Post(n)));
            return;
        }

        // Повтор того же уведомления от источника (опрос Kawaki после перезапуска) не дублируем.
        if (n.ExternalId is not null && _history.Any(h => h.Source == n.Source && h.ExternalId == n.ExternalId))
            return;

        _history.Insert(0, n);
        if (_history.Count > MaxHistory)
            _history.RemoveRange(MaxHistory, _history.Count - MaxHistory);
        Recount();
        Changed?.Invoke();
        ScheduleSave();
        if (!n.Silent)
            Posted?.Invoke(n);
    }

    public void MarkAllRead()
    {
        if (UnreadCount == 0) return;
        foreach (var n in _history) n.IsRead = true;
        Recount();
        Changed?.Invoke();
        ScheduleSave();
    }

    public void MarkRead(IsletNotification n)
    {
        if (n.IsRead) return;
        n.IsRead = true;
        Recount();
        Changed?.Invoke();
        ScheduleSave();
    }

    public void Remove(IsletNotification n)
    {
        if (!_history.Remove(n)) return;
        Recount();
        Changed?.Invoke();
        ScheduleSave();
    }

    public void Clear()
    {
        _history.Clear();
        Recount();
        Changed?.Invoke();
        ScheduleSave();
    }

    private void Recount() => UnreadCount = _history.Count(n => !n.IsRead);

    private void ScheduleSave()
    {
        _saveTimer?.Stop();
        _saveTimer?.Start();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var items = JsonSerializer.Deserialize<List<IsletNotification>>(File.ReadAllText(FilePath), Json);
            if (items is null) return;
            _history.AddRange(items.Take(MaxHistory));
            Recount();
        }
        catch (Exception e)
        {
            Log.Write($"notifications.json unreadable: {e.Message}");
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Paths.Config);
            // Действия внутри процесса (Callback) не сохраняются — у таких уведомлений
            // после перезапуска просто не будет действия.
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_history, Json));
        }
        catch (Exception e)
        {
            Log.Write($"notifications.json not saved: {e.Message}");
        }
    }
}
