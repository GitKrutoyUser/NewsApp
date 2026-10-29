using System.Text;
using System.Text.Json;

namespace NewsApp.Services;

public sealed class LocalTranslationService
{
    private readonly HttpClient _httpClient = new() { BaseAddress = new Uri("http://127.0.0.1:5000"), Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string> TranslateAsync(string text, string sourceLanguage, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text) || string.Equals(sourceLanguage, "ru", StringComparison.OrdinalIgnoreCase))
            return text;

        var translated = new StringBuilder();
        foreach (var chunk in SplitText(text, 3500))
        {
            var part = await TranslateChunkAsync(chunk, sourceLanguage, cancellationToken);
            translated.Append(part);
            translated.Append("\n\n");
        }
        return translated.Length == 0 ? text : translated.ToString().Trim();
    }

    private async Task<string> TranslateChunkAsync(string text, string sourceLanguage, CancellationToken cancellationToken)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                q = text,
                source = sourceLanguage,
                target = "ru",
                format = "text"
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync("/translate", content, cancellationToken);
            if (!response.IsSuccessStatusCode) return text;
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("translatedText", out var value)
                ? value.GetString() ?? text
                : text;
        }
        catch (HttpRequestException) { return text; }
        catch (TaskCanceledException) { return text; }
        catch (JsonException) { return text; }
    }

    private static IEnumerable<string> SplitText(string text, int maxCharacters)
    {
        for (var start = 0; start < text.Length; start += maxCharacters)
            yield return text.Substring(start, Math.Min(maxCharacters, text.Length - start));
    }
}
