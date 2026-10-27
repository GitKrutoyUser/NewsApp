using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using HtmlAgilityPack;
using NewsApp.Models;

namespace NewsApp.Services;

public sealed class NewsService
{
    private const string ApiUrl = "https://freenewsapi.ai/v1/search?lang=ru";
    private static readonly string[] WorldCountryCodes = { "US", "RU", "DE", "FR", "GB", "CN", "JP", "IN", "BR", "CA", "AU", "IT", "ES", "TR", "KR", "MX", "PL", "NL", "SE", "CH", "IL", "UA", "ZA" };
    private readonly HttpClient _httpClient;

    static NewsService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public NewsService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WorldPulse/1.0");
    }

    public async Task<IReadOnlyList<NewsArticle>> FetchNewsAsync(CancellationToken cancellationToken = default)
    {
        var articlesFromApi = new List<NewsApiItem>();
        await Parallel.ForEachAsync(WorldCountryCodes, new ParallelOptions
        {
            MaxDegreeOfParallelism = 4,
            CancellationToken = cancellationToken
        }, async (countryCode, token) =>
        {
            var items = await FetchCountryAsync(countryCode, token);
            lock (articlesFromApi) articlesFromApi.AddRange(items);
        });

        var articles = articlesFromApi
            .Where(x => string.Equals(x.Language, "ru", StringComparison.OrdinalIgnoreCase))
            .Select(ToArticle)
            .Where(x => x is not null)
            .Cast<NewsArticle>()
            .ToList();

        await Parallel.ForEachAsync(articles, new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = cancellationToken
        }, async (article, token) =>
        {
            if (string.IsNullOrWhiteSpace(article.ImageUrl))
                article.ImageUrl = await TryGetOgImageAsync(article.Url, token);
        });
        return articles;
    }

    public async Task<Bitmap?> DownloadImageAsync(string? imageUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)) return null;
        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length == 0 ? null : new Bitmap(new MemoryStream(bytes));
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (ArgumentException) { return null; }
    }

    private async Task<List<NewsApiItem>> FetchCountryAsync(string countryCode, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync($"{ApiUrl}&country={countryCode}&size=12", cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var result = await JsonSerializer.DeserializeAsync<NewsApiResponse>(stream, cancellationToken: cancellationToken);
            return result?.Results ?? new List<NewsApiItem>();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new List<NewsApiItem>(); }
        catch (HttpRequestException) { return new List<NewsApiItem>(); }
    }

    private static NewsArticle? ToArticle(NewsApiItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Url)) return null;
        var country = item.Country?.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(country)) country = "WORLD";
        if (country.Length > 8) country = "WORLD";
        var imageUrl = MakeAbsoluteUrl(item.Url, item.Image);

        var publishedAt = ParseDate(item.PublishedAt);
        if (publishedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            publishedAt = ParseDate(item.CrawledAt);

        return new NewsArticle
        {
            Url = item.Url,
            CountryCode = country,
            CountryName = CountryNameFor(country),
            Title = item.Title ?? "Без заголовка",
            Description = item.Description ?? string.Empty,
            PublishedAt = publishedAt,
            ImageUrl = imageUrl
        };
    }

    private static string CountryNameFor(string code) => code switch
    {
        "US" => "США", "RU" => "Россия", "DE" => "Германия", "FR" => "Франция", "GB" => "Великобритания",
        "CN" => "Китай", "JP" => "Япония", "IN" => "Индия", "BR" => "Бразилия", "CA" => "Канада",
        "AU" => "Австралия", "IT" => "Италия", "ES" => "Испания", "TR" => "Турция", "KR" => "Южная Корея",
        "MX" => "Мексика", "PL" => "Польша", "NL" => "Нидерланды", "SE" => "Швеция", "CH" => "Швейцария",
        "IL" => "Израиль", "UA" => "Украина", "ZA" => "ЮАР", _ => "Мир"
    };

    private async Task<string?> TryGetOgImageAsync(string articleUrl, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(articleUrl, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            Encoding encoding;
            try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            var html = encoding.GetString(bytes);
            var document = new HtmlDocument();
            document.LoadHtml(html);
            var node = document.DocumentNode.SelectSingleNode("//meta[@property='og:image'] | //meta[@name='og:image']");
            var image = node?.GetAttributeValue("content", string.Empty);
            return MakeAbsoluteUrl(articleUrl, image);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    private static string? MakeAbsoluteUrl(string baseUrl, string? image)
    {
        if (string.IsNullOrWhiteSpace(image)) return null;
        image = WebUtility.HtmlDecode(image);
        return Uri.TryCreate(new Uri(baseUrl), image, out var absolute) ? absolute.ToString() : image;
    }

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date : DateTimeOffset.UtcNow;
}
