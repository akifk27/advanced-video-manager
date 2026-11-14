using System.Configuration;
using System.Data;
using System.Windows;

namespace avmsoftware
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AvmLanguage.Initialize();
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            if (!TermsAgreement.Confirm())
            {
                Shutdown();
                return;
            }

            var window = new MainWindow();
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            if (Array.IndexOf(e.Args, "--updated") >= 0)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (window.IsLoaded)
                        MessageBox.Show(window, AvmLanguage.Text("Güncelleme tamamlandı.", "Update completed."), window.Title);
                }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

}


