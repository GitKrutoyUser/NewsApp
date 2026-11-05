using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Text.Json.Serialization;

namespace NewsApp.Models;

public partial class NewsArticle : ObservableObject
{
    public string Url { get; set; } = string.Empty;
    public string CountryCode { get; set; } = "WORLD";
    public string CountryName { get; set; } = "Мир";
    public string Title { get; set; } = "Без заголовка";
    public string OriginalTitle { get; set; } = string.Empty;
    [ObservableProperty]
    private string _description = string.Empty;
    public string OriginalDescription { get; set; } = string.Empty;
    public string Language { get; set; } = "ru";
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ImageUrl { get; set; }
    public string PublishedAtText => PublishedAt.ToLocalTime().ToString("dd.MM.yyyy");

    [ObservableProperty]
    private Bitmap? _imageBitmap;
    public bool HasImage => ImageBitmap is not null;
    partial void OnImageBitmapChanged(Bitmap? value) => OnPropertyChanged(nameof(HasImage));
}

public partial class Country : ObservableObject
{
    public Country(string code, string name, string flag) { Code = code; Name = name; Flag = flag; }
    public string Code { get; }
    public string Name { get; }
    public string Flag { get; }
    [ObservableProperty] private bool _hasNewNews;
    public string DisplayName => Name;
    public string NewNewsMarker => HasNewNews ? "  ●" : string.Empty;
    partial void OnHasNewNewsChanged(bool value) => OnPropertyChanged(nameof(NewNewsMarker));
}

public sealed class NewsApiResponse
{
    [JsonPropertyName("results")] public List<NewsApiItem> Results { get; set; } = new();
}

public sealed class NewsApiItem
{
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("published_at")] public string? PublishedAt { get; set; }
    [JsonPropertyName("crawled_at")] public string? CrawledAt { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("tld")] public string? Tld { get; set; }
    [JsonPropertyName("lang")] public string? Language { get; set; }
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("image_url")] public string? ImageUrl { get; set; }
    [JsonPropertyName("imageUrl")] public string? ImageUrlCamelCase { get; set; }
}

public sealed class ArticleContent
{
    public List<string> Paragraphs { get; init; } = new();
    public List<string> ImageUrls { get; init; } = new();
}
