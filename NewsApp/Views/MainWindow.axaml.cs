using Avalonia;
using Avalonia.Controls;
using NewsApp.ViewModels;

namespace NewsApp.Views;

public partial class MainWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += (_, _) =>
        {
            Globe.StopAutoRotation();
            (DataContext as IDisposable)?.Dispose();
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            Globe.CountrySelected -= OnCountrySelected;
            _viewModel.NewCountriesChanged -= OnNewCountriesChanged;
        }
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null)
        {
            Globe.CountrySelected += OnCountrySelected;
            _viewModel.NewCountriesChanged += OnNewCountriesChanged;
        }
    }

    private void OnCountrySelected(object? sender, string countryCode) =>
        _viewModel?.SelectCountryCode(countryCode, Globe.SelectedCountryName);

    private void OnNewCountriesChanged(object? sender, IReadOnlySet<string> codes) => Globe.MarkNewCountries(codes);

    private void OnAutoRotationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Globe.ToggleAutoRotation();
        AutoRotationButton.Content = Globe.IsAutoRotating ? "■" : "▶";
    }

    private void OnArticleScrollToTop(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ArticleScrollViewer.Offset = new Vector(0, 0);
    }
}
