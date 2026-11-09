namespace NewsApp.Services;

public sealed class NewsApiSettings
{
    public string BaseUrl { get; init; } = "https://freenewsapi.ai";
    public string SearchPath { get; init; } = "/v1/search";
    public string ArticlePath { get; init; } = "/v1/article";
    public string Language { get; init; } = "ru,en";
    public string ApiKey { get; init; } = string.Empty;
    public string RussianApiKey { get; init; } = string.Empty;
    public string EnglishApiKey { get; init; } = string.Empty;
    public int NewsPagesPerLanguage { get; init; } = 3;
    public int NewsPageSize { get; init; } = 100;

    public string SearchEndpoint => BuildEndpoint(SearchPath);
    public string ArticleEndpoint => BuildEndpoint(ArticlePath);

    public static NewsApiSettings Load()
    {
        LoadDotEnv();
        return new NewsApiSettings
        {
            BaseUrl = Read("FREENEWSAPI_BASE_URL", "https://freenewsapi.ai").TrimEnd('/'),
            SearchPath = Read("FREENEWSAPI_SEARCH_PATH", "/v1/search"),
            ArticlePath = Read("FREENEWSAPI_ARTICLE_PATH", "/v1/article"),
            Language = Read("FREENEWSAPI_LANG", "ru,en"),
            ApiKey = Read("FREENEWSAPI_API_KEY", string.Empty),
            RussianApiKey = Read("FREENEWSAPI_RU_API_KEY", Read("FREENEWSAPI_API_KEY", string.Empty)),
            EnglishApiKey = Read("FREENEWSAPI_EN_API_KEY", Read("FREENEWSAPI_API_KEY", string.Empty)),
            NewsPagesPerLanguage = ReadInt("FREENEWSAPI_PAGES_PER_LANGUAGE", 3, 1, 3),
            NewsPageSize = ReadInt("FREENEWSAPI_PAGE_SIZE", 100, 1, 100)
        };
    }

    private string BuildEndpoint(string path) =>
        $"{BaseUrl.TrimEnd('/')}/{path.TrimStart('/')}";

    private static string Read(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static int ReadInt(string name, int fallback, int minimum, int maximum)
    {
        return int.TryParse(Read(name, fallback.ToString()), out var value)
            ? Math.Clamp(value, minimum, maximum)
            : fallback;
    }

    private static void LoadDotEnv()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env")
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            foreach (var rawLine in File.ReadLines(path))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase)) line = line[7..].Trim();
                var separator = line.IndexOf('=');
                if (separator <= 0) continue;
                var name = line[..separator].Trim();
                var value = line[(separator + 1)..].Trim().Trim('"', '\'');
                if (name.Length > 0 && Environment.GetEnvironmentVariable(name) is null)
                    Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
