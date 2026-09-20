using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinComp = Windows.UI.Composition;

namespace Islet.Native;

/// <summary>
/// Подложка окна островка. Окно прозрачное; под пилюлей — размытый рабочий
/// стол (стекло), вырезанный по её форме маской. Тонировку даёт полупрозрачный
/// фон самой пилюли в XAML.
///
/// Работает в системном композиторе (Windows.UI.Composition): только он умеет
/// брать то, что под окном (HostBackdropBrush). Если стекло недоступно
/// (выключены эффекты прозрачности, старая Windows) — просто прозрачно.
/// </summary>
internal sealed class IslandBackdrop : SystemBackdrop
{
    private static WinComp.Compositor? _compositor;
    private static object? _queueController;

    private ICompositionSupportsSystemBackdrop? _target;
    private bool _glass;

    private WinComp.ShapeVisual? _shapeVisual;
    private WinComp.CompositionRoundedRectangleGeometry? _geometry;
    private WinComp.CompositionVisualSurface? _surface;

    public bool Glass
    {
        get => _glass;
        set
        {
            if (_glass == value) return;
            _glass = value;
            Apply();
        }
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        _target = connectedTarget;
        Apply();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        disconnectedTarget.SystemBackdrop = null;
        _target = null;
        base.OnTargetDisconnected(disconnectedTarget);
    }

    /// <summary>Форма пилюли в пикселях окна. Вызывается на каждом кадре анимации.</summary>
    public void UpdateShape(Vector2 windowSize, Vector2 offset, Vector2 size, float radius)
    {
        if (_shapeVisual is null || _geometry is null || _surface is null) return;
        _shapeVisual.Size = windowSize;
        _surface.SourceSize = windowSize;
        _geometry.Offset = offset;
        _geometry.Size = size;
        _geometry.CornerRadius = new Vector2(radius, radius);
    }

    private void Apply()
    {
        if (_target is null) return;
        var compositor = GetCompositor();

        if (_glass)
        {
            try
            {
                _geometry = compositor.CreateRoundedRectangleGeometry();
                var shape = compositor.CreateSpriteShape(_geometry);
                shape.FillBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
                _shapeVisual = compositor.CreateShapeVisual();
                _shapeVisual.Shapes.Add(shape);

                _surface = compositor.CreateVisualSurface();
                _surface.SourceVisual = _shapeVisual;

                var mask = compositor.CreateSurfaceBrush(_surface);
                mask.Stretch = WinComp.CompositionStretch.None;
                mask.HorizontalAlignmentRatio = 0;
                mask.VerticalAlignmentRatio = 0;

                // MaskBrush не принимает подложку как источник — нужен эффект.
                // Win2D здесь только описывает граф: исполняет его системный композитор.
                var effect = new Microsoft.Graphics.Canvas.Effects.AlphaMaskEffect
                {
                    Source = new WinComp.CompositionEffectSourceParameter("backdrop"),
                    AlphaMask = new WinComp.CompositionEffectSourceParameter("mask"),
                };
                var brush = compositor.CreateEffectFactory(effect).CreateBrush();
                brush.SetSourceParameter("backdrop", compositor.CreateHostBackdropBrush());
                brush.SetSourceParameter("mask", mask);

                _target.SystemBackdrop = brush;
                return;
            }
            catch (Exception e)
            {
                Log.Write($"glass unavailable: {e.Message}");
            }
        }

        _shapeVisual = null;
        _geometry = null;
        _surface = null;
        _target.SystemBackdrop = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    private static WinComp.Compositor GetCompositor()
    {
        if (_compositor is not null) return _compositor;

        // Системному композитору нужна системная очередь диспетчера на этом потоке.
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

        return _compositor = new WinComp.Compositor();
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
