using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace NewsApp.Controls;

public sealed class StarfieldControl : Control
{
    private readonly Star[] _stars;
    private readonly DispatcherTimer _timer;
    private double _time;

    public bool DrawBackground { get; set; } = true;

    private sealed record Star(double X, double Y, double Speed, double Size, double Phase, byte Alpha, bool Blue);

    public StarfieldControl()
    {
        IsHitTestVisible = false;
        _stars = CreateStars();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _timer.Tick += (_, _) =>
        {
            _time += 0.0018;
            InvalidateVisual();
        };
        _timer.Start();
    }

    public override void Render(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        if (DrawBackground)
        {
            context.FillRectangle(new SolidColorBrush(Color.Parse("#040811")), new Rect(0, 0, width, height));
            DrawNebula(context, width, height);
        }

        foreach (var star in _stars)
        {
            var x = (star.X + Math.Sin(_time * 1.3 + star.Phase) * 0.004) * width;
            var normalizedY = (star.Y + _time * star.Speed) % 1.0;
            var y = normalizedY * height;
            var pulse = 0.72 + Math.Sin(_time * 8 + star.Phase) * 0.28;
            var alpha = (byte)Math.Clamp(star.Alpha * pulse, 20, 255);
            var color = star.Blue
                ? Color.FromArgb(alpha, 170, 210, 255)
                : Color.FromArgb(alpha, 245, 247, 255);
            var brush = new SolidColorBrush(color);
            context.DrawEllipse(brush, null, new Point(x, y), star.Size, star.Size);
            if (star.Size > 1.35)
            {
                var tail = Math.Min(14, star.Speed * height * 0.035);
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb((byte)(alpha / 3), color.R, color.G, color.B)), 0.8),
                    new Point(x, y - tail), new Point(x, y));
            }
        }
    }

    private static void DrawNebula(DrawingContext context, double width, double height)
    {
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(16, 34, 90, 150)), null,
            new Point(width * 0.14, height * 0.24), width * 0.28, height * 0.20);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(13, 94, 48, 130)), null,
            new Point(width * 0.82, height * 0.68), width * 0.32, height * 0.25);
        context.DrawEllipse(new SolidColorBrush(Color.FromArgb(10, 25, 116, 150)), null,
            new Point(width * 0.54, height * 0.08), width * 0.26, height * 0.13);
    }

    private static Star[] CreateStars()
    {
        var random = new Random(30102026);
        return Enumerable.Range(0, 330)
            .Select(_ => new Star(
                random.NextDouble(),
                random.NextDouble(),
                0.015 + random.NextDouble() * 0.055,
                0.35 + Math.Pow(random.NextDouble(), 2) * 1.9,
                random.NextDouble() * Math.PI * 2,
                (byte)(55 + random.Next(0, 190)),
                random.Next(0, 4) == 0))
            .ToArray();
    }
}
