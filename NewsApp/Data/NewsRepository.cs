using Microsoft.Data.Sqlite;
using NewsApp.Models;

namespace NewsApp.Data;

public sealed class NewsRepository
{
    private readonly string _connectionString;

    static NewsRepository() => SQLitePCL.Batteries_V2.Init();

    public NewsRepository(string? databasePath = null)
    {
        databasePath ??= Path.Combine(AppContext.BaseDirectory, "world_news.db");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS news (
                url TEXT PRIMARY KEY,
                country_code TEXT NOT NULL,
                country_name TEXT NOT NULL,
                title TEXT NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                published_at TEXT NOT NULL,
                image_url TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_news_country_published
                ON news(country_code, published_at DESC);
            CREATE TABLE IF NOT EXISTS app_state (
                state_key TEXT PRIMARY KEY,
                state_value TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }

    public void SaveNews(IEnumerable<NewsArticle> articles)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO news (url, country_code, country_name, title, description, published_at, image_url)
            VALUES ($url, $country, $country_name, $title, $description, $published_at, $image_url)
            ON CONFLICT(url) DO UPDATE SET
                country_code = excluded.country_code,
                country_name = excluded.country_name,
                title = excluded.title,
                description = excluded.description,
                published_at = excluded.published_at,
                image_url = CASE
                    WHEN news.image_url IS NULL OR news.image_url = ''
                    THEN excluded.image_url
                    ELSE news.image_url
                END;
            """;

        var url = command.Parameters.Add("$url", SqliteType.Text);
        var country = command.Parameters.Add("$country", SqliteType.Text);
        var countryName = command.Parameters.Add("$country_name", SqliteType.Text);
        var title = command.Parameters.Add("$title", SqliteType.Text);
        var description = command.Parameters.Add("$description", SqliteType.Text);
        var published = command.Parameters.Add("$published_at", SqliteType.Text);
        var image = command.Parameters.Add("$image_url", SqliteType.Text);

        foreach (var article in articles.Where(x => !string.IsNullOrWhiteSpace(x.Url)))
        {
            url.Value = article.Url;
            country.Value = article.CountryCode;
            countryName.Value = article.CountryName;
            title.Value = article.Title;
            description.Value = article.Description;
            published.Value = article.PublishedAt.ToUniversalTime().ToString("O");
            image.Value = (object?)article.ImageUrl ?? DBNull.Value;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public HashSet<string> GetKnownUrls()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT url FROM news;";
        using var reader = command.ExecuteReader();
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read()) urls.Add(reader.GetString(0));
        return urls;
    }

    public DateTimeOffset? GetLastApiSync()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT state_value FROM app_state WHERE state_key = 'last_api_sync';";
        var value = command.ExecuteScalar()?.ToString();
        return DateTimeOffset.TryParse(value, out var result) ? result : null;
    }

    public void SetLastApiSync(DateTimeOffset value)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_state(state_key, state_value) VALUES ('last_api_sync', $value)
            ON CONFLICT(state_key) DO UPDATE SET state_value = excluded.state_value;
            """;
        command.Parameters.AddWithValue("$value", value.ToUniversalTime().ToString("O"));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<NewsArticle> GetNewsByCountry(string countryCode)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT url, country_code, country_name, title, description, published_at, image_url
            FROM news
            WHERE $country = 'WORLD' OR country_code = $country
            ORDER BY published_at DESC;
            """;
        command.Parameters.AddWithValue("$country", countryCode);

        using var reader = command.ExecuteReader();
        var result = new List<NewsArticle>();
        while (reader.Read())
        {
            result.Add(new NewsArticle
            {
                Url = reader.GetString(0),
                CountryCode = reader.GetString(1),
                CountryName = reader.GetString(2),
                Title = reader.GetString(3),
                Description = reader.GetString(4),
                PublishedAt = DateTimeOffset.TryParse(reader.GetString(5), out var date)
                    ? date : DateTimeOffset.UtcNow,
                ImageUrl = reader.IsDBNull(6) ? null : reader.GetString(6)
            });
        }
        return result;
    }
}
