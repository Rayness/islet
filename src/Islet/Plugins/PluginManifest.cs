using System.Text.Json;
using System.Text.Json.Serialization;

namespace Islet.Plugins;

/// <summary>
/// Строка, которую можно задать одним значением или по языкам:
/// <c>"Поиск"</c> или <c>{"en": "Search", "ru": "Поиск"}</c>.
/// </summary>
[JsonConverter(typeof(LocalizedTextConverter))]
public sealed class LocalizedText
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

    public override string ToString()
    {
        if (Values.Count == 0) return "";
        var ui = System.Globalization.CultureInfo.CurrentUICulture;
        var language = Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride is { Length: > 0 } o ? o : ui.Name;
        var two = language.Split('-')[0];
        return Values.TryGetValue(language, out var exact) ? exact
            : Values.TryGetValue(two, out var neutral) ? neutral
            : Values.TryGetValue("en", out var en) ? en
            : Values.TryGetValue("", out var plain) ? plain
            : Values.Values.First();
    }

    public static implicit operator string(LocalizedText? text) => text?.ToString() ?? "";
}

internal sealed class LocalizedTextConverter : JsonConverter<LocalizedText>
{
    public override LocalizedText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = new LocalizedText();
        if (reader.TokenType == JsonTokenType.String)
        {
            text.Values[""] = reader.GetString() ?? "";
            return text;
        }
        using var doc = JsonDocument.ParseValue(ref reader);
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    text.Values[p.Name] = p.Value.GetString() ?? "";
            }
        }
        return text;
    }

    public override void Write(Utf8JsonWriter writer, LocalizedText value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value.Values, options);
}

/// <summary>plugin.json — описание плагина. Подробно — docs/plugins.md.</summary>
public sealed class PluginManifest
{
    public string Id { get; set; } = "";
    public LocalizedText? Name { get; set; }
    public string Version { get; set; } = "1.0.0";
    public string? Author { get; set; }
    public LocalizedText? Description { get; set; }
    /// <summary>Глиф Segoe Fluent Icons или путь к картинке относительно папки плагина.</summary>
    public string? Icon { get; set; }
    /// <summary>Версия протокола, под которую писали. Сейчас 1.</summary>
    public int Api { get; set; } = 1;

    /// <summary>Ключевые слова процессного плагина: «kill », «ip ».</summary>
    public List<string> Keywords { get; set; } = [];
    /// <summary>Отвечать ли процессу на любой запрос, без ключевого слова.</summary>
    public bool Global { get; set; }
    /// <summary>Место в выдаче; встроенные группы — 0…100.</summary>
    public int Order { get; set; } = 60;
    public int MaxResults { get; set; } = 8;
    /// <summary>Отвечать на пустой запрос в своей области.</summary>
    public bool AnswersEmpty { get; set; }
    /// <summary>Запускать процесс вместе с островком (для фоновых уведомлений и активностей).</summary>
    public bool Startup { get; set; }
    /// <summary>Сколько ждать ответа на запрос, мс.</summary>
    public int TimeoutMs { get; set; } = 1500;

    public PluginRun? Run { get; set; }
    /// <summary>Готовые строки: команды без кода.</summary>
    public List<PluginItem> Items { get; set; } = [];
    /// <summary>Поиск по сайтам: «gh запрос» → github.com/search?q=запрос.</summary>
    public List<PluginShortcut> Shortcuts { get; set; } = [];
}

public sealed class PluginRun
{
    /// <summary>Исполняемый файл: python, node, powershell или путь относительно папки плагина.</summary>
    public string Command { get; set; } = "";
    public List<string> Args { get; set; } = [];
}

public sealed class PluginItem
{
    public LocalizedText? Title { get; set; }
    public LocalizedText? Subtitle { get; set; }
    public string? Glyph { get; set; }
    public string? Icon { get; set; }
    /// <summary>Синонимы для поиска через пробел.</summary>
    public string? Keywords { get; set; }
    public JsonElement? Action { get; set; }
    public string? Confirm { get; set; }
}

public sealed class PluginShortcut
{
    public string Keyword { get; set; } = "";
    public List<string> Aliases { get; set; } = [];
    public LocalizedText? Name { get; set; }
    /// <summary>Адрес с {query} — туда подставляется запрос.</summary>
    public string Url { get; set; } = "";
    /// <summary>Адрес для пустого запроса (главная сайта).</summary>
    public string? Home { get; set; }
    public string? Glyph { get; set; }
    public string? Icon { get; set; }
}
