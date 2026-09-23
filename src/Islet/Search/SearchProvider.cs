namespace Islet.Search;

/// <summary>
/// Запрос к провайдерам. <see cref="Text"/> — без ключевого слова области;
/// <see cref="AltText"/> — тот же запрос в другой раскладке («ghbdtn» → «привет»),
/// если он набран одними буквами одного алфавита.
/// </summary>
internal sealed record SearchQuery(string Text, string? Scope, string? AltText)
{
    public bool IsScoped => Scope is not null;
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// Источник строк выдачи: приложения, файлы, калькулятор, команды, Kawaki,
/// плагины. Быстрые (<see cref="IsInstant"/>) опрашиваются на каждую букву на
/// UI-потоке и обязаны укладываться в миллисекунды; медленные — после паузы в
/// наборе, в фоне, с отменой при следующей букве.
/// </summary>
internal abstract class SearchProvider
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public virtual string Glyph => "";
    /// <summary>Слова, после которых с пробелом поиск идёт только здесь: «k », «>», «cb ».</summary>
    public virtual IReadOnlyList<string> Keywords => [];
    /// <summary>Строка справки в «?»: что ищет.</summary>
    public virtual string Description => "";
    /// <summary>Отвечает ли без ключевого слова.</summary>
    public virtual bool IsGlobal => true;
    public virtual bool IsInstant => true;
    /// <summary>Место группы в выдаче: меньше — выше.</summary>
    public abstract int Order { get; }
    public virtual bool IsEnabled => true;
    /// <summary>Сколько строк провайдер даёт в общую выдачу (в своей области — больше).</summary>
    public virtual int MaxGlobal => 4;
    public virtual int MaxScoped => 12;
    /// <summary>Отвечает и на пустой запрос в своей области (буфер, таймеры, справка).</summary>
    public virtual bool AnswersEmptyScoped => false;

    public virtual List<ResultItem> Query(SearchQuery query, int max) => [];

    public virtual Task<List<ResultItem>> QueryAsync(SearchQuery query, int max, CancellationToken ct) =>
        Task.FromResult(new List<ResultItem>());
}
