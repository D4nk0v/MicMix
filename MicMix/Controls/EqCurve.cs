using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace MicMix.Controls;

/// <summary>
/// The 5-band voice EQ drawn as its real frequency response. Drag a point up/down to change that
/// band; double-click a point to reset it to 0 dB. Uses the same RBJ filter shapes as the DSP.
/// </summary>
public sealed class EqCurve : FrameworkElement
{
    private static readonly double[] Freqs = [100, 250, 1000, 4000, 10000];
    private static readonly string[] Labels = ["100 Гц", "250 Гц", "1 кГц", "4 кГц", "10 кГц"];
    private const double MinF = 20, MaxF = 20000, RangeDb = 12, SampleRate = 48000;
    private const double PadTop = 10, PadBottom = 22, HandleRadius = 7;

    public static readonly DependencyProperty Gain100Property = Reg(nameof(Gain100));
    public static readonly DependencyProperty Gain250Property = Reg(nameof(Gain250));
    public static readonly DependencyProperty Gain1kProperty = Reg(nameof(Gain1k));
    public static readonly DependencyProperty Gain4kProperty = Reg(nameof(Gain4k));
    public static readonly DependencyProperty Gain10kProperty = Reg(nameof(Gain10k));

    public float Gain100 { get => (float)GetValue(Gain100Property); set => SetValue(Gain100Property, value); }
    public float Gain250 { get => (float)GetValue(Gain250Property); set => SetValue(Gain250Property, value); }
    public float Gain1k { get => (float)GetValue(Gain1kProperty); set => SetValue(Gain1kProperty, value); }
    public float Gain4k { get => (float)GetValue(Gain4kProperty); set => SetValue(Gain4kProperty, value); }
    public float Gain10k { get => (float)GetValue(Gain10kProperty); set => SetValue(Gain10kProperty, value); }

    private static readonly DependencyProperty[] GainProps =
        [Gain100Property, Gain250Property, Gain1kProperty, Gain4kProperty, Gain10kProperty];

    private static DependencyProperty Reg(string name) => DependencyProperty.Register(name, typeof(float), typeof(EqCurve),
        new FrameworkPropertyMetadata(0f, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    private int _dragging = -1;
    private int _hover = -1;

    public EqCurve()
    {
        Cursor = Cursors.Arrow;
        Focusable = false;
        MinHeight = 120;
        ToolTip = "Тяни точку вверх или вниз. Двойной щелчок по точке — сбросить в 0.";
    }

    private float Gain(int band) => (float)GetValue(GainProps[band]);

    private double X(double f) => Math.Log(f / MinF) / Math.Log(MaxF / MinF) * ActualWidth;
    private double Y(double db) => PadTop + (RangeDb - db) / (2 * RangeDb) * (ActualHeight - PadTop - PadBottom);
    private double DbAt(double y) => RangeDb - (y - PadTop) / (ActualHeight - PadTop - PadBottom) * 2 * RangeDb;

    protected override void OnRender(DrawingContext dc)
    {
        var fg = (Brush)FindResource("TextBrush");
        var muted = (Brush)FindResource("MutedTextBrush");
        var accent = (Brush)FindResource("AccentBrush");
        var grid = new Pen((Brush)FindResource("HairlineBrush"), 1);
        var zero = new Pen((Brush)FindResource("HairlineStrongBrush"), 1) { DashStyle = new DashStyle([3, 4], 0) };
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h)); // whole area hit-testable

        foreach (double db in new[] { -12.0, -6, 6, 12 })
            dc.DrawLine(grid, new Point(0, Y(db)), new Point(w, Y(db)));
        dc.DrawLine(zero, new Point(0, Y(0)), new Point(w, Y(0)));
        foreach (double f in new[] { 50.0, 100, 200, 500, 1000, 2000, 5000, 10000 })
            dc.DrawLine(grid, new Point(X(f), PadTop), new Point(X(f), h - PadBottom));

        // Response curve.
        var coefs = Enumerable.Range(0, 5).Select(i => Biquad(i, Gain(i))).ToArray();
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            for (int px = 0; px <= (int)w; px += 2)
            {
                double f = MinF * Math.Pow(MaxF / MinF, px / w);
                double db = coefs.Sum(c => MagnitudeDb(c, f));
                var pt = new Point(px, Y(Math.Clamp(db, -RangeDb - 2, RangeDb + 2)));
                if (px == 0) ctx.BeginFigure(pt, false, false); else ctx.LineTo(pt, true, true);
            }
        }
        geo.Freeze();
        dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round }, geo);

        // Handles + labels.
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (int i = 0; i < 5; i++)
        {
            var c = new Point(X(Freqs[i]), Y(Gain(i)));
            double r = i == _dragging || i == _hover ? HandleRadius + 1.5 : HandleRadius;
            dc.DrawEllipse((Brush)FindResource("WindowBrush"), new Pen(accent, 2), c, r, r);

            float g = Gain(i);
            string text = Labels[i] + (Math.Abs(g) < 0.05 ? "" : (g > 0 ? " +" : " ") + g.ToString("0.#", CultureInfo.InvariantCulture));
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, i == _dragging ? fg : muted, dpi);
            double tx = Math.Clamp(c.X - ft.Width / 2, 0, w - ft.Width);
            dc.DrawText(ft, new Point(tx, h - PadBottom + 5));
        }
    }

    private int HitHandle(Point p)
    {
        for (int i = 0; i < 5; i++)
            if ((new Point(X(Freqs[i]), Y(Gain(i))) - p).Length <= HandleRadius + 6) return i;
        // Generous: anywhere in a band's column grabs it.
        for (int i = 0; i < 5; i++)
            if (Math.Abs(X(Freqs[i]) - p.X) <= 14) return i;
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        int i = HitHandle(e.GetPosition(this));
        if (i < 0) return;
        if (e.ClickCount == 2)
        {
            SetValue(GainProps[i], 0f);
            return;
        }
        _dragging = i;
        CaptureMouse();
        UpdateDrag(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_dragging >= 0) { UpdateDrag(p); return; }
        int hover = HitHandle(p);
        if (hover != _hover)
        {
            _hover = hover;
            Cursor = hover >= 0 ? Cursors.SizeNS : Cursors.Arrow;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = -1;
        ReleaseMouseCapture();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hover >= 0 && _dragging < 0) { _hover = -1; InvalidateVisual(); }
    }

    private void UpdateDrag(Point p)
    {
        double db = Math.Round(Math.Clamp(DbAt(p.Y), -RangeDb, RangeDb) * 2) / 2; // 0.5 dB steps
        SetValue(GainProps[_dragging], (float)db);
    }

    #region Filter math (RBJ cookbook, same shapes/params as VoiceProcessor)

    private readonly record struct Coefs(double B0, double B1, double B2, double A1, double A2);

    private static Coefs Biquad(int band, double gainDb)
    {
        double f = Freqs[band], a = Math.Pow(10, gainDb / 40), w0 = 2 * Math.PI * f / SampleRate;
        double cos = Math.Cos(w0), sin = Math.Sin(w0);
        double b0, b1, b2, a0, a1, a2;
        if (band == 0 || band == 4)
        {
            const double shelf = 0.8;
            double alpha = sin / 2 * Math.Sqrt((a + 1 / a) * (1 / shelf - 1) + 2), sq = 2 * Math.Sqrt(a) * alpha;
            if (band == 0)
            {
                b0 = a * ((a + 1) - (a - 1) * cos + sq); b1 = 2 * a * ((a - 1) - (a + 1) * cos); b2 = a * ((a + 1) - (a - 1) * cos - sq);
                a0 = (a + 1) + (a - 1) * cos + sq; a1 = -2 * ((a - 1) + (a + 1) * cos); a2 = (a + 1) + (a - 1) * cos - sq;
            }
            else
            {
                b0 = a * ((a + 1) + (a - 1) * cos + sq); b1 = -2 * a * ((a - 1) + (a + 1) * cos); b2 = a * ((a + 1) + (a - 1) * cos - sq);
                a0 = (a + 1) - (a - 1) * cos + sq; a1 = 2 * ((a - 1) - (a + 1) * cos); a2 = (a + 1) - (a - 1) * cos - sq;
            }
        }
        else
        {
            double alpha = sin / (2 * 1.0);
            b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
            a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
        }
        return new Coefs(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    private static double MagnitudeDb(Coefs c, double f)
    {
        double w = 2 * Math.PI * f / SampleRate;
        var z1 = new System.Numerics.Complex(Math.Cos(-w), Math.Sin(-w));
        var z2 = z1 * z1;
        var h = (c.B0 + c.B1 * z1 + c.B2 * z2) / (1 + c.A1 * z1 + c.A2 * z2);
        return 20 * Math.Log10(Math.Max(h.Magnitude, 1e-9));
    }

    #endregion
}
