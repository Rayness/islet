using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Islet.Ipc;

/// <summary>
/// Именованный канал островка: по нему второй запуск, скрипты и сторонние
/// программы присылают сообщения (одна строка JSON на сообщение).
///
/// Канал только для текущего пользователя (<see cref="PipeOptions.CurrentUserOnly"/>):
/// чужая учётная запись на той же машине подключиться к нему не может.
/// </summary>
internal static class IpcChannel
{
    /// <summary>Имя канала: \\.\pipe\Islet.&lt;имя пользователя&gt;.</summary>
    public static string PipeName { get; } = $"Islet.{Environment.UserName}";
}

internal static class IpcClient
{
    /// <summary>Строка JSON в канал островка или, если задан <paramref name="pipeName"/>, в чужой (ClipTide).</summary>
    public static bool TrySend(string jsonLine, int timeoutMs = 2000, string? pipeName = null)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName ?? IpcChannel.PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(jsonLine.ReplaceLineEndings(" ") + "\n");
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Сервер канала. Подключения принимаются по одному; каждое читается до конца,
/// каждая строка — отдельное сообщение. Обработчик вызывается на фоновом потоке.
/// </summary>
internal sealed class IpcServer : IDisposable
{
    private const int MaxLineBytes = 64 * 1024;

    private readonly CancellationTokenSource _cts = new();

    public event Action<JsonElement>? MessageReceived;

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    IpcChannel.PipeName, PipeDirection.In, 4,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_cts.Token);
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    if (line.Length == 0 || line.Length > MaxLineBytes) continue;
                    Dispatch(line);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                Log.Write($"ipc: {e.Message}");
                // Не крутим цикл вхолостую, если канал не создаётся.
                try { await Task.Delay(1000, _cts.Token); } catch { return; }
            }
        }
    }

    private void Dispatch(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            MessageReceived?.Invoke(doc.RootElement.Clone());
        }
        catch (JsonException e)
        {
            Log.Write($"ipc: bad message: {e.Message}");
        }
    }

    public void Dispose() => _cts.Cancel();
}
