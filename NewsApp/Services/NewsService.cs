using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Media.Imaging;
using HtmlAgilityPack;
using NewsApp.Models;

namespace NewsApp.Services;

public sealed class NewsService
{
    private const int MaxImageBytes = 10 * 1024 * 1024;
    private const int MaxHtmlBytes = 5 * 1024 * 1024;
    private const string LunarFeedUrl = "https://www.nasa.gov/missions/artemis/feed/";
    private static readonly XNamespace ContentNamespace = "http://purl.org/rss/1.0/modules/content/";
    private static readonly string[] WorldCountryCodes =
    {
        "US", "RU", "UA", "CA", "MX", "BR", "AR", "CL", "CO", "PE", "VE",
        "GB", "IE", "FR", "DE", "ES", "PT", "IT", "NL", "BE", "CH", "AT", "PL", "CZ", "SK", "HU", "RO", "BG", "GR", "SE", "NO", "DK", "FI", "IS", "EE", "LV", "LT", "RS", "HR", "SI", "BA", "AL", "MD", "GE", "AM", "AZ", "BY",
        "CN", "JP", "KR", "IN", "PK", "BD", "ID", "MY", "SG", "TH", "VN", "PH", "TW", "HK",
        "AU", "NZ", "ZA", "EG", "MA", "DZ", "TN", "NG", "KE", "ET", "GH", "TZ", "IL", "TR", "SA", "AE", "QA", "IR", "IQ", "KZ", "UZ"
    };
    private readonly HttpClient _httpClient;
    private readonly HttpClient _contentClient;
    private readonly NewsApiSettings _apiSettings;
    public string? LastApiError { get; private set; }

    static NewsService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public NewsService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _apiSettings = NewsApiSettings.Load();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WorldPulse/1.0");
        if (!string.IsNullOrWhiteSpace(_apiSettings.ApiKey))
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiSettings.ApiKey);
        _contentClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _contentClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 WorldPulse/1.0");
        _contentClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
    }

    public async Task<IReadOnlyList<NewsArticle>> FetchNewsAsync(CancellationToken cancellationToken = default)
    {
        LastApiError = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(90));
        var requestToken = timeout.Token;
        var articlesFromApi = new List<NewsApiItem>();
        try
        {
            await Parallel.ForEachAsync(WorldCountryCodes.Chunk(12), new ParallelOptions
            {
                MaxDegreeOfParallelism = 2,
                CancellationToken = requestToken
            }, async (countryCode, token) =>
            {
                var items = await FetchCountryAsync(string.Join(",", countryCode), token);
                lock (articlesFromApi) articlesFromApi.AddRange(items);
            });
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { }

        try
        {
            var russianLunarNews = await FetchRussianLunarNewsAsync(requestToken);
            articlesFromApi.AddRange(russianLunarNews);
        }
        catch (OperationCanceledException) when (requestToken.IsCancellationRequested) { }

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

    private async Task<List<NewsApiItem>> FetchRussianLunarNewsAsync(CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString("лунная миссия");
        using var response = await _httpClient.GetAsync($"{_apiSettings.SearchEndpoint}?lang={Uri.EscapeDataString(_apiSettings.Language)}&q={query}&size=20", cancellationToken);
        if (!response.IsSuccessStatusCode) return new List<NewsApiItem>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<NewsApiResponse>(stream, cancellationToken: cancellationToken);
        var items = (result?.Results ?? new List<NewsApiItem>())
            .Where(IsMoonArticle)
            .ToList();
        foreach (var item in items) item.Country = "MOON";
        return items;
    }

    private static bool IsMoonArticle(NewsApiItem item)
    {
        var text = $"{item.Title} {item.Description}".ToLowerInvariant();
        var isMoon = text.Contains("лун") || text.Contains("луна") ||
                     text.Contains("артемис") || text.Contains("apollo") || text.Contains("селена");
        var isAnotherPlanet = text.Contains("марс") || text.Contains("марсиан") ||
                              text.Contains("юпитер") || text.Contains("сатурн") ||
                              text.Contains("венер") || text.Contains("меркур") ||
                              text.Contains("нептун") || text.Contains("уран");
        return isMoon && !isAnotherPlanet;
    }

    private async Task<List<NewsApiItem>> FetchLunarNewsAsync(CancellationToken cancellationToken)
    {
        using var response = await _contentClient.GetAsync(LunarFeedUrl, cancellationToken);
        if (!response.IsSuccessStatusCode) return new List<NewsApiItem>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        var items = new List<NewsApiItem>();
        foreach (var item in document.Descendants("item").Take(20))
        {
            var url = item.Element("link")?.Value.Trim();
            var title = item.Element("title")?.Value.Trim();
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) continue;
            var description = item.Element("description")?.Value.Trim();
            var encoded = item.Element(ContentNamespace + "encoded")?.Value;
            var image = ExtractFeedImage(encoded);
            items.Add(new NewsApiItem
            {
                Url = url,
                Country = "MOON",
                Title = title,
                Description = string.IsNullOrWhiteSpace(description) ? "Новости лунных миссий NASA." : HtmlEntity.DeEntitize(description),
                Image = image,
                PublishedAt = item.Element("pubDate")?.Value,
                CrawledAt = item.Element("pubDate")?.Value
            });
        }
        return items;
    }

    private static string? ExtractFeedImage(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var document = new HtmlDocument();
        document.LoadHtml(html);
        return document.DocumentNode.SelectSingleNode("//img")?.GetAttributeValue("src", string.Empty);
    }

    public async Task<Bitmap?> DownloadImageAsync(string? imageUrl, CancellationToken cancellationToken = default)
        => await DownloadImageAsync(imageUrl, null, cancellationToken);

    public async Task<Bitmap?> DownloadImageAsync(string? imageUrl, string? referer, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri)) return null;
        if (!IsHttpUri(uri)) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
            if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
                request.Headers.Referrer = refererUri;
            using var response = await _contentClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await ReadBytesUpToAsync(response.Content, MaxImageBytes, cancellationToken);
            return bytes is { Length: > 0 } ? new Bitmap(new MemoryStream(bytes)) : null;
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
        if (!Uri.TryCreate(articleUrl, UriKind.Absolute, out var articleUri) || !IsHttpUri(articleUri)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            using var response = await _contentClient.GetAsync(articleUri, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await ReadBytesUpToAsync(response.Content, MaxHtmlBytes, timeout.Token);
            if (bytes is null) return null;
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

    public async Task TranslateForeignNewsAsync(IReadOnlyList<NewsArticle> articles, CancellationToken cancellationToken = default)
    {
        if (!_apiSettings.TranslationEnabled || string.IsNullOrWhiteSpace(_apiSettings.TranslationEndpoint)) return;
        foreach (var article in articles.Where(x => !string.Equals(x.Language, "ru", StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
                var sourceLanguage = article.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? "ru" : "en";
                if (sourceLanguage == "ru") continue;
                var title = await TranslateTextAsync(article.Title, sourceLanguage, cancellationToken);
                var description = await TranslateTextAsync(article.Description, sourceLanguage, cancellationToken);
                if (!string.IsNullOrWhiteSpace(title)) article.Title = title;
                if (!string.IsNullOrWhiteSpace(description)) article.Description = description;
                await Task.Delay(180, cancellationToken);
        }
    }

    private async Task<string?> TranslateTextAsync(string text, string sourceLanguage, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        try
        {
            var endpoint = $"{_apiSettings.TranslationEndpoint}?q={Uri.EscapeDataString(text[..Math.Min(text.Length, 480)])}&langpair={Uri.EscapeDataString($"{sourceLanguage}|ru")}";
            using var response = await _contentClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (document.RootElement.TryGetProperty("responseStatus", out var status) && status.GetInt32() != 200) return null;
            return document.RootElement.TryGetProperty("responseData", out var data) && data.TryGetProperty("translatedText", out var translated)
                ? translated.GetString()
                : null;
        }
        catch (JsonException) { return null; }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    private async Task<List<NewsApiItem>> FetchCountryAsync(string countryCode, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await _httpClient.GetAsync($"{_apiSettings.SearchEndpoint}?lang={Uri.EscapeDataString(_apiSettings.Language)}&country={Uri.EscapeDataString(countryCode)}&size=100", cancellationToken);
                var temporaryFailure = response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500;
                if (temporaryFailure && attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500 * (attempt + 1)), cancellationToken);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    LastApiError = await GetApiErrorAsync(response, cancellationToken);
                    return new List<NewsApiItem>();
                }
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

    private static async Task<string> GetApiErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var details = await response.Content.ReadAsStringAsync(cancellationToken);
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "API отклонил ключ (401). Проверь FREENEWSAPI_API_KEY в .env.",
            HttpStatusCode.Forbidden => "API запретил доступ (403). Проверь тариф или ключ.",
            _ => $"API вернул {(int)response.StatusCode}: {details[..Math.Min(details.Length, 120)]}"
        };
    }

    private static NewsArticle? ToArticle(NewsApiItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Url)) return null;
        var country = item.Country?.ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(country)) country = CountryFromTld(item.Tld);
        if (string.IsNullOrWhiteSpace(country)) country = "WORLD";
        if (country.Length > 8) country = "WORLD";
        var imageUrl = MakeAbsoluteUrl(item.Url, item.Image ?? item.ImageUrl ?? item.ImageUrlCamelCase);

        var publishedAt = ParseDate(item.PublishedAt);
        if (publishedAt > DateTimeOffset.UtcNow.AddMinutes(5))
            publishedAt = ParseDate(item.CrawledAt);

        return new NewsArticle
        {
            Url = item.Url,
            CountryCode = country,
            CountryName = CountryNameFor(country),
            Title = item.Title ?? "Без заголовка",
            OriginalTitle = item.Title ?? "Без заголовка",
            Description = item.Description ?? string.Empty,
            OriginalDescription = item.Description ?? string.Empty,
            Language = NormalizeLanguage(item.Language, item.Tld),
            PublishedAt = publishedAt,
            ImageUrl = imageUrl
        };
    }

    public async Task<ArticleContent?> DownloadArticleAsync(string articleUrl, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(articleUrl, UriKind.Absolute, out var uri)) return null;
        if (!IsHttpUri(uri)) return null;
        var apiContent = await DownloadArticleFromApiAsync(articleUrl, cancellationToken);
        if (apiContent is not null) return apiContent;
        try
        {
            using var response = await _contentClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await ReadBytesUpToAsync(response.Content, MaxHtmlBytes, cancellationToken);
            if (bytes is null) return null;
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

    private async Task<ArticleContent?> DownloadArticleFromApiAsync(string articleUrl, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"{_apiSettings.ArticleEndpoint}?url={Uri.EscapeDataString(articleUrl)}&full_text=true";
            using var response = await _httpClient.GetAsync(endpoint, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var text = FirstJsonString(root, "text", "full_text", "fullText", "content", "body", "article");
            if (string.IsNullOrWhiteSpace(text)) return null;
            var paragraphs = ExtractArticleParagraphs(text);
            if (paragraphs.Count == 0) paragraphs.Add(text.Trim());
            var image = FirstJsonString(root, "image", "image_url", "imageUrl");
            return new ArticleContent
            {
                Paragraphs = paragraphs,
                ImageUrls = string.IsNullOrWhiteSpace(image) ? new List<string>() : new List<string> { MakeAbsoluteUrl(articleUrl, image)! }
            };
        }
        catch (JsonException) { return null; }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    private static string? FirstJsonString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (!root.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind == JsonValueKind.Array)
            {
                var joined = string.Join("\n", value.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x)));
                if (!string.IsNullOrWhiteSpace(joined)) return joined;
            }
        }
        return null;
    }

    private static List<string> ExtractArticleParagraphs(string text)
    {
        var document = new HtmlDocument();
        document.LoadHtml(text);
        var htmlParagraphs = document.DocumentNode.SelectNodes("//p")?
            .Select(x => HtmlEntity.DeEntitize(x.InnerText).Trim())
            .Where(x => x.Length >= 35)
            .ToList();
        if (htmlParagraphs is { Count: > 0 })
            return htmlParagraphs.Distinct(StringComparer.Ordinal).Take(80).ToList();

        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split("\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(HtmlEntity.DeEntitize)
            .Where(x => x.Length >= 35)
            .Distinct(StringComparer.Ordinal)
            .Take(80)
            .ToList();
    }

    private static string NormalizeLanguage(string? language, string? tld)
    {
        var value = language?.Trim().ToLowerInvariant().Replace('_', '-');
        if (string.IsNullOrWhiteSpace(value))
            return tld?.ToLowerInvariant() switch { "ru" or "by" or "ua" => "ru", _ => "en" };
        if (value.StartsWith("ru", StringComparison.Ordinal)) return "ru";
        if (value.StartsWith("en", StringComparison.Ordinal)) return "en";
        return value.Length >= 2 ? value[..2] : "en";
    }

    private static string? CountryFromTld(string? tld) => tld?.Trim('.').ToLowerInvariant() switch
    {
        "ru" => "RU", "by" => "BY", "ua" => "UA", "us" => "US", "gb" or "uk" => "GB",
        "de" => "DE", "fr" => "FR", "es" => "ES", "it" => "IT", "cn" => "CN", "jp" => "JP",
        "in" => "IN", "br" => "BR", "ca" => "CA", "au" => "AU", "pl" => "PL", "tr" => "TR",
        "kz" => "KZ", "uz" => "UZ", "il" => "IL", "za" => "ZA", _ => null
    };

    private static string CountryNameFor(string code) => code switch
    {
        "US" => "США", "RU" => "Россия", "UA" => "Украина", "BY" => "Беларусь", "DE" => "Германия", "FR" => "Франция", "GB" => "Великобритания",
        "CN" => "Китай", "JP" => "Япония", "IN" => "Индия", "BR" => "Бразилия", "CA" => "Канада", "MX" => "Мексика",
        "AU" => "Австралия", "NZ" => "Новая Зеландия", "IT" => "Италия", "ES" => "Испания", "PT" => "Португалия", "IE" => "Ирландия",
        "TR" => "Турция", "KR" => "Южная Корея", "PL" => "Польша", "NL" => "Нидерланды", "BE" => "Бельгия", "CH" => "Швейцария",
        "AT" => "Австрия", "SE" => "Швеция", "NO" => "Норвегия", "DK" => "Дания", "FI" => "Финляндия", "GR" => "Греция",
        "CZ" => "Чехия", "RO" => "Румыния", "HU" => "Венгрия", "BG" => "Болгария", "IL" => "Израиль", "SA" => "Саудовская Аравия",
        "AE" => "ОАЭ", "EG" => "Египет", "ZA" => "ЮАР", "NG" => "Нигерия", "KE" => "Кения", "MA" => "Марокко",
        "AR" => "Аргентина", "CL" => "Чили", "CO" => "Колумбия", "PE" => "Перу", "KZ" => "Казахстан", "UZ" => "Узбекистан",
        "PK" => "Пакистан", "BD" => "Бангладеш", "ID" => "Индонезия", "MY" => "Малайзия", "SG" => "Сингапур", "TH" => "Таиланд",
        "VN" => "Вьетнам", "PH" => "Филиппины", "IR" => "Иран", "IQ" => "Ирак", "GE" => "Грузия", "AM" => "Армения",
        "MOON" => "Луна", "WORLD" => "Мир · страна не указана", _ => "Мир · страна не указана"
    };

    private async Task<string?> TryGetOgImageAsync(string articleUrl, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(articleUrl, UriKind.Absolute, out var articleUri) || !IsHttpUri(articleUri)) return null;
        try
        {
            using var response = await _contentClient.GetAsync(articleUri, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            var bytes = await ReadBytesUpToAsync(response.Content, MaxHtmlBytes, cancellationToken);
            if (bytes is null) return null;
            var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"', '\'');
            Encoding encoding;
            try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
            catch (ArgumentException) { encoding = Encoding.UTF8; }
            var html = encoding.GetString(bytes);
            var document = new HtmlDocument();
            document.LoadHtml(html);
            var image = document.DocumentNode.SelectNodes("//meta | //link")?
                .Select(node =>
                {
                    var property = node.GetAttributeValue("property", string.Empty);
                    var name = node.GetAttributeValue("name", string.Empty);
                    var rel = node.GetAttributeValue("rel", string.Empty);
                    var key = $"{property} {name} {rel}".ToLowerInvariant();
                    if (!key.Contains("og:image") && !key.Contains("twitter:image") && !key.Contains("image_src"))
                        return null;
                    return FirstNonEmpty(
                        node.GetAttributeValue("content", string.Empty).Trim(),
                        node.GetAttributeValue("href", string.Empty).Trim());
                })
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (string.IsNullOrWhiteSpace(image))
            {
                var imageNode = document.DocumentNode.SelectSingleNode("//article//img | //main//img | //img");
                image = new[]
                {
                    imageNode?.GetAttributeValue("src", string.Empty),
                    imageNode?.GetAttributeValue("data-src", string.Empty),
                    imageNode?.GetAttributeValue("data-lazy-src", string.Empty),
                    imageNode?.GetAttributeValue("data-original", string.Empty),
                    imageNode?.GetAttributeValue("srcset", string.Empty)?.Split(',').FirstOrDefault()?.Trim().Split(' ').FirstOrDefault()
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

    private static bool IsHttpUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static string FirstNonEmpty(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static async Task<byte[]?> ReadBytesUpToAsync(HttpContent content, int maxBytes, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maxBytes) return null;
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var output = new MemoryStream();
        var buffer = new byte[81920];
        var total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes) return null;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return output.ToArray();
    }

    private static DateTimeOffset ParseDate(string? value) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
            ? date : DateTimeOffset.UtcNow;
}
