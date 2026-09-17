using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace KawakiIsland.Native;

/// <summary>
/// Прозрачная подложка окна: всё, что не нарисовано XAML, видно насквозь.
/// Так островок может быть любой формы, а не прямоугольником с системными углами.
/// Работает в паре с <see cref="Win32.EnablePerPixelTransparency"/>.
/// </summary>
internal sealed class TransparentBackdrop : SystemBackdrop
{
    // Подложка окна живёт в системном композиторе (Windows.UI.Composition), а не
    // в WinUI-шном, и ему нужна системная очередь диспетчера на этом потоке.
    private static Windows.UI.Composition.Compositor? _compositor;
    private static object? _queueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        connectedTarget.SystemBackdrop = GetCompositor().CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }

    private static Windows.UI.Composition.Compositor GetCompositor()
    {
        if (_compositor is not null) return _compositor;

        if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
        {
            var options = new DispatcherQueueOptions
            {
                dwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                threadType = 2,      // DQTYPE_THREAD_CURRENT
                apartmentType = 0,   // DQTAT_COM_NONE
            };
            Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out var controller));
            _queueController = WinRT.MarshalInspectable<object>.FromAbi(controller);
        }

        return _compositor = new Windows.UI.Composition.Compositor();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int dwSize;
        public int threadType;
        public int apartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint dispatcherQueueController);
}
