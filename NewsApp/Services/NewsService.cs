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
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WorldPulse/1.0");
    }

    public async Task<IReadOnlyList<NewsArticle>> FetchNewsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var requestToken = timeout.Token;
        var articlesFromApi = new List<NewsApiItem>();
        try
        {
            await Parallel.ForEachAsync(WorldCountryCodes, new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = requestToken
            }, async (countryCode, token) =>
            {
                var items = await FetchCountryAsync(countryCode, token);
                lock (articlesFromApi) articlesFromApi.AddRange(items);
            });
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }

        var articles = articlesFromApi
            .Where(x => string.Equals(x.Language, "ru", StringComparison.OrdinalIgnoreCase))
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Select(ToArticle)
            .Where(x => x is not null)
            .Cast<NewsArticle>()
            .ToList();

        try
        {
            await Parallel.ForEachAsync(articles, new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = requestToken
            }, async (article, token) =>
            {
                if (string.IsNullOrWhiteSpace(article.ImageUrl))
                    article.ImageUrl = await TryGetOgImageAsync(article.Url, token);
            });
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }
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
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{ApiUrl}&country={countryCode}&size=12", cancellationToken);
                var temporaryFailure = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (temporaryFailure && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return new List<NewsApiItem>();
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                var result = await JsonSerializer.DeserializeAsync<NewsApiResponse>(stream, cancellationToken: cancellationToken);
                return result?.Results ?? new List<NewsApiItem>();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return new List<NewsApiItem>(); }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
            }
            catch (HttpRequestException) { return new List<NewsApiItem>(); }
        }
        return new List<NewsApiItem>();
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

    public async Task<ArticleContent?> DownloadArticleAsync(string articleUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(articleUrl, UriKind.Absolute, out var uri)) return null;
        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            Encoding encoding;
            try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }

            var document = new HtmlDocument();
            document.LoadHtml(encoding.GetString(bytes));
            foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//noscript|//nav|//footer|//header|//aside|//form") ?? Enumerable.Empty<HtmlNode>())
                node.Remove();

            var root = document.DocumentNode.SelectSingleNode("//article")
                ?? document.DocumentNode.SelectSingleNode("//main")
                ?? document.DocumentNode;
            var paragraphs = root.SelectNodes(".//p")?
                .Select(x => HtmlEntity.DeEntitize(x.InnerText).Trim())
                .Where(x => x.Length >= 35)
                .Distinct(StringComparer.Ordinal)
                .Take(80)
                .ToList();
            var images = root.SelectNodes(".//img")?
                .Select(x => x.GetAttributeValue("src", string.Empty))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => MakeAbsoluteUrl(articleUrl, x))
                .Where(x => x is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList() ?? new List<string>();
            return paragraphs is { Count: > 0 }
                ? new ArticleContent { Paragraphs = paragraphs, ImageUrls = images }
                : null;
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
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
