using System.ComponentModel;
using Islet.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Islet.Search;

public enum ResultKind
{
    App,
    File,
    Folder,
    Web,
    Command,
    Calc,
    Hint,
    Clipboard,
    Kawaki,
    Plugin,
    Timer,
    Url,
}

/// <summary>Дополнительное действие строки — пункт её контекстного меню.</summary>
public sealed record ResultAction(string Title, string Glyph, IsletAction Action);

public sealed class ResultItem : INotifyPropertyChanged
{
    public required string Title { get; init; }
    public required ResultKind Kind { get; init; }

    /// <summary>Идентификатор приложения, путь, адрес или id команды — в зависимости от <see cref="Kind"/>.</summary>
    public required string Target { get; init; }

    /// <summary>Какой провайдер нашёл: apps, files, kawaki, plugin:&lt;id&gt;…</summary>
    public string ProviderId { get; init; } = "";

    /// <summary>Откуда брать иконку оболочки (путь, shell:AppsFolder\…); null — нет.</summary>
    public string? IconSource { get; init; }
    /// <summary>Картинка по адресу: https:// или ms-appx:///.</summary>
    public string? IconUrl { get; init; }
    /// <summary>Глиф Segoe Fluent Icons, пока картинки нет. Пусто — по виду строки.</summary>
    public string? GlyphOverride { get; init; }

    /// <summary>Подпись справа: «Enter — копировать», «Kawaki», «Команда».</summary>
    public string Trailing { get; init; } = "";

    /// <summary>Что делать по Enter. Null — по виду: открыть приложение, файл, адрес.</summary>
    public IsletAction? Action { get; init; }

    /// <summary>Пункты контекстного меню сверх стандартных.</summary>
    public IReadOnlyList<ResultAction>? Actions { get; init; }

    /// <summary>
    /// Опасное действие (выключить, очистить корзину): первый Enter показывает
    /// этот текст вместо подзаголовка, второй — выполняет.
    /// </summary>
    public string? Confirm { get; init; }

    /// <summary>Оценка внутри провайдера — для смешивания и частоты запусков.</summary>
    public int Score { get; set; }

    /// <summary>Строка из «Недавнего» — у команд она приходит от провайдера команд, поэтому отдельный флаг.</summary>
    public bool IsRecent { get; set; }

    /// <summary>Запоминать ли запуск для «Недавних» и поднятия в выдаче.</summary>
    public bool Remember { get; init; } = true;

    private string _subtitle = "";
    public string Subtitle
    {
        get => _subtitle;
        set
        {
            if (_subtitle == value) return;
            _subtitle = value;
            PropertyChanged?.Invoke(this, new(nameof(Subtitle)));
            PropertyChanged?.Invoke(this, new(nameof(SubtitleVisibility)));
        }
    }

    public Visibility SubtitleVisibility => _subtitle.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    private bool _confirming;
    /// <summary>Ждёт второго Enter — строка подсвечивает подзаголовок.</summary>
    public bool Confirming
    {
        get => _confirming;
        set
        {
            if (_confirming == value) return;
            _confirming = value;
            PropertyChanged?.Invoke(this, new(nameof(Confirming)));
        }
    }

    public string Glyph => GlyphOverride ?? Kind switch
    {
        ResultKind.App => "",
        ResultKind.Folder => "",
        ResultKind.Web => "",
        ResultKind.Command => "",
        ResultKind.Calc => "",
        ResultKind.Hint => "",
        ResultKind.Clipboard => "",
        ResultKind.Kawaki => "",
        ResultKind.Plugin => "",
        ResultKind.Timer => "",
        ResultKind.Url => "",
        _ => "",
    };

    public Visibility TrailingVisibility => Trailing.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public bool CanPin => Kind is ResultKind.App or ResultKind.File or ResultKind.Folder or ResultKind.Url or ResultKind.Kawaki;
    public bool CanReveal => Kind is ResultKind.File or ResultKind.Folder;
    public bool CanCopyPath => Kind is ResultKind.File or ResultKind.Folder or ResultKind.Url or ResultKind.Kawaki or ResultKind.Web;

    /// <summary>Ключ для частоты запусков: одинаковый у одной и той же вещи из разных поисков.</summary>
    public string HistoryKey => $"{Kind}|{Target}".ToLowerInvariant();

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
