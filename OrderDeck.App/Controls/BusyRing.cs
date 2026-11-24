using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace OrderDeck.App.Controls;

/// <summary>
/// Meşgul halkası: 270 derecelik, 2 px çizgili, yuvarlak uçlu bir yay; iş sürerken kendi
/// merkezinde döner. Düğme şablonlarında içeriğin yerinde durur (bkz. <see cref="ButtonState"/>).
///
/// <para><b>Kendi kendini yönetir.</b> Yalnız görünürken ve <see cref="MotionPolicy"/> izin verirken
/// döner; gizlenince animasyon durur. Şablonların her birinde hazır beklediği için (yaklaşık 150
/// düğme) gizliyken dönmemesi şart. Hareket azaltmada yay yine çizilir, dönmez.</para>
///
/// <para>Ölçüsünü <c>Width</c>/<c>Height</c> verir; yay kısa kenara sığdırılır. Fare almaz.</para>
/// </summary>
public sealed class BusyRing : FrameworkElement
{
    /// <summary>Yayın çizgi kalınlığı.</summary>
    private const double StrokeThickness = 2;

    /// <summary>Yayın rengi; şablonda düğmenin <c>Foreground</c>'una bağlanır.</summary>
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(BusyRing),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Bir turun süresi; şablonda <c>OD.Dur.Slow</c>'dan verilir.</summary>
    public static readonly DependencyProperty PeriodProperty = DependencyProperty.Register(
        nameof(Period), typeof(Duration), typeof(BusyRing),
        new FrameworkPropertyMetadata(new Duration(TimeSpan.FromMilliseconds(850))));

    private readonly RotateTransform _rotation = new();

    public BusyRing()
    {
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = _rotation;
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => UpdateSpin();
    }

    /// <inheritdoc cref="StrokeProperty"/>
    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    /// <inheritdoc cref="PeriodProperty"/>
    public Duration Period
    {
        get => (Duration)GetValue(PeriodProperty);
        set => SetValue(PeriodProperty, value);
    }

    /// <summary>Dönme animasyonu çalışıyor mu (görünür ve hareket açık).</summary>
    internal bool IsSpinning { get; private set; }

    /// <inheritdoc />
    protected override void OnRender(DrawingContext drawingContext)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (Stroke is not { } stroke || side <= StrokeThickness) return;

        var radius = (side - StrokeThickness) / 2;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        // Tepeden başlar, saat yönünde sola kadar: çeyreği açık bir halka.
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), isFilled: false, isClosed: false);
            context.ArcTo(
                new Point(center.X - radius, center.Y), new Size(radius, radius), rotationAngle: 0,
                isLargeArc: true, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        geometry.Freeze();

        var pen = new Pen(stroke, StrokeThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        if (pen.CanFreeze) pen.Freeze();

        drawingContext.DrawGeometry(null, pen, geometry);
    }

    /// <summary>Görünürlük değişince döngüyü başlatır ya da durdurur.</summary>
    private void UpdateSpin()
    {
        var spin = IsVisible && MotionPolicy.IsEnabled;
        if (spin == IsSpinning) return;

        IsSpinning = spin;
        _rotation.BeginAnimation(
            RotateTransform.AngleProperty,
            spin ? new DoubleAnimation(0, 360, Period) { RepeatBehavior = RepeatBehavior.Forever } : null);
    }
}
