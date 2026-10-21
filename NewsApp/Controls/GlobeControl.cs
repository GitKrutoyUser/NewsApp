using System.Text.Json;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;

namespace NewsApp.Controls;

public sealed class GlobeControl : Control
{
    private sealed class CountryShape
    {
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public List<List<Point>> Polygons { get; } = new();
        public Point LabelPoint { get; set; }
    }

    private readonly List<CountryShape> _countries = new();
    private Point _lastPointer;
    private bool _dragging;
    private bool _moved;
    private double _yaw = -0.55;
    private double _pitch = 0.12;

    public event EventHandler<string>? CountrySelected;

    public string? SelectedCountryCode { get; private set; }
    public string? SelectedCountryName { get; private set; }

    public GlobeControl()
    {
        Focusable = true;
        LoadGeoJson();
    }

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
                    _countries.Add(country);
                }
            }
        }
        catch
        {
            // The application still opens if the optional map asset is damaged.
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
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Max(10, Math.Min(Bounds.Width, Bounds.Height) / 2 - 12);
        var globeRect = new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2);
        context.DrawEllipse(new SolidColorBrush(Color.Parse("#13253C")), new Pen(new SolidColorBrush(Color.Parse("#3B79AF")), 2), center, radius, radius);

        foreach (var country in _countries)
        {
            var isSelected = country.Code.Equals(SelectedCountryCode, StringComparison.OrdinalIgnoreCase);
            var fill = isSelected ? new SolidColorBrush(Color.Parse("#F5B642")) : CountryColor(country.Code);
            var pen = new Pen(new SolidColorBrush(isSelected ? Colors.White : Color.Parse("#B9D5E8")), isSelected ? 1.6 : 0.75);
            foreach (var polygon in country.Polygons)
                DrawPolygon(context, polygon, center, radius, fill, pen);
            var label = Project(country.LabelPoint.X, country.LabelPoint.Y, center, radius);
            if (label is not null && radius > 130)
            {
                var text = new FormattedText(country.Name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 9, Brushes.White);
                context.DrawText(text, new Point(label.Value.X - text.Width / 2, label.Value.Y - text.Height / 2));
            }
        }

        context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.Parse("#6FA8D2")), 1), center, radius, radius);
    }

    private static IBrush CountryColor(string code)
    {
        var palette = new[] { "#3D7EA6", "#4F8F6A", "#A56B46", "#7A65A8", "#B05D72", "#3F8E89", "#8A7A42", "#A65B8D" };
        return new SolidColorBrush(Color.Parse(palette[Math.Abs(code.GetHashCode()) % palette.Length]));
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
                var code = HitTestCountry(e.GetPosition(this));
                if (code is not null)
                {
                    SelectedCountryCode = code;
                    SelectedCountryName = _countries.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Name ?? code;
                    CountrySelected?.Invoke(this, code);
                    InvalidateVisual();
                }
            }
            e.Handled = true;
        }
        base.OnPointerReleased(e);
    }

    private string? HitTestCountry(Point screenPoint)
    {
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var radius = Math.Max(10, Math.Min(Bounds.Width, Bounds.Height) / 2 - 12);
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
        return null;
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
