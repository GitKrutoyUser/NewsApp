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
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public string? ImageUrl { get; set; }
    public string PublishedAtText => PublishedAt.ToLocalTime().ToString("dd.MM.yyyy");

    [ObservableProperty]
    private Bitmap? _imageBitmap;
}

public sealed record Country(string Code, string Name, string Flag)
{
    public string DisplayName => $"{Flag}  {Name}";
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
}
