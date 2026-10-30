using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace NewsApp.Controls;

public sealed class SolarSystemControl : Control
{
    private sealed record Body(string Code, string Name, double Distance, double Radius, Color Color, bool GasGiant = false, bool Rings = false);

    private static readonly Body[] Bodies =
    {
        new("SUN", "Солнце", 0, 23, Color.Parse("#F6B73C")),
        new("MERCURY", "Меркурий", 0.39, 3.2, Color.Parse("#9B9288")),
        new("VENUS", "Венера", 0.72, 5.3, Color.Parse("#D5A35D")),
        new("EARTH", "Земля", 1.00, 5.8, Color.Parse("#3A8FC7")),
        new("MOON", "Луна", 1.08, 1.8, Color.Parse("#BFC4C6")),
        new("MARS", "Марс", 1.52, 4.2, Color.Parse("#C96545")),
        new("JUPITER", "Юпитер", 5.20, 13.0, Color.Parse("#C9A47D"), true),
        new("SATURN", "Сатурн", 9.58, 11.5, Color.Parse("#D7B878"), true, true),
        new("URANUS", "Уран", 19.2, 8.0, Color.Parse("#79C4CE"), true),
        new("NEPTUNE", "Нептун", 30.1, 7.7, Color.Parse("#4778C9"), true)
    };

    private readonly (double X, double Y, double Size, byte Alpha)[] _stars;
    private readonly DispatcherTimer _timer;
    private Point _lastPointer;
    private bool _dragging;
    private bool _moved;
    private double _rotation;
    private double _zoom = 1;

    public event EventHandler<(string Code, string Name)>? BodySelected;
    public bool IsAutoRotating { get; set; }

    public SolarSystemControl()
    {
        Focusable = true;
        _stars = CreateStars();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) =>
        {
            if (IsAutoRotating) _rotation += 0.0015;
            InvalidateVisual();
        };
        _timer.Start();
    }

    public override void Render(DrawingContext context)
    {
        DrawStars(context);
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var maxOrbit = Math.Min(Bounds.Width, Bounds.Height) * 0.44 * _zoom;
        var sunRadius = Math.Max(20, maxOrbit * 0.10);
        foreach (var body in Bodies.Where(x => x.Distance > 0))
        {
            var orbit = maxOrbit * (0.12 + 0.88 * Math.Log10(body.Distance + 1) / Math.Log10(31.1));
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(68, 125, 160, 190)), 0.65), center, orbit, orbit * 0.52);
        }

        DrawBody(context, Bodies[0], center, sunRadius);
        foreach (var body in Bodies.Skip(1))
        {
            var orbit = maxOrbit * (0.12 + 0.88 * Math.Log10(body.Distance + 1) / Math.Log10(31.1));
            var angle = _rotation + body.Distance * 0.31;
            var point = new Point(center.X + Math.Cos(angle) * orbit, center.Y + Math.Sin(angle) * orbit * 0.52);
            var radius = Math.Max(3, body.Radius * Math.Pow(_zoom, 0.32));
            DrawBody(context, body, point, radius);
        }
    }

    private void DrawBody(DrawingContext context, Body body, Point center, double radius)
    {
        if (body.Rings)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(175, 218, 193, 128)), Math.Max(2, radius * 0.35)), center, radius * 1.8, radius * 0.55);
        context.DrawEllipse(new SolidColorBrush(body.Color), new Pen(new SolidColorBrush(Color.FromArgb(210, 240, 245, 250)), Math.Max(0.7, radius * 0.08)), center, radius, radius);
        if (body.Code == "EARTH")
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(145, 87, 166, 91)), null, new Point(center.X - radius * 0.18, center.Y - radius * 0.1), radius * 0.42, radius * 0.22);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(125, 94, 181, 102)), null, new Point(center.X + radius * 0.28, center.Y + radius * 0.18), radius * 0.22, radius * 0.15);
        }
        else if (body.GasGiant)
        {
            for (var i = -2; i <= 2; i++)
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(55, 255, 245, 220)), Math.Max(0.6, radius * 0.08)),
                    new Point(center.X - radius * 0.86, center.Y + i * radius * 0.28),
                    new Point(center.X + radius * 0.86, center.Y + i * radius * 0.28));
        }
        else
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(45, 35, 35, 35)), null, new Point(center.X + radius * 0.25, center.Y + radius * 0.12), radius * 0.20, radius * 0.13);
        }
        var label = new FormattedText(body.Name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, Brushes.White);
        context.DrawText(label, new Point(center.X - label.Width / 2, center.Y + radius + 6));
    }

    private void DrawStars(DrawingContext context)
    {
        foreach (var star in _stars)
        {
            var point = new Point(star.X * Bounds.Width, star.Y * Bounds.Height);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(star.Alpha, 211, 231, 248)), null, point, star.Size, star.Size);
        }
    }

    private static (double X, double Y, double Size, byte Alpha)[] CreateStars()
    {
        var random = new Random(30101986);
        return Enumerable.Range(0, 500).Select(_ =>
            (random.NextDouble(), random.NextDouble(), 0.45 + random.NextDouble() * 1.8, (byte)(65 + random.Next(0, 175)))).ToArray();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _moved = false;
            _lastPointer = e.GetPosition(this);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
        base.OnPointerPressed(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_dragging)
        {
            var current = e.GetPosition(this);
            var delta = current - _lastPointer;
            if (Math.Abs(delta.X) + Math.Abs(delta.Y) > 2) _moved = true;
            _rotation += delta.X * 0.003;
            _lastPointer = current;
            InvalidateVisual();
            e.Handled = true;
        }
        base.OnPointerMoved(e);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_dragging)
        {
            e.Pointer.Capture(null);
            _dragging = false;
            if (!_moved && e.InitialPressMouseButton == MouseButton.Left)
            {
                var selected = HitTestBody(e.GetPosition(this));
                if (selected is not null) BodySelected?.Invoke(this, (selected.Code, selected.Name));
            }
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom + e.Delta.Y * 0.12, 0.55, 2.6);
        InvalidateVisual();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    private Body? HitTestBody(Point point)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var maxOrbit = Math.Min(Bounds.Width, Bounds.Height) * 0.44 * _zoom;
        var sunDelta = point - center;
        if (Math.Sqrt(sunDelta.X * sunDelta.X + sunDelta.Y * sunDelta.Y) <= Math.Max(20, maxOrbit * 0.10)) return Bodies[0];
        foreach (var body in Bodies.Skip(1).Reverse())
        {
            var orbit = maxOrbit * (0.12 + 0.88 * Math.Log10(body.Distance + 1) / Math.Log10(31.1));
            var angle = _rotation + body.Distance * 0.31;
            var bodyPoint = new Point(center.X + Math.Cos(angle) * orbit, center.Y + Math.Sin(angle) * orbit * 0.52);
            var radius = Math.Max(3, body.Radius * Math.Pow(_zoom, 0.32));
            var delta = point - bodyPoint;
            if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) <= radius + 8) return body;
        }
        return null;
    }
}
