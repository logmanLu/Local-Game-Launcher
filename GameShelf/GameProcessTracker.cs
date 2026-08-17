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

    public event EventHandler<GameProcessStateChangedEventArgs>? StateChanged;

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

    public bool IsRunning(int gameId)
    {
        lock (_gate)
        {
            if (!_processes.TryGetValue(gameId, out var process)) return false;
            try { return !process.HasExited; }
            catch { _processes.Remove(gameId); return false; }
        }
    }

    /// <summary>One-shot native Windows query used when a detail page is opened; never a polling loop.</summary>
    public bool RefreshGameState(GameEntry game)
    {
        if (IsRunning(game.Id)) return true;
        var expectedPath = _store.ResolveGamePath(game.GamePath);
        if (string.IsNullOrWhiteSpace(expectedPath)) return false;
        var process = FindRunningProcessByImagePath(expectedPath);
        if (process is null) return false;
        Attach(game.Id, process);
        return IsRunning(game.Id);
    }

    public void TrackLaunchedProcess(int gameId, bool directGameExecutable, Process? process)
    {
        if (process is null) return;
        try
        {
            if (directGameExecutable) { Attach(gameId, process, mayLaunchChild: true); return; }
            var target = _store.ResolveGamePath(_store.GetGame(gameId).GamePath);
            if (PathEquals(ProcessPath(process), target)) Attach(gameId, process);
            else process.Dispose(); // A region launcher; its target is caught by the WMI start event.
        }
        catch { process.Dispose(); }
    }

    public void RequestStop(int gameId)
    {
        Process? process;
        lock (_gate) _processes.TryGetValue(gameId, out process);
        if (process is null || HasExited(process)) return;
        try
        {
            // This posts WM_CLOSE to a graphical game. State returns to Play only after its real exit event.
            if (!process.CloseMainWindow()) throw new InvalidOperationException("The game does not currently expose a window that can receive a close request.");
            AppLog.Information("ProcessTracker", $"Requested normal close for game {gameId}.");
        }
        catch (Exception ex)
        {
            AppLog.Error("ProcessTracker", $"Could not request stop for game {gameId}.", ex);
            throw new InvalidOperationException("Could not request the game to stop: " + ex.Message, ex);
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

    private void Attach(int gameId, Process process, bool mayLaunchChild = false)
    {
        if (HasExited(process)) { process.Dispose(); return; }
        Process? previous = null;
        lock (_gate)
        {
            if (_processes.TryGetValue(gameId, out previous) && !HasExited(previous) && previous.Id == process.Id) { process.Dispose(); return; }
            _processes[gameId] = process;
        }
        if (previous is not null && !ReferenceEquals(previous, process)) previous.Dispose();
        try
        {
            process.Exited += (_, _) =>
            {
                if (mayLaunchChild && TryAdoptChildProcess(gameId, process.Id)) return;
                Detached(gameId, process);
            };
            Remember(gameId, process);
            process.EnableRaisingEvents = true;
            StateChanged?.Invoke(this, new GameProcessStateChangedEventArgs(gameId, true));
            if (mayLaunchChild) ObserveChildProcess(gameId, process.Id);
            if (process.HasExited) Detached(gameId, process);
        }
        catch (Exception ex) { _store.Log("Could not attach game process " + gameId + ": " + ex.Message); Detached(gameId, process); }
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
        if (changed) StateChanged?.Invoke(this, new GameProcessStateChangedEventArgs(gameId, false));
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
            await Task.Delay(450);
            TryAdoptChildProcess(gameId, parentProcessId);
        }
        catch (Exception ex) { _store.Log("Could not inspect game child process " + gameId + ": " + ex.Message); }
    }
    private bool TryAdoptChildProcess(int gameId, int parentProcessId)
    {
        if (_disposed) return false;
        var candidates = DescendantProcessIds(parentProcessId)
            .Select(id => { try { return Process.GetProcessById(id); } catch { return null; } })
            .Where(process => process is not null && !HasExited(process!))
            .Cast<Process>()
            .OrderByDescending(ProcessHasWindow)
            .ToList();
        if (candidates.Count == 0) return false;
        var adopted = candidates[0];
        foreach (var candidate in candidates.Skip(1)) candidate.Dispose();
        Attach(gameId, adopted);
        return true;
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
        catch { return ""; }
    }
    private static bool HasExited(Process process) { try { return process.HasExited; } catch { return true; } }
    private static bool ProcessHasWindow(Process process) { try { return process.MainWindowHandle != IntPtr.Zero; } catch { return false; } }
    private static bool PathEquals(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }
    private static Process? FindRunningProcessByImagePath(string expectedPath)
    {
        foreach (var processId in AllProcessIds())
        {
            var handle = OpenProcess(0x1000, false, (uint)processId); // PROCESS_QUERY_LIMITED_INFORMATION
            if (handle == IntPtr.Zero) continue;
            try
            {
                var size = 32768u; var path = new StringBuilder((int)size);
                if (QueryFullProcessImageName(handle, 0, path, ref size) && PathEquals(path.ToString(), expectedPath))
                    try { return Process.GetProcessById(processId); } catch (ArgumentException) { }
            }
            finally { CloseHandle(handle); }
        }
        return null;
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

    private static IEnumerable<int> AllProcessIds()
    {
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) yield break;
        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) yield break;
            do
            {
                yield return (int)entry.th32ProcessID;
                entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
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

public sealed class GameProcessStateChangedEventArgs(int gameId, bool isRunning) : EventArgs
{
    public int GameId { get; } = gameId;
    public bool IsRunning { get; } = isRunning;
}
