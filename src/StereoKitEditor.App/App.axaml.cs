using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace StereoKitEditor.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            try
            {
                desktop.MainWindow = new MainWindow();
            }
            catch (Exception exception) when (exception is FileNotFoundException
                                               or InvalidDataException
                                               or System.Text.Json.JsonException)
            {
                desktop.MainWindow = new ProjectLauncherWindow(exception.Message);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
