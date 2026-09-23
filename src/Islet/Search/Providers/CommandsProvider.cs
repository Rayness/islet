using Islet.Core;
using Islet.Settings;
using Islet.Shell;

namespace Islet.Search.Providers;

/// <summary>
/// Команды: заблокировать, сон, перезагрузка, корзина, тема, скриншот,
/// страницы «Параметров» Windows и действия самого островка.
///
/// Ищутся по названию на языке интерфейса и по синонимам на обоих языках:
/// «блок», «lock», «wifi», «вайфай» — всё ведёт куда надо, какой бы язык ни стоял.
/// Опасные (выключение, выход, корзина) просят второй Enter.
/// </summary>
internal sealed class CommandsProvider : SearchProvider
{
    private sealed record Command(
        string Id,
        string TitleKey,
        string Glyph,
        string Aliases,
        Func<ActionOutcome> Run,
        bool Dangerous = false,
        Func<bool>? Visible = null);

    private readonly List<Command> _commands;

    public override string Id => "commands";
    public override string Name => Loc.T("Provider_Commands");
    public override string Glyph => "";
    public override IReadOnlyList<string> Keywords => [">"];
    public override string Description => Loc.T("Provider_CommandsHint");
    public override int Order => 20;
    public override int MaxGlobal => 3;
    public override int MaxScoped => 40;
    public override bool AnswersEmptyScoped => true;
    public override bool IsEnabled => SettingsStore.Current.CommandsEnabled;

    public CommandsProvider()
    {
        static Func<ActionOutcome> Do(Func<bool> action, ActionOutcome ok = ActionOutcome.DoneRestoreFocus) =>
            () => action() ? ok : ActionOutcome.Failed;
        static Func<ActionOutcome> Settings(string page) => Do(() => Launcher.Open("ms-settings:" + page), ActionOutcome.Done);

        _commands =
        [
            new("lock", "Cmd_Lock", "", "lock блок заблокировать экран", Do(SystemActions.Lock)),
            new("sleep", "Cmd_Sleep", "", "sleep сон спящий режим", Do(SystemActions.Sleep)),
            new("restart", "Cmd_Restart", "", "restart reboot перезагрузка перезапуск", Do(SystemActions.Restart), Dangerous: true),
            new("shutdown", "Cmd_Shutdown", "", "shutdown power off выключить завершение работы", Do(SystemActions.Shutdown), Dangerous: true),
            new("signout", "Cmd_SignOut", "", "sign out log off выйти выход из системы", Do(SystemActions.SignOut), Dangerous: true),
            new("recycle", "Cmd_EmptyRecycle", "", "empty recycle bin trash очистить корзину", Do(SystemActions.EmptyRecycleBin), Dangerous: true),
            new("theme", "Cmd_Theme", "", "theme dark light mode тема тёмная темная светлая", Do(SystemActions.ToggleTheme)),
            new("snip", "Cmd_Screenshot", "", "screenshot snip снимок экрана скриншот ножницы", Do(() => Launcher.Open("ms-screenclip:"), ActionOutcome.Done)),
            new("downloads", "Cmd_Downloads", "", "downloads загрузки", Do(() => Launcher.Open(KnownFolders.Downloads), ActionOutcome.Done)),
            new("desktop", "Cmd_Desktop", "", "desktop рабочий стол", Do(() => Launcher.Open(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), ActionOutcome.Done)),
            new("taskmgr", "Cmd_TaskManager", "", "task manager диспетчер задач процессы", Do(() => Launcher.Open("taskmgr.exe"), ActionOutcome.Done)),

            new("islet-settings", "Cmd_IsletSettings", "", "islet settings настройки островка", () =>
            {
                App.Current.Dispatch(() => App.Current.OpenSettings());
                return ActionOutcome.Done;
            }),
            new("islet-plugins", "Cmd_IsletPlugins", "", "islet plugins плагины островка", () =>
            {
                App.Current.Dispatch(() => App.Current.OpenSettings("plugins"));
                return ActionOutcome.Done;
            }),
            new("islet-reindex", "Cmd_Reindex", "", "reindex rebuild index переиндексировать индекс", () =>
            {
                App.Current.DriveIndex.Rescan();
                return ActionOutcome.DoneRestoreFocus;
            }),
            new("islet-clear-notifications", "Cmd_ClearNotifications", "", "clear notifications очистить уведомления", () =>
            {
                App.Current.Notifications.Clear();
                return ActionOutcome.Stay;
            }, Visible: () => App.Current.Notifications.History.Count > 0),
            new("islet-stop-timers", "Cmd_StopTimers", "", "stop timers остановить таймеры", () =>
            {
                App.Current.Timers.CancelAll();
                return ActionOutcome.DoneRestoreFocus;
            }, Visible: () => App.Current.Timers.Timers.Count > 0),
            new("islet-clear-clipboard", "Cmd_ClearClipboard", "", "clear clipboard history очистить историю буфера", () =>
            {
                App.Current.Clipboard.Clear();
                return ActionOutcome.Stay;
            }, Visible: () => App.Current.Clipboard.Items.Count > 0),
            new("islet-quit", "Cmd_Quit", "", "quit exit islet выйти закрыть островок", () =>
            {
                App.Current.Dispatch(App.Current.Shutdown);
                return ActionOutcome.Done;
            }),

            new("ms-display", "Set_Display", "", "display screen resolution scale экран дисплей разрешение масштаб", Settings("display")),
            new("ms-nightlight", "Set_NightLight", "", "night light ночной свет", Settings("nightlight")),
            new("ms-sound", "Set_Sound", "", "sound audio volume звук громкость", Settings("sound")),
            new("ms-bluetooth", "Set_Bluetooth", "", "bluetooth блютуз устройства devices", Settings("bluetooth")),
            new("ms-wifi", "Set_Wifi", "", "wifi wi-fi wireless вайфай беспроводная сеть", Settings("network-wifi")),
            new("ms-network", "Set_Network", "", "network internet ethernet vpn сеть интернет", Settings("network")),
            new("ms-personalization", "Set_Personalization", "", "personalization wallpaper background персонализация обои фон", Settings("personalization-background")),
            new("ms-colors", "Set_Colors", "", "colors accent цвета акцент", Settings("colors")),
            new("ms-taskbar", "Set_Taskbar", "", "taskbar панель задач", Settings("taskbar")),
            new("ms-apps", "Set_Apps", "", "apps uninstall installed приложения удалить установленные", Settings("appsfeatures")),
            new("ms-defaultapps", "Set_DefaultApps", "", "default apps приложения по умолчанию", Settings("defaultapps")),
            new("ms-startup", "Set_Startup", "", "startup apps автозагрузка автозапуск", Settings("startupapps")),
            new("ms-update", "Set_Update", "", "windows update обновление обновления", Settings("windowsupdate")),
            new("ms-power", "Set_Power", "", "power sleep battery питание батарея электропитание", Settings("powersleep")),
            new("ms-storage", "Set_Storage", "", "storage disk space память хранилище место на диске", Settings("storagesense")),
            new("ms-notifications", "Set_Notifications", "", "notifications уведомления", Settings("notifications")),
            new("ms-mouse", "Set_Mouse", "", "mouse touchpad мышь тачпад", Settings("mousetouchpad")),
            new("ms-keyboard", "Set_Keyboard", "", "keyboard typing клавиатура ввод", Settings("typing")),
            new("ms-language", "Set_Language", "", "language region язык регион раскладка", Settings("regionlanguage")),
            new("ms-datetime", "Set_DateTime", "", "date time clock дата время часы", Settings("dateandtime")),
            new("ms-accounts", "Set_Accounts", "", "accounts user учётные записи пользователь", Settings("yourinfo")),
            new("ms-privacy", "Set_Privacy", "", "privacy security конфиденциальность безопасность", Settings("privacy")),
            new("ms-about", "Set_About", "", "about system pc name о системе компьютер", Settings("about")),
            new("ms-focus", "Set_Focus", "", "focus do not disturb фокусировка не беспокоить", Settings("quiethours")),
            new("ms-gaming", "Set_Gaming", "", "gaming game mode игры игровой режим", Settings("gaming-gamemode")),
        ];
    }

    public override List<ResultItem> Query(SearchQuery query, int max)
    {
        var q = Normalize(query.Text);
        if (q.Length == 0 && !query.IsScoped) return [];
        // Без ключевого слова — только уверенные совпадения, иначе «с» выдавало бы полсписка.
        if (!query.IsScoped && q.Length < 2) return [];

        return _commands
            .Where(c => c.Visible?.Invoke() ?? true)
            .Select(c => (Command: c, Score: q.Length == 0 ? 1 : Score(c, q)))
            .Where(x => x.Score > (query.IsScoped ? 0 : 40))
            .Select(x =>
            {
                var item = ToItem(x.Command);
                item.Score = x.Score + Frecency.Boost(item);
                return item;
            })
            .OrderByDescending(x => x.Score)
            .Take(max)
            .ToList();
    }

    /// <summary>Команда по id — для «Недавних».</summary>
    public ResultItem? Find(string id) => _commands.FirstOrDefault(c => c.Id == id) is { } c ? ToItem(c) : null;

    private ResultItem ToItem(Command c) => new()
    {
        Title = Loc.T(c.TitleKey),
        Subtitle = c.Id.StartsWith("ms-") ? Loc.T("Result_WindowsSettings") : c.Id.StartsWith("islet-") ? "Islet" : Loc.T("Result_Command"),
        Kind = ResultKind.Command,
        Target = c.Id,
        ProviderId = Id,
        GlyphOverride = c.Glyph,
        Confirm = c.Dangerous ? Loc.T("Confirm_Again") : null,
        Action = IsletAction.Run(c.Run),
    };

    private static int Score(Command c, string q)
    {
        var title = Normalize(Loc.T(c.TitleKey));
        if (title == q) return 100;
        if (title.StartsWith(q, StringComparison.Ordinal)) return 85;
        if (title.Contains(' ' + q, StringComparison.Ordinal)) return 70;
        foreach (var alias in c.Aliases.Split(' '))
        {
            if (alias.StartsWith(q, StringComparison.Ordinal)) return 65;
        }
        // Несколько слов запроса — каждое должно начинать слово названия или синоним.
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1)
        {
            var haystack = (title + " " + c.Aliases).Split(' ');
            if (words.All(w => haystack.Any(h => h.StartsWith(w, StringComparison.Ordinal))))
                return 60;
        }
        if (title.Contains(q, StringComparison.Ordinal)) return 30;
        return 0;
    }

    private static string Normalize(string s) => s.Trim().ToLowerInvariant().Replace('ё', 'е');
}

internal static class KnownFolders
{
    /// <summary>Настоящая папка «Загрузки», даже если её перенесли на другой диск.</summary>
    public static string Downloads
    {
        get
        {
            try
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (Native.ShellFolders.SHGetKnownFolderPath(ref id, 0, 0, out var path) == 0)
                    return path;
            }
            catch { /* ниже запасной путь */ }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
