using System.Text.Json;
using System.Text.Json.Serialization;
using Islet.Shell;

namespace Islet.Core;

/// <summary>Что делать островку после действия.</summary>
public enum ActionOutcome
{
    /// <summary>Не вышло — островок остаётся открытым.</summary>
    Failed,
    /// <summary>Сделано, запущенное окно само заберёт фокус.</summary>
    Done,
    /// <summary>Сделано, фокус вернуть окну, где человек был до островка.</summary>
    DoneRestoreFocus,
    /// <summary>Текст уже в буфере: вернуть фокус прежнему окну и вставить.</summary>
    Paste,
    /// <summary>Островок остаётся открытым (подстановка запроса, подтверждение).</summary>
    Stay,
}

/// <summary>
/// Действие, которое можно сохранить и передать: у уведомления, у строки
/// выдачи, у кнопки плагина. Задано ровно одно поле; в JSON (протокол
/// плагинов и канала) это объект вида <c>{"open": "https://…"}</c>.
/// </summary>
public sealed record IsletAction
{
    /// <summary>Файл, папка, ссылка или протокол — чем открывает Windows.</summary>
    public string? Open { get; init; }
    /// <summary>Аргументы к <see cref="Open"/>.</summary>
    public string? Args { get; init; }
    /// <summary>Показать файл в папке.</summary>
    public string? Reveal { get; init; }
    /// <summary>Приложение из shell:AppsFolder.</summary>
    public string? App { get; init; }
    /// <summary>Положить текст в буфер обмена.</summary>
    public string? Copy { get; init; }
    /// <summary>Вставить текст в окно, где человек был до островка.</summary>
    public string? Paste { get; init; }
    /// <summary>Подставить запрос в строку поиска, островок остаётся открытым.</summary>
    public string? Query { get; init; }

    /// <summary>Вызов обратно в плагин: id плагина, id действия, данные.</summary>
    public string? Plugin { get; init; }
    public string? Invoke { get; init; }
    public JsonElement? Data { get; init; }

    /// <summary>Действие внутри процесса. Не сохраняется.</summary>
    [JsonIgnore]
    public Func<ActionOutcome>? Callback { get; init; }

    [JsonIgnore]
    public bool IsEmpty => Open is null && Reveal is null && App is null && Copy is null && Paste is null
        && Query is null && Invoke is null && Callback is null;

    public static IsletAction OpenTarget(string target) => new() { Open = target };

    public static IsletAction Run(Func<ActionOutcome> callback) => new() { Callback = callback };

    /// <summary>
    /// Разбор из протокола. Строка — это «открыть»; объект — одно из полей.
    /// <paramref name="pluginId"/> приписывается к invoke: плагин может вызвать только себя.
    /// </summary>
    public static IsletAction? FromJson(JsonElement? element, string? pluginId = null)
    {
        if (element is not { } e) return null;
        if (e.ValueKind == JsonValueKind.String)
            return e.GetString() is { Length: > 0 } s ? new IsletAction { Open = s } : null;
        if (e.ValueKind != JsonValueKind.Object) return null;

        string? Str(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        var action = new IsletAction
        {
            Open = Str("open"),
            Args = Str("args"),
            Reveal = Str("reveal"),
            App = Str("app"),
            Copy = Str("copy"),
            Paste = Str("paste"),
            Query = Str("query"),
            Invoke = pluginId is null ? null : Str("invoke"),
            Plugin = pluginId,
            Data = e.TryGetProperty("data", out var d) ? d.Clone() : null,
        };
        return action.IsEmpty ? null : action;
    }
}

/// <summary>Исполнитель действий. Вызов обратно в плагин подставляет менеджер плагинов.</summary>
internal static class ActionRunner
{
    /// <summary>Вызов плагина: (id плагина, id действия, данные) → итог.</summary>
    public static Func<string, string, JsonElement?, ActionOutcome>? PluginInvoker { get; set; }

    public static ActionOutcome Run(IsletAction action)
    {
        try
        {
            if (action.Callback is { } callback) return callback();
            if (action.Invoke is { } invoke && action.Plugin is { } plugin)
                return PluginInvoker?.Invoke(plugin, invoke, action.Data) ?? ActionOutcome.Failed;
            if (action.Paste is { } paste)
                return ClipboardText.Set(paste) ? ActionOutcome.Paste : ActionOutcome.Failed;
            if (action.Copy is { } copy)
                return ClipboardText.Set(copy) ? ActionOutcome.DoneRestoreFocus : ActionOutcome.Failed;
            if (action.Reveal is { } reveal)
                return Launcher.Reveal(reveal) ? ActionOutcome.Done : ActionOutcome.Failed;
            if (action.App is { } app)
                return Launcher.OpenApp(app) ? ActionOutcome.Done : ActionOutcome.Failed;
            if (action.Open is { } open)
                return Launcher.Open(open, action.Args) ? ActionOutcome.Done : ActionOutcome.Failed;
            if (action.Query is not null)
                return ActionOutcome.Stay;
        }
        catch (Exception e)
        {
            Log.Write($"action failed: {e.Message}");
        }
        return ActionOutcome.Failed;
    }
}
