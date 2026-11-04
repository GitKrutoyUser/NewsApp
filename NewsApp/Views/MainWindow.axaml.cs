using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using NewsApp.ViewModels;

namespace NewsApp.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private readonly DispatcherTimer _refreshAnimationTimer;
    private readonly DispatcherTimer _paneAnimationTimer;
    private readonly DispatcherTimer _contentFadeTimer;
    private double _refreshAngle;
    private double _paneFromNews;
    private double _paneToNews;
    private DateTime _paneAnimationStarted;
    private DateTime _contentFadeStarted;

    public MainWindow()
    {
        InitializeComponent();
        LoadingGlobe.ToggleAutoRotation();
        RefreshIcon.RenderTransform = new RotateTransform(0);
        RefreshIcon.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _refreshAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _refreshAnimationTimer.Tick += (_, _) =>
        {
            _refreshAngle = (_refreshAngle - 12 + 360) % 360;
            ((RotateTransform)RefreshIcon.RenderTransform!).Angle = _refreshAngle;
        };
        _paneAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(28) };
        _paneAnimationTimer.Tick += OnPaneAnimationTick;
        _contentFadeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(24) };
        _contentFadeTimer.Tick += OnContentFadeTick;
        MainLayout.SizeChanged += (_, _) =>
        {
            if (!_paneAnimationTimer.IsEnabled)
                SetPaneLayout(_viewModel?.IsNewsPaneVisible != false, false);
        };
        DataContextChanged += OnDataContextChanged;
        Closed += (_, _) =>
        {
            Globe.StopAutoRotation();
            LoadingGlobe.StopAutoRotation();
            _refreshAnimationTimer.Stop();
            _paneAnimationTimer.Stop();
            _contentFadeTimer.Stop();
            (DataContext as IDisposable)?.Dispose();
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            Globe.CountrySelected -= OnCountrySelected;
            _viewModel.NewCountriesChanged -= OnNewCountriesChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            Globe.CountrySelected += OnCountrySelected;
            _viewModel.NewCountriesChanged += OnNewCountriesChanged;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateNewsPaneButton();
            NewsContent.IsVisible = _viewModel.IsNewsPaneVisible;
            SetPaneLayout(_viewModel.IsNewsPaneVisible, false);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.IsRefreshing))
        {
            if (_viewModel?.IsRefreshing == true) _refreshAnimationTimer.Start();
            else
            {
                _refreshAnimationTimer.Stop();
                _refreshAngle = 0;
                ((RotateTransform)RefreshIcon.RenderTransform!).Angle = 0;
            }
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsNewsPaneVisible))
        {
            _contentFadeTimer.Stop();
            NewsContent.IsVisible = false;
            NewsContent.Opacity = 0;
            UpdateNewsPaneButton();
            AnimateNewsPane(_viewModel?.IsNewsPaneVisible != false);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedCountry))
        {
            if (_viewModel?.SelectedCountry.Code == "MOON") Globe.FocusMoon();
            else Globe.FocusEarth();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.IsLoading) && _viewModel?.IsLoading == false)
        {
            LoadingGlobe.StopAutoRotation();
        }
    }

    private void UpdateNewsPaneButton()
    {
        NewsPaneToggleButton.Content = CreateIcon(_viewModel?.IsNewsPaneVisible == false
            ? "M 7,2 L 18,12 L 7,22 L 4,19 L 12,12 L 4,5 Z"
            : "M 17,2 L 6,12 L 17,22 L 20,19 L 12,12 L 20,5 Z");
    }

    private void AnimateNewsPane(bool visible)
    {
        if (MainLayout.Bounds.Width <= 0) return;
        var available = Math.Max(0, MainLayout.Bounds.Width);
        _paneFromNews = NewsPanel.Bounds.Width;
        if (_paneFromNews <= 1) _paneFromNews = visible ? ExpandedNewsWidth() : 54;
        _paneToNews = visible ? ExpandedNewsWidth() : 54;
        UpdateGlobeCenter(_paneToNews);
        _paneAnimationStarted = DateTime.UtcNow;
        _paneAnimationTimer.Start();
    }

    private void OnPaneAnimationTick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp((DateTime.UtcNow - _paneAnimationStarted).TotalMilliseconds / 260d, 0, 1);
        var eased = progress * progress * (3 - 2 * progress);
        var newsWidth = _paneFromNews + (_paneToNews - _paneFromNews) * eased;
        SetPaneWidths(newsWidth);
        if (progress >= 1)
        {
            _paneAnimationTimer.Stop();
            SetPaneLayout(_viewModel?.IsNewsPaneVisible != false, false);
            if (_viewModel?.IsNewsPaneVisible == true)
            {
                NewsContent.IsVisible = true;
                _contentFadeStarted = DateTime.UtcNow;
                _contentFadeTimer.Start();
            }
        }
    }

    private void OnContentFadeTick(object? sender, EventArgs e)
    {
        var progress = Math.Clamp((DateTime.UtcNow - _contentFadeStarted).TotalMilliseconds / 220d, 0, 1);
        NewsContent.Opacity = progress * progress * (3 - 2 * progress);
        if (progress >= 1) _contentFadeTimer.Stop();
    }

    private void SetPaneWidths(double newsWidth)
    {
        var available = Math.Max(0, MainLayout.Bounds.Width);
        NewsPanel.Width = Math.Clamp(newsWidth, 54, Math.Max(54, available - 20));
    }

    private void UpdateGlobeCenter(double newsWidth)
    {
        var effectiveNewsWidth = Math.Max(0, newsWidth - 54);
        Globe.ViewCenterX = effectiveNewsWidth < 1
            ? MainLayout.Bounds.Width / 2
            : Math.Max(180, (MainLayout.Bounds.Width - effectiveNewsWidth - 26) / 2);
        Globe.InvalidateVisual();
    }

    private double ExpandedNewsWidth() =>
        Math.Max(480, Math.Round(MainLayout.Bounds.Width * 0.50));

    private void SetPaneLayout(bool visible, bool animate)
    {
        if (animate)
        {
            AnimateNewsPane(visible);
            return;
        }
        if (visible)
        {
            NewsPanel.Width = ExpandedNewsWidth();
            UpdateGlobeCenter(NewsPanel.Width);
        }
        else
        {
            NewsPanel.Width = 54;
            UpdateGlobeCenter(0);
        }
    }

    private void OnCountrySelected(object? sender, string countryCode) =>
        _viewModel?.SelectCountryCode(countryCode, Globe.SelectedCountryName);

    private void OnNewCountriesChanged(object? sender, IReadOnlySet<string> codes) => Globe.MarkNewCountries(codes);

    private void OnAutoRotationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Globe.ToggleAutoRotation();
        AutoRotationButton.Content = CreateIcon(Globe.IsAutoRotating
            ? "M 5,5 H 19 V 19 H 5 Z"
            : "M 6,3 L 20,12 L 6,21 Z");
    }

    private static PathIcon CreateIcon(string data) => new()
    {
        Data = StreamGeometry.Parse(data),
        Width = 20,
        Height = 20,
        Foreground = Brushes.White
    };

    private void OnArticleScrollToTop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ArticleScrollViewer.Offset = new Vector(0, 0);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F11) return;
        WindowState = WindowState == WindowState.FullScreen
            ? WindowState.Normal
            : WindowState.FullScreen;
        e.Handled = true;
    }
}
