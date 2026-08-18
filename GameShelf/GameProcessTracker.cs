using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace GameShelf;

/// <summary>
/// Tracks registered game executables through process start/exit events. The only
/// enumeration is the one-time recovery scan performed when GameShelf starts.
/// </summary>
public sealed class GameProcessTracker : IDisposable
{
    private readonly DataStore _store;
    private readonly object _gate = new();
    private readonly Dictionary<int, Process> _processes = [];
    private ManagementEventWatcher? _startWatcher;
    private bool _disposed;

    public GameProcessTracker(DataStore store) => _store = store;

    public void Start()
    {
        AppLog.Information("ProcessTracker", "Starting game process tracking.");
        RecoverExistingProcesses();
        try
        {
            _startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            _startWatcher.EventArrived += ProcessStarted;
            _startWatcher.Start();
            AppLog.Information("ProcessTracker", "Windows process-start event watcher is active.");
        }
        catch (Exception ex)
        {
            // Direct launches and recovered processes still use Process.Exited if WMI is unavailable.
            AppLog.Warning("ProcessTracker", "Process-start event watcher could not start; direct tracking remains available.", ex);
            _startWatcher?.Dispose();
            _startWatcher = null;
        }
    }

    public void TrackLaunchedProcess(int gameId, bool directGameExecutable, Process? process)
    {
        if (process is null) return;
        try
        {
            if (directGameExecutable)
            {
                AppLog.Information("ProcessTracker", $"Tracking directly launched game {gameId}: pid {process.Id}, '{ProcessPath(process)}'.");
                Attach(gameId, process, mayLaunchChild: true);
                return;
            }
            var target = _store.ResolveGamePath(_store.GetGame(gameId).GamePath);
            if (PathEquals(ProcessPath(process), target))
            {
                AppLog.Information("ProcessTracker", $"Region command started the game executable directly for game {gameId}: pid {process.Id}.");
                Attach(gameId, process);
            }
            else
            {
                // Region launchers such as Locale Emulator are parent processes.
                // WMI start notifications are optional and commonly denied, therefore
                // retain the parent and make bounded descendant checks after launch.
                AppLog.Information("ProcessTracker", $"Tracking region launcher for game {gameId}: pid {process.Id}, '{ProcessPath(process)}'; target '{target}'.");
                Attach(gameId, process, mayLaunchChild: true);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warning("ProcessTracker", $"Could not track launched process for game {gameId}.", ex);
            process.Dispose();
        }
    }

    private void RecoverExistingProcesses()
    {
        var recovered = new HashSet<int>();
        var staleEntries = false;
        foreach (var entry in _store.Data.Settings.RunningGameProcesses.ToArray())
        {
            try
            {
                var game = _store.Data.Games.FirstOrDefault(item => item.Id == entry.Key);
                if (game is null) { _store.Data.Settings.RunningGameProcesses.Remove(entry.Key); staleEntries = true; continue; }
                var process = Process.GetProcessById(entry.Value.ProcessId);
                if (HasExited(process) || process.StartTime.ToUniversalTime().Ticks != entry.Value.StartTimeUtcTicks)
                {
                    process.Dispose();
                    if (TryRecoverRelatedProcess(game, entry.Value.StartTimeUtcTicks)) recovered.Add(entry.Key);
                    else { _store.Data.Settings.RunningGameProcesses.Remove(entry.Key); staleEntries = true; }
                    continue;
                }
                Attach(entry.Key, process); recovered.Add(entry.Key);
            }
            catch
            {
                var game = _store.Data.Games.FirstOrDefault(item => item.Id == entry.Key);
                if (game is not null && TryRecoverRelatedProcess(game, entry.Value.StartTimeUtcTicks)) recovered.Add(entry.Key);
                else { _store.Data.Settings.RunningGameProcesses.Remove(entry.Key); staleEntries = true; }
            }
        }
        if (staleEntries) PersistTrackedProcesses();
        foreach (var game in _store.Data.Games)
        {
            if (recovered.Contains(game.Id)) continue;
            var target = _store.ResolveGamePath(game.GamePath);
            if (string.IsNullOrWhiteSpace(target)) continue;
            foreach (var process in ProcessesNamedLike(target))
            {
                if (PathEquals(ProcessPath(process), target)) { Attach(game.Id, process); break; }
                process.Dispose();
            }
        }
    }

    private void ProcessStarted(object sender, EventArrivedEventArgs args)
    {
        try
        {
            var processId = Convert.ToInt32(args.NewEvent.Properties["ProcessID"].Value);
            using var process = Process.GetProcessById(processId);
            var executable = ProcessPath(process);
            if (string.IsNullOrWhiteSpace(executable)) return;
            var game = _store.Data.Games.FirstOrDefault(item => PathEquals(_store.ResolveGamePath(item.GamePath), executable));
            if (game is not null) Attach(game.Id, Process.GetProcessById(processId));
        }
        catch (ArgumentException) { } // Process ended before the event was handled.
        catch (Exception ex) { _store.Log("Could not inspect a process-start event: " + ex.Message); }
    }

    private bool Attach(int gameId, Process process, bool mayLaunchChild = false)
    {
        if (HasExited(process)) { process.Dispose(); return false; }
        Process? previous = null;
        lock (_gate)
        {
            if (_processes.TryGetValue(gameId, out previous) && !HasExited(previous) && previous.Id == process.Id) { process.Dispose(); return true; }
            _processes[gameId] = process;
        }
        try
        {
            process.Exited += (_, _) =>
            {
                if (mayLaunchChild && TryAdoptChildProcess(gameId, process.Id)) return;
                Detached(gameId, process);
            };
            process.EnableRaisingEvents = true;
            Remember(gameId, process);
            if (previous is not null && !ReferenceEquals(previous, process)) previous.Dispose();
            if (mayLaunchChild) ObserveChildProcess(gameId, process.Id);
            if (process.HasExited) Detached(gameId, process);
            return true;
        }
        catch (Exception ex)
        {
            // Do not publish a false state transition here. In particular, an
            // inaccessible region-launcher child used to make the detail page
            // immediately query/attach again, causing a high-frequency rebuild.
            lock (_gate)
            {
                if (_processes.TryGetValue(gameId, out var tracked) && ReferenceEquals(tracked, process))
                {
                    if (previous is not null && !HasExited(previous)) _processes[gameId] = previous;
                    else _processes.Remove(gameId);
                }
            }
            process.Dispose();
            AppLog.Warning("ProcessTracker", $"Could not attach process {process.Id} for game {gameId}; keeping the prior tracker state.", ex);
            return false;
        }
    }

    private void Detached(int gameId, Process process)
    {
        var changed = false;
        lock (_gate)
        {
            if (_processes.TryGetValue(gameId, out var tracked) && ReferenceEquals(tracked, process)) { _processes.Remove(gameId); changed = true; }
        }
        process.Dispose();
        if (changed) Forget(gameId);
    }

    private void Remember(int gameId, Process process)
    {
        try
        {
            _store.Data.Settings.RunningGameProcesses[gameId] = new RunningGameProcess { ProcessId = process.Id, StartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks };
            PersistTrackedProcesses();
        }
        catch (Exception ex) { _store.Log("Could not persist tracked game process " + gameId + ": " + ex.Message); }
    }
    private void Forget(int gameId)
    {
        if (_store.Data.Settings.RunningGameProcesses.Remove(gameId)) PersistTrackedProcesses();
    }
    private void PersistTrackedProcesses()
    {
        try { _store.Save(); }
        catch (Exception ex) { _store.Log("Could not persist game process tracking: " + ex.Message); }
    }

    private async void ObserveChildProcess(int gameId, int parentProcessId)
    {
        try
        {
            // These are bounded launch-time checks, not periodic polling. Some
            // locale/region launchers create the game after their own setup phase.
            foreach (var delay in new[] { 450, 1500 })
            {
                await Task.Delay(delay);
                if (TryAdoptChildProcess(gameId, parentProcessId)) return;
            }
        }
        catch (Exception ex) { _store.Log("Could not inspect game child process " + gameId + ": " + ex.Message); }
    }
    private bool TryAdoptChildProcess(int gameId, int parentProcessId)
    {
        if (_disposed) return false;
        var target = _store.ResolveGamePath(_store.GetGame(gameId).GamePath);
        var candidates = DescendantProcessIds(parentProcessId)
            .Select(id => { try { return Process.GetProcessById(id); } catch { return null; } })
            .Where(process => process is not null && !HasExited(process!))
            .Cast<Process>()
            .ToList();
        if (candidates.Count == 0)
        {
            AppLog.Debug("ProcessTracker", $"No descendant candidate found yet for game {gameId} under launcher pid {parentProcessId}.");
            return false;
        }
        var matching = candidates.Where(process => PathEquals(ProcessPath(process), target)).OrderByDescending(ProcessHasWindow).ToList();
        foreach (var candidate in candidates.Except(matching)) candidate.Dispose();
        if (matching.Count == 0)
        {
            AppLog.Debug("ProcessTracker", $"Descendants found under launcher pid {parentProcessId}, but none matched game {gameId}'s registered executable yet.");
            return false;
        }
        var adopted = matching[0];
        foreach (var candidate in matching.Skip(1)) candidate.Dispose();
        AppLog.Information("ProcessTracker", $"Adopted launched child for game {gameId}: pid {adopted.Id}, '{ProcessPath(adopted)}'.");
        return Attach(gameId, adopted);
    }
    private bool TryRecoverRelatedProcess(GameEntry game, long launcherStartTicks)
    {
        var gameDirectory = Path.GetDirectoryName(_store.ResolveGamePath(game.GamePath));
        if (string.IsNullOrWhiteSpace(gameDirectory)) return false;
        var earliest = new DateTime(launcherStartTicks, DateTimeKind.Utc);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var path = ProcessPath(process);
                var started = process.StartTime.ToUniversalTime();
                if (!string.IsNullOrWhiteSpace(path) && Path.GetDirectoryName(path)?.StartsWith(gameDirectory, StringComparison.OrdinalIgnoreCase) == true && started >= earliest && started <= earliest.AddMinutes(10))
                {
                    Attach(game.Id, process); return true;
                }
            }
            catch { }
            process.Dispose();
        }
        return false;
    }

    private static IEnumerable<Process> ProcessesNamedLike(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        if (string.IsNullOrWhiteSpace(name)) return [];
        try { return Process.GetProcessesByName(name); }
        catch { return []; }
    }
    private static string ProcessPath(Process process)
    {
        try { return process.MainModule?.FileName ?? ""; }
        catch { return ProcessPath(process.Id); }
    }
    private static string ProcessPath(int processId)
    {
        var handle = OpenProcess(0x1000, false, (uint)processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle == IntPtr.Zero) return "";
        try
        {
            var size = 32768u; var path = new StringBuilder((int)size);
            return QueryFullProcessImageName(handle, 0, path, ref size) ? path.ToString() : "";
        }
        finally { CloseHandle(handle); }
    }
    private static bool HasExited(Process process) { try { return process.HasExited; } catch { return true; } }
    private static bool ProcessHasWindow(Process process) { try { return process.MainWindowHandle != IntPtr.Zero; } catch { return false; } }
    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        _disposed = true;
        if (_startWatcher is not null)
        {
            _startWatcher.EventArrived -= ProcessStarted;
            try { _startWatcher.Stop(); } catch { }
            _startWatcher.Dispose(); _startWatcher = null;
        }
        lock (_gate) { foreach (var process in _processes.Values) process.Dispose(); _processes.Clear(); }
    }

    private static IEnumerable<int> DescendantProcessIds(int rootProcessId)
    {
        var children = new Dictionary<int, List<int>>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) yield break;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) yield break;
            do
            {
                if (!children.TryGetValue((int)entry.th32ParentProcessID, out var list)) children[(int)entry.th32ParentProcessID] = list = [];
                list.Add((int)entry.th32ProcessID);
                entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        var pending = new Queue<int>(); pending.Enqueue(rootProcessId);
        while (pending.Count > 0)
        {
            var parent = pending.Dequeue();
            if (!children.TryGetValue(parent, out var list)) continue;
            foreach (var child in list) { yield return child; pending.Enqueue(child); }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct ProcessEntry32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder executablePath, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
