using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KawakiIsland.Search;

public enum ResultKind
{
    App,
    File,
    Folder,
    Web,
}

public sealed class ResultItem : INotifyPropertyChanged
{
    public required string Title { get; init; }
    public string Subtitle { get; init; } = "";
    public required ResultKind Kind { get; init; }

    /// <summary>Идентификатор приложения, путь или адрес — в зависимости от <see cref="Kind"/>.</summary>
    public required string Target { get; init; }

    /// <summary>Откуда брать иконку; null — показать глиф.</summary>
    public string? IconSource { get; init; }

    public string Glyph => Kind switch
    {
        ResultKind.App => "",
        ResultKind.Folder => "",
        ResultKind.Web => "",
        _ => "",
    };

    public bool CanPin => Kind != ResultKind.Web;
    public bool CanReveal => Kind is ResultKind.File or ResultKind.Folder;

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            PropertyChanged?.Invoke(this, new(nameof(Icon)));
            PropertyChanged?.Invoke(this, new(nameof(GlyphVisibility)));
        }
    }

    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}
