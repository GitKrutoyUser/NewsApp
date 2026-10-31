using System.Text.Json;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace NewsApp.Controls;

public sealed class GlobeControl : Control
{
    private sealed class CountryShape
    {
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public List<List<Point>> Polygons { get; } = new();
        public Point LabelPoint { get; set; }
        public double SizeScore { get; set; }
    }

    private readonly List<CountryShape> _countries = new();
    private readonly Bitmap? _moonTexture;
    private readonly (double X, double Y, double Size, byte Alpha)[] _stars;
    private Point _lastPointer;
    private bool _dragging;
    private bool _moved;
    private double _yaw = -0.55;
    private double _pitch = 0.12;
    private double _zoom = 1.0;
    private double _moonAngle = -0.45;
    private double _focusLevel;
    private double _focusTarget;
    private readonly HashSet<string> _newCountryCodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _rotationTimer;

    public event EventHandler<string>? CountrySelected;

    public string? SelectedCountryCode { get; private set; }
    public string? SelectedCountryName { get; private set; }
    public bool IsAutoRotating { get; private set; }
    public double ViewCenterX { get; set; } = double.NaN;

    public void MarkNewCountries(IEnumerable<string> codes)
    {
        _newCountryCodes.Clear();
        foreach (var code in codes) _newCountryCodes.Add(code);
        InvalidateVisual();
    }

    public GlobeControl()
    {
        Focusable = true;
        _stars = CreateStars();
        LoadGeoJson();
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://NewsApp/Assets/moon-texture.jpg"));
            _moonTexture = new Bitmap(stream);
        }
        catch { _moonTexture = null; }
        _rotationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _rotationTimer.Tick += (_, _) =>
        {
            _moonAngle += 0.0025;
            if (IsAutoRotating) _yaw += 0.0045;
            _focusLevel += (_focusTarget - _focusLevel) * 0.14;
            if (Math.Abs(_focusTarget - _focusLevel) < 0.001) _focusLevel = _focusTarget;
            InvalidateVisual();
        };
        _rotationTimer.Start();
    }

    public void ToggleAutoRotation()
    {
        IsAutoRotating = !IsAutoRotating;
    }

    public void StopAutoRotation()
    {
        IsAutoRotating = false;
        _rotationTimer.Stop();
    }

    private void SetFocus(bool moon)
    {
        _focusTarget = moon ? 1 : 0;
    }

    public void FocusEarth() => SetFocus(false);

    public void FocusMoon() => SetFocus(true);

    private void LoadGeoJson()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://NewsApp/Assets/countries.geojson"));
            using var document = JsonDocument.Parse(stream);
            foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
            {
                var properties = feature.GetProperty("properties");
                var code = properties.TryGetProperty("ISO_A2", out var iso) ? iso.GetString() : null;
                var name = properties.TryGetProperty("ADMIN", out var admin) ? admin.GetString() : null;
                if (string.IsNullOrWhiteSpace(code) || code == "-99") continue;

                var country = new CountryShape { Code = code, Name = RussianName(code, name ?? code) };
                var geometry = feature.GetProperty("geometry");
                var type = geometry.GetProperty("type").GetString();
                var coordinates = geometry.GetProperty("coordinates");
                if (type == "Polygon") AddPolygonSet(country, coordinates);
                else if (type == "MultiPolygon")
                    foreach (var polygon in coordinates.EnumerateArray()) AddPolygonSet(country, polygon);
                if (country.Polygons.Count > 0)
                {
                    var points = country.Polygons.SelectMany(x => x).ToList();
                    country.LabelPoint = new Point(points.Average(x => x.X), points.Average(x => x.Y));
                    country.SizeScore = country.Polygons.Max(PolygonAreaScore);
                    _countries.Add(country);
                }
            }
        }
        catch
        {

        }
    }

    private static void AddPolygonSet(CountryShape country, JsonElement rings)
    {
        if (rings.ValueKind != JsonValueKind.Array) return;
        var outerRing = rings.EnumerateArray().FirstOrDefault();
        if (outerRing.ValueKind != JsonValueKind.Array) return;
        var points = new List<Point>();
        foreach (var coordinate in outerRing.EnumerateArray())
        {
            if (coordinate.GetArrayLength() < 2) continue;
            points.Add(new Point(coordinate[0].GetDouble(), coordinate[1].GetDouble()));
        }
        if (points.Count >= 3) country.Polygons.Add(points);
    }

    public override void Render(DrawingContext context)
    {
        GetSceneGeometry(out var center, out var radius, out var moonCenter, out var moonRadius, out var moonOrbit);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#142B3A")), new Pen(new SolidColorBrush(Color.Parse("#78A7A9")), 2), center, radius, radius);

        var occupiedLabels = new List<Rect>();
        foreach (var country in _countries.OrderByDescending(x => x.SizeScore))
        {
            var isSelected = country.Code.Equals(SelectedCountryCode, StringComparison.OrdinalIgnoreCase);
            var fill = isSelected ? new SolidColorBrush(Color.Parse("#F5B642")) : CountryColor(country);
            var pen = new Pen(new SolidColorBrush(isSelected ? Colors.White : Color.Parse("#B8C9AE")), isSelected ? 1.8 : 0.6);
            foreach (var polygon in country.Polygons)
                DrawPolygon(context, polygon, center, radius, fill, pen);
            var label = Project(country.LabelPoint.X, country.LabelPoint.Y, center, radius);
            if (label is not null && radius > 130 && ShouldShowLabel(country))
            {
                var text = new FormattedText(country.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 9, Brushes.White);
                var textRect = new Rect(label.Value.X - text.Width / 2 - 3, label.Value.Y - text.Height / 2 - 2, text.Width + 6, text.Height + 4);
                if (!occupiedLabels.Any(existing => existing.Intersects(textRect)))
                {
                    occupiedLabels.Add(textRect);
                    context.DrawText(text, new Point(label.Value.X - text.Width / 2, label.Value.Y - text.Height / 2));
                    if (_newCountryCodes.Contains(country.Code))
                        context.DrawEllipse(Brushes.OrangeRed, null, new Point(textRect.Right + 4, label.Value.Y), 4, 4);
                }
            }
        }

        DrawGlobeGrid(context, center, radius);
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#A7C4B2")), 1.1), center, radius, radius);
        DrawMoon(context, center, radius, moonCenter, moonRadius, moonOrbit);
    }

    private static (double X, double Y, double Size, byte Alpha)[] CreateStars()
    {
        var random = new Random(27101986);
        return Enumerable.Range(0, 360)
            .Select(_ => (random.NextDouble(), random.NextDouble(),
                0.4 + random.NextDouble() * 1.45,
                (byte)(65 + random.Next(0, 170))))
            .ToArray();
    }

    private void DrawStars(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#050A14")), new Rect(0, 0, Bounds.Width, Bounds.Height));
        foreach (var star in _stars)
        {
            var point = new Point(star.X * Bounds.Width, star.Y * Bounds.Height);
            var color = Color.FromArgb(star.Alpha, 210, 229, 244);
            context.DrawEllipse(new SolidColorBrush(color), null, point, star.Size, star.Size);
        }
    }

    private void GetSceneGeometry(out Point earthCenter, out double earthRadius, out Point moonCenter, out double moonRadius, out double moonOrbit)
    {
        var canvasCenter = new Point(double.IsNaN(ViewCenterX) ? Bounds.Width / 2 : ViewCenterX, Bounds.Height / 2);
        var baseRadius = Math.Max(10, (Math.Min(Bounds.Width, Bounds.Height) / 2 - 12) * 0.62 * _zoom);
        moonOrbit = baseRadius * 1.38;
        var normalMoonCenter = new Point(
            canvasCenter.X + Math.Cos(_moonAngle) * moonOrbit,
            canvasCenter.Y + Math.Sin(_moonAngle) * moonOrbit * 0.62);
        var direction = normalMoonCenter - canvasCenter;
        var length = Math.Max(1, Math.Sqrt(direction.X * direction.X + direction.Y * direction.Y));
        var earthFocusCenter = new Point(
            canvasCenter.X - direction.X / length * baseRadius * 1.05,
            canvasCenter.Y - direction.Y / length * baseRadius * 1.05);
        earthCenter = Lerp(canvasCenter, earthFocusCenter, _focusLevel);
        moonCenter = Lerp(normalMoonCenter, canvasCenter, _focusLevel);
        earthRadius = baseRadius * (1 - _focusLevel * 0.30);
        moonRadius = Math.Min(earthRadius * 0.72, Math.Max(18, baseRadius * (0.23 + _focusLevel * 0.22)));
    }

    private static Point Lerp(Point first, Point second, double amount) =>
        new(first.X + (second.X - first.X) * amount, first.Y + (second.Y - first.Y) * amount);

    private void DrawMoon(DrawingContext context, Point center, double earthRadius, Point moonCenter, double moonRadius, double orbitRadius)
    {
        var moonRect = new Rect(moonCenter.X - moonRadius, moonCenter.Y - moonRadius, moonRadius * 2, moonRadius * 2);
        var moonPen = new Pen(new SolidColorBrush(Color.Parse("#E4E8E8")), Math.Max(1, earthRadius * 0.009));
        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(70, 170, 190, 205)), 0.7),
            center, orbitRadius, orbitRadius * 0.62);
        if (_moonTexture is not null)
        {
            using (context.PushClip(new RoundedRect(moonRect, moonRadius)))
            {
                DrawWrappedMoonTexture(context, moonRect);
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(34, 255, 255, 255)), null,
                    new Point(moonCenter.X - moonRadius * 0.27, moonCenter.Y - moonRadius * 0.28), moonRadius * 0.72, moonRadius * 0.72);
                context.DrawEllipse(new SolidColorBrush(Color.FromArgb(58, 0, 0, 0)), null,
                    new Point(moonCenter.X + moonRadius * 0.30, moonCenter.Y + moonRadius * 0.04), moonRadius * 0.90, moonRadius * 0.98);
            }
        }
        else
        {
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#B8BEC2")), null, moonCenter, moonRadius, moonRadius);
        }
        context.DrawEllipse(null, moonPen, moonCenter, moonRadius, moonRadius);
    }

    private void DrawWrappedMoonTexture(DrawingContext context, Rect destination)
    {
        if (_moonTexture is null) return;
        var textureWidth = _moonTexture.PixelSize.Width;
        var textureHeight = _moonTexture.PixelSize.Height;
        var sourceWidth = Math.Min(textureWidth, textureHeight);
        var offset = (_moonAngle / (Math.PI * 2) * textureWidth) % textureWidth;
        if (offset < 0) offset += textureWidth;
        var firstWidth = Math.Min(sourceWidth, textureWidth - offset);
        var firstDestinationWidth = destination.Width * firstWidth / sourceWidth;
        context.DrawImage(_moonTexture,
            new Rect(offset, 0, firstWidth, textureHeight),
            new Rect(destination.X, destination.Y, firstDestinationWidth, destination.Height));
        var remaining = sourceWidth - firstWidth;
        if (remaining > 0)
        {
            context.DrawImage(_moonTexture,
                new Rect(0, 0, remaining, textureHeight),
                new Rect(destination.X + firstDestinationWidth, destination.Y,
                    destination.Width - firstDestinationWidth, destination.Height));
        }
    }

    private static IBrush CountryColor(CountryShape country)
    {
        var code = country.Code.ToUpperInvariant();
        if (code is "AQ" or "ATA" or "GL" or "GRL")
            return new SolidColorBrush(Color.Parse("#D9E4E2"));

        var latitude = country.LabelPoint.Y;
        if (latitude > 58 || latitude < -48)
            return new SolidColorBrush(Color.Parse("#55735F"));
        if (code is "SA" or "AE" or "EG" or "LY" or "DZ" or "MA" or "NA" or "BW" or "MN" or "AU")
            return new SolidColorBrush(Color.Parse("#A5966C"));
        if (latitude is > 20 and < 42)
            return new SolidColorBrush(Color.Parse("#789064"));
        return new SolidColorBrush(Color.Parse("#638160"));
    }

    private void DrawGlobeGrid(DrawingContext context, Point center, double radius)
    {
        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#6F9691")), 0.32);
        for (var longitude = -180; longitude <= 180; longitude += 30)
            DrawGeoLine(context, center, radius, gridPen, longitude, true);
        for (var latitude = -60; latitude <= 60; latitude += 30)
            DrawGeoLine(context, center, radius, gridPen, latitude, false);
    }

    private void DrawGeoLine(DrawingContext context, Point center, double radius, Pen pen, double coordinate, bool longitudeLine)
    {
        var visiblePoints = new List<Point>();
        for (var step = -180; step <= 180; step += 3)
        {
            var longitude = longitudeLine ? coordinate : step;
            var latitude = longitudeLine ? step / 2 : coordinate;
            var point = Project(longitude, latitude, center, radius);
            if (point is null)
            {
                DrawLineSegment(context, visiblePoints, pen);
                visiblePoints.Clear();
            }
            else
            {
                visiblePoints.Add(point.Value);
            }
        }
        DrawLineSegment(context, visiblePoints, pen);
    }

    private static void DrawLineSegment(DrawingContext context, List<Point> points, Pen pen)
    {
        if (points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var builder = geometry.Open())
        {
            builder.BeginFigure(points[0], false);
            foreach (var point in points.Skip(1)) builder.LineTo(point);
            builder.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private bool ShouldShowLabel(CountryShape country)
    {
        var minimumSize = _zoom < 0.85 ? 1100 : _zoom < 1.25 ? 250 : 18;
        return country.SizeScore >= minimumSize;
    }

    private static double PolygonAreaScore(List<Point> polygon)
    {
        var area = 0d;
        for (var i = 0; i < polygon.Count; i++)
        {
            var next = (i + 1) % polygon.Count;
            area += polygon[i].X * polygon[next].Y - polygon[next].X * polygon[i].Y;
        }
        return Math.Abs(area) / 2;
    }

    private static string RussianName(string code, string fallback) => code.ToUpperInvariant() switch
    {
        "US" => "США", "RU" => "Россия", "DE" => "Германия", "FR" => "Франция", "GB" => "Великобритания",
        "CN" => "Китай", "JP" => "Япония", "IN" => "Индия", "BR" => "Бразилия", "CA" => "Канада",
        "AU" => "Австралия", "IT" => "Италия", "ES" => "Испания", "TR" => "Турция", "KR" => "Корея",
        "MX" => "Мексика", "PL" => "Польша", "NL" => "Нидерланды", "SE" => "Швеция", "CH" => "Швейцария",
        "IL" => "Израиль", "UA" => "Украина", "ZA" => "ЮАР", _ => fallback
    };

    private void DrawPolygon(DrawingContext context, List<Point> polygon, Point center, double radius, IBrush fill, Pen pen)
    {
        var projected = new List<Point?>();
        var crossesDateLine = false;
        for (var index = 0; index < polygon.Count; index++)
        {
            var current = polygon[index];
            var previous = polygon[(index + polygon.Count - 1) % polygon.Count];
            if (Math.Abs(current.X - previous.X) > 180) crossesDateLine = true;
            projected.Add(Project(current.X, current.Y, center, radius));
        }

        // Заполняем только цельный контур, полностью находящийся на передней
        // полусфере. Это не позволяет заливке перескочить через горизонт.
        if (!crossesDateLine && projected.All(point => point is not null))
        {
            DrawClosedPolygon(context, projected.Select(point => point!.Value).ToList(), fill, pen);
            return;
        }

        // На горизонте оставляем только реальные видимые линии. Заполнять
        // неполный контур нельзя: Avalonia замкнула бы его ложной диагональю.
        var visiblePart = new List<Point>();
        for (var index = 0; index < projected.Count; index++)
        {
            var previous = polygon[(index + projected.Count - 1) % projected.Count];
            if (Math.Abs(polygon[index].X - previous.X) > 180 || projected[index] is null)
            {
                DrawVisibleLinePart(context, visiblePart, pen);
                continue;
            }

            var point = projected[index]!.Value;
            if (visiblePart.Count > 0 && Distance(visiblePart[^1], point) > radius * 0.24)
                DrawVisibleLinePart(context, visiblePart, pen);
            visiblePart.Add(point);
        }
        DrawVisibleLinePart(context, visiblePart, pen);
    }

    private static void DrawClosedPolygon(DrawingContext context, List<Point> points, IBrush fill, Pen pen)
    {
        var geometry = new StreamGeometry();
        using (var builder = geometry.Open())
        {
            builder.BeginFigure(points[0], true);
            foreach (var point in points.Skip(1)) builder.LineTo(point);
            builder.EndFigure(true);
        }
        context.DrawGeometry(fill, pen, geometry);
    }

    private static void DrawVisibleLinePart(DrawingContext context, List<Point> points, Pen pen)
    {
        if (points.Count >= 2)
        {
            var geometry = new StreamGeometry();
            using (var builder = geometry.Open())
            {
                builder.BeginFigure(points[0], false);
                foreach (var point in points.Skip(1)) builder.LineTo(point);
                builder.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
        points.Clear();
    }

    private static double Distance(Point first, Point second)
    {
        var dx = first.X - second.X;
        var dy = first.Y - second.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private Point? Project(double longitude, double latitude, Point center, double radius)
    {
        var lon = longitude * Math.PI / 180;
        var lat = latitude * Math.PI / 180;
        var x = Math.Cos(lat) * Math.Cos(lon);
        var y = Math.Sin(lat);
        var z = Math.Cos(lat) * Math.Sin(lon);

        var x1 = x * Math.Cos(_yaw) - z * Math.Sin(_yaw);
        var z1 = x * Math.Sin(_yaw) + z * Math.Cos(_yaw);
        var y2 = y * Math.Cos(_pitch) - z1 * Math.Sin(_pitch);
        var z2 = y * Math.Sin(_pitch) + z1 * Math.Cos(_pitch);
        if (z2 < -0.02) return null;
        return new Point(center.X + x1 * radius, center.Y - y2 * radius);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.Handled && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragging = true;
            _moved = false;
            _lastPointer = e.GetPosition(this);
            e.Pointer.Capture(this);
            Focus();
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
            _yaw += delta.X * 0.009;
            _pitch = Math.Clamp(_pitch - delta.Y * 0.009, -1.35, 1.35);
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
                var code = HitTestCountry(e.GetPosition(this)) ?? "WORLD";
                SelectedCountryCode = code;
                SetFocus(code == "MOON");
                SelectedCountryName = code switch
                {
                    "WORLD" => "Мир",
                    "MOON" => "Луна",
                    _ => _countries.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name ?? code
                };
                CountrySelected?.Invoke(this, code);
                InvalidateVisual();
            }
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        _zoom = Math.Clamp(_zoom + e.Delta.Y * 0.12, 0.65, 2.4);
        InvalidateVisual();
        e.Handled = true;
        base.OnPointerWheelChanged(e);
    }

    private string? HitTestCountry(Point screenPoint)
    {
        GetSceneGeometry(out var center, out var radius, out var moonCenter, out var moonRadius, out _);
        var moonDistance = screenPoint - moonCenter;
        if (Math.Sqrt(moonDistance.X * moonDistance.X + moonDistance.Y * moonDistance.Y) <= moonRadius)
            return "MOON";
        var nx = (screenPoint.X - center.X) / radius;
        var ny = -(screenPoint.Y - center.Y) / radius;
        var squared = nx * nx + ny * ny;
        if (squared > 1) return null;
        var nz = Math.Sqrt(1 - squared);

        var y1 = ny * Math.Cos(_pitch) + nz * Math.Sin(_pitch);
        var z1 = -ny * Math.Sin(_pitch) + nz * Math.Cos(_pitch);
        var x = nx * Math.Cos(_yaw) + z1 * Math.Sin(_yaw);
        var z = -nx * Math.Sin(_yaw) + z1 * Math.Cos(_yaw);
        var longitude = Math.Atan2(z, x) * 180 / Math.PI;
        var latitude = Math.Asin(Math.Clamp(y1, -1, 1)) * 180 / Math.PI;

        foreach (var country in _countries)
            if (country.Polygons.Any(polygon => PointInPolygon(longitude, latitude, polygon))) return country.Code;
        return "WORLD";
    }

    private static bool PointInPolygon(double x, double y, List<Point> polygon)
    {
        var inside = false;
        for (var i = 0; i < polygon.Count; i++)
        {
            var j = (i + polygon.Count - 1) % polygon.Count;
            var intersects = ((polygon[i].Y > y) != (polygon[j].Y > y)) &&
                x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y + 0.0000001) + polygon[i].X;
            if (intersects) inside = !inside;
        }
        return inside;
    }
}
