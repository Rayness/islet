using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Islet.Core;

namespace Islet.Plugins;

/// <summary>
/// Процесс плагина и разговор с ним: JSON по строке в stdin и stdout.
///
/// Процесс живёт, пока островок работает, и запускается по первому запросу
/// (или сразу, если в манифесте <c>startup: true</c>). Упавший поднимается
/// заново при следующем запросе, но не чаще раза в пять секунд; трижды упавший
/// за минуту — выключается с ошибкой в настройках. Ответ, опоздавший дольше
/// <c>timeoutMs</c>, выбрасывается: островок не ждёт медленный плагин.
/// </summary>
internal sealed class PluginHost : IDisposable
{
    private readonly PluginInfo _plugin;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Lock _gate = new();
    private readonly List<DateTime> _crashes = [];
    private Process? _process;
    private StreamWriter? _stdin;
    private int _nextId;
    private DateTime _lastStart;
    private bool _disposed;

    public PluginHost(PluginInfo plugin) => _plugin = plugin;

    public bool IsRunning => _process is { HasExited: false };

    public void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed || IsRunning || _plugin.Error is not null) return;
            if (DateTime.Now - _lastStart < TimeSpan.FromSeconds(5)) return;
            _lastStart = DateTime.Now;
            Start();
        }
    }

    private void Start()
    {
        var run = _plugin.Manifest.Run!;
        var command = run.Command;
        // Путь относительно папки плагина — только если такой файл там есть: «python» ищется в PATH.
        var local = Path.Combine(_plugin.Directory, command);
        if (File.Exists(local)) command = local;

        var info = new ProcessStartInfo(command)
        {
            WorkingDirectory = _plugin.Directory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
        };
        foreach (var arg in run.Args)
            info.ArgumentList.Add(arg.Replace("{pluginDir}", _plugin.Directory));
        info.Environment["ISLET_API"] = "1";
        info.Environment["ISLET_PLUGIN_DIR"] = _plugin.Directory;
        info.Environment["ISLET_PIPE"] = @"\\.\pipe\" + Ipc.IpcChannel.PipeName;
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUNBUFFERED"] = "1";

        try
        {
            var process = Process.Start(info) ?? throw new InvalidOperationException("process did not start");
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => OnExited(process);
            _process = process;
            _stdin = process.StandardInput;
            _stdin.AutoFlush = true;
            _ = Task.Run(() => ReadLoop(process));
            _ = Task.Run(() => ErrorLoop(process));

            Send(new JsonObject
            {
                ["type"] = "hello",
                ["api"] = 1,
                ["islet"] = typeof(PluginHost).Assembly.GetName().Version?.ToString(3),
                ["language"] = Loc.CurrentLanguage,
                ["pluginDir"] = _plugin.Directory,
            });
            Log.Write($"plugin {_plugin.Id}: started (pid {process.Id})");
        }
        catch (Exception e)
        {
            _plugin.Error = Loc.T("Plugin_StartFailed", e.Message);
            Log.Write($"plugin {_plugin.Id}: {e.Message}");
            App.Current.Plugins.RaiseChanged();
        }
    }

    public async Task<JsonElement?> QueryAsync(string text, bool scoped, CancellationToken ct)
    {
        EnsureStarted();
        if (!IsRunning) return null;

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            Send(new JsonObject { ["type"] = "query", ["id"] = id, ["text"] = text, ["scoped"] = scoped });
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(_plugin.Manifest.TimeoutMs, 200, 10_000)), ct);
        }
        catch (TimeoutException)
        {
            Log.Write($"plugin {_plugin.Id}: query timed out");
            return null;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public void Invoke(string action, JsonElement? data)
    {
        EnsureStarted();
        var msg = new JsonObject { ["type"] = "invoke", ["action"] = action };
        if (data is { } d) msg["data"] = JsonNode.Parse(d.GetRawText());
        Send(msg);
    }

    private void Send(JsonObject message)
    {
        try
        {
            lock (_gate)
                _stdin?.WriteLine(message.ToJsonString());
        }
        catch (Exception e)
        {
            Log.Write($"plugin {_plugin.Id}: write failed: {e.Message}");
        }
    }

    private async Task ReadLoop(Process process)
    {
        try
        {
            var reader = process.StandardOutput;
            while (await reader.ReadLineAsync() is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                JsonElement msg;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    msg = doc.RootElement.Clone();
                }
                catch (JsonException)
                {
                    Log.Write($"plugin {_plugin.Id}: not JSON: {Clip(line)}");
                    continue;
                }

                if (Protocol.Str(msg, "type") == "results")
                {
                    if (msg.TryGetProperty("id", out var idProp) && idProp.TryGetInt32(out var id) && _pending.TryGetValue(id, out var tcs))
                        tcs.TrySetResult(msg);
                    continue;
                }
                Protocol.Handle(msg, $"plugin:{_plugin.Id}", _plugin.Name, _plugin.Id);
            }
        }
        catch (Exception e)
        {
            Log.Write($"plugin {_plugin.Id}: read loop: {e.Message}");
        }
    }

    private async Task ErrorLoop(Process process)
    {
        try
        {
            var lines = 0;
            while (await process.StandardError.ReadLineAsync() is { } line)
            {
                // Плагин, сыплющий в stderr, не должен раздувать лог островка.
                if (++lines <= 50) Log.Write($"plugin {_plugin.Id} stderr: {Clip(line)}");
            }
        }
        catch { /* процесс закрылся */ }
    }

    private void OnExited(Process process)
    {
        if (_disposed) return;
        var code = -1;
        try { code = process.ExitCode; } catch { }
        Log.Write($"plugin {_plugin.Id}: exited with {code}");
        foreach (var pending in _pending.Values) pending.TrySetCanceled();
        App.Current.Activities.ClearSource($"plugin:{_plugin.Id}");

        lock (_gate)
        {
            _crashes.Add(DateTime.Now);
            _crashes.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(1));
            if (_crashes.Count >= 3)
            {
                _plugin.Error = Loc.T("Plugin_Crashed", code);
                App.Current.Plugins.RaiseChanged();
            }
        }
        // Плагину, который работает в фоне, нужен живой процесс — поднимаем его снова.
        if (_plugin.Manifest.Startup && _plugin.Error is null)
            _ = Task.Delay(5500).ContinueWith(_ => EnsureStarted());
    }

    public void Dispose()
    {
        _disposed = true;
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            Send(new JsonObject { ["type"] = "shutdown" });
            if (!process.WaitForExit(300))
                process.Kill(entireProcessTree: true);
        }
        catch { /* уже закрылся */ }
        process.Dispose();
    }

    private static string Clip(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
