using Islet.Native;
using Microsoft.Win32.SafeHandles;

namespace Islet.Integrations.Devices;

/// <summary>
/// Одна HID-коллекция, открытая для разговора с устройством.
///
/// Семантика — как у hidapi, на котором написаны HeadsetControl, rivalcfg и почти все
/// разборы протоколов: при записи байт 0 — номер отчёта (0, если отчёты без номеров),
/// короткий пакет дополняется нулями до длины отчёта; при чтении у отчёта без номера
/// ведущий 0 отбрасывается; у feature-отчёта номер остаётся в байте 0. Так рецепты
/// из этих проектов переносятся байт в байт.
/// </summary>
internal interface IHidChannel : IDisposable
{
    int InputLength { get; }
    int OutputLength { get; }
    int FeatureLength { get; }

    bool Write(byte[] report);
    /// <summary>Следующий входной отчёт или null по таймауту.</summary>
    byte[]? Read(TimeSpan timeout);
    bool SetFeature(byte[] report);
    byte[]? GetFeature(byte reportId);
    /// <summary>Выбросить накопившиеся входные отчёты — чтобы старый ответ не сошёл за новый.</summary>
    void Flush();
}

internal sealed class HidChannel : IHidChannel
{
    private readonly SafeFileHandle _handle;
    private readonly FileStream _stream;
    private readonly Hid.Caps _caps;

    private HidChannel(SafeFileHandle handle, Hid.Caps caps)
    {
        _handle = handle;
        _caps = caps;
        // bufferSize 0: без буфера FileStream — каждый Read/Write ровно один отчёт.
        _stream = new FileStream(handle, FileAccess.ReadWrite, 0, isAsync: true);
    }

    public static HidChannel? Open(string path)
    {
        var handle = Hid.Open(path, overlapped: true);
        if (handle is null) return null;
        if (Hid.GetCaps(handle) is { } caps) return new HidChannel(handle, caps);
        handle.Dispose();
        return null;
    }

    public int InputLength => _caps.InputReportByteLength;
    public int OutputLength => _caps.OutputReportByteLength;
    public int FeatureLength => _caps.FeatureReportByteLength;

    public bool Write(byte[] report)
    {
        if (OutputLength == 0 || report.Length > OutputLength) return false;
        var buffer = new byte[OutputLength];
        report.CopyTo(buffer, 0);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            _stream.WriteAsync(buffer, timeout.Token).AsTask().GetAwaiter().GetResult();
            return true;
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public byte[]? Read(TimeSpan timeout)
    {
        if (InputLength == 0 || timeout <= TimeSpan.Zero) return null;
        var buffer = new byte[InputLength];
        try
        {
            using var cts = new CancellationTokenSource(timeout);
            var read = _stream.ReadAsync(buffer, cts.Token).AsTask().GetAwaiter().GetResult();
            if (read <= 0) return null;
            // hidapi: у отчётов без номера Windows кладёт 0 в начало — его отрезают.
            return buffer[0] == 0 ? buffer[1..read] : buffer[..read];
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            return null;
        }
    }

    public bool SetFeature(byte[] report)
    {
        if (FeatureLength == 0 || report.Length > FeatureLength) return false;
        var buffer = new byte[FeatureLength];
        report.CopyTo(buffer, 0);
        return Hid.SetFeature(_handle, buffer);
    }

    public byte[]? GetFeature(byte reportId)
    {
        if (FeatureLength == 0) return null;
        var buffer = new byte[FeatureLength];
        buffer[0] = reportId;
        return Hid.GetFeature(_handle, buffer) ? buffer : null;
    }

    public void Flush()
    {
        for (var i = 0; i < 32 && Read(TimeSpan.FromMilliseconds(4)) is not null; i++) { }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _handle.Dispose();
    }
}
