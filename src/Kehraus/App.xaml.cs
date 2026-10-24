using System.Windows;
using System.Windows.Threading;

namespace Kehraus
{
    public partial class App : Application
    {
        // Unerwartete Fehler anzeigen statt das Programm wortlos zu beenden
        private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show("Unerwarteter Fehler:\n\n" + e.Exception.Message, "Kehraus",
                            MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }
    }
}
