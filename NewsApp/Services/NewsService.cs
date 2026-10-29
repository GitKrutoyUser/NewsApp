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
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Select(ToArticle)
            .Where(x => x is not null)
            .Cast<NewsArticle>()
            .ToList();

        return articles;
    }

    public async Task<Bitmap?> DownloadImageAsync(string? imageUrl, CancellationToken cancellationToken = default)
        => await DownloadImageAsync(imageUrl, null, cancellationToken);

    public async Task<Bitmap?> DownloadImageAsync(string? imageUrl, string? referer, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
            if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                request.Headers.Referrer = refererUri;
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return bytes.Length == 0 ? null : new Bitmap(new MemoryStream(bytes));
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
        catch (ArgumentException) { return null; }
    }

    public async Task<string?> FindArticleImageAsync(string articleUrl, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        return await TryGetOgImageAsync(articleUrl, timeout.Token);
    }

    public async Task<string?> FindArticleDescriptionAsync(string articleUrl, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _httpClient.GetAsync(articleUrl, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token);
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            Encoding encoding;
            try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }

            var document = new HtmlDocument();
            document.LoadHtml(encoding.GetString(bytes));
            var meta = document.DocumentNode.SelectSingleNode(
                "//meta[contains(translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'description')]" +
                " | //meta[contains(translate(@property,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'description')]" +
                " | //meta[@itemprop='description']")
                ?.GetAttributeValue("content", string.Empty)?.Trim();
            if (!string.IsNullOrWhiteSpace(meta)) return HtmlEntity.DeEntitize(meta);

            return document.DocumentNode.SelectNodes("//article//p | //main//p | //p")?
                .Select(x => HtmlEntity.DeEntitize(x.InnerText).Trim())
                .FirstOrDefault(x => x.Length >= 60);
        }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    public async Task<IReadOnlyList<NewsArticle>> CompleteDescriptionsAsync(
        IReadOnlyList<NewsArticle> articles,
        CancellationToken cancellationToken = default)
    {
        await Parallel.ForEachAsync(articles.Where(x => string.IsNullOrWhiteSpace(x.Description)), new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = cancellationToken
        }, async (article, token) =>
        {
            var description = await FindArticleDescriptionAsync(article.Url, token);
            if (!string.IsNullOrWhiteSpace(description))
                article.Description = description;
        });

        return articles
            .Where(x => !string.IsNullOrWhiteSpace(x.Description))
            .ToList();
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
            var node = document.DocumentNode.SelectSingleNode(
                "//meta[contains(translate(@property,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'og:image')]" +
                " | //meta[contains(translate(@name,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'twitter:image')]" +
                " | //link[contains(translate(@rel,'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'image_src')]");
            var image = node?.GetAttributeValue("content", string.Empty) ?? node?.GetAttributeValue("href", string.Empty);
            if (string.IsNullOrWhiteSpace(image))
            {
                var imageNode = document.DocumentNode.SelectSingleNode("//article//img | //main//img | //img");
                image = new[]
                {
                    imageNode?.GetAttributeValue("src", string.Empty),
                    imageNode?.GetAttributeValue("data-src", string.Empty),
                    imageNode?.GetAttributeValue("data-lazy-src", string.Empty)
                }.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            }
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
