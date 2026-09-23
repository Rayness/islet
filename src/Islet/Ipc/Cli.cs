using System.Globalization;
using System.Text.Json.Nodes;

namespace Islet.Ipc;

/// <summary>
/// Ключи командной строки → сообщение протокола островка (см. docs/protocol.md).
/// Один и тот же формат идёт по каналу от второго запуска, от плагинов и от
/// сторонних программ, поэтому ключи — лишь короткая запись этих сообщений.
///
///   --open [запрос]                 раскрыть островок, по желанию с запросом
///   --notify "Заголовок" ["Текст"] ["Источник"]  показать уведомление
///   --activity id "Текст" [0..1]    живая активность в свёрнутой капсуле
///   --activity-clear id             убрать активность
///   --settings [страница]           открыть настройки
///   --demo запрос                   раскрыть с запросом, не забирая фокус (для скриншотов)
///   --quit                          закрыть островок
/// </summary>
internal static class Cli
{
    public static string OpenMessage() => new JsonObject { ["type"] = "open" }.ToJsonString();

    public static string? ToMessage(string[] args)
    {
        if (args.Length == 0) return null;
        string? Arg(int i) => i < args.Length ? args[i] : null;

        JsonObject? msg = args[0].ToLowerInvariant() switch
        {
            "--open" or "--search" => new() { ["type"] = "open", ["query"] = Arg(1) },
            "--notify" when Arg(1) is { } title => new()
            {
                ["type"] = "notify",
                ["title"] = title,
                ["body"] = Arg(2) ?? "",
                // Третий аргумент — подпись источника в колоколе: «Сборка», «Бэкап».
                ["source"] = Arg(3),
            },
            "--activity" when Arg(1) is { } id => new()
            {
                ["type"] = "activity",
                ["id"] = id,
                ["text"] = Arg(2) ?? "",
                ["progress"] = double.TryParse(Arg(3), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : null,
            },
            "--activity-clear" when Arg(1) is { } id => new() { ["type"] = "activity", ["id"] = id, ["clear"] = true },
            "--settings" => new() { ["type"] = "settings", ["page"] = Arg(1) },
            "--demo" when Arg(1) is { } query => new() { ["type"] = "demo", ["query"] = query },
            "--quit" or "--exit" => new() { ["type"] = "quit" },
            _ => null,
        };
        return msg?.ToJsonString();
    }
}
