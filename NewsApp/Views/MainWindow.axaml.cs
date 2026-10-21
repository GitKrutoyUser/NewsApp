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
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null) Globe.CountrySelected -= OnCountrySelected;
        _viewModel = DataContext as MainWindowViewModel;
        if (_viewModel is not null) Globe.CountrySelected += OnCountrySelected;
    }

    private void OnCountrySelected(object? sender, string countryCode) =>
        _viewModel?.SelectCountryCode(countryCode, Globe.SelectedCountryName);
}
