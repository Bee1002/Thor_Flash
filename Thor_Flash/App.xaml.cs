using System.IO;
using System.Windows;
using System.Windows.Threading;
using Protocol.Thor.Library.Communication;

namespace Thor_Flash;

public partial class App : Application {
    protected override void OnStartup(StartupEventArgs e) {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogFatal((Exception)args.ExceptionObject, "UnhandledException");
        DispatcherUnhandledException += (_, args) => {
            LogFatal(args.Exception, "DispatcherUnhandledException");
            args.Handled = true;
            MessageBox.Show(
                args.Exception.Message,
                "Thor_Flash Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        };
        TaskScheduler.UnobservedTaskException += (_, args) => {
            LogFatal(args.Exception, "UnobservedTaskException");
            args.SetObserved();
        };

        try {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        } catch (Exception ex) {
            LogFatal(ex, "Startup");
            MessageBox.Show(
                ex.Message + "\n\n" + ex.StackTrace,
                "Could not start Thor_Flash",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e) {
        try {
            USB.Shutdown();
        } catch {
            /* ignorar al cerrar */
        }
        base.OnExit(e);
    }

    private static void LogFatal(Exception ex, string source) {
        try {
            var path = Path.Combine(AppContext.BaseDirectory, "odin_crash.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:O}] {source}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        } catch {
            /* ignorar */
        }
    }
}
