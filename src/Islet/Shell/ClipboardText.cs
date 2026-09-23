using Islet.Native;
using Windows.ApplicationModel.DataTransfer;

namespace Islet.Shell;

/// <summary>Текст в буфере обмена. Вызывать с UI-потока.</summary>
internal static class ClipboardText
{
    /// <summary>Текст, который положил сам островок, — история его не пишет второй раз.</summary>
    public static string? LastSetByIslet { get; private set; }

    public static bool Set(string text)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            LastSetByIslet = text;
            Clipboard.SetContent(package);
            // Иначе содержимое пропадёт вместе с островком.
            Clipboard.Flush();
            return true;
        }
        catch (Exception e)
        {
            Log.Write($"clipboard set failed: {e.Message}");
            return false;
        }
    }

    public static async Task<string?> GetAsync()
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                var content = Clipboard.GetContent();
                return content.Contains(StandardDataFormats.Text) ? await content.GetTextAsync() : null;
            }
            catch
            {
                // Буфер занят другой программой — одна повторная попытка чуть позже.
                await Task.Delay(60);
            }
        }
        return null;
    }
}

/// <summary>
/// История буфера обмена — только текст, только в памяти, до перезапуска.
///
/// Менеджеры паролей помечают скопированное форматами
/// «ExcludeClipboardContentFromMonitorProcessing» и «CanIncludeInClipboardHistory = 0» —
/// такое в историю не попадает, как и у встроенного журнала Windows (Win+V).
/// </summary>
internal sealed class ClipboardHistory
{
    public sealed record Entry(string Text, DateTime Time);

    private const int MaxLength = 20_000;
    private readonly List<Entry> _items = [];
    private static readonly uint ExcludeFormat = Win32.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryFormat = Win32.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private static readonly uint IgnoreFormat = Win32.RegisterClipboardFormat("Clipboard Viewer Ignore");

    public IReadOnlyList<Entry> Items => _items;

    /// <summary>Вызывается на WM_CLIPBOARDUPDATE (UI-поток).</summary>
    public async void OnClipboardChanged()
    {
        var settings = Settings.SettingsStore.Current;
        if (!settings.ClipboardHistory) return;
        if (IsPrivate()) return;

        var text = await ClipboardText.GetAsync();
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength) return;

        _items.RemoveAll(e => e.Text == text);
        _items.Insert(0, new Entry(text, DateTime.Now));
        var max = Math.Clamp(settings.ClipboardMax, 5, 100);
        if (_items.Count > max)
            _items.RemoveRange(max, _items.Count - max);
    }

    public void Remove(Entry entry) => _items.Remove(entry);

    public void Clear() => _items.Clear();

    private static bool IsPrivate()
    {
        try
        {
            if (Win32.IsClipboardFormatAvailable(ExcludeFormat) || Win32.IsClipboardFormatAvailable(IgnoreFormat))
                return true;
            // Значение формата — DWORD: 0 означает «не для истории».
            return Win32.IsClipboardFormatAvailable(HistoryFormat) && Win32.ReadClipboardDword(HistoryFormat) == 0;
        }
        catch
        {
            return false;
        }
    }
}
