using System.Text.Json;
using Islet.Core;
using Islet.Search;
using Islet.Settings;

namespace Islet.Plugins;

/// <summary>Найденный плагин: манифест, папка, включён ли, что с ним не так.</summary>
internal sealed class PluginInfo
{
    public required PluginManifest Manifest { get; init; }
    public required string Directory { get; init; }
    /// <summary>Лежит рядом с exe и приходит с обновлениями; удалить его нельзя, только выключить.</summary>
    public bool IsBuiltIn { get; init; }
    public string? Error { get; set; }
    public PluginHost? Host { get; set; }

    public string Id => Manifest.Id;
    public string Name => Manifest.Name is { } n && n.ToString().Length > 0 ? n : Manifest.Id;
    public bool Enabled => !SettingsStore.Current.DisabledPlugins.Contains(Id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Иконка: глиф или полный путь к картинке.</summary>
    public (string? Glyph, string? Image) ResolveIcon(string? icon)
    {
        if (string.IsNullOrEmpty(icon)) return (null, null);
        if (icon.Length <= 2) return (icon, null);
        if (icon.StartsWith("http", StringComparison.OrdinalIgnoreCase) || icon.StartsWith("ms-appx", StringComparison.OrdinalIgnoreCase))
            return (null, icon);
        var path = Path.GetFullPath(Path.Combine(Directory, icon));
        // Картинка только из папки плагина: манифест не должен указывать куда угодно на диске.
        return path.StartsWith(Directory, StringComparison.OrdinalIgnoreCase) && File.Exists(path) ? (null, path) : (null, null);
    }
}

/// <summary>
/// Плагины островка. Ищутся в двух местах:
///   &lt;папка Islet&gt;\plugins — встроенные, приходят с обновлениями;
///   %APPDATA%\Islet\plugins    — свои: положите папку с plugin.json и нажмите «Перезагрузить».
/// Свой плагин с тем же id заменяет встроенный.
/// </summary>
internal sealed class PluginManager : IDisposable
{
    public static readonly string BuiltInDirectory = Path.Combine(AppContext.BaseDirectory, "plugins");
    public static readonly string UserDirectory = Path.Combine(Paths.Config, "plugins");

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private List<PluginInfo> _plugins = [];
    private List<SearchProvider> _providers = [];

    public IReadOnlyList<PluginInfo> Plugins => _plugins;
    public IReadOnlyList<SearchProvider> Providers => _providers;

    /// <summary>Список или состояние плагинов поменялись. С любого потока.</summary>
    public event Action? Changed;

    public void RaiseChanged() => Changed?.Invoke();

    public void Load()
    {
        foreach (var p in _plugins) p.Host?.Dispose();

        var found = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, builtIn) in new[] { (BuiltInDirectory, true), (UserDirectory, false) })
        {
            if (!System.IO.Directory.Exists(root)) continue;
            foreach (var dir in System.IO.Directory.EnumerateDirectories(root))
            {
                var manifestPath = Path.Combine(dir, "plugin.json");
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(manifestPath), Json)
                        ?? throw new JsonException("empty manifest");
                    if (string.IsNullOrWhiteSpace(manifest.Id))
                        manifest.Id = Path.GetFileName(dir);
                    var info = new PluginInfo { Manifest = manifest, Directory = Path.GetFullPath(dir) + Path.DirectorySeparatorChar, IsBuiltIn = builtIn };
                    if (manifest.Api > 1)
                        info.Error = Loc.T("Plugin_NewerApi", manifest.Api);
                    found[manifest.Id] = info;
                }
                catch (Exception e)
                {
                    Log.Write($"plugin manifest {manifestPath}: {e.Message}");
                    var id = Path.GetFileName(dir);
                    found[id] = new PluginInfo
                    {
                        Manifest = new PluginManifest { Id = id },
                        Directory = dir,
                        IsBuiltIn = builtIn,
                        Error = Loc.T("Plugin_BadManifest", e.Message),
                    };
                }
            }
        }

        _plugins = [.. found.Values.OrderBy(p => p.IsBuiltIn ? 0 : 1).ThenBy(p => p.Name)];
        BuildProviders();
        Log.Write($"plugins: {_plugins.Count} found, {_plugins.Count(p => p.Enabled && p.Error is null)} active");
        Changed?.Invoke();

        foreach (var p in _plugins.Where(p => p.Enabled && p.Error is null && p.Manifest.Startup && p.Host is not null))
            p.Host!.EnsureStarted();
    }

    private void BuildProviders()
    {
        var providers = new List<SearchProvider>();
        foreach (var plugin in _plugins)
        {
            plugin.Host = null;
            if (!plugin.Enabled || plugin.Error is not null) continue;
            var m = plugin.Manifest;

            if (m.Run is { Command.Length: > 0 })
            {
                plugin.Host = new PluginHost(plugin);
                if (m.Keywords.Count > 0 || m.Global)
                    providers.Add(new ProcessPluginProvider(plugin));
            }
            if (m.Items.Count > 0)
                providers.Add(new ItemsPluginProvider(plugin));
            foreach (var shortcut in m.Shortcuts.Where(s => s.Keyword.Length > 0 && s.Url.Contains("{query}")))
                providers.Add(new ShortcutProvider(plugin, shortcut));
        }
        _providers = providers;
    }

    public void SetEnabled(PluginInfo plugin, bool enabled)
    {
        SettingsStore.Update(s =>
        {
            s.DisabledPlugins.RemoveAll(id => string.Equals(id, plugin.Id, StringComparison.OrdinalIgnoreCase));
            if (!enabled) s.DisabledPlugins.Add(plugin.Id);
        });
        if (!enabled)
            App.Current.Activities.ClearSource($"plugin:{plugin.Id}");
        Load();
    }

    /// <summary>Вызов действия плагина — из строки выдачи, уведомления или активности.</summary>
    public ActionOutcome Invoke(string pluginId, string action, JsonElement? data)
    {
        var plugin = _plugins.FirstOrDefault(p => string.Equals(p.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        if (plugin?.Host is null) return ActionOutcome.Failed;
        plugin.Host.Invoke(action, data);
        var keepOpen = data is { ValueKind: JsonValueKind.Object } d && d.TryGetProperty("keepOpen", out var k) && k.ValueKind == JsonValueKind.True;
        return keepOpen ? ActionOutcome.Stay : ActionOutcome.DoneRestoreFocus;
    }

    public void Dispose()
    {
        foreach (var p in _plugins) p.Host?.Dispose();
    }
}

/// <summary>Строки, которые возвращает процесс плагина.</summary>
internal sealed class ProcessPluginProvider(PluginInfo plugin) : SearchProvider
{
    public override string Id => $"plugin:{plugin.Id}";
    public override string Name => plugin.Name;
    public override string Glyph => plugin.ResolveIcon(plugin.Manifest.Icon).Glyph ?? "\uEA86";
    public override IReadOnlyList<string> Keywords => plugin.Manifest.Keywords;
    public override string Description => plugin.Manifest.Description ?? "";
    public override bool IsGlobal => plugin.Manifest.Global;
    public override bool IsInstant => false;
    public override bool AnswersEmptyScoped => plugin.Manifest.AnswersEmpty;
    public override int Order => plugin.Manifest.Order;
    public override int MaxGlobal => Math.Min(plugin.Manifest.MaxResults, 4);
    public override int MaxScoped => plugin.Manifest.MaxResults;

    public override async Task<List<ResultItem>> QueryAsync(SearchQuery query, int max, CancellationToken ct)
    {
        if (plugin.Host is null) return [];
        var response = await plugin.Host.QueryAsync(query.Text, query.IsScoped, ct);
        if (response is not { } r || !r.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<ResultItem>();
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (results.Count >= max) break;
            if (PluginResults.ToItem(plugin, item, Id, index++) is { } result)
                results.Add(result);
        }
        return results;
    }
}

/// <summary>Готовые строки из манифеста — ищутся по названию и синонимам, как команды.</summary>
internal sealed class ItemsPluginProvider(PluginInfo plugin) : SearchProvider
{
    public override string Id => $"plugin-items:{plugin.Id}";
    public override string Name => plugin.Name;
    public override IReadOnlyList<string> Keywords => plugin.Manifest.Keywords;
    public override string Description => plugin.Manifest.Description ?? "";
    public override int Order => plugin.Manifest.Order;
    public override int MaxGlobal => 3;
    public override bool AnswersEmptyScoped => true;

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var q = query.Text.Trim().ToLowerInvariant();
        if (q.Length < (query.IsScoped ? 0 : 2)) return [];
        var results = new List<ResultItem>();
        var index = 0;
        foreach (var item in plugin.Manifest.Items)
        {
            index++;
            var title = ((string)item.Title!).ToLowerInvariant();
            var aliases = (item.Keywords ?? "").ToLowerInvariant();
            var match = q.Length == 0 || title.StartsWith(q) || title.Contains(' ' + q)
                || aliases.Split(' ').Any(a => a.StartsWith(q));
            if (!match) continue;

            var (glyph, image) = plugin.ResolveIcon(item.Icon);
            results.Add(new ResultItem
            {
                Title = item.Title!,
                Subtitle = item.Subtitle is { } s && s.ToString().Length > 0 ? s : plugin.Name,
                Kind = ResultKind.Plugin,
                Target = $"{plugin.Id}:item:{index}",
                ProviderId = Id,
                GlyphOverride = item.Glyph ?? glyph ?? plugin.ResolveIcon(plugin.Manifest.Icon).Glyph,
                IconUrl = image,
                Confirm = item.Confirm,
                Action = IsletAction.FromJson(item.Action, plugin.Id),
                Remember = false,
            });
            if (results.Count >= max) break;
        }
        return results;
    }
}

/// <summary>«gh запрос» → поиск на сайте. Пустой запрос — главная сайта.</summary>
internal sealed class ShortcutProvider(PluginInfo plugin, PluginShortcut shortcut) : SearchProvider
{
    public override string Id => $"shortcut:{plugin.Id}:{shortcut.Keyword}";
    public override string Name => shortcut.Name ?? shortcut.Keyword;
    public override string Glyph => shortcut.Glyph ?? "\uE774";
    public override IReadOnlyList<string> Keywords => [shortcut.Keyword, .. shortcut.Aliases];
    public override string Description => Loc.T("Shortcut_Hint", Name);
    public override bool IsGlobal => false;
    public override bool AnswersEmptyScoped => shortcut.Home is not null;
    public override int Order => 5;

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var (_, image) = plugin.ResolveIcon(shortcut.Icon);
        if (query.IsEmpty)
        {
            return shortcut.Home is { } home
                ? [Row(Loc.T("Shortcut_Home", Name), home, image)]
                : [];
        }
        var url = shortcut.Url.Replace("{query}", Uri.EscapeDataString(query.Text));
        return [Row(Loc.T("Shortcut_Search", query.Text, Name), url, image)];
    }

    private ResultItem Row(string title, string url, string? image) => new()
    {
        Title = title,
        Subtitle = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url,
        Kind = ResultKind.Web,
        Target = url,
        ProviderId = Id,
        GlyphOverride = Glyph,
        IconUrl = image,
        Remember = false,
    };
}

/// <summary>Строка выдачи из JSON плагина.</summary>
internal static class PluginResults
{
    public static ResultItem? ToItem(PluginInfo plugin, JsonElement item, string providerId, int index)
    {
        if (Protocol.Str(item, "title") is not { Length: > 0 } title) return null;
        var (glyph, image) = plugin.ResolveIcon(Protocol.Str(item, "icon"));
        var iconUrl = image;
        string? iconSource = null;
        // Иконка файла или приложения по пути: «icon»: «C:\\…\\app.exe».
        if (iconUrl is null && Protocol.Str(item, "iconPath") is { Length: > 0 } iconPath)
            iconSource = iconPath;

        var actions = new List<ResultAction>();
        if (item.TryGetProperty("actions", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in extra.EnumerateArray())
            {
                if (Protocol.Str(a, "title") is { } t && IsletAction.FromJson(a.TryGetProperty("action", out var act) ? act : null, plugin.Id) is { } parsed)
                    actions.Add(new ResultAction(t, Protocol.Str(a, "glyph") ?? "\uE7C1", parsed));
            }
        }

        return new ResultItem
        {
            Title = title,
            Subtitle = Protocol.Str(item, "subtitle") ?? "",
            Kind = ResultKind.Plugin,
            Target = Protocol.Str(item, "id") is { } id ? $"{plugin.Id}:{id}" : $"{plugin.Id}:#{index}:{title}",
            ProviderId = providerId,
            GlyphOverride = Protocol.Str(item, "glyph") ?? glyph ?? plugin.ResolveIcon(plugin.Manifest.Icon).Glyph,
            IconUrl = iconUrl,
            IconSource = iconSource,
            Trailing = Protocol.Str(item, "trailing") ?? "",
            Confirm = Protocol.Str(item, "confirm"),
            Action = IsletAction.FromJson(item.TryGetProperty("action", out var action) ? action : null, plugin.Id),
            Actions = actions.Count > 0 ? actions : null,
            Remember = false,
        };
    }
}
