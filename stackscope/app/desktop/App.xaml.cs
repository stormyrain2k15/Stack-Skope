using System.Windows;
using System.Windows.Threading;

namespace StackScope.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Software rendering opt-in (accessibility / low-interference mode)
        if (Environment.GetEnvironmentVariable("STACKSCOPE_SOFTWARE_RENDER") == "1")
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        DispatcherUnhandledException += (s, args) =>
        {
            MessageBox.Show(
                args.Exception.ToString(),
                "StackScope — unhandled exception",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        base.OnStartup(e);
    }
}
