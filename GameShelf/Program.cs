namespace GameShelf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var paths = AppPaths.FromExecutable();
        AppLog.Initialize(paths);
        Application.ThreadException += (_, eventArgs) => AppLog.Critical("UI", "Unhandled Windows Forms exception.", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) => AppLog.Critical("Runtime", "Unhandled application-domain exception.", eventArgs.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, eventArgs) => { AppLog.Error("Task", "Unobserved task exception.", eventArgs.Exception); eventArgs.SetObserved(); };
        try
        {
            AppLog.Information("Application", "Starting GameShelf.");
            using var store = new DataStore(paths);
            Application.Run(new MainForm(store));
        }
        catch (Exception ex)
        {
            AppLog.Critical("Application", "Startup or top-level application failure.", ex);
            MessageBox.Show(ex.Message, "GameShelf", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { AppLog.Information("Application", "GameShelf stopped."); }
    }
}
