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
    private static readonly TimeSpan MinimumApiInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinimumManualInterval = TimeSpan.FromMinutes(5);
    public event EventHandler<IReadOnlySet<string>>? NewCountriesChanged;
    private readonly NewsRepository _repository;
    private readonly NewsService _service;
    private const int NewsPageSize = 30;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly DispatcherTimer _searchTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _articleReadCancellation;
    private int _articleReadVersion;

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
        new Country("BY", "Беларусь", "🇧🇾"),
        new Country("ZA", "ЮАР", "🇿🇦")
    });

    public ObservableCollection<NewsArticle> Articles { get; } = new();
    public ObservableCollection<string> OpenedArticleParagraphs { get; } = new();
    private IReadOnlyList<NewsArticle> _loadedArticles = Array.Empty<NewsArticle>();
    private int _newsOffset;
    private bool _hasMoreNews;

    [ObservableProperty] private Country _selectedCountry;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isNewsPaneVisible = true;
    [ObservableProperty] private string _statusText = "Проверяем локальный кэш…";
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private bool _isReadingArticle;
    [ObservableProperty] private NewsArticle? _openedArticle;
    [ObservableProperty] private string _openedArticleText = string.Empty;
    [ObservableProperty] private bool _isArticleLoading;
    [ObservableProperty] private bool _isLoadingMore;
    [ObservableProperty] private double _articleFontSize = 17;
    [ObservableProperty] private bool _showOriginalSummary;
    [ObservableProperty] private string _languageFilter = "all";
    [ObservableProperty] private int _periodDays;
    [ObservableProperty] private bool _onlyWithImages;
    public string FilterSummary => (LanguageFilter == "all" ? "Все языки" : LanguageFilter == "ru" ? "Русский" : "Английский")
        + (PeriodDays == 0 ? " · всё время" : $" · {PeriodDays} дн.")
        + (OnlyWithImages ? " · с фото" : string.Empty);

    public MainWindowViewModel() : this(new NewsRepository(), new NewsService()) { }

    public MainWindowViewModel(NewsRepository repository, NewsService service)
    {
        _repository = repository;
        _service = service;
        _selectedCountry = Countries[0];
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            ApplyArticleFilter();
        };
        _refreshTimer = new System.Timers.Timer(TimeSpan.FromHours(1).TotalMilliseconds) { AutoReset = true };
        _refreshTimer.Elapsed += OnTimerElapsed;
        _refreshTimer.Start();
        _ = InitializeAsync();
    }

    partial void OnSelectedCountryChanged(Country value) => _ = LoadCachedNewsAsync(value.Code);

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnLanguageFilterChanged(string value) { ApplyArticleFilter(); OnPropertyChanged(nameof(FilterSummary)); }
    partial void OnPeriodDaysChanged(int value) { ApplyArticleFilter(); OnPropertyChanged(nameof(FilterSummary)); }
    partial void OnOnlyWithImagesChanged(bool value) { ApplyArticleFilter(); OnPropertyChanged(nameof(FilterSummary)); }

    private void ApplyArticleFilter()
    {
        var query = SearchText.Trim();
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _loadedArticles
            : _loadedArticles.Where(x =>
                x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.CountryName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (LanguageFilter == "ru") filtered = filtered.Where(x => string.Equals(x.Language, "ru", StringComparison.OrdinalIgnoreCase)).ToList();
        if (LanguageFilter == "en") filtered = filtered.Where(x => x.Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)).ToList();
        if (PeriodDays > 0) filtered = filtered.Where(x => x.PublishedAt >= DateTimeOffset.UtcNow.AddDays(-PeriodDays)).ToList();
        if (OnlyWithImages) filtered = filtered.Where(x => !string.IsNullOrWhiteSpace(x.ImageUrl)).ToList();
        filtered = filtered.OrderByDescending(x => x.PublishedAt).ToList();

        Articles.Clear();
        foreach (var article in filtered) Articles.Add(article);
    }

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
    private void SetLanguageFilter(string value) => LanguageFilter = value;

    [RelayCommand]
    private void SetPeriodFilter(string value) => PeriodDays = int.TryParse(value, out var days) ? days : 0;

    [RelayCommand]
    private void ToggleImagesFilter() => OnlyWithImages = !OnlyWithImages;

    [RelayCommand]
    private void ResetFilters()
    {
        SearchText = string.Empty;
        LanguageFilter = "all";
        PeriodDays = 0;
        OnlyWithImages = false;
    }

    [RelayCommand]
    private void IncreaseArticleFont() => ArticleFontSize = Math.Min(24, ArticleFontSize + 1);

    [RelayCommand]
    private void DecreaseArticleFont() => ArticleFontSize = Math.Max(14, ArticleFontSize - 1);

    [RelayCommand]
    private void ToggleOriginalSummary() => ShowOriginalSummary = !ShowOriginalSummary;

    [RelayCommand]
    private async Task LoadCacheAsync()
    {
        await LoadCachedNewsAsync(SelectedCountry.Code);
        await SetUiAsync(() => StatusText = Articles.Count == 0
            ? "В базе нет новостей для выбранного раздела"
            : $"Показано из базы: {Articles.Count} материалов");
    }

    private async Task RefreshInternalAsync(bool manual, bool reloadCurrentCountry = true)
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
                    if (manual)
                        await SetUiAsync(() => StatusText = $"Ручное обновление доступно после {lastSync.Value.Add(MinimumManualInterval).ToLocalTime():HH:mm}");
                    return;
                }
            }
            if (manual || reloadCurrentCountry)
                await SetUiAsync(() => { IsRefreshing = true; StatusText = "Новости обновляются"; });
            var fetchedNews = await _service.FetchNewsAsync(_lifetime.Token);
            var news = fetchedNews
                .Where(x => !string.IsNullOrWhiteSpace(x.Description))
                .ToList();
            await _service.TranslateForeignNewsAsync(news, _lifetime.Token);
            var removed = await Task.Run(() => _repository.DeleteNewsOlderThan(TimeSpan.FromDays(30)), _lifetime.Token);
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
            if (reloadCurrentCountry) await LoadCachedNewsAsync(SelectedCountry.Code);
            if (reloadCurrentCountry)
                await SetUiAsync(() => StatusText = news.Count == 0
                    ? _service.LastApiError ?? "API не вернул новости · проверь фильтры и ключ в .env"
                    : $"Обновлено: {DateTime.Now:HH:mm} · {news.Count} материалов");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Debug.WriteLine($"News refresh failed: {exception}");
            if (reloadCurrentCountry)
                await SetUiAsync(() => StatusText = "Нет соединения · показываем последние сохранённые новости");
            if (reloadCurrentCountry) await LoadCachedNewsAsync(SelectedCountry.Code);
        }
        finally
        {
            if (manual || reloadCurrentCountry) await SetUiAsync(() => IsRefreshing = false);
            _refreshGate.Release();
        }
    }

    [RelayCommand]
    private async Task OpenArticleAsync(NewsArticle article)
    {
        _articleReadCancellation?.Cancel();
        _articleReadCancellation?.Dispose();
        _articleReadCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var articleToken = _articleReadCancellation.Token;
        var readVersion = ++_articleReadVersion;
        OpenedArticle = article;
        OpenedArticleText = article.Description;
        OpenedArticleParagraphs.Clear();
        if (!string.IsNullOrWhiteSpace(article.Description)) OpenedArticleParagraphs.Add(article.Description);
        IsReadingArticle = true;
        IsArticleLoading = true;
        try
        {
            var content = await _service.DownloadArticleAsync(article.Url, articleToken);
            if (content is not null && readVersion == _articleReadVersion && ReferenceEquals(OpenedArticle, article))
            {
                OpenedArticleParagraphs.Clear();
                foreach (var paragraph in content.Paragraphs) OpenedArticleParagraphs.Add(paragraph);
            }
        }
        catch (OperationCanceledException) when (articleToken.IsCancellationRequested) { }
        catch (Exception exception) { Debug.WriteLine($"Read article failed: {exception}"); }
        finally
        {
            if (readVersion == _articleReadVersion) IsArticleLoading = false;
        }
    }

    [RelayCommand]
    private void CloseArticle()
    {
        _articleReadCancellation?.Cancel();
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
            if (Uri.TryCreate(article.Url, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception exception) { Debug.WriteLine($"Open article in browser failed: {exception}"); }
    }

    private async void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try { await RefreshInternalAsync(false, false); }
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
            var items = await Task.Run(() => _repository.GetNewsByCountry(countryCode, NewsPageSize, 0), _lifetime.Token);
            _newsOffset = items.Count;
            _hasMoreNews = items.Count == NewsPageSize;
            await SetUiAsync(() =>
            {
                _loadedArticles = items;
                ApplyArticleFilter();
                if (!IsRefreshing && items.Count == 0)
                    StatusText = "пока что нет новостей для этой страны";
            });
            _ = LoadImagesAsync(items);
            _ = TranslateCachedNewsAsync(items);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Debug.WriteLine($"Cache read failed: {exception}");
            await SetUiAsync(() => StatusText = "Не удалось прочитать локальный кэш новостей");
        }
    }

    private async Task TranslateCachedNewsAsync(IReadOnlyList<NewsArticle> items)
    {
        try
        {
            var foreign = items.Where(x => !string.Equals(x.Language, "ru", StringComparison.OrdinalIgnoreCase)
                && (string.Equals(x.OriginalTitle, x.Title, StringComparison.Ordinal)
                    || string.Equals(x.OriginalDescription, x.Description, StringComparison.Ordinal))).ToList();
            if (foreign.Count == 0) return;
            await _service.TranslateForeignNewsAsync(foreign, _lifetime.Token);
            await Task.Run(() => _repository.SaveNews(foreign), _lifetime.Token);
            await SetUiAsync(ApplyArticleFilter);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Debug.WriteLine($"Cached translation failed: {exception}"); }
    }

    public async Task LoadMoreNewsAsync()
    {
        if (SelectedCountry.Code != "WORLD" || !_hasMoreNews || IsLoadingMore) return;
        IsLoadingMore = true;
        try
        {
            var page = await Task.Run(() => _repository.GetNewsByCountry("WORLD", NewsPageSize, _newsOffset), _lifetime.Token);
            _newsOffset += page.Count;
            _hasMoreNews = page.Count == NewsPageSize;
            if (page.Count == 0) return;
            var combined = _loadedArticles.Concat(page).ToList();
            await SetUiAsync(() =>
            {
                _loadedArticles = combined;
                ApplyArticleFilter();
            });
            _ = LoadImagesAsync(page);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            IsLoadingMore = false;
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
        _searchTimer.Stop();
        _lifetime.Cancel();
        _lifetime.Dispose();
        _refreshGate.Dispose();
    }
}
