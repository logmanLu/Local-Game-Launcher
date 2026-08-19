using System.Text;

namespace GameShelf;

/// <summary>
/// Lightweight, dependency-free, daily rolling application log. Logging must never
/// interrupt normal launcher use, including when the application directory is read-only.
/// </summary>
public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical,
    None
}

public static class AppLog
{
    private const int RetentionDays = 30;
    private static readonly object Gate = new();
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private static string? _directory;
    private static LogLevel _minimum = DefaultMinimumLevel();

    public static LogLevel MinimumLevel => _minimum;

    public static void Initialize(AppPaths paths)
    {
        _directory = paths.LogDirectory;
        _minimum = ReadMinimumLevel();
        try
        {
            Directory.CreateDirectory(_directory);
            DeleteExpiredFiles();
        }
        catch
        {
            // The rest of the application remains usable if logging cannot be initialized.
        }
    }

    public static void Trace(string component, string message) => Write(LogLevel.Trace, component, message);
    public static void Debug(string component, string message) => Write(LogLevel.Debug, component, message);
    public static void Information(string component, string message) => Write(LogLevel.Information, component, message);
    public static void Warning(string component, string message, Exception? exception = null) => Write(LogLevel.Warning, component, message, exception);
    public static void Error(string component, string message, Exception? exception = null) => Write(LogLevel.Error, component, message, exception);
    public static void Critical(string component, string message, Exception? exception = null) => Write(LogLevel.Critical, component, message, exception);

    public static void Write(LogLevel level, string component, string message, Exception? exception = null)
    {
        if (level < _minimum || _minimum == LogLevel.None || string.IsNullOrWhiteSpace(_directory)) return;
        try
        {
            var now = DateTimeOffset.Now;
            var entry = $"{now:O} [{level.ToString().ToUpperInvariant()}] {component}: {message}";
            if (exception is not null) entry += Environment.NewLine + exception;
            entry += Environment.NewLine;
            lock (Gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(Path.Combine(_directory, $"gameshelf-{now:yyyy-MM-dd}.log"), entry, Utf8NoBom);
            }
        }
        catch
        {
            // Never recurse or surface a logging failure to the user.
        }
    }

    private static void DeleteExpiredFiles()
    {
        if (string.IsNullOrWhiteSpace(_directory) || !Directory.Exists(_directory)) return;
        var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "gameshelf-*.log", SearchOption.TopDirectoryOnly))
        {
            try { if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file); }
            catch { /* A locked or protected old file is retried next startup. */ }
        }
    }

    private static LogLevel ReadMinimumLevel()
    {
        var configured = Environment.GetEnvironmentVariable("GAMESHELF_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(configured) && Enum.TryParse<LogLevel>(configured, true, out var level)) return level;
        return DefaultMinimumLevel();
    }

    private static LogLevel DefaultMinimumLevel()
    {
#if DEBUG
        return LogLevel.Debug;
#else
        // Alpha/beta packages intentionally retain Debug diagnostics even when
        // published with the Release compiler configuration. Stable versions use
        // the quieter Information threshold.
        var informationalVersion = typeof(AppLog).Assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "";
        return informationalVersion.Contains("a", StringComparison.OrdinalIgnoreCase) || informationalVersion.Contains("b", StringComparison.OrdinalIgnoreCase) ? LogLevel.Debug : LogLevel.Information;
#endif
    }
}
