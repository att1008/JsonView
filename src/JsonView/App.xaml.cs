using System.Windows;

namespace JsonView
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (MainWindow == null) new MainWindow().Show();
        }
    }
}
