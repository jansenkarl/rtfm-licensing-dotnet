// A WPF sample for the RTFM.Licensing package: one window that shows the license status and lets the customer enter
// a key, start a trial, check now or deactivate. Put the App integration file from the License Manager next to the
// project as app-integration.json, then: dotnet run --project samples/WpfSample
using System.IO;
using System.Windows;
using RTFM.Licensing;

namespace WpfSample;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        RTFMLicense license;
        try { license = RTFMLicense.ForApp("RTFM Sample", "WPF Sample"); }
        catch (Exception error) when (error is FileNotFoundException or VerificationException)
        {
            MessageBox.Show(error.Message, "RTFM WPF Sample", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        // Created here, on the UI thread, so StatusChanged arrives on the UI thread.
        var window = new MainWindow(license);
        window.Closed += (_, _) => license.Dispose();
        window.Show();
    }
}
