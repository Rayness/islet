using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;

namespace Islet.Core;

/// <summary>
/// Таймеры из строки поиска: «timer 5m», «таймер 10 мин», «t 1:30».
/// Идущий таймер — живая активность в капсуле с обратным отсчётом; по
/// окончании — уведомление и системный звук.
///
/// Секундный тик заводится только пока есть хоть один таймер.
/// </summary>
internal sealed partial class TimerService
{
    public sealed record RunningTimer(string Id, string Label, DateTime EndsAt, TimeSpan Duration);

    private readonly List<RunningTimer> _timers = [];
    private DispatcherQueueTimer? _tick;

    public IReadOnlyList<RunningTimer> Timers => _timers;

    public void Attach(DispatcherQueue queue)
    {
        _tick = queue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.Tick += (_, _) => OnTick();
    }

    public RunningTimer Start(TimeSpan duration, string? label)
    {
        var timer = new RunningTimer(Guid.NewGuid().ToString("N")[..8], label ?? "", DateTime.Now + duration, duration);
        _timers.Add(timer);
        _tick?.Start();
        OnTick();
        return timer;
    }

    public void Cancel(string id)
    {
        _timers.RemoveAll(t => t.Id == id);
        App.Current.Activities.Clear("timer");
        OnTick();
    }

    public void CancelAll()
    {
        _timers.Clear();
        App.Current.Activities.Clear("timer");
        _tick?.Stop();
    }

    private string _lastText = "";

    private void OnTick()
    {
        var now = DateTime.Now;
        foreach (var done in _timers.Where(t => t.EndsAt <= now).ToList())
        {
            _timers.Remove(done);
            Finish(done);
        }

        if (_timers.Count == 0)
        {
            _tick?.Stop();
            App.Current.Activities.Clear("timer");
            _lastText = "";
            return;
        }

        var next = _timers.MinBy(t => t.EndsAt)!;
        var left = next.EndsAt - now;
        // Подпись («чай») рядом с отсчётом: несколько таймеров различаются только по ней.
        var text = Format(left) + (next.Label.Length > 0 ? $" · {next.Label}" : "") + (_timers.Count > 1 ? $"  +{_timers.Count - 1}" : "");
        if (text == _lastText) return;
        _lastText = text;

        App.Current.Activities.Set(new LiveActivity
        {
            Id = "timer",
            Source = "timer",
            Text = text,
            Glyph = "",
            Color = left.TotalSeconds <= 10 ? "#FFB454" : null,
            Progress = 1 - left.TotalSeconds / Math.Max(1, next.Duration.TotalSeconds),
            Priority = 100,
        });
    }

    private static void Finish(RunningTimer timer)
    {
        var title = timer.Label.Length > 0 ? timer.Label : Loc.T("Timer_Done");
        App.Current.Notifications.Post(new IsletNotification
        {
            Source = "timer",
            SourceName = Loc.T("Timer_Source"),
            Title = title,
            Body = Loc.T("Timer_DoneBody", Format(timer.Duration)),
            Glyph = "",
        });
        Native.Win32.MessageBeep(0x40);
    }

    public static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        // Секунды вверх: «0:01» висит ровно до конца, а не показывает «0:00» секунду.
        var total = (int)Math.Ceiling(t.TotalSeconds);
        var h = total / 3600;
        var m = total % 3600 / 60;
        var s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    /// <summary>
    /// «5m», «5 мин», «90s», «1h30m», «1:30», «2,5 ч», просто «5» — минуты.
    /// Остаток строки после длительности — подпись таймера.
    /// </summary>
    public static bool TryParse(string text, out TimeSpan duration, out string? label)
    {
        duration = TimeSpan.Zero;
        label = null;
        text = text.Trim();

        var clock = ClockRegex().Match(text);
        if (clock.Success)
        {
            var a = int.Parse(clock.Groups[1].Value);
            var b = int.Parse(clock.Groups[2].Value);
            duration = clock.Groups[3].Success
                ? new TimeSpan(a, b, int.Parse(clock.Groups[3].Value))
                : new TimeSpan(0, a, b);
            label = Rest(text, clock);
            return duration > TimeSpan.Zero;
        }

        var match = PartsRegex().Match(text);
        if (!match.Success || match.Index != 0) return false;

        foreach (Capture capture in match.Groups["part"].Captures)
        {
            var part = UnitRegex().Match(capture.Value);
            if (!part.Success) continue;
            var number = double.Parse(part.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            var unit = part.Groups[2].Value.ToLowerInvariant();
            duration += unit switch
            {
                "h" or "ч" or "час" or "часа" or "часов" or "hr" or "hour" or "hours" => TimeSpan.FromHours(number),
                "s" or "с" or "сек" or "sec" or "секунд" or "секунды" or "секунду" => TimeSpan.FromSeconds(number),
                _ => TimeSpan.FromMinutes(number),
            };
        }
        label = Rest(text, match);
        return duration > TimeSpan.Zero && duration < TimeSpan.FromDays(1);
    }

    private static string? Rest(string text, Match match)
    {
        var rest = text[(match.Index + match.Length)..].Trim();
        return rest.Length > 0 ? rest : null;
    }

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})(?::(\d{2}))?")]
    private static partial Regex ClockRegex();

    [GeneratedRegex(@"^(?:(?<part>\d+(?:[.,]\d+)?\s*(?:(?:часов|часа|час|ч|hours|hour|hr|h|минут[аы]?|мин|min|m|м|секунд[уы]?|сек|sec|s|с)(?![a-zа-яё]))?)\s*)+", RegexOptions.IgnoreCase)]
    private static partial Regex PartsRegex();

    [GeneratedRegex(@"(\d+(?:[.,]\d+)?)\s*([a-zа-я]*)", RegexOptions.IgnoreCase)]
    private static partial Regex UnitRegex();
}
