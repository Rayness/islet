using System.Numerics;
using System.Runtime.InteropServices;
using Islet.Native;

namespace Islet.Media;

/// <summary>
/// Спектр того, что сейчас звучит в колонках, — для визуализатора.
///
/// Звук берётся захватом «петлёй» (WASAPI loopback): это копия общего микса
/// устройства вывода, микрофон не участвует и разрешений не нужно. Каждые
/// 1024 отсчёта — БПФ с окном Ханна, энергия раскладывается по полосам на
/// логарифмической шкале 60 Гц … 14 кГц. Громкость полосы нормируется по её
/// же недавнему максимуму: тихая музыка и громкая дают одинаково живые
/// столбики, а чувствительность из настроек лишь сдвигает эту планку.
///
/// Поток захвата работает, только пока визуализатор виден (<see cref="Start"/>/
/// <see cref="Stop"/>): без музыки на экране островок звук не трогает вовсе.
/// </summary>
internal sealed class AudioSpectrum : IDisposable
{
    private const int FftSize = 1024;
    public const int MaxBands = 12;

    private readonly float[] _levels = new float[MaxBands];
    private readonly float[] _peaks = new float[MaxBands];
    private readonly float[] _ring = new float[FftSize];
    private readonly Complex[] _fft = new Complex[FftSize];
    private readonly float[] _window = new float[FftSize];
    private readonly Lock _gate = new();
    private int _ringPos;
    private int _fresh;
    private Thread? _thread;
    private volatile bool _running;
    private volatile int _generation;
    private double _sampleRate = 48_000;
    private volatile int _bands = 4;
    private volatile float _sensitivity = 1;

    public AudioSpectrum()
    {
        for (var i = 0; i < FftSize; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FftSize - 1)));
    }

    public bool IsRunning => _running;

    public void Configure(int bands, double sensitivity)
    {
        _bands = Math.Clamp(bands, 1, MaxBands);
        _sensitivity = (float)Math.Clamp(sensitivity, 0.3, 4);
    }

    /// <summary>Текущие уровни 0…1 — копия, читается с UI-потока.</summary>
    public void Read(Span<float> target)
    {
        lock (_gate)
        {
            for (var i = 0; i < target.Length && i < MaxBands; i++)
                target[i] = _levels[i];
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        // Поколение: поток от прошлого запуска, не успевший выйти, увидит чужой номер и уйдёт сам.
        var generation = ++_generation;
        _thread = new Thread(() => CaptureLoop(generation)) { IsBackground = true, Name = "Islet.Loopback", Priority = ThreadPriority.BelowNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        lock (_gate) Array.Clear(_levels);
    }

    private bool Alive(int generation) => _running && generation == _generation;

    private void CaptureLoop(int generation)
    {
        while (Alive(generation))
        {
            try
            {
                CaptureOnce(generation);
            }
            catch (Exception e)
            {
                // Устройство сменили или выдернули — через секунду подключимся к новому.
                Log.Write($"loopback: {e.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private void CaptureOnce(int generation)
    {
        var device = CoreAudio.DefaultRenderDevice();
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        var format = nint.Zero;
        try
        {
            client = CoreAudio.Activate<IAudioClient>(device, CoreAudio.IID_IAudioClient);
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out format));
            var (channels, isFloat, bits, rate) = ReadFormat(format);
            _sampleRate = rate;
            Marshal.ThrowExceptionForHR(client.Initialize(CoreAudio.AUDCLNT_SHAREMODE_SHARED, CoreAudio.AUDCLNT_STREAMFLAGS_LOOPBACK,
                2_000_000, 0, format, 0));
            var iid = CoreAudio.IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var service));
            capture = (IAudioCaptureClient)service;
            Marshal.ThrowExceptionForHR(client.Start());

            var idle = 0;
            while (Alive(generation))
            {
                Thread.Sleep(15);
                Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var packet));
                if (packet == 0)
                {
                    // Тишина: loopback ничего не присылает — столбики плавно опускаются.
                    if (++idle > 3) Decay();
                    continue;
                }
                idle = 0;
                while (packet > 0)
                {
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
                    Consume(data, (int)frames, channels, isFloat, bits, (flags & CoreAudio.AUDCLNT_BUFFERFLAGS_SILENT) != 0);
                    capture.ReleaseBuffer(frames);
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out packet));
                }
            }
            client.Stop();
        }
        finally
        {
            if (format != 0) Marshal.FreeCoTaskMem(format);
            if (capture is not null) Marshal.ReleaseComObject(capture);
            if (client is not null) Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(device);
        }
    }

    private static (int Channels, bool IsFloat, int Bits, int Rate) ReadFormat(nint format)
    {
        var tag = (ushort)Marshal.ReadInt16(format, 0);
        var channels = (ushort)Marshal.ReadInt16(format, 2);
        var rate = Marshal.ReadInt32(format, 4);
        var bits = (ushort)Marshal.ReadInt16(format, 14);
        var isFloat = tag == 3;
        if (tag == 0xFFFE)
        {
            var subtype = Marshal.PtrToStructure<Guid>(format + 24);
            isFloat = subtype == CoreAudio.FloatSubtype;
        }
        return (Math.Max((int)channels, 1), isFloat, bits, rate > 0 ? rate : 48_000);
    }

    private unsafe void Consume(nint data, int frames, int channels, bool isFloat, int bits, bool silent)
    {
        for (var f = 0; f < frames; f++)
        {
            float sample = 0;
            if (!silent)
            {
                // Каналы сводим в моно: визуализатору стерео ни к чему.
                if (isFloat && bits == 32)
                {
                    var p = (float*)data + f * channels;
                    for (var c = 0; c < channels; c++) sample += p[c];
                }
                else if (bits == 16)
                {
                    var p = (short*)data + f * channels;
                    for (var c = 0; c < channels; c++) sample += p[c] / 32768f;
                }
                else if (bits == 32)
                {
                    var p = (int*)data + f * channels;
                    for (var c = 0; c < channels; c++) sample += p[c] / 2147483648f;
                }
                sample /= channels;
            }
            _ring[_ringPos] = sample;
            _ringPos = (_ringPos + 1) % FftSize;
            // Полшага окна — 512 отсчётов, около 90 обновлений в секунду на 48 кГц.
            if (++_fresh >= FftSize / 2)
            {
                _fresh = 0;
                Analyze();
            }
        }
    }

    private void Analyze()
    {
        for (var i = 0; i < FftSize; i++)
            _fft[i] = new Complex(_ring[(_ringPos + i) % FftSize] * _window[i], 0);
        Fft(_fft);

        var bands = _bands;
        Span<float> raw = stackalloc float[MaxBands];
        // Полосы на логарифмической шкале: бас не тонет среди сотен верхних бинов.
        const double low = 60, high = 14_000;
        var binHz = _sampleRate / FftSize;
        for (var b = 0; b < bands; b++)
        {
            var from = low * Math.Pow(high / low, (double)b / bands);
            var to = low * Math.Pow(high / low, (double)(b + 1) / bands);
            var start = Math.Max(1, (int)(from / binHz));
            var end = Math.Max(start + 1, (int)(to / binHz));
            double sum = 0;
            for (var k = start; k < end && k < FftSize / 2; k++)
                sum += _fft[k].Magnitude;
            raw[b] = (float)(sum / (end - start));
        }

        lock (_gate)
        {
            for (var b = 0; b < bands; b++)
            {
                // Своя планка у каждой полосы: максимум, медленно тающий со временем.
                _peaks[b] = Math.Max(raw[b], _peaks[b] * 0.996f);
                var floor = Math.Max(_peaks[b], 0.02f);
                var target = Math.Clamp(raw[b] / floor * _sensitivity, 0, 1);
                target = MathF.Pow(target, 1.4f);
                // Вверх — быстро, вниз — плавно: так столбики «прыгают» в такт, а не дрожат.
                var k = target > _levels[b] ? 0.6f : 0.18f;
                _levels[b] += (target - _levels[b]) * k;
            }
        }
    }

    private void Decay()
    {
        lock (_gate)
        {
            for (var b = 0; b < MaxBands; b++)
                _levels[b] *= 0.8f;
        }
    }

    /// <summary>Классическое БПФ по основанию 2, на месте.</summary>
    private static void Fft(Complex[] a)
    {
        var n = a.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (a[i], a[j]) = (a[j], a[i]);
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wLen = new Complex(Math.Cos(angle), Math.Sin(angle));
            for (var i = 0; i < n; i += len)
            {
                var w = Complex.One;
                for (var k = 0; k < len / 2; k++)
                {
                    var u = a[i + k];
                    var v = a[i + k + len / 2] * w;
                    a[i + k] = u + v;
                    a[i + k + len / 2] = u - v;
                    w *= wLen;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
