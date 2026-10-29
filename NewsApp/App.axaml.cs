using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NewsApp.Data;
using NewsApp.Services;
using NewsApp.ViewModels;
using NewsApp.Views;

namespace NewsApp;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel(new NewsRepository(), new NewsService())
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
