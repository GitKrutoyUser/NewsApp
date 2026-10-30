using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using NewsApp.ViewModels;

namespace NewsApp.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;
    private readonly DispatcherTimer _refreshAnimationTimer;
    private readonly DispatcherTimer _paneAnimationTimer;
    private double _refreshAngle;
    private double _paneFromNews;
    private double _paneToNews;
    private DateTime _paneAnimationStarted;

    public MainWindow()
    {
        InitializeComponent();
        LoadingGlobe.ToggleAutoRotation();
        RefreshIcon.RenderTransform = new RotateTransform(0);
        RefreshIcon.RenderTransformOrigin = new RelativePoint(0.5, 0.5, RelativeUnit.Relative);
        _refreshAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(35) };
        _refreshAnimationTimer.Tick += (_, _) =>
        {
            _refreshAngle = (_refreshAngle + 12) % 360;
            ((RotateTransform)RefreshIcon.RenderTransform!).Angle = _refreshAngle;
        };
        _paneAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _paneAnimationTimer.Tick += OnPaneAnimationTick;
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
            UpdateNewsPaneButton();
            AnimateNewsPane(_viewModel?.IsNewsPaneVisible != false);
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.SelectedCountry))
        {
            if (_viewModel?.SelectedCountry.Code == "MOON") Globe.FocusMoon();
            else Globe.FocusEarth();
        }
    }

    private void UpdateNewsPaneButton()
    {
        NewsPaneToggleButton.Content = new TextBlock
        {
            Text = _viewModel?.IsNewsPaneVisible == false ? ">" : "<",
            FontSize = 22,
            FontWeight = FontWeight.SemiBold
        };
    }

    private void AnimateNewsPane(bool visible)
    {
        if (MainLayout.Bounds.Width <= 0) return;
        var available = Math.Max(0, MainLayout.Bounds.Width);
        _paneFromNews = NewsPanel.Bounds.Width;
        if (_paneFromNews <= 1) _paneFromNews = visible ? ExpandedNewsWidth() : 54;
        _paneToNews = visible ? ExpandedNewsWidth() : 54;
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
        }
    }

    private void SetPaneWidths(double newsWidth)
    {
        var available = Math.Max(0, MainLayout.Bounds.Width);
        NewsPanel.Width = Math.Clamp(newsWidth, 54, Math.Max(54, available - 20));
        UpdateGlobeCenter(newsWidth);
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
        Math.Clamp(MainLayout.Bounds.Width * 0.60, 600, 900);

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
        AutoRotationButton.Content = new TextBlock
        {
            Text = Globe.IsAutoRotating ? "■" : "▶",
            Classes = { "toolbarIcon" }
        };
    }

    private void OnArticleScrollToTop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ArticleScrollViewer.Offset = new Vector(0, 0);
    }
}
