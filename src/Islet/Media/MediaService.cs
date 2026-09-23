using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Control;

namespace Islet.Media;

/// <summary>
/// «Сейчас играет» — то же, что показывает Windows над громкостью: любой
/// плеер, который сообщает о себе системе (Spotify, браузер, Яндекс Музыка,
/// AIMP, медиаплеер Windows).
///
/// Работает только на событиях: ни одного таймера, пока играет музыка.
/// Обложка декодируется один раз на трек и в размер капсулы.
/// </summary>
internal sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private DispatcherQueue? _queue;
    private string _thumbKey = "";
    private int _generation;

    public bool HasSession => _session is not null && Title.Length > 0;
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string AppId { get; private set; } = "";
    public bool IsPlaying { get; private set; }
    public bool CanNext { get; private set; }
    public bool CanPrevious { get; private set; }
    public ImageSource? Thumbnail { get; private set; }

    public TimeSpan Position { get; private set; }
    public TimeSpan Duration { get; private set; }
    public DateTimeOffset PositionUpdatedAt { get; private set; }

    /// <summary>Любое изменение. Всегда на UI-потоке.</summary>
    public event Action? Changed;

    public async Task StartAsync(DispatcherQueue queue)
    {
        _queue = queue;
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => OnUi(AttachCurrent);
            _manager.SessionsChanged += (_, _) => OnUi(AttachCurrent);
            AttachCurrent();
        }
        catch (Exception e)
        {
            Log.Write($"media sessions unavailable: {e.Message}");
        }
    }

    public void Stop()
    {
        Detach();
        _manager = null;
    }

    /// <summary>Текущая позиция с поправкой на время с последнего сообщения плеера.</summary>
    public TimeSpan EstimatedPosition
    {
        get
        {
            if (!IsPlaying || PositionUpdatedAt == default) return Position;
            var estimated = Position + (DateTimeOffset.Now - PositionUpdatedAt);
            return Duration > TimeSpan.Zero && estimated > Duration ? Duration : estimated;
        }
    }

    public async void TogglePlayPause() => await Try(s => s.TryTogglePlayPauseAsync().AsTask());
    public async void Next() => await Try(s => s.TrySkipNextAsync().AsTask());
    public async void Previous() => await Try(s => s.TrySkipPreviousAsync().AsTask());

    private async Task Try(Func<GlobalSystemMediaTransportControlsSession, Task<bool>> command)
    {
        if (_session is not { } session) return;
        try { await command(session); }
        catch (Exception e) { Log.Write($"media command failed: {e.Message}"); }
    }

    private void AttachCurrent()
    {
        var current = PickSession();
        if (ReferenceEquals(current, _session))
        {
            _ = RefreshAsync();
            return;
        }

        Detach();
        _session = current;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += OnPropertiesChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }
        _ = RefreshAsync();
    }

    /// <summary>
    /// Текущий сеанс по мнению Windows — это последний, кто нажимал «играть».
    /// Если он на паузе, а другой играет, показываем играющий.
    /// </summary>
    private GlobalSystemMediaTransportControlsSession? PickSession()
    {
        if (_manager is null) return null;
        var current = _manager.GetCurrentSession();
        try
        {
            if (current?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                return current;
            var playing = _manager.GetSessions().FirstOrDefault(s =>
                s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
            return playing ?? current;
        }
        catch
        {
            return current;
        }
    }

    private void Detach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= OnPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackChanged;
        _session.TimelinePropertiesChanged -= OnTimelineChanged;
        _session = null;
    }

    private void OnPropertiesChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) => OnUi(() => _ = RefreshAsync());
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) => OnUi(OnPlayback);
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) => OnUi(() =>
    {
        ReadTimeline();
        Changed?.Invoke();
    });

    private void OnPlayback()
    {
        // Другой плеер мог начать играть, пока текущий стоит на паузе.
        var better = PickSession();
        if (!ReferenceEquals(better, _session))
        {
            AttachCurrent();
            return;
        }
        ReadPlayback();
        Changed?.Invoke();
    }

    private void ReadPlayback()
    {
        try
        {
            var info = _session?.GetPlaybackInfo();
            IsPlaying = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            CanNext = info?.Controls.IsNextEnabled ?? false;
            CanPrevious = info?.Controls.IsPreviousEnabled ?? false;
        }
        catch
        {
            IsPlaying = false;
        }
    }

    private void ReadTimeline()
    {
        try
        {
            var t = _session?.GetTimelineProperties();
            if (t is null) return;
            Position = t.Position - t.StartTime;
            Duration = t.EndTime - t.StartTime;
            PositionUpdatedAt = t.LastUpdatedTime;
        }
        catch { /* плеер не сообщает таймлайн — полоски не будет */ }
    }

    private async Task RefreshAsync()
    {
        var generation = ++_generation;
        if (_session is not { } session)
        {
            Title = Artist = AppId = "";
            IsPlaying = false;
            Thumbnail = null;
            _thumbKey = "";
            Changed?.Invoke();
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (generation != _generation) return;
            Title = props?.Title ?? "";
            Artist = props?.Artist is { Length: > 0 } artist ? artist : props?.AlbumArtist ?? "";
            AppId = session.SourceAppUserModelId ?? "";
            ReadPlayback();
            ReadTimeline();

            var key = $"{AppId}|{Title}|{Artist}";
            if (key != _thumbKey)
            {
                _thumbKey = key;
                Thumbnail = props?.Thumbnail is { } reference ? await LoadThumbnailAsync(reference) : null;
                if (generation != _generation) return;
            }
        }
        catch (Exception e)
        {
            Log.Write($"media properties failed: {e.Message}");
        }
        Changed?.Invoke();
    }

    private static async Task<ImageSource?> LoadThumbnailAsync(Windows.Storage.Streams.IRandomAccessStreamReference reference)
    {
        try
        {
            using var stream = await reference.OpenReadAsync();
            // Декодируем сразу в размер карточки: обложки бывают 1200×1200.
            var bitmap = new BitmapImage { DecodePixelWidth = 96, DecodePixelType = DecodePixelType.Logical };
            await bitmap.SetSourceAsync(stream);
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private void OnUi(Action action)
    {
        if (_queue is null) return;
        if (_queue.HasThreadAccess) action();
        else _queue.TryEnqueue(() => Guard.Run(action));
    }
}
