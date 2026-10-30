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

    private sealed record PlanetVisual(string Code, string Name, double Distance, double Radius, Color Color, bool GasGiant = false, bool Rings = false);

    private static readonly PlanetVisual[] Planets =
    {
        new("SUN", "Солнце", 0, 17, Color.Parse("#F4B53F")),
        new("MERCURY", "Меркурий", 0.39, 3, Color.Parse("#9C9388")),
        new("VENUS", "Венера", 0.72, 4.5, Color.Parse("#D5A35D")),
        new("EARTH", "Земля", 1, 5, Color.Parse("#3B91C8")),
        new("MARS", "Марс", 1.52, 3.8, Color.Parse("#C96545")),
        new("JUPITER", "Юпитер", 5.2, 11, Color.Parse("#C9A47D"), true),
        new("SATURN", "Сатурн", 9.58, 9.5, Color.Parse("#D7B878"), true, true),
        new("URANUS", "Уран", 19.2, 6.7, Color.Parse("#79C4CE"), true),
        new("NEPTUNE", "Нептун", 30.1, 6.5, Color.Parse("#4778C9"), true)
    };

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
        DrawStars(context);
        if (_zoom <= 0.78)
        {
            RenderSolarSystem(context);
            return;
        }
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

    private void RenderSolarSystem(DrawingContext context)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var systemRadius = Math.Min(Bounds.Width, Bounds.Height) * 0.45;
        var systemZoom = Math.Clamp(1 + (0.78 - _zoom) * 1.5, 1, 1.12);
        var scale = systemRadius * systemZoom / 30.1;
        DrawPlanet(context, Planets[0], center, Planets[0].Radius * systemZoom);
        foreach (var planet in Planets.Skip(1))
        {
            var orbit = planet.Distance * scale;
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(78, 125, 160, 190)), 0.7), center, orbit, orbit * 0.52);
            var angle = _yaw + planet.Distance * 0.31;
            var point = new Point(center.X + Math.Cos(angle) * orbit, center.Y + Math.Sin(angle) * orbit * 0.52);
            DrawPlanet(context, planet, point, Math.Max(3, planet.Radius * Math.Pow(systemZoom, 0.3)));
            if (planet.Code == "EARTH")
            {
                var moonPoint = new Point(point.X + Math.Cos(_moonAngle) * 14, point.Y + Math.Sin(_moonAngle) * 7);
                DrawPlanet(context, new PlanetVisual("MOON", "Луна", 0, 2.2, Color.Parse("#BFC4C6")), moonPoint, 3.1);
            }
        }
    }

    private void DrawPlanet(DrawingContext context, PlanetVisual planet, Point center, double radius)
    {
        if (planet.Rings)
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(190, 218, 193, 128)), Math.Max(1.5, radius * 0.3)), center, radius * 1.8, radius * 0.55);
        context.DrawEllipse(new SolidColorBrush(planet.Color), new Pen(new SolidColorBrush(Color.FromArgb(215, 238, 245, 250)), Math.Max(0.7, radius * 0.08)), center, radius, radius);
        if (planet.Code == "EARTH")
        {
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(170, 87, 166, 91)), null, new Point(center.X - radius * 0.2, center.Y - radius * 0.1), radius * 0.42, radius * 0.22);
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(145, 94, 181, 102)), null, new Point(center.X + radius * 0.25, center.Y + radius * 0.2), radius * 0.22, radius * 0.15);
        }
        else if (planet.GasGiant)
        {
            for (var i = -2; i <= 2; i++)
                context.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(58, 255, 245, 220)), Math.Max(0.5, radius * 0.08)), new Point(center.X - radius * 0.85, center.Y + i * radius * 0.27), new Point(center.X + radius * 0.85, center.Y + i * radius * 0.27));
        }
        else if (planet.Code != "SUN")
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb(55, 35, 35, 35)), null, new Point(center.X + radius * 0.22, center.Y + radius * 0.12), radius * 0.2, radius * 0.13);
        var text = new FormattedText(planet.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 10, Brushes.White);
        context.DrawText(text, new Point(center.X - text.Width / 2, center.Y + radius + 5));
    }

    private string? HitTestSolarSystem(Point point)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var systemRadius = Math.Min(Bounds.Width, Bounds.Height) * 0.45;
        var scale = systemRadius * Math.Clamp(1 + (0.78 - _zoom) * 1.5, 1, 1.12) / 30.1;
        var sunDelta = point - center;
        if (Math.Sqrt(sunDelta.X * sunDelta.X + sunDelta.Y * sunDelta.Y) < 25) return "SUN";
        foreach (var planet in Planets.Skip(1).Reverse())
        {
            var orbit = planet.Distance * scale;
            var angle = _yaw + planet.Distance * 0.31;
            var bodyPoint = new Point(center.X + Math.Cos(angle) * orbit, center.Y + Math.Sin(angle) * orbit * 0.52);
            var radius = Math.Max(3, planet.Radius);
            var delta = point - bodyPoint;
            if (Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y) <= radius + 8) return planet.Code;
        }
        return null;
    }

    private static (double X, double Y, double Size, byte Alpha)[] CreateStars()
    {
        var random = new Random(27101986);
        return Enumerable.Range(0, 150)
            .Select(_ => (random.NextDouble(), random.NextDouble(),
                0.55 + random.NextDouble() * 1.7,
                (byte)(75 + random.Next(0, 150))))
            .ToArray();
    }

    private void DrawStars(DrawingContext context)
    {
        foreach (var star in _stars)
        {
            var point = new Point(star.X * Bounds.Width, star.Y * Bounds.Height);
            var color = Color.FromArgb(star.Alpha, 210, 229, 244);
            context.DrawEllipse(new SolidColorBrush(color), null, point, star.Size, star.Size);
        }
    }

    private void GetSceneGeometry(out Point earthCenter, out double earthRadius, out Point moonCenter, out double moonRadius, out double moonOrbit)
    {
        var canvasCenter = new Point(Bounds.Width / 2, Bounds.Height / 2);
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
        var projected = polygon.Select(point => Project(point.X, point.Y, center, radius)).ToList();
        var visible = projected.Where(x => x is not null).Select(x => x!.Value).ToList();
        if (visible.Count < 3) return;

        var geometry = new StreamGeometry();
        using (var builder = geometry.Open())
        {
            builder.BeginFigure(visible[0], true);
            foreach (var point in visible.Skip(1)) builder.LineTo(point);
            builder.EndFigure(true);
        }
        context.DrawGeometry(fill, pen, geometry);
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
                SelectedCountryName = PlanetName(code) ?? code switch
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
        if (_zoom <= 0.78)
            return HitTestSolarSystem(screenPoint);
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

    private static string? PlanetName(string code) =>
        Planets.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name;

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
