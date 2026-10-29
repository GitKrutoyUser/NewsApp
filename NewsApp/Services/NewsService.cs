using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using HtmlAgilityPack;
using NewsApp.Models;

namespace NewsApp.Services;

public sealed class NewsService
{
    private const string ApiUrl = "https://freenewsapi.ai/v1/search";
    private const string AllCountryCodesText = "AD AE AF AG AL AM AO AR AT AU AZ BA BB BD BE BF BG BH BI BJ BN BO BR BS BT BW BY BZ CA CD CF CG CH CI CL CM CN CO CR CU CV CY CZ DE DJ DK DM DO DZ EC EE EG ER ES ET FI FJ FM FR GA GB GD GE GH GM GN GQ GR GT GW GY HN HR HT HU ID IE IL IN IQ IR IS IT JM JO JP KE KG KH KI KM KN KP KR KW KZ LA LB LC LI LK LR LS LT LU LV LY MA MC MD ME MG MH MK ML MM MN MR MT MU MV MW MX MY MZ NA NE NG NI NL NO NP NR NZ OM PA PE PG PH PK PL PS PT PW PY QA RO RS RU RW SA SB SC SD SE SG SI SK SL SM SN SO SR SS ST SV SY SZ TD TG TH TJ TL TM TN TO TR TT TV TW TZ UA UG US UY UZ VC VE VN VU WS YE ZA ZM ZW";
    private static readonly string[] AllCountryCodes = AllCountryCodesText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    private static readonly IReadOnlyList<FeedGroup> FeedGroups = BuildFeedGroups();
    private readonly HttpClient _httpClient;

    private sealed record FeedGroup(string Language, string Countries);

    private static IReadOnlyList<FeedGroup> BuildFeedGroups()
    {
        var byLanguage = AllCountryCodes
            .Select(code => (Code: code, Language: PrimaryLanguage(code)))
            .GroupBy(x => x.Language, StringComparer.OrdinalIgnoreCase);

        return byLanguage
            .SelectMany(group => group.Chunk(40).Select(chunk => new FeedGroup(
                group.Key,
                string.Join(',', chunk.Select(x => x.Code)))))
            .ToList();
    }

    private static string PrimaryLanguage(string code) => code switch
    {
        "ES" or "MX" or "AR" or "CO" or "CL" or "PE" or "VE" or "EC" or "BO" or "UY" or "PY" or "CR" or "PA" or "DO" or "GT" or "HN" or "SV" or "NI" or "CU" => "es",
        "FR" or "BE" or "LU" or "MC" or "CI" or "SN" or "CM" or "MG" or "ML" or "BF" or "NE" or "TG" or "BJ" or "CD" or "CG" or "GA" or "RW" or "HT" => "fr",
        "DE" or "AT" or "LI" => "de",
        "PT" or "BR" or "AO" or "MZ" => "pt",
        "IT" or "SM" or "VA" => "it",
        "RU" or "BY" or "KZ" or "KG" => "ru",
        "UA" => "uk",
        "PL" => "pl",
        "TR" => "tr",
        "IL" => "he",
        "JP" => "ja",
        "CN" or "TW" or "HK" => "zh",
        "KR" => "ko",
        "NL" => "nl",
        "SE" => "sv",
        "NO" => "no",
        "DK" => "da",
        "FI" => "fi",
        "CZ" => "cs",
        "GR" or "CY" => "el",
        "HU" => "hu",
        "RO" or "MD" => "ro",
        "BG" => "bg",
        "RS" or "BA" or "ME" => "sr",
        "HR" => "hr",
        "SK" => "sk",
        "SI" => "sl",
        "ID" => "id",
        "MY" => "ms",
        "TH" => "th",
        "VN" => "vi",
        "IR" => "fa",
        "PK" => "ur",
        "BD" => "bn",
        "LK" => "si",
        "SA" or "AE" or "EG" or "IQ" or "JO" or "MA" or "DZ" or "TN" or "LB" or "SY" or "QA" or "KW" or "BH" or "OM" or "YE" => "ar",
        _ => "en"
    };

    static NewsService() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public NewsService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("WorldPulse/1.0");
    }

    public async Task<IReadOnlyList<NewsArticle>> FetchNewsAsync(CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var requestToken = timeout.Token;
        var articlesFromApi = new List<NewsApiItem>();
        try
        {
            await Parallel.ForEachAsync(FeedGroups, new ParallelOptions
            {
                MaxDegreeOfParallelism = 8,
                CancellationToken = requestToken
            }, async (countryCode, token) =>
            {
                var items = await FetchGroupAsync(countryCode, token);
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

    public async Task<IReadOnlyList<NewsArticle>> FetchNewsForCountryAsync(string countryCode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(countryCode) || countryCode.Equals("WORLD", StringComparison.OrdinalIgnoreCase))
            return Array.Empty<NewsArticle>();

        var code = countryCode.ToUpperInvariant();
        var items = await FetchGroupAsync(new FeedGroup(PrimaryLanguage(code), code), cancellationToken);
        return items
            .Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .GroupBy(x => x.Url, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Select(ToArticle)
            .Where(x => x is not null)
            .Cast<NewsArticle>()
            .ToList();
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

    private async Task<List<NewsApiItem>> FetchGroupAsync(FeedGroup group, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var language = string.IsNullOrWhiteSpace(group.Language) ? string.Empty : $"lang={group.Language}&";
                using var response = await _httpClient.GetAsync($"{ApiUrl}?{language}country={group.Countries}&size=50", cancellationToken);
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
            LanguageCode = string.IsNullOrWhiteSpace(item.Language) ? "en" : item.Language.ToLowerInvariant(),
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
