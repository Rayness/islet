using System.Text.Json;

namespace Islet.Core;

/// <summary>
/// Сообщения протокола островка — один формат для канала, ключей командной
/// строки и плагинов. Описание для авторов — docs/protocol.md.
///
///   {"type":"notify","title":"…","body":"…","icon":"…","glyph":"…","action":{…}}
///   {"type":"activity","id":"build","text":"Сборка","progress":0.4,"color":"#3BE5CE"}
///   {"type":"activity","id":"build","clear":true}
///   {"type":"open","query":"…"}
///   {"type":"settings","page":"plugins"}
/// </summary>
internal static class Protocol
{
    /// <summary>
    /// Выполнить сообщение. <paramref name="source"/> — ipc/cli или plugin:&lt;id&gt;;
    /// <paramref name="sourceName"/> — подпись в колоколе. Вызывается с любого потока.
    /// </summary>
    public static void Handle(JsonElement msg, string source, string sourceName, string? pluginId = null)
    {
        if (msg.ValueKind != JsonValueKind.Object) return;
        var type = Str(msg, "type");
        var app = App.Current;

        switch (type)
        {
            case "notify":
            {
                var title = Str(msg, "title") ?? "";
                var body = Str(msg, "body") ?? "";
                if (title.Length == 0 && body.Length == 0) return;
                app.Notifications.Post(new IsletNotification
                {
                    Source = source,
                    SourceName = Str(msg, "source") is { Length: > 0 } s && pluginId is null ? s : sourceName,
                    Title = Clip(title, 200),
                    Body = Clip(body, 1000),
                    Icon = Str(msg, "icon"),
                    Glyph = Str(msg, "glyph"),
                    Action = IsletAction.FromJson(Prop(msg, "action"), pluginId),
                    ExternalId = Str(msg, "id"),
                    Silent = msg.TryGetProperty("silent", out var silent) && silent.ValueKind == JsonValueKind.True,
                });
                break;
            }

            case "activity":
            {
                if (Str(msg, "id") is not { Length: > 0 } rawId) return;
                // Чужие активности не пересекаются по id: у каждого источника своё пространство.
                var id = $"{source}:{rawId}";
                if (msg.TryGetProperty("clear", out var clear) && clear.ValueKind == JsonValueKind.True)
                {
                    app.Activities.Clear(id);
                    return;
                }
                double? progress = msg.TryGetProperty("progress", out var p) && p.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(p.GetDouble(), 0, 1)
                    : null;
                app.Activities.Set(new LiveActivity
                {
                    Id = id,
                    Source = source,
                    Text = Clip(Str(msg, "text") ?? "", 80),
                    Progress = progress,
                    Glyph = Str(msg, "glyph"),
                    Icon = Str(msg, "icon"),
                    Color = Str(msg, "color"),
                    // Внешнее не выше таймера: он у человека на счету по секундам.
                    Priority = 50,
                    Action = IsletAction.FromJson(Prop(msg, "action"), pluginId),
                });
                break;
            }

            case "open":
                app.Dispatch(() => app.Island?.OpenWithQuery(Str(msg, "query")));
                break;

            case "demo":
                app.Dispatch(() => app.Island?.ShowDemo(Str(msg, "query") ?? ""));
                break;

            case "settings" when pluginId is null:
                app.Dispatch(() => app.OpenSettings(Str(msg, "page")));
                break;

            case "quit" when pluginId is null:
                app.Dispatch(app.Shutdown);
                break;

            case "log":
                Log.Write($"{source}: {Clip(Str(msg, "message") ?? "", 500)}");
                break;
        }
    }

    private static JsonElement? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? v : null;

    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
