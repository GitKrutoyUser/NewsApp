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
    private double _refreshAngle;

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
        DataContextChanged += OnDataContextChanged;
        Closed += (_, _) =>
        {
            Globe.StopAutoRotation();
            LoadingGlobe.StopAutoRotation();
            _refreshAnimationTimer.Stop();
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
            UpdateNewsPaneButton();
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

    private void OnCountrySelected(object? sender, string countryCode) =>
        _viewModel?.SelectCountryCode(countryCode, Globe.SelectedCountryName);

    private void OnNewCountriesChanged(object? sender, IReadOnlySet<string> codes) => Globe.MarkNewCountries(codes);

    private void OnAutoRotationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Globe.ToggleAutoRotation();
        AutoRotationButton.Content = Globe.IsAutoRotating ? "■" : "↻";
    }

    private void OnArticleScrollToTop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ArticleScrollViewer.Offset = new Vector(0, 0);
    }
}
