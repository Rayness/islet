using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Islet.Core;
using Islet.Search;
using Islet.Settings;

namespace Islet.Integrations;

/// <summary>
/// Kawaki (kawaki.ru) в островке: вход по коду, уведомления аккаунта пиком,
/// поиск аниме по каталогу («k наруто»).
///
/// Вход — device flow, как у приставки: островок получает короткий код,
/// человек подтверждает его на сайте, где уже вошёл. Пароль островок не видит
/// никогда. Токены лежат в %APPDATA%\Islet\kawaki.dat, зашифрованные DPAPI —
/// прочитать их может только эта учётная запись Windows.
///
/// Уведомлений Kawaki не пушит — островок спрашивает сам, раз в полторы минуты,
/// и только пока вход выполнен.
/// </summary>
internal sealed class KawakiClient
{
    public const string Site = "https://kawaki.ru";
    private const string Api = Site + "/api/v1";
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(90);
    private static readonly string TokenPath = Path.Combine(Paths.Config, "kawaki.dat");
    private static readonly byte[] Entropy = "Islet.Kawaki.v1"u8.ToArray();

    private static readonly HttpClient Http = CreateHttp();

    public sealed record DeviceCode(string UserCode, string Code, DateTimeOffset ExpiresAt)
    {
        public string Formatted => UserCode.Length == 8 ? $"{UserCode[..4]}-{UserCode[4..]}" : UserCode;
        public string ActivateUrl => $"{Site}/auth/activate?code={Uri.EscapeDataString(UserCode)}";
    }

    private sealed class Account
    {
        public string Username { get; set; } = "";
        public string? AvatarUrl { get; set; }
        public string AccessToken { get; set; } = "";
        public long AccessExpiresAt { get; set; }
        public string RefreshToken { get; set; } = "";
    }

    private Account? _account;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private CancellationTokenSource? _pollCts;
    private readonly HashSet<string> _known = [];
    private bool _firstPoll = true;

    public bool SignedIn => _account is not null;
    public string? Username => _account?.Username;
    public string? AvatarUrl => _account?.AvatarUrl;
    public int UnreadCount { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Вход, выход, ошибка. Может прийти с фонового потока.</summary>
    public event Action? StateChanged;

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        var version = typeof(KawakiClient).Assembly.GetName().Version?.ToString(3) ?? "0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Islet/{version} (Windows)");
        return http;
    }

    public void Start()
    {
        _account = LoadAccount();
        StateChanged?.Invoke();
        RestartPolling();
    }

    public void Stop() => _pollCts?.Cancel();

    // ------------------------------------------------------------------
    // Вход
    // ------------------------------------------------------------------

    public async Task<DeviceCode?> StartLoginAsync()
    {
        try
        {
            var response = await Http.PostAsJsonAsync($"{Api}/auth/device/start", new
            {
                platform = "DESKTOP",
                deviceName = $"Islet · {Environment.MachineName}",
            });
            var data = await ReadData(response);
            if (data is not { } d) return null;
            return new DeviceCode(
                d.GetProperty("userCode").GetString()!,
                d.GetProperty("deviceCode").GetString()!,
                DateTimeOffset.FromUnixTimeMilliseconds(d.GetProperty("expiresAt").GetInt64()));
        }
        catch (Exception e)
        {
            Fail($"device start: {e.Message}");
            return null;
        }
    }

    /// <summary>Опрашивает, пока код не подтвердят или он не истечёт. true — вход выполнен.</summary>
    public async Task<bool> WaitForApprovalAsync(DeviceCode code, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && DateTimeOffset.Now < code.ExpiresAt)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                var response = await Http.PostAsJsonAsync($"{Api}/auth/device/poll", new
                {
                    deviceCode = code.Code,
                    platform = "DESKTOP",
                    deviceName = $"Islet · {Environment.MachineName}",
                }, ct);
                if (await ReadData(response) is not { } data) continue;
                var status = data.GetProperty("status").GetString();
                if (status == "expired") return false;
                if (status != "approved") continue;

                var tokens = data.GetProperty("tokens");
                var user = data.GetProperty("user");
                _account = new Account
                {
                    Username = user.GetProperty("username").GetString() ?? "",
                    AvatarUrl = user.TryGetProperty("avatarUrl", out var avatar) && avatar.ValueKind == JsonValueKind.String ? avatar.GetString() : null,
                    AccessToken = tokens.GetProperty("accessToken").GetString()!,
                    AccessExpiresAt = tokens.GetProperty("accessTokenExpiresAt").GetInt64(),
                    RefreshToken = tokens.GetProperty("refreshToken").GetString()!,
                };
                SaveAccount();
                LastError = null;
                _firstPoll = true;
                Log.Write($"kawaki: signed in as {_account.Username}");
                StateChanged?.Invoke();
                RestartPolling();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception e)
            {
                // Сеть моргнула — код ещё жив, просто спросим снова.
                Log.Write($"kawaki poll: {e.Message}");
            }
        }
        return false;
    }

    public async Task SignOutAsync()
    {
        var refresh = _account?.RefreshToken;
        _account = null;
        UnreadCount = 0;
        _known.Clear();
        _pollCts?.Cancel();
        try { File.Delete(TokenPath); } catch { }
        StateChanged?.Invoke();

        // Отзываем сессию на сервере, чтобы она не висела в «Устройствах» аккаунта.
        if (refresh is not null)
        {
            try { await Http.PostAsJsonAsync($"{Api}/auth/logout", new { refreshToken = refresh }); }
            catch { /* не вышло — сессия истечёт сама */ }
        }
    }

    // ------------------------------------------------------------------
    // Поиск
    // ------------------------------------------------------------------

    public async Task<List<ResultItem>> SearchAnimeAsync(string query, int max, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/catalog?search={Uri.EscapeDataString(query)}");
        using var response = await Http.SendAsync(request, ct);
        if (await ReadData(response, ct) is not { } data) return [];
        if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) return [];

        var results = new List<ResultItem>();
        foreach (var a in items.EnumerateArray())
        {
            if (results.Count >= max) break;
            var title = Str(a, "title") ?? Str(a, "titleEn");
            if (title is null || !a.TryGetProperty("externalId", out var idProp) || !idProp.TryGetInt64(out var externalId))
                continue;

            var meta = new List<string>();
            if (a.TryGetProperty("year", out var year) && year.ValueKind == JsonValueKind.Number) meta.Add(year.GetInt32().ToString());
            if (Str(a, "format") is { } format) meta.Add(FormatLabel(format));
            if (Str(a, "titleEn") is { } en && en != title) meta.Add(en);

            var poster = Str(a, "posterUrl");
            results.Add(new ResultItem
            {
                Title = title,
                Subtitle = string.Join(" · ", meta),
                Kind = ResultKind.Kawaki,
                Target = Site + AnimePath(externalId, Str(a, "titleEn"), title),
                ProviderId = "kawaki",
                IconUrl = poster is { Length: > 0 } && !poster.Contains("/assets/globals/missing", StringComparison.Ordinal) ? Absolute(poster) : "ms-appx:///Assets/kawaki.ico",
                Trailing = "Kawaki",
            });
        }
        return results;
    }

    private static string FormatLabel(string format) => format.ToUpperInvariant() switch
    {
        "MOVIE" => Loc.T("Kawaki_Movie"),
        "SPECIAL" => Loc.T("Kawaki_Special"),
        var f => f,
    };

    // ------------------------------------------------------------------
    // Уведомления
    // ------------------------------------------------------------------

    private void RestartPolling()
    {
        _pollCts?.Cancel();
        if (_account is null || !SettingsStore.Current.KawakiNotifications) return;
        var cts = _pollCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                await PollNotificationsAsync(cts.Token);
                try { await Task.Delay(PollEvery, cts.Token); }
                catch (OperationCanceledException) { return; }
            }
        });
    }

    /// <summary>Настройку уведомлений переключили — опрос встаёт или останавливается.</summary>
    public void ApplySettings() => RestartPolling();

    /// <summary>Островок раскрыли — свежий счётчик, но не чаще раза в 20 секунд.</summary>
    public void Nudge()
    {
        if (_account is null || DateTime.UtcNow - _lastPoll < TimeSpan.FromSeconds(20)) return;
        _ = PollNotificationsAsync(CancellationToken.None);
    }

    private DateTime _lastPoll;

    private async Task PollNotificationsAsync(CancellationToken ct)
    {
        _lastPoll = DateTime.UtcNow;
        try
        {
            var data = await GetAuthorizedAsync("/me/notifications?limit=15&scope=recent", ct);
            if (data is not { } d) return;
            UnreadCount = d.TryGetProperty("unreadCount", out var unread) ? unread.GetInt32() : 0;

            var fresh = new List<JsonElement>();
            foreach (var n in d.GetProperty("notifications").EnumerateArray())
            {
                var id = n.GetProperty("id").GetString()!;
                if (!_known.Add(id)) continue;
                if (n.GetProperty("isRead").GetBoolean()) continue;
                fresh.Add(n);
            }

            // Первый опрос после запуска: всё непрочитанное — в колокол тихо, пиком только
            // самое свежее и только если ему меньше часа. Иначе запуск Islet встречал бы
            // человека очередью из десятка старых уведомлений.
            for (var i = fresh.Count - 1; i >= 0; i--)
            {
                var n = fresh[i];
                var created = DateTime.TryParse(Str(n, "createdAt"), out var t) ? t.ToLocalTime() : DateTime.Now;
                var silent = _firstPoll && (i != 0 || DateTime.Now - created > TimeSpan.FromHours(1));
                Post(n, created, silent);
            }
            _firstPoll = false;
            LastError = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            Log.Write($"kawaki notifications: {e.Message}");
        }
    }

    private static void Post(JsonElement n, DateTime created, bool silent)
    {
        string? actor = null, avatar = null;
        if (n.TryGetProperty("actor", out var a) && a.ValueKind == JsonValueKind.Object)
        {
            actor = Str(a, "username");
            avatar = Str(a, "avatarUrl");
        }
        var link = Str(n, "link");
        App.Current.Notifications.Post(new IsletNotification
        {
            Source = "kawaki",
            SourceName = "Kawaki",
            ExternalId = Str(n, "id"),
            Title = actor ?? "Kawaki",
            Body = Str(n, "content") ?? "",
            Icon = avatar is { Length: > 0 } ? Absolute(avatar) : "ms-appx:///Assets/kawaki.ico",
            Action = IsletAction.OpenTarget(link is { Length: > 0 } ? Absolute(link) : Site),
            Time = created,
            Silent = silent,
        });
    }

    public async Task MarkReadAsync(string? id)
    {
        if (_account is null || id is null) return;
        try
        {
            await SendAuthorizedAsync(HttpMethod.Post, "/me/notifications/read", new JsonObject { ["id"] = id }, CancellationToken.None);
            if (UnreadCount > 0) UnreadCount--;
        }
        catch (Exception e)
        {
            Log.Write($"kawaki mark read: {e.Message}");
        }
    }

    // ------------------------------------------------------------------
    // Запросы с токеном
    // ------------------------------------------------------------------

    private Task<JsonElement?> GetAuthorizedAsync(string path, CancellationToken ct) =>
        SendAuthorizedAsync(HttpMethod.Get, path, null, ct);

    private async Task<JsonElement?> SendAuthorizedAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_account is null) return null;
            if (DateTimeOffset.Now.ToUnixTimeMilliseconds() > _account.AccessExpiresAt - 30_000 && !await RefreshAsync(ct))
                return null;

            using var request = new HttpRequestMessage(method, Api + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _account!.AccessToken);
            if (body is not null)
                request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                // Токен отозвали или часы разошлись — обновляем и пробуем ещё раз.
                _account.AccessExpiresAt = 0;
                continue;
            }
            return await ReadData(response, ct);
        }
        return null;
    }

    private async Task<bool> RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            if (_account is null) return false;
            if (DateTimeOffset.Now.ToUnixTimeMilliseconds() < _account.AccessExpiresAt - 30_000) return true;

            var response = await Http.PostAsJsonAsync($"{Api}/auth/refresh", new { refreshToken = _account.RefreshToken }, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // Сессию отозвали на сайте («Устройства» → выйти): вход придётся повторить.
                Log.Write("kawaki: refresh rejected, signing out");
                LastError = Loc.T("Kawaki_SessionExpired");
                await SignOutAsync();
                return false;
            }
            if (await ReadData(response, ct) is not { } data) return false;
            var tokens = data.GetProperty("tokens");
            _account.AccessToken = tokens.GetProperty("accessToken").GetString()!;
            _account.AccessExpiresAt = tokens.GetProperty("accessTokenExpiresAt").GetInt64();
            _account.RefreshToken = tokens.GetProperty("refreshToken").GetString()!;
            SaveAccount();
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Write($"kawaki refresh: {e.Message}");
            return false;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static async Task<JsonElement?> ReadData(HttpResponseMessage response, CancellationToken ct = default)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (text.Length == 0) return null;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True && root.TryGetProperty("data", out var data))
            return data.Clone();
        if (root.TryGetProperty("error", out var error))
            Log.Write($"kawaki api: {(int)response.StatusCode} {Str(error, "code")} {Str(error, "message")}");
        return null;
    }

    private void Fail(string message)
    {
        LastError = message;
        Log.Write($"kawaki: {message}");
        StateChanged?.Invoke();
    }

    // ------------------------------------------------------------------
    // Хранение
    // ------------------------------------------------------------------

    private static Account? LoadAccount()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(TokenPath), Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Account>(bytes);
        }
        catch (Exception e)
        {
            Log.Write($"kawaki.dat unreadable: {e.Message}");
            return null;
        }
    }

    private void SaveAccount()
    {
        if (_account is null) return;
        try
        {
            Directory.CreateDirectory(Paths.Config);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(_account), Entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenPath, bytes);
        }
        catch (Exception e)
        {
            Log.Write($"kawaki.dat not saved: {e.Message}");
        }
    }

    // ------------------------------------------------------------------

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Absolute(string url) =>
        url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : Site + (url.StartsWith('/') ? url : "/" + url);

    private static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "yo",
        ['ж'] = "zh", ['з'] = "z", ['и'] = "i", ['й'] = "y", ['к'] = "k", ['л'] = "l", ['м'] = "m",
        ['н'] = "n", ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u",
        ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch",
        ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>Тот же адрес, что строит сайт: /anime/{externalId}-{slug} (lib/anime-url.ts).</summary>
    private static string AnimePath(long externalId, string? titleEn, string title)
    {
        var source = (titleEn?.Trim() is { Length: > 0 } en ? en : title.Trim()).ToLowerInvariant();
        var sb = new StringBuilder();
        foreach (var c in source)
            sb.Append(Translit.TryGetValue(c, out var t) ? t : c.ToString());
        var slug = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "[^a-z0-9]+", "-").Trim('-');
        return slug.Length > 0 ? $"/anime/{externalId}-{slug}" : $"/anime/{externalId}";
    }
}
