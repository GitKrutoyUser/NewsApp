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
    private readonly NewsRepository _repository;
    private readonly NewsService _service;
    private readonly System.Timers.Timer _refreshTimer;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();

    public ObservableCollection<Country> Countries { get; } = new(new[]
    {
        new Country("WORLD", "Мир", "🌐"), new Country("US", "США", "🇺🇸"), new Country("RU", "Россия", "🇷🇺"),
        new Country("DE", "Германия", "🇩🇪"), new Country("FR", "Франция", "🇫🇷"), new Country("GB", "Великобритания", "🇬🇧"),
        new Country("CN", "Китай", "🇨🇳"), new Country("JP", "Япония", "🇯🇵"), new Country("IN", "Индия", "🇮🇳"),
        new Country("BR", "Бразилия", "🇧🇷"), new Country("CA", "Канада", "🇨🇦"), new Country("AU", "Австралия", "🇦🇺"),
        new Country("IT", "Италия", "🇮🇹"), new Country("ES", "Испания", "🇪🇸"), new Country("TR", "Турция", "🇹🇷"),
        new Country("KR", "Южная Корея", "🇰🇷"), new Country("MX", "Мексика", "🇲🇽"), new Country("PL", "Польша", "🇵🇱"),
        new Country("NL", "Нидерланды", "🇳🇱"), new Country("SE", "Швеция", "🇸🇪"), new Country("CH", "Швейцария", "🇨🇭"),
        new Country("IL", "Израиль", "🇮🇱"), new Country("UA", "Украина", "🇺🇦"), new Country("ZA", "ЮАР", "🇿🇦")
    });

    public ObservableCollection<NewsArticle> Articles { get; } = new();

    [ObservableProperty] private Country _selectedCountry;
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _statusText = "Проверяем локальный кэш…";

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
            country = new Country(code, string.IsNullOrWhiteSpace(countryName) ? code : countryName, "🌐");
            Countries.Add(country);
        }
        SelectedCountry = country;
    }

    [RelayCommand]
    private void SelectCountry(Country country) => SelectedCountry = country;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            await SetUiAsync(() => { IsRefreshing = true; StatusText = "Обновляем мировую ленту…"; });
            var news = await _service.FetchNewsAsync(_lifetime.Token);
            await Task.Run(() => _repository.SaveNews(news), _lifetime.Token);
            await LoadCachedNewsAsync(SelectedCountry.Code);
            await SetUiAsync(() => StatusText = $"Обновлено: {DateTime.Now:HH:mm} · {news.Count} материалов");
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
    private void OpenArticle(NewsArticle article)
    {
        try
        {
            if (Uri.TryCreate(article.Url, UriKind.Absolute, out var uri))
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        }
        catch (Exception exception) { Debug.WriteLine($"Open article failed: {exception}"); }
    }

    private async void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        try { await RefreshAsync(); }
        catch (Exception exception) { Debug.WriteLine($"Timer refresh failed: {exception}"); }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await LoadCachedNewsAsync(SelectedCountry.Code);
            await RefreshAsync();
        }
        catch (Exception exception) { Debug.WriteLine($"Initialization failed: {exception}"); }
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
                    StatusText = "В кэше пока нет новостей для этой страны";
            });
            await LoadImagesAsync(items);
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
        await Parallel.ForEachAsync(items.Where(x => !string.IsNullOrWhiteSpace(x.ImageUrl)), new ParallelOptions
        {
            MaxDegreeOfParallelism = 6,
            CancellationToken = _lifetime.Token
        }, async (article, token) =>
        {
            var bitmap = await _service.DownloadImageAsync(article.ImageUrl, token);
            if (bitmap is not null)
                await SetUiAsync(() => article.ImageBitmap = bitmap);
        });
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
