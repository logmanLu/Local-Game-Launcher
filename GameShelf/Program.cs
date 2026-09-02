using System.Security.Cryptography;
using System.Text;

namespace GameShelf;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var paths = AppPaths.FromExecutable();
        AppLog.Initialize(paths);
        using var instance = AcquireSingleInstance(paths, args);
        if (instance is null) return;
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
        finally
        {
            AppLog.Information("Application", "GameShelf stopped.");
            try { instance.ReleaseMutex(); } catch (ApplicationException) { /* Never acquired more than once. */ }
        }
    }

    private static Mutex? AcquireSingleInstance(AppPaths paths, IEnumerable<string> arguments)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths.Root));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedRoot)));
        var mutex = new Mutex(false, @"Global\GameShelf-" + digest[..24]);
        var isHandoff = arguments.Any(argument => string.Equals(argument, "--launcher-handoff", StringComparison.OrdinalIgnoreCase));
        try
        {
            // A version handoff launches its successor before this process has
            // released the lock. It waits briefly for that orderly shutdown;
            // an ordinary second launch never waits and is rejected at once.
            if (!mutex.WaitOne(isHandoff ? TimeSpan.FromSeconds(8) : TimeSpan.Zero))
            {
                AppLog.Warning("Application", "Rejected a second GameShelf instance for the same application folder.");
                MessageBox.Show("GameShelf is already running.", "GameShelf", MessageBoxButtons.OK, MessageBoxIcon.Information);
                mutex.Dispose();
                return null;
            }
            return mutex;
        }
        catch (AbandonedMutexException)
        {
            AppLog.Warning("Application", "Recovered the GameShelf instance lock after an unexpected previous shutdown.");
            return mutex;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }
    }
}
