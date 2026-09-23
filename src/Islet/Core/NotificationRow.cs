using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Islet.Core;

/// <summary>Строка колокола: уведомление плюс загруженная картинка.</summary>
public sealed class NotificationRow : INotifyPropertyChanged
{
    public NotificationRow(IsletNotification notification) => Notification = notification;

    public IsletNotification Notification { get; }

    public string Title => Notification.Title.Length > 0 ? Notification.Title : Notification.SourceName;
    public string Body => Notification.Body.Replace('\n', ' ');
    public string Glyph => Notification.Glyph ?? "";

    /// <summary>«Kawaki · 12:07» сегодня, «ClipTide · 21.09» раньше.</summary>
    public string Meta
    {
        get
        {
            var t = Notification.Time;
            var when = t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("dd.MM");
            return $"{Notification.SourceName} · {when}";
        }
    }

    public Visibility UnreadVisibility => Notification.IsRead ? Visibility.Collapsed : Visibility.Visible;

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new(nameof(Icon)));
            PropertyChanged?.Invoke(this, new(nameof(GlyphVisibility)));
        }
    }

    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public void RefreshRead() => PropertyChanged?.Invoke(this, new(nameof(UnreadVisibility)));

    public event PropertyChangedEventHandler? PropertyChanged;
}
