using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NewsApp.Data;
using NewsApp.Models;
using NewsApp.Services;

namespace NewsApp.ViewModels;

public partial class MainWindowViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan MinimumApiInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan MinimumManualInterval = TimeSpan.FromMinutes(5);
    public event EventHandler<IReadOnlySet<string>>? NewCountriesChanged;
    private readonly NewsRepository _repository;
    private readonly NewsService _service;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    public ObservableCollection<Country> Countries { get; } = new(new[]
    {
        new Country("WORLD", "Мир", "🌐"),
        new Country("MOON", "Луна", "◐"),
        new Country("US", "США", "🇺🇸"),
        new Country("RU", "Россия", "🇷🇺"),
        new Country("DE", "Германия", "🇩🇪"),
        new Country("FR", "Франция", "🇫🇷"),
        new Country("GB", "Великобритания", "🇬🇧"),
        new Country("CN", "Китай", "🇨🇳"),

        new Country("JP", "Япония", "🇯🇵"),
        new Country("IN", "Индия", "🇮🇳"),
        new Country("BR", "Бразилия", "🇧🇷"),
        new Country("CA", "Канада", "🇨🇦"),
        new Country("AU", "Австралия", "🇦🇺"),
        new Country("IT", "Италия", "🇮🇹"), 
        new Country("ES", "Испания", "🇪🇸"),
        new Country("TR", "Турция", "🇹🇷"),
        new Country("KR", "Южная Корея", "🇰🇷"),
        new Country("MX", "Мексика", "🇲🇽")
        , new Country("PL", "Польша", "🇵🇱"),
        new Country("NL", "Нидерланды", "🇳🇱")
        , new Country("SE", "Швеция", "🇸🇪"),
        new Country("CH", "Швейцария", "🇨🇭"),
        new Country("IL", "Израиль", "🇮🇱"), 
        new Country("UA", "Украина", "🇺🇦"), 
        new Country("ZA", "ЮАР", "🇿🇦")
    });

    public ObservableCollection<NewsArticle> Articles { get; } = new();
    public ObservableCollection<string> OpenedArticleParagraphs { get; } = new();

    [ObservableProperty] private Country _selectedCountry;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isNewsPaneVisible = true;
    [ObservableProperty] private string _statusText = "Проверяем локальный кэш…";
    [ObservableProperty] private bool _isReadingArticle;
    [ObservableProperty] private NewsArticle? _openedArticle;
    [ObservableProperty] private string _openedArticleText = string.Empty;
    [ObservableProperty] private bool _isArticleLoading;

    public MainWindowViewModel() : this(new NewsRepository(), new NewsService()) { }

    public MainWindowViewModel(NewsRepository repository, NewsService service)
    {
        _repository = repository;
        _service = service;
        _selectedCountry = Countries[0];
        _refreshTimer = new System.Timers.Timer(TimeSpan.FromMinutes(30).TotalMilliseconds) { AutoReset = true };
        _refreshTimer.Elapsed += OnTimerElapsed;
        _refreshTimer.Start();
        _ = InitializeAsync();
    }

    partial void OnSelectedCountryChanged(Country value) => _ = LoadCachedNewsAsync(value.Code);

    public void SelectCountryCode(string code)
    {
        var country = Countries.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (country is not null) SelectedCountry = country;
    }

    public void SelectCountryCode(string code, string? countryName)
    {
        var country = Countries.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (country is null)
        {
            country = new Country(code, string.IsNullOrWhiteSpace(countryName) ? code : countryName, "");
            Countries.Add(country);
        }
        SelectedCountry = country;
    }

    [RelayCommand]
    private void SelectCountry(Country country) => SelectedCountry = country;

    [RelayCommand]
    private void SelectMoon() => SelectedCountry = Countries.First(x => x.Code == "MOON");

    [RelayCommand]
    private Task RefreshAsync() => RefreshInternalAsync(true);

    [RelayCommand]
    private void ToggleNewsPane() => IsNewsPaneVisible = !IsNewsPaneVisible;

    [RelayCommand]
    private async Task LoadCacheAsync()
    {
        await LoadCachedNewsAsync(SelectedCountry.Code);
        await SetUiAsync(() => StatusText = Articles.Count == 0
            ? "В базе нет новостей для выбранного раздела"
            : $"Показано из базы: {Articles.Count} материалов");
    }

    private async Task RefreshInternalAsync(bool manual)
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            var hasAnyNews = await Task.Run(() => _repository.HasAnyNews(), _lifetime.Token);
            var lastSync = await Task.Run(() => _repository.GetLastApiSync(), _lifetime.Token);
            if (hasAnyNews && lastSync is not null)
            {
                var minimumInterval = manual ? MinimumManualInterval : MinimumApiInterval;
                if (DateTimeOffset.UtcNow - lastSync.Value < minimumInterval)
                {
                var next = lastSync.Value.Add(MinimumApiInterval).ToLocalTime();
                    await SetUiAsync(() => StatusText = manual
                        ? $"Ручное обновление доступно после {lastSync.Value.Add(MinimumManualInterval).ToLocalTime():HH:mm}"
                        : $"Следующее автоматическое обновление после {next:HH:mm}");
                    return;
                }
            }
            await SetUiAsync(() => { IsRefreshing = true; StatusText = "Новости обновляются"; });
            var fetchedNews = await _service.FetchNewsAsync(_lifetime.Token);
            var news = await _service.CompleteDescriptionsAsync(fetchedNews, _lifetime.Token);
            await Task.Run(() => _repository.DeleteNewsByCountry("MOON"), _lifetime.Token);
            var knownUrls = await Task.Run(() => _repository.GetKnownUrls(), _lifetime.Token);
            var newCountries = news.Where(x => !knownUrls.Contains(x.Url)).Select(x => x.CountryCode)
                .Where(x => x != "WORLD").ToHashSet(StringComparer.OrdinalIgnoreCase);
            await Task.Run(() => _repository.SaveNews(news), _lifetime.Token);
            
            if (news.Count > 0)
                await Task.Run(() => _repository.SetLastApiSync(DateTimeOffset.UtcNow), _lifetime.Token);
            await SetUiAsync(() =>
            {
                foreach (var country in Countries) country.HasNewNews = newCountries.Contains(country.Code);
                NewCountriesChanged?.Invoke(this, newCountries);
            });
            await LoadCachedNewsAsync(SelectedCountry.Code);
            await SetUiAsync(() => StatusText = news.Count == 0
                ? "API не вернул новости · показываем локальную базу"
                : $"Обновлено: {DateTime.Now:HH:mm} · {news.Count} материалов");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Debug.WriteLine($"News refresh failed: {exception}");
            await SetUiAsync(() => StatusText = "Нет соединения · показываем последние сохранённые новости");
            await LoadCachedNewsAsync(SelectedCountry.Code);
        }
        finally
        {
            await SetUiAsync(() => IsRefreshing = false);
            _refreshGate.Release();
        }
    }

    [RelayCommand]
    private async Task OpenArticleAsync(NewsArticle article)
    {
        OpenedArticle = article;
        OpenedArticleText = article.Description;
        OpenedArticleParagraphs.Clear();
        if (!string.IsNullOrWhiteSpace(article.Description)) OpenedArticleParagraphs.Add(article.Description);
        IsReadingArticle = true;
        IsArticleLoading = true;
        try
        {
            var content = await _service.DownloadArticleAsync(article.Url, _lifetime.Token);
            if (content is not null)
            {
                OpenedArticleParagraphs.Clear();
                foreach (var paragraph in content.Paragraphs) OpenedArticleParagraphs.Add(paragraph);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Debug.WriteLine($"Read article failed: {exception}"); }
        finally
        {
            IsArticleLoading = false;
        }
    }

    [RelayCommand]
    private void CloseArticle()
    {
        IsReadingArticle = false;
        OpenedArticle = null;
        OpenedArticleText = string.Empty;
        OpenedArticleParagraphs.Clear();
    }

    [RelayCommand]
    private void OpenArticleInBrowser(NewsArticle article)
    {
        try
        {
            if (Uri.TryCreate(article.Url, UriKind.Absolute, out var uri))
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception exception) { Debug.WriteLine($"Open article in browser failed: {exception}"); }
    }

    private async void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try { await RefreshInternalAsync(false); }
        catch (Exception exception) { Debug.WriteLine($"Timer refresh failed: {exception}"); }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await LoadCachedNewsAsync(SelectedCountry.Code);
            await SetUiAsync(() => IsLoading = false);
            await RefreshInternalAsync(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"Initialization failed: {exception}");
            await SetUiAsync(() => IsLoading = false);
        }
    }

    private async Task LoadCachedNewsAsync(string countryCode)
    {
        try
        {
            var items = await Task.Run(() => _repository.GetNewsByCountry(countryCode), _lifetime.Token);
            await SetUiAsync(() =>
            {
                Articles.Clear();
                foreach (var article in items) Articles.Add(article);
                if (!IsRefreshing && items.Count == 0)
                    StatusText = "пока что нет новостей для этой страны";
            });
            _ = LoadImagesAsync(items);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Debug.WriteLine($"Cache read failed: {exception}");
            await SetUiAsync(() => StatusText = "Не удалось прочитать локальный кэш новостей");
        }
    }

    private async Task LoadImagesAsync(IEnumerable<NewsArticle> items)
    {
        try
        {
            await Parallel.ForEachAsync(items, new ParallelOptions
            {
                MaxDegreeOfParallelism = 6,
                CancellationToken = _lifetime.Token
            }, async (article, token) =>
            {
                var metadataChanged = false;
                var bitmap = await _service.DownloadImageAsync(article.ImageUrl, article.Url, token);
                if (bitmap is null)
                {
                    var fallbackImageUrl = await _service.FindArticleImageAsync(article.Url, token);
                    if (!string.IsNullOrWhiteSpace(fallbackImageUrl) && !fallbackImageUrl.Equals(article.ImageUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        article.ImageUrl = fallbackImageUrl;
                        await Task.Run(() => _repository.UpdateImageUrl(article.Url, fallbackImageUrl), token);
                        bitmap = await _service.DownloadImageAsync(fallbackImageUrl, article.Url, token);
                    }
                }
                if (string.IsNullOrWhiteSpace(article.Description))
                {
                    var description = await _service.FindArticleDescriptionAsync(article.Url, token);
                    if (!string.IsNullOrWhiteSpace(description))
                    {
                        article.Description = description;
                        metadataChanged = true;
                    }
                }
                if (metadataChanged)
                    await Task.Run(() => _repository.SaveNews(new[] { article }), token);
                if (bitmap is not null)
                    await SetUiAsync(() => article.ImageBitmap = bitmap);
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Debug.WriteLine($"Image loading failed: {exception}"); }
    }

    private static Task SetUiAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();

    public void Dispose()
    {
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _refreshGate.Dispose();
    }
}
