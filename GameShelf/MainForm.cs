using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameShelf;

public sealed class MainForm : Form
{
    private sealed record LauncherChoice(string Path, int Major, int Minor, int Patch, string? Preview)
    {
        public bool IsPreview => !string.IsNullOrWhiteSpace(Preview);
        public int PreviewKindRank => Preview?.StartsWith("b", StringComparison.OrdinalIgnoreCase) == true ? 2 : 1;
        public int PreviewRevision => int.TryParse(Preview?[1..], out var revision) ? revision : 0;
        public string MinorLabel => $"{Major}.{Minor}";
        public string FullLabel => $"{Major}.{Minor}.{Patch}" + (Preview ?? "");
        // Stable releases are displayed only at major.minor granularity, but
        // selecting one pins the exact highest patch visible at that moment.
        // A later patch must never silently replace a user's explicit choice.
        public string Selection => "exact:" + FullLabel.ToLowerInvariant();
    }
    private readonly DataStore _store;
    private readonly PackageService _packages;
    private Localizer _t;
    private readonly BufferedFlowLayoutPanel _content = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(42) };
    private sealed record LibraryCardSnapshot(string PresentationKey, List<int> GameIds, List<string> GameFingerprints);
    private LibraryCardSnapshot? _libraryCardSnapshot;
    private int _cachedLibraryScrollY;
    private readonly Panel _top = new() { Dock = DockStyle.Top, Height = 108 };
    private VirtualGameCardPanel? _libraryCards;
    private readonly List<EventHandler> _topResizeHandlers = [];
    private int? _selectedId;
    private string _page = "library";
    private bool _management;
    private bool _fullScreen;
    private Rectangle _restoreBounds;
    private FormBorderStyle _restoreBorderStyle = FormBorderStyle.Sizable;
    private readonly Dictionary<int, DateTime> _playStatusClicks = [];
    private readonly Dictionary<int, DateTime> _gameStatusClicks = [];
    // Detail status controls are intentionally retained separately: changing a
    // status must repaint only its lamp, never reconstruct the detail page.
    private Panel? _detailPlayStatusBlock;
    private Panel? _detailGameStatusBlock;
    private sealed record StatusBlockState(StatusKind Kind, int Id);
    private readonly ToolTip _statusToolTip = new();
    private readonly Panel _resizeMask = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(27, 29, 30), Visible = false };
    private bool _interactiveResize;
    private bool _resizeRefreshQueued;
    // FormBorderStyle/WindowState changes emit Resize but not ResizeEnd. Suppress
    // resize handling around those programmatic transitions and refresh once after.
    private bool _suppressResizeLayout;
    private const int WmCommand = 0x0111;
    private const uint MfString = 0x0000, MfPopup = 0x0010, MfChecked = 0x0008;
    private const int LauncherAutoLatestCommand = 0x7000, LauncherAutoStableCommand = 0x7001, LauncherUpdateCommand = 0x7002, LauncherCommandBase = 0x7010, LanguageCommandBase = 0x7200;
    private const int WmEnterMenuLoop = 0x0211, WmExitMenuLoop = 0x0212;
    private readonly Dictionary<int, Action> _nativeMenuCommands = [];
    private readonly System.Windows.Forms.Timer _nativeMenuHoverTimer = new() { Interval = 120 };
    private IntPtr _nativeMenu;
    private bool _nativeMenuBuilt;
    private readonly List<LauncherChoice> _discoveredLaunchers = [];
    private LauncherChoice? _availableLauncherUpdate;
    private bool _launcherScanStarted;
    private bool _nativeMenuVisible;
    private bool _nativeMenuInLoop;
    private bool _launcherHandoffInProgress;

    public MainForm(DataStore store)
    {
        _store = store; _packages = new PackageService(store); _t = new Localizer(store.Data.Settings.Language);
        Text = "GameShelf"; Font = new Font("Segoe UI", 14f, FontStyle.Bold); MinimumSize = new Size(720, 405); KeyPreview = true; StartPosition = FormStartPosition.Manual; FormBorderStyle = FormBorderStyle.Sizable;
        // Defer fullscreen until Load has established whether this executable is
        // the selected launcher version. Otherwise a fixed Launcher.exe can
        // briefly enter fullscreen before handing off to its versioned target.
        RestoreWindow(restoreWindowState: false); Controls.Add(_content); Controls.Add(_top); Controls.Add(_resizeMask); ApplyTheme();
        _selectedId = store.Data.Settings.SelectedGameId;
        _management = false;
        if (_selectedId is not null && store.Data.Games.Any(game => game.Id == _selectedId) && store.Data.Settings.Page is "detail" or "edit") ShowDetail(); else { _selectedId = null; ShowLibrary(); }
        KeyDown += HandleKeys; FormClosing += (_, _) => PersistWindow(); FormClosed += (_, _) => { _statusToolTip.Dispose(); _nativeMenuHoverTimer.Stop(); _nativeMenuHoverTimer.Dispose(); DetachNativeMenu(); };
        _nativeMenuHoverTimer.Tick += (_, _) => UpdateNativeMenuVisibility();
        // Resolve a launcher policy before the first paint. Doing this from
        // Shown made the fixed Launcher.exe briefly appear before it handed
        // off to the selected versioned executable.
        Load += (_, _) =>
        {
            if (ApplyLauncherSelection()) return;
            RestorePersistedWindowState();
            RememberCurrentLauncherVersion();
        };
        Shown += (_, _) =>
        {
            BuildNativeMenu();
            _nativeMenuHoverTimer.Start();
            ScheduleLauncherScan();
            if (!_fullScreen) return;
            // RestoreWindow runs before the native form handle exists, so its
            // fullscreen transition cannot queue the usual post-transition refresh.
            RefreshResponsiveLayout();
            EndResizeMask();
            AppLog.Debug("UI", $"Restored fullscreen on first show; refreshed '{_page}' once at {ClientSize.Width}x{ClientSize.Height}.");
        };
        ResizeBegin += (_, _) => BeginInteractiveResize();
        ResizeEnd += (_, _) => EndInteractiveResize();
        Resize += (_, _) => QueueResponsiveLayout();
        _content.Scroll += (_, _) => RefreshLibraryCardViewport();
    }

    private void RestoreWindow(bool restoreWindowState = true)
    {
        var s = _store.Data.Settings;
        var screen = Screen.AllScreens.FirstOrDefault(x => x.WorkingArea.IntersectsWith(new Rectangle(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight)));
        Bounds = screen is null ? new Rectangle((Screen.PrimaryScreen!.WorkingArea.Width - 1280) / 2, (Screen.PrimaryScreen.WorkingArea.Height - 720) / 2, 1280, 720) : new Rectangle(s.WindowX, s.WindowY, s.WindowWidth, s.WindowHeight);
        if (restoreWindowState) RestorePersistedWindowState();
    }
    private void RestorePersistedWindowState()
    {
        if (_store.Data.Settings.IsFullscreen) ToggleFullscreen();
        else if (_store.Data.Settings.IsMaximized) WindowState = FormWindowState.Maximized;
    }

    /// <summary>
    /// Adds a traditional Windows menu bar below the native caption.  The
    /// caption remains an ordinary Windows non-client title bar, so system
    /// controls, snapping, resizing, and the system menu stay untouched.
    /// </summary>
    private void BuildNativeMenu()
    {
        if (_nativeMenuBuilt || !IsHandleCreated) return;
        var menu = CreateMenu();
        var launcherMenu = CreatePopupMenu();
        var languageMenu = CreatePopupMenu();
        if (menu == IntPtr.Zero || launcherMenu == IntPtr.Zero || languageMenu == IntPtr.Zero) return;
        _nativeMenuCommands.Clear();
        var current = Path.GetFullPath(Application.ExecutablePath);
        // Do not enumerate files while the window is starting or when the menu
        // opens. The background scan updates this cache after the first frame.
        var launchers = VersionMenuLaunchers(_discoveredLaunchers);
        var currentChoice = CurrentLauncherChoice();
        if (currentChoice is not null && launchers.All(item => !SameLauncher(item, currentChoice))) launchers.Insert(0, currentChoice);
        var lastChoice = LastUsedLauncherChoice();
        if (lastChoice is not null && launchers.All(item => !SameLauncher(item, lastChoice))) launchers.Insert(0, lastChoice);
        var selectedPolicy = _store.Data.Settings.LauncherSelection;
        AppendMenu(launcherMenu, MfString | (selectedPolicy == "auto-latest" ? MfChecked : 0), LauncherAutoLatestCommand, "Automatically select latest version");
        _nativeMenuCommands[LauncherAutoLatestCommand] = () => SelectLauncherPolicy("auto-latest");
        AppendMenu(launcherMenu, MfString | (selectedPolicy == "auto-stable" ? MfChecked : 0), LauncherAutoStableCommand, "Automatically select latest stable version");
        _nativeMenuCommands[LauncherAutoStableCommand] = () => SelectLauncherPolicy("auto-stable");
        AppendMenu(launcherMenu, 0x0800, 0, null);
        if (_availableLauncherUpdate is not null)
        {
            AppendMenu(launcherMenu, MfString, LauncherUpdateCommand, "Update to " + _availableLauncherUpdate.FullLabel);
            _nativeMenuCommands[LauncherUpdateCommand] = InstallAvailableLauncherUpdate;
            AppendMenu(launcherMenu, 0x0800, 0, null);
        }
        if (launchers.Count == 0)
        {
            AppendMenu(launcherMenu, MfString, 0, "No published versions found");
        }
        else
        {
            for (var index = 0; index < launchers.Count; index++)
            {
                var item = launchers[index];
                var launcher = item.Path;
                var isCurrent = string.Equals(Path.GetFullPath(launcher), current, StringComparison.OrdinalIgnoreCase);
                var command = LauncherCommandBase + index;
                var display = item.IsPreview ? item.FullLabel : item.MinorLabel;
                AppendMenu(launcherMenu, MfString | (selectedPolicy == item.Selection ? MfChecked : 0), (nuint)command, isCurrent ? display + " (running)" : display);
                _nativeMenuCommands[command] = () => SelectLauncherPolicy(item.Selection);
            }
        }

        var choices = new[] { ("en", "English"), ("zh-Hant", "Traditional Chinese"), ("zh-Hans", "Simplified Chinese"), ("ja", "Japanese") };
        for (var index = 0; index < choices.Length; index++)
        {
            var choice = choices[index];
            var command = LanguageCommandBase + index;
            var selected = string.Equals(_store.Data.Settings.Language, choice.Item1, StringComparison.OrdinalIgnoreCase);
            AppendMenu(languageMenu, MfString | (selected ? MfChecked : 0), (nuint)command, choice.Item2);
            _nativeMenuCommands[command] = () => ChangeUiLanguage(choice.Item1);
        }
        AppendMenu(menu, MfPopup, (nuint)launcherMenu, "&Version");
        AppendMenu(menu, MfPopup, (nuint)languageMenu, "&Language");
        _nativeMenu = menu;
        _nativeMenuBuilt = true;
        // The top menu is intentionally collapsed by default. It is attached
        // only while the cursor is at the window's upper reveal band.
        SetNativeMenuVisible(false);
    }

    private static LauncherChoice? ParseLauncherChoice(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"^Launcher_(?<core>[0-9]+(?:_[0-9]+)*)(?<preview>[ab][0-9]*)?$", RegexOptions.IgnoreCase);
        if (!match.Success) return null;
        var parts = match.Groups["core"].Value.Split('_').Select(int.Parse).ToArray();
        if (parts.Length > 3) return null;
        var preview = match.Groups["preview"].Success ? match.Groups["preview"].Value.ToLowerInvariant() : null;
        return new LauncherChoice(path, parts[0], parts.ElementAtOrDefault(1), parts.ElementAtOrDefault(2), preview);
    }

    private List<LauncherChoice> DiscoverLaunchers()
    {
        try
        {
            return Directory.EnumerateFiles(_store.Paths.Root, "Launcher_*.exe")
                .Select(ParseLauncherChoice).OfType<LauncherChoice>().ToList();
        }
        catch (Exception ex) { AppLog.Warning("UI", "Could not enumerate versioned launchers.", ex); return []; }
    }

    private static int CompareCore(LauncherChoice left, LauncherChoice right)
    {
        var result = left.Major.CompareTo(right.Major);
        if (result != 0) return result;
        result = left.Minor.CompareTo(right.Minor);
        return result != 0 ? result : left.Patch.CompareTo(right.Patch);
    }

    private static LauncherChoice? LatestStable(IEnumerable<LauncherChoice> candidates) => candidates
        .Where(item => !item.IsPreview).OrderByDescending(item => item.Major).ThenByDescending(item => item.Minor).ThenByDescending(item => item.Patch).FirstOrDefault();

    private static LauncherChoice? EligibleLatestPreview(IEnumerable<LauncherChoice> candidates, LauncherChoice? newestStable)
    {
        var preview = candidates.Where(item => item.IsPreview)
            .OrderByDescending(item => item.Major).ThenByDescending(item => item.Minor).ThenByDescending(item => item.Patch).ThenByDescending(item => item.PreviewKindRank).ThenByDescending(item => item.PreviewRevision)
            .FirstOrDefault();
        return preview is not null && (newestStable is null || CompareCore(preview, newestStable) > 0) ? preview : null;
    }

    private static List<LauncherChoice> VersionMenuLaunchers(IEnumerable<LauncherChoice> candidates)
    {
        var all = candidates.ToList();
        var stable = all.Where(item => !item.IsPreview)
            .GroupBy(item => (item.Major, item.Minor))
            .Select(group => group.OrderByDescending(item => item.Patch).First())
            .OrderByDescending(item => item.Major).ThenByDescending(item => item.Minor).ToList();
        var preview = EligibleLatestPreview(all, LatestStable(all));
        if (preview is not null) stable.Insert(0, preview);
        return stable;
    }

    private LauncherChoice? ResolveLauncherSelection(IEnumerable<LauncherChoice> candidates)
    {
        var all = candidates.ToList();
        var selection = _store.Data.Settings.LauncherSelection;
        if (selection == "auto-stable") return LatestStable(all);
        if (selection == "auto-latest") return EligibleLatestPreview(all, LatestStable(all)) ?? LatestStable(all);
        return selection.StartsWith("exact:", StringComparison.Ordinal)
            ? all.FirstOrDefault(item => string.Equals(item.FullLabel, selection[6..], StringComparison.OrdinalIgnoreCase))
            : null;
    }

    private LauncherChoice? CurrentLauncherChoice() => ParseLauncherChoice(Application.ExecutablePath);
    private LauncherChoice? LastUsedLauncherChoice()
    {
        var label = _store.Data.Settings.LastLauncherVersion;
        if (string.IsNullOrWhiteSpace(label)) return null;
        var path = Path.Combine(_store.Paths.Root, "Launcher_" + label.Replace('.', '_') + ".exe");
        return File.Exists(path) ? ParseLauncherChoice(path) : null;
    }
    private static bool SameLauncher(LauncherChoice left, LauncherChoice right) =>
        string.Equals(Path.GetFullPath(left.Path), Path.GetFullPath(right.Path), StringComparison.OrdinalIgnoreCase);
    private void RememberCurrentLauncherVersion()
    {
        var current = CurrentLauncherChoice();
        if (current is null || string.Equals(_store.Data.Settings.LastLauncherVersion, current.FullLabel, StringComparison.OrdinalIgnoreCase)) return;
        _store.Data.Settings.LastLauncherVersion = current.FullLabel;
        _store.Save();
    }
    private bool ApplyLauncherSelection()
    {
        var selection = _store.Data.Settings.LauncherSelection;
        LauncherChoice? target = null;
        if (selection is "auto-latest" or "auto-stable")
        {
            target = LastUsedLauncherChoice();
            // A first installation has no remembered version, so only that
            // bootstrap case performs an immediate scan.
            if (target is null) target = ResolveLauncherSelection(DiscoverLaunchers());
        }
        else target = ResolveLauncherSelection(DiscoverLaunchers());
        return target is not null && RestartWithLauncher(target.Path, "configured version policy");
    }

    private void SelectLauncherPolicy(string selection)
    {
        _store.Data.Settings.LauncherSelection = selection;
        _availableLauncherUpdate = null;
        if (selection is "auto-latest" or "auto-stable")
        {
            RememberCurrentLauncherVersion();
            _store.Save();
            AppLog.Information("UI", $"Selected passive launcher policy '{selection}'.");
            ScheduleLauncherScan();
            BeginInvoke((Action)(() => { if (!IsDisposed) RebuildNativeMenu(); }));
            return;
        }
        _store.Save();
        AppLog.Information("UI", $"Selected launcher policy '{selection}'.");
        var target = ResolveLauncherSelection(DiscoverLaunchers());
        if (target is not null && RestartWithLauncher(target.Path, "selected version policy")) return;
        BeginInvoke((Action)(() => { if (!IsDisposed) RebuildNativeMenu(); }));
    }
    private void ScheduleLauncherScan()
    {
        if (_launcherScanStarted) return;
        _launcherScanStarted = true;
        _ = Task.Run(DiscoverLaunchers).ContinueWith(task =>
        {
            if (task.IsCanceled || task.IsFaulted) return;
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed) return;
                _discoveredLaunchers.Clear();
                _discoveredLaunchers.AddRange(task.Result);
                _availableLauncherUpdate = null;
                if (_store.Data.Settings.LauncherSelection is "auto-latest" or "auto-stable")
                {
                    var target = ResolveLauncherSelection(_discoveredLaunchers);
                    var current = CurrentLauncherChoice();
                    if (target is not null && current is not null && !SameLauncher(target, current) && IsNewerLauncher(target, current)) _availableLauncherUpdate = target;
                }
                AppLog.Debug("UI", $"Passive launcher scan found {_discoveredLaunchers.Count} versioned executable(s){(_availableLauncherUpdate is null ? "." : "; update available: " + _availableLauncherUpdate.FullLabel + ".")}");
                RebuildNativeMenu();
            }));
        });
    }
    private static bool IsNewerLauncher(LauncherChoice candidate, LauncherChoice current)
    {
        var core = CompareCore(candidate, current);
        if (core != 0) return core > 0;
        if (candidate.IsPreview != current.IsPreview) return !candidate.IsPreview;
        if (!candidate.IsPreview) return false;
        var kind = candidate.PreviewKindRank.CompareTo(current.PreviewKindRank);
        return kind != 0 ? kind > 0 : candidate.PreviewRevision > current.PreviewRevision;
    }
    private void InstallAvailableLauncherUpdate()
    {
        var target = _availableLauncherUpdate;
        if (target is null) return;
        _store.Data.Settings.LastLauncherVersion = target.FullLabel;
        _store.Save();
        RestartWithLauncher(target.Path, "available launcher update");
    }

    private void RebuildNativeMenu()
    {
        DetachNativeMenu();
        _nativeMenuBuilt = false;
        _nativeMenuVisible = false;
        BuildNativeMenu();
    }

    private void UpdateNativeMenuVisibility()
    {
        if (!_nativeMenuBuilt || _nativeMenuInLoop || !IsHandleCreated || IsDisposed) return;
        var cursor = Cursor.Position;
        var inside = Bounds.Contains(cursor);
        var revealHeight = _fullScreen ? 10 : SystemInformation.CaptionHeight + 10;
        var keepHeight = _fullScreen ? SystemInformation.MenuHeight + 12 : SystemInformation.CaptionHeight + SystemInformation.MenuHeight + 12;
        SetNativeMenuVisible(inside && cursor.Y <= Bounds.Top + (_nativeMenuVisible ? keepHeight : revealHeight));
    }

    private void SetNativeMenuVisible(bool visible)
    {
        if (_nativeMenu == IntPtr.Zero || !IsHandleCreated || _nativeMenuVisible == visible) return;
        _suppressResizeLayout = true;
        try
        {
            SetMenu(Handle, visible ? _nativeMenu : IntPtr.Zero);
            DrawMenuBar(Handle);
            _nativeMenuVisible = visible;
        }
        finally { _suppressResizeLayout = false; }
    }

    private void DetachNativeMenu()
    {
        if (_nativeMenu == IntPtr.Zero) return;
        if (IsHandleCreated) SetMenu(Handle, IntPtr.Zero);
        DestroyMenu(_nativeMenu);
        _nativeMenu = IntPtr.Zero;
        _nativeMenuVisible = false;
    }

    private bool RestartWithLauncher(string launcher, string reason)
    {
        if (string.Equals(Path.GetFullPath(launcher), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            AppLog.Information("UI", $"Restarting GameShelf with {reason}: '{launcher}'.");
            Process.Start(new ProcessStartInfo(launcher) { WorkingDirectory = _store.Paths.Root, UseShellExecute = true });
            _launcherHandoffInProgress = true;
            Close();
            return true;
        }
        catch (Exception ex) { AppLog.Error("UI", "Could not restart with the selected launcher.", ex); MessageBox.Show("Could not restart with the selected launcher. See the log for details."); }
        return false;
    }

    private void ChangeUiLanguage(string language)
    {
        if (string.Equals(_store.Data.Settings.Language, language, StringComparison.OrdinalIgnoreCase)) return;
        _store.Data.Settings.Language = language;
        _store.Save();
        AppLog.Information("UI", $"Changed UI language to '{language}', restarting to apply it.");
        Application.Restart();
    }

    private void PersistWindow()
    {
        if (_launcherHandoffInProgress)
        {
            AppLog.Debug("UI", "Skipping window-state persistence during launcher handoff.");
            return;
        }
        var b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        var s = _store.Data.Settings; s.WindowX = b.X; s.WindowY = b.Y; s.WindowWidth = b.Width; s.WindowHeight = b.Height; s.IsMaximized = WindowState == FormWindowState.Maximized; s.IsFullscreen = _fullScreen; s.Page = _page; s.SelectedGameId = _selectedId;
        _store.Save();
        AppLog.Debug("UI", $"Persisted page '{_page}' and selected game '{_selectedId?.ToString() ?? "none"}'.");
    }

    private void HandleKeys(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F2 && _page != "library") { _management = false; ShowLibrary(); e.Handled = true; }
        else if (e.KeyCode == Keys.F3) { if (_page == "library") { _management = !_management; ShowLibrary(rebuildCards: false); } else if (_page == "detail") ShowEdit(); else if (_page == "edit") ShowGlobal(); e.Handled = true; }
        else if (e.KeyCode == Keys.F4 && !e.Alt && (_page == "detail" || _page == "edit" || _page == "global" || (_page == "library" && _management))) { if (_page == "global") ShowEdit(); else if (_page == "edit") ShowDetail(); else { _management = false; ShowLibrary(rebuildCards: false); } e.Handled = true; }
        else if (e.KeyCode == Keys.F11) { ToggleFullscreen(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape && _fullScreen) { ToggleFullscreen(); e.Handled = true; }
    }

    private void ToggleFullscreen()
    {
        var entering = !_fullScreen;
        BeginResizeMask();
        _suppressResizeLayout = true;
        AppLog.Debug("UI", entering ? "Entering fullscreen; drag-resize layout scheduling is suspended." : "Leaving fullscreen; drag-resize layout scheduling is suspended.");
        try
        {
            if (entering)
            {
                _restoreBounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                _restoreBorderStyle = FormBorderStyle;
                // Set this first so native messages produced by the transition are
                // consistently treated as fullscreen messages.
                _fullScreen = true;
                SetNativeMenuVisible(false);
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Maximized;
            }
            else
            {
                WindowState = FormWindowState.Normal;
                FormBorderStyle = _restoreBorderStyle;
                Bounds = _restoreBounds;
                _fullScreen = false;
                SetNativeMenuVisible(false);
            }
        }
        finally { _suppressResizeLayout = false; }

        // Programmatic resizes do not raise ResizeEnd.  Queue exactly one rebuild
        // after Windows has committed the final client size.
        if (!IsHandleCreated) return;
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed) return;
            RefreshResponsiveLayout();
            EndResizeMask();
            AppLog.Debug("UI", $"Fullscreen transition completed; refreshed '{_page}' once at {ClientSize.Width}x{ClientSize.Height}.");
        }));
    }
    private void EnforceAspect() { if (WindowState == FormWindowState.Normal && !_fullScreen && Width > 0) Height = Math.Max(MinimumSize.Height, (int)Math.Round(Width * 9d / 16d)); }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
    private int ResizeHitTest(Point point)
    {
        const int HtClient = 1, HtLeft = 10, HtRight = 11, HtTop = 12, HtTopLeft = 13, HtTopRight = 14, HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17, edge = 7;
        if (_fullScreen || WindowState == FormWindowState.Maximized) return HtClient;
        var left = point.X <= edge; var right = point.X >= ClientSize.Width - edge; var top = point.Y <= edge; var bottom = point.Y >= ClientSize.Height - edge;
        return left && top ? HtTopLeft : right && top ? HtTopRight : left && bottom ? HtBottomLeft : right && bottom ? HtBottomRight : left ? HtLeft : right ? HtRight : top ? HtTop : bottom ? HtBottom : HtClient;
    }
    protected override void WndProc(ref Message m)
    {
        const int WmSetCursor = 0x20, WmNcHitTest = 0x84, WmSizing = 0x214, HtClient = 1;
        if (m.Msg == WmEnterMenuLoop) _nativeMenuInLoop = true;
        if (m.Msg == WmExitMenuLoop)
        {
            _nativeMenuInLoop = false;
            BeginInvoke((Action)(() => { if (!IsDisposed) UpdateNativeMenuVisibility(); }));
        }
        if (m.Msg == WmCommand && _nativeMenuCommands.TryGetValue((int)((long)m.WParam & 0xffff), out var command))
        {
            command();
            return;
        }
        if (m.Msg == WmSizing && !_fullScreen && WindowState == FormWindowState.Normal && m.LParam != IntPtr.Zero)
        {
            var rect = Marshal.PtrToStructure<NativeRect>(m.LParam); var width = Math.Max(MinimumSize.Width, rect.Right - rect.Left); var height = Math.Max(MinimumSize.Height, (int)Math.Round(width * 9d / 16d));
            switch ((int)m.WParam)
            {
                case 1: rect.Left = rect.Right - width; rect.Bottom = rect.Top + height; break; // left
                case 2: rect.Right = rect.Left + width; rect.Bottom = rect.Top + height; break; // right
                case 3: height = Math.Max(MinimumSize.Height, rect.Bottom - rect.Top); width = Math.Max(MinimumSize.Width, (int)Math.Round(height * 16d / 9d)); rect.Top = rect.Bottom - height; rect.Right = rect.Left + width; break; // top
                case 4: rect.Left = rect.Right - width; rect.Top = rect.Bottom - height; break; // top-left
                case 5: rect.Right = rect.Left + width; rect.Top = rect.Bottom - height; break; // top-right
                case 6: height = Math.Max(MinimumSize.Height, rect.Bottom - rect.Top); width = Math.Max(MinimumSize.Width, (int)Math.Round(height * 16d / 9d)); rect.Bottom = rect.Top + height; rect.Right = rect.Left + width; break; // bottom
                case 7: rect.Left = rect.Right - width; rect.Bottom = rect.Top + height; break; // bottom-left
                case 8: rect.Right = rect.Left + width; rect.Bottom = rect.Top + height; break; // bottom-right
            }
            Marshal.StructureToPtr(rect, m.LParam, false);
        }
        if (FormBorderStyle != FormBorderStyle.None) { base.WndProc(ref m); return; }
        if (m.Msg == WmSetCursor)
        {
            var hit = ResizeHitTest(PointToClient(Cursor.Position));
            var cursor = hit switch { 10 or 11 => Cursors.SizeWE, 12 or 15 => Cursors.SizeNS, 13 or 17 => Cursors.SizeNWSE, 14 or 16 => Cursors.SizeNESW, _ => null };
            if (cursor is not null) { Cursor = cursor; m.Result = (IntPtr)1; return; }
        }
        base.WndProc(ref m);
        if (m.Msg != WmNcHitTest || _fullScreen || WindowState == FormWindowState.Maximized || (int)m.Result != HtClient) return;
        var point = PointToClient(new Point((short)((long)m.LParam & 0xffff), (short)(((long)m.LParam >> 16) & 0xffff)));
        m.Result = (IntPtr)ResizeHitTest(point);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    private static extern bool AppendMenu(IntPtr menu, uint flags, nuint identifier, string? text);
    [DllImport("user32.dll")] private static extern IntPtr CreateMenu();
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll")] private static extern bool SetMenu(IntPtr window, IntPtr menu);
    [DllImport("user32.dll")] private static extern bool DrawMenuBar(IntPtr window);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);

    // Dark styling is a product constant; savedata no longer carries a theme.
    // Existing layout helpers use this compile-time value while their dark-only
    // colours are progressively shared by editor controls.
    private const bool IsDarkTheme = true;
    private void ApplyTheme()
    {
        BackColor = Color.FromArgb(27, 29, 30); ForeColor = Color.FromArgb(181, 228, 245);
        _top.BackColor = Color.FromArgb(19, 20, 20); _content.BackColor = BackColor;
        RestyleButtons(this);
        ApplyTextTheme(this);
    }
    private void RestyleButtons(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is Button button) StyleButton(button);
            RestyleButtons(child);
        }
    }
    private void ApplyTextTheme(Control parent)
    {
        var color = Color.White;
        foreach (Control child in parent.Controls)
        {
            if (child is GroupBox group) { group.ForeColor = color; group.Font = new Font(group.Font, group.Font.Style | FontStyle.Bold); }
            else if (child is Label label && label.Text != "●") { label.ForeColor = color; label.Font = new Font(label.Font, label.Font.Style | FontStyle.Bold); }
            else if (child is LinkLabel link) { link.LinkColor = color; link.Font = new Font(link.Font, link.Font.Style | FontStyle.Bold); }
            else if (child is TextBox input) { input.BackColor = Color.FromArgb(25, 25, 25); input.ForeColor = color; input.Font = new Font(input.Font, input.Font.Style | FontStyle.Bold); }
            else if (child is ComboBox combo) { combo.BackColor = Color.FromArgb(25, 25, 25); combo.ForeColor = color; combo.Font = new Font(combo.Font, combo.Font.Style | FontStyle.Bold); }
            ApplyTextTheme(child);
        }
    }

    private void StyleButton(Button button)
    {
        button.UseVisualStyleBackColor = false;
        button.BackColor = ButtonColor(button.Tag as string ?? button.Text);
        button.ForeColor = Color.FromArgb(200, 239, 250);
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = ControlPaint.Light(button.BackColor, .35f);
        button.FlatAppearance.BorderSize = 2;
        button.Font = new Font("Segoe UI Symbol", 30f, FontStyle.Bold);
    }
    private static Color ButtonColor(string glyph) => ButtonColor(glyph, true);
    private static Color ButtonColor(string glyph, bool dark) => glyph switch
    {
        "×" => dark ? Color.FromArgb(166, 59, 73) : Color.FromArgb(199, 72, 83),
        "＋" or "✓" or "▶" => dark ? Color.FromArgb(43, 133, 91) : Color.FromArgb(47, 157, 100),
        "⇧" or "⇩" => dark ? Color.FromArgb(176, 112, 44) : Color.FromArgb(206, 138, 48),
        "⚙" => dark ? Color.FromArgb(164, 111, 41) : Color.FromArgb(190, 129, 39),
        "✎" => dark ? Color.FromArgb(170, 82, 48) : Color.FromArgb(197, 94, 55),
        "←" => dark ? Color.FromArgb(57, 130, 145) : Color.FromArgb(62, 151, 166),
        "☀" or "☾" => dark ? Color.FromArgb(163, 126, 42) : Color.FromArgb(190, 153, 51),
        _ => dark ? Color.FromArgb(45, 119, 115) : Color.FromArgb(53, 143, 135)
    };
    private Button CreateIconButton(string glyph, string tooltip, EventHandler click, string? iconKey = null)
    {
        iconKey ??= "glyph:" + glyph;
        var b = new Button { Text = glyph, Tag = glyph, Width = 90, Height = 76, Margin = new Padding(12), AccessibleName = tooltip, TabStop = true };
        StyleButton(b); ApplyRoundedCorners(b); new ToolTip().SetToolTip(b, tooltip); b.Click += click;
        if (_store.Data.Settings.ButtonIcons.TryGetValue(iconKey, out var vector) && !string.IsNullOrWhiteSpace(vector))
        {
            b.Text = "";
            b.Paint += (_, e) => { e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; StatusIconVectors.Draw(e.Graphics, b.ClientRectangle, vector); };
        }
        return b;
    }
    private static void ApplyRoundedCorners(Button button)
    {
        void UpdateShape()
        {
            using var path = new GraphicsPath(); var arc = Math.Min(24, Math.Min(button.Width, button.Height) / 2);
            path.AddArc(0, 0, arc, arc, 180, 90); path.AddArc(button.Width - arc, 0, arc, arc, 270, 90); path.AddArc(button.Width - arc, button.Height - arc, arc, arc, 0, 90); path.AddArc(0, button.Height - arc, arc, arc, 90, 90); path.CloseFigure();
            var previous = button.Region;
            button.Region = new Region(path);
            previous?.Dispose();
        }
        UpdateShape(); button.SizeChanged += (_, _) => UpdateShape();
    }
    private static void ApplyRoundedCorners(Control control, int radius)
    {
        void UpdateShape()
        {
            if (control.Width <= 1 || control.Height <= 1) return;
            using var path = new GraphicsPath(); var arc = Math.Min(radius, Math.Min(control.Width, control.Height) / 2);
            path.AddArc(0, 0, arc, arc, 180, 90); path.AddArc(control.Width - arc, 0, arc, arc, 270, 90); path.AddArc(control.Width - arc, control.Height - arc, arc, arc, 0, 90); path.AddArc(0, control.Height - arc, arc, arc, 90, 90); path.CloseFigure();
            var previous = control.Region;
            control.Region = new Region(path);
            previous?.Dispose();
        }
        UpdateShape(); control.SizeChanged += (_, _) => UpdateShape();
    }
    private Button TextButton(string tooltip, EventHandler click) => CreateIconButton(ActionGlyph(tooltip), tooltip, click);
    private static string ActionGlyph(string text)
    {
        if (text == "×" || text.Contains("Delete", StringComparison.OrdinalIgnoreCase) || text.Contains("刪") || text.Contains("削除")) return "×";
        if (text.Contains("Add", StringComparison.OrdinalIgnoreCase) || text.Contains("新增") || text.Contains("追加")) return "＋";
        if (text.Contains("Import", StringComparison.OrdinalIgnoreCase) || text.Contains("匯入") || text.Contains("导入") || text.Contains("インポート")) return "⇧";
        if (text.Contains("Export", StringComparison.OrdinalIgnoreCase) || text.Contains("匯出") || text.Contains("导出") || text.Contains("エクスポート")) return "⇩";
        if (text.Contains("management", StringComparison.OrdinalIgnoreCase) || text.Contains("管理")) return "⚙";
        if (text.Contains("Edit", StringComparison.OrdinalIgnoreCase) || text.Contains("編輯") || text.Contains("编辑")) return "✎";
        if (text.Contains("Save", StringComparison.OrdinalIgnoreCase) || text.Contains("儲存") || text.Contains("保存")) return "✓";
        if (text.Contains("Choose", StringComparison.OrdinalIgnoreCase) || text.Contains("選擇") || text.Contains("选择")) return "▣";
        if (text.StartsWith("▶")) return "▶";
        if (text.Contains("Back", StringComparison.OrdinalIgnoreCase) || text.Contains("返回") || text.Contains("戻") || text.StartsWith("←")) return "←";
        if (text == "…") return "▣";
        if (text == "□") return "▤";
        if (text == "↺") return "↺";
        return "●";
    }
    private void BuildTop(bool editAvailable)
    {
        BuildTopCore(editAvailable);
        return;
#if false
        _top.Controls.Clear();
        var home = CreateIconButton("⌂", _t["Library"] + " (F2)", (_, _) => { _management = false; ShowLibrary(); }); home.Location = new Point(24, 14);
        var edit = CreateIconButton("✎", _t["Edit"] + " (F3)", (_, _) => { if (editAvailable) HandleKeys(this, new KeyEventArgs(Keys.F3)); }); edit.Enabled = editAvailable; edit.Location = new Point(130, 14);
        if (_page is "edit" or "global")
        {
            var back = CreateIconButton("←", _t["Back"] + " (F4)", (_, _) => { if (_page == "global") ShowEdit(); else ShowDetail(); }); back.Location = new Point(236, 14); _top.Controls.Add(back);
        }
        if (_page == "library" && _management)
        {
            var add = TextButton(_t["Add"], (_, _) => AddGame()); add.Location = new Point(236, 14); _top.Controls.Add(add);
            var import = TextButton(_t["Import"], (_, _) => ImportGame()); import.Location = new Point(342, 14); _top.Controls.Add(import);
        }
        _top.Controls.Add(home); _top.Controls.Add(edit);
        if (_page == "library") BuildHeaderFilters();
        var separator = new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = Color.FromArgb(244, 204, 89) }; _top.Controls.Add(separator);
#endif
    }
    private void BuildTopCore(bool editAvailable)
    {
        ClearTop(); var nextLeft = 24;
        if (_page != "library") { var home = CreateIconButton("⌂", "Library (F2)", (_, _) => { _management = false; ShowLibrary(); }); home.Location = new Point(nextLeft, 14); _top.Controls.Add(home); nextLeft += 106; }
        if (editAvailable) { var edit = CreateIconButton("✎", "Edit (F3)", (_, _) => HandleKeys(this, new KeyEventArgs(Keys.F3))); edit.Location = new Point(nextLeft, 14); _top.Controls.Add(edit); nextLeft += 106; }
        if (_page is "detail" or "edit" or "global" || (_page == "library" && _management))
        {
            var back = CreateIconButton("←", "Back (F4)", (_, _) => { if (_page == "global") ShowEdit(); else if (_page == "edit") ShowDetail(); else { _management = false; ShowLibrary(rebuildCards: false); } });
            back.Location = new Point(nextLeft, 14); _top.Controls.Add(back); nextLeft += 106;
        }
        if (_page == "library" && _management)
        {
            var add = CreateIconButton("＋", "Add game", (_, _) => AddGame()); add.Location = new Point(nextLeft, 14); _top.Controls.Add(add); nextLeft += 106;
            var import = CreateIconButton("⇧", "Import game", (_, _) => ImportGame()); import.Location = new Point(nextLeft, 14); _top.Controls.Add(import); nextLeft += 106;
            var dimensions = CreateIconButton("☷", "Choose library card dimensions", (_, _) => ChooseHomeDisplayDimensions()); dimensions.Location = new Point(nextLeft, 14); _top.Controls.Add(dimensions);
        }
        if (_page == "library") BuildHeaderFilters();
        _top.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 4, BackColor = Color.FromArgb(244, 204, 89) });
    }
    private void BuildHeaderFilters()
    {
        var filterButton = CreateIconButton("", "Filter games", (_, _) => ShowFilterPopup(), "filter");
        filterButton.Paint += (_, e) =>
        {
            if (_store.Data.Settings.ButtonIcons.TryGetValue("filter", out var vector) && !string.IsNullOrWhiteSpace(vector)) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var centerX = filterButton.ClientSize.Width / 2f; var centerY = filterButton.ClientSize.Height / 2f - S(4);
            using var brush = new SolidBrush(Color.White);
            e.Graphics.FillPolygon(brush, [new PointF(centerX - S(21), centerY - S(9)), new PointF(centerX + S(21), centerY - S(9)), new PointF(centerX, centerY + S(8))]);
            using var pen = new Pen(Color.White, Math.Max(2, S(4))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            e.Graphics.DrawLine(pen, centerX, centerY + S(7), centerX, centerY + S(23));
        };
        filterButton.Width = S(76); filterButton.Height = S(76); filterButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        filterButton.Location = new Point(_top.ClientSize.Width - filterButton.Width - S(20), S(14));
        RegisterTopResize((_, _) => filterButton.Left = _top.ClientSize.Width - filterButton.Width - S(20));
        _top.Controls.Add(filterButton);

        var clearSearch = CreateIconButton("×", "Clear title search", (_, _) => { _store.Data.Settings.TitleSearch = ""; _store.Save(); ShowLibrary(); });
        clearSearch.Width = S(54); clearSearch.Height = S(54); clearSearch.Visible = !string.IsNullOrWhiteSpace(_store.Data.Settings.TitleSearch);
        var search = new TextBox { Text = _store.Data.Settings.TitleSearch, PlaceholderText = "Search titles", Width = S(250), Height = S(54), BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(22, 24, 25), ForeColor = Color.FromArgb(181, 228, 245), Font = new Font(Font.FontFamily, S(14), FontStyle.Bold) };
        void PositionSearch()
        {
            clearSearch.Location = new Point(filterButton.Left - clearSearch.Width - S(8), S(25));
            search.Location = new Point(clearSearch.Left - search.Width - S(8), S(25));
        }
        PositionSearch(); RegisterTopResize((_, _) => PositionSearch());
        search.TextChanged += (_, _) =>
        {
            _store.Data.Settings.TitleSearch = search.Text;
            clearSearch.Visible = !string.IsNullOrWhiteSpace(search.Text);
            _store.Save(); PopulateLibraryCards();
        };
        _top.Controls.Add(search); _top.Controls.Add(clearSearch);

        var chips = new FlowLayoutPanel { Left = S(342), Top = S(14), Height = S(78), Width = Math.Max(S(100), search.Left - S(350)), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, FlowDirection = FlowDirection.RightToLeft, WrapContents = true, AutoScroll = true, Padding = new Padding(S(2)) };
        RegisterTopResize((_, _) => chips.Width = Math.Max(S(100), search.Left - chips.Left - S(8)));
        foreach (var d in _store.Data.TagSchema)
        {
            if (!_store.Data.Settings.SelectedTagFilters.TryGetValue(d.DimensionId, out var values)) continue;
            foreach (var value in values.Distinct().Where(value => value != 0 && d.Values.ContainsKey(value))) chips.Controls.Add(FilterChip(d.Values[value], d.IsMultiSelect ? MultiTagColor : SingleTagColor));
        }
        if (_store.Data.Settings.SelectedPlayStatusFilter is int play) chips.Controls.Add(FilterChip(StatusName(StatusKind.Play, play), PlayFilterColor));
        if (_store.Data.Settings.SelectedGameStatusFilter is int game) chips.Controls.Add(FilterChip(StatusName(StatusKind.Game, game), GameFilterColor));
        _top.Controls.Add(chips);
    }
    private static readonly Color SingleTagColor = Color.FromArgb(103, 74, 142);
    private static readonly Color MultiTagColor = Color.FromArgb(190, 103, 42);
    private static readonly Color PlayFilterColor = Color.FromArgb(71, 151, 102);
    private static readonly Color GameFilterColor = Color.FromArgb(62, 125, 181);
    private Label FilterChip(string text, Color? color = null) => new()
    {
        Text = text, AutoSize = true, Padding = new Padding(S(9), S(5), S(9), S(5)), Margin = new Padding(S(3)),
        BackColor = color ?? SingleTagColor, ForeColor = Color.White, Font = new Font(Font.FontFamily, Math.Max(10, S(11)), FontStyle.Bold)
    };
    private void ShowFilterPopup()
    {
        var popup = new Form { Text = "Filter games", StartPosition = FormStartPosition.CenterParent, Size = new Size(S(760), S(560)), MinimumSize = new Size(580, 420), BackColor = Color.FromArgb(35, 38, 39), ForeColor = Color.White, Font = Font };
        var scroll = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(S(20)), BackColor = popup.BackColor };
        var selected = _store.Data.Settings.SelectedTagFilters.ToDictionary(x => x.Key, x => x.Value.ToHashSet());
        var selectedPlay = _store.Data.Settings.SelectedPlayStatusFilter;
        var selectedGame = _store.Data.Settings.SelectedGameStatusFilter;
        FlowLayoutPanel Section(string title, Color color)
        {
            var section = new FlowLayoutPanel { Width = S(680), AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, S(18)), BackColor = Color.FromArgb(42, 46, 48), Padding = new Padding(S(12)) };
            section.Controls.Add(new Label { Text = title, Width = S(620), Height = S(30), Font = new Font(Font.FontFamily, S(16), FontStyle.Bold), ForeColor = color });
            scroll.Controls.Add(section); return section;
        }
        CheckBox Tile(string text, bool check, Color color)
        {
            var tileFont = new Font(Font.FontFamily, Math.Max(12, S(12)), FontStyle.Bold); var textSize = TextRenderer.MeasureText(text, tileFont);
            var tile = new CheckBox { Text = text, Appearance = Appearance.Button, AutoSize = false, Width = textSize.Width + S(30), Height = Math.Max(S(46), textSize.Height + S(18)), TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, Padding = new Padding(S(8), S(4), S(8), S(4)), Margin = new Padding(S(4)), ForeColor = Color.White, BackColor = Color.FromArgb(55, 61, 63), Font = tileFont, Checked = check };
            tile.FlatAppearance.CheckedBackColor = color; tile.FlatAppearance.BorderColor = color; return tile;
        }
        void AddStatusFilter(string title, StatusKind kind, Color color, Func<int?> getSelected, Action<int?> setSelected)
        {
            var section = Section(title, color); var any = Tile("Any", !getSelected().HasValue, color); section.Controls.Add(any);
            var tiles = new List<(int id, CheckBox tile)>();
            foreach (var status in kind == StatusKind.Play ? _store.Data.PlayStatuses : _store.Data.GameStatuses)
            {
                var item = Tile(status.Name, getSelected() == status.Id, color); tiles.Add((status.Id, item)); section.Controls.Add(item);
                item.CheckedChanged += (_, _) => { if (!item.Checked) return; setSelected(status.Id); any.Checked = false; foreach (var other in tiles.Where(other => other.tile != item)) other.tile.Checked = false; };
            }
            any.CheckedChanged += (_, _) => { if (!any.Checked) return; setSelected(null); foreach (var item in tiles) item.tile.Checked = false; };
        }
        AddStatusFilter("Play status", StatusKind.Play, PlayFilterColor, () => selectedPlay, value => selectedPlay = value);
        AddStatusFilter("Game status", StatusKind.Game, GameFilterColor, () => selectedGame, value => selectedGame = value);
        foreach (var dimension in _store.Data.TagSchema.OrderBy(dimension => dimension.IsMultiSelect ? 1 : 0))
        {
            var color = dimension.IsMultiSelect ? MultiTagColor : SingleTagColor;
            var section = Section(dimension.Name, color);
            foreach (var value in OrderedDimensionValues(dimension).Where(value => value.Key != 0))
            {
                var id = value.Key; var tile = Tile(value.Value, selected.TryGetValue(dimension.DimensionId, out var set) && set.Contains(id), color);
                tile.CheckedChanged += (_, _) => { if (!selected.TryGetValue(dimension.DimensionId, out var values)) selected[dimension.DimensionId] = values = []; if (tile.Checked) values.Add(id); else values.Remove(id); };
                section.Controls.Add(tile);
            }
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(92), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(12), S(8), S(12), S(8)), BackColor = popup.BackColor };
        var apply = CreateIconButton("✓", "Apply filters", (_, _) => { _store.Data.Settings.SelectedTagFilters = selected.Where(x => x.Value.Any(v => v != 0)).ToDictionary(x => x.Key, x => x.Value.Where(v => v != 0).Order().ToList()); _store.Data.Settings.SelectedPlayStatusFilter = selectedPlay; _store.Data.Settings.SelectedGameStatusFilter = selectedGame; _store.Save(); popup.DialogResult = DialogResult.OK; popup.Close(); });
        var clear = CreateIconButton("×", "Clear filters", (_, _) => { selected.Clear(); selectedPlay = null; selectedGame = null; foreach (var check in Descendants(scroll).OfType<CheckBox>()) check.Checked = false; });
        buttons.Controls.Add(apply); buttons.Controls.Add(clear); popup.Controls.Add(scroll); popup.Controls.Add(buttons);
        if (popup.ShowDialog(this) == DialogResult.OK) ShowLibrary();
    }
    private void ChooseHomeDisplayDimensions()
    {
        using var popup = new Form { Text = "Library card dimensions", StartPosition = FormStartPosition.CenterParent, Size = new Size(S(650), S(520)), MinimumSize = new Size(520, 420), BackColor = Color.FromArgb(35, 38, 39), ForeColor = Color.White, Font = Font };
        var selected = _store.Data.Settings.HomeDisplayDimensionIds.ToHashSet();
        var selectedMulti = _store.Data.Settings.HomeMultiDisplayDimensionIds.ToHashSet();
        var message = new Label { Dock = DockStyle.Top, Height = S(80), Padding = new Padding(S(18), S(12), S(18), 0), Text = "Choose up to three single-select dimensions and one multi-select dimension to show on Library cards.", ForeColor = Color.FromArgb(244, 204, 89), Font = new Font(Font.FontFamily, S(15), FontStyle.Bold) };
        var list = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(S(18)), BackColor = popup.BackColor };
        list.Controls.Add(new Label { Text = "Single-select dimensions", Width = S(560), Height = S(34), ForeColor = Color.FromArgb(190, 151, 235), Font = new Font(Font.FontFamily, S(15), FontStyle.Bold) });
        foreach (var dimension in _store.Data.TagSchema.Where(dimension => !dimension.IsMultiSelect))
        {
            var id = dimension.DimensionId; var itemFont = new Font(Font.FontFamily, S(14), FontStyle.Bold); var measured = TextRenderer.MeasureText(dimension.Name, itemFont);
            var item = new CheckBox { Text = dimension.Name, Appearance = Appearance.Button, Checked = selected.Contains(id), AutoSize = false, Width = measured.Width + S(30), Height = Math.Max(S(46), measured.Height + S(18)), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(S(6)), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(72, 83, 93), Font = itemFont };
            item.FlatAppearance.CheckedBackColor = Color.FromArgb(104, 76, 151); item.CheckedChanged += (_, _) => { if (item.Checked && !selected.Contains(id) && selected.Count >= 3) { item.Checked = false; return; } if (item.Checked) selected.Add(id); else selected.Remove(id); };
            list.Controls.Add(item);
        }
        list.Controls.Add(new Label { Text = "Choose up to two multi-select dimensions", Width = S(560), Height = S(34), Margin = new Padding(S(6), S(20), S(6), S(6)), ForeColor = MultiTagColor, Font = new Font(Font.FontFamily, S(15), FontStyle.Bold) });
        foreach (var dimension in _store.Data.TagSchema.Where(dimension => dimension.IsMultiSelect))
        {
            var id = dimension.DimensionId; var itemFont = new Font(Font.FontFamily, S(14), FontStyle.Bold); var measured = TextRenderer.MeasureText(dimension.Name, itemFont);
            var item = new CheckBox { Text = dimension.Name, Appearance = Appearance.Button, Checked = selectedMulti.Contains(id), AutoSize = false, Width = measured.Width + S(30), Height = Math.Max(S(46), measured.Height + S(18)), TextAlign = ContentAlignment.MiddleCenter, Margin = new Padding(S(6)), FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(72, 83, 93), Font = itemFont };
            item.FlatAppearance.CheckedBackColor = MultiTagColor; item.CheckedChanged += (_, _) => { if (item.Checked && !selectedMulti.Contains(id) && selectedMulti.Count >= 2) { item.Checked = false; return; } if (item.Checked) selectedMulti.Add(id); else selectedMulti.Remove(id); };
            list.Controls.Add(item);
        }
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(92), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(12), S(8), S(12), S(8)), BackColor = popup.BackColor };
        var save = CreateIconButton("✓", "Save library card dimensions", (_, _) => { _store.SetHomeDisplayDimensions(selected, selectedMulti); popup.DialogResult = DialogResult.OK; }); buttons.Controls.Add(save);
        popup.Controls.AddRange([list, message, buttons]); if (popup.ShowDialog(this) == DialogResult.OK) ShowLibrary();
    }

    private void ClearTop()
    {
        foreach (var handler in _topResizeHandlers) _top.Resize -= handler;
        _topResizeHandlers.Clear();
        foreach (Control control in _top.Controls.Cast<Control>().ToArray()) control.Dispose();
        _top.Controls.Clear();
    }
    private void RegisterTopResize(EventHandler handler) { _top.Resize += handler; _topResizeHandlers.Add(handler); }
    private void Clear(bool preserveLibrary = false, bool discardLibrary = false)
    {
        // Keep the virtual Library grid detached in-memory while the user
        // visits detail/edit/global pages.  It is session-only and is thrown
        // away explicitly when the Library presentation itself changes.
        if (_libraryCards is { IsDisposed: false } && _libraryCards.Parent == _content)
        {
            _cachedLibraryScrollY = _content.VerticalScroll.Value;
            _content.Controls.Remove(_libraryCards);
        }
        if (discardLibrary) DiscardLibraryCardCache();
        _detailPlayStatusBlock = null;
        _detailGameStatusBlock = null;
        foreach (Control control in _content.Controls.Cast<Control>().ToArray()) control.Dispose();
        _content.Controls.Clear();
    }
    private void DiscardLibraryCardCache()
    {
        if (_libraryCards is { IsDisposed: false })
        {
            _libraryCards.Parent?.Controls.Remove(_libraryCards);
            _libraryCards.Dispose();
        }
        _libraryCards = null; _libraryCardSnapshot = null; _cachedLibraryScrollY = 0;
    }
    private void ShowCurrent() { if (_page == "library") ShowLibrary(); else if (_page == "detail") ShowDetail(); else if (_page == "edit") ShowEdit(); else ShowGlobal(); }
    private float UiScale => Math.Clamp(ClientSize.Width / 1280f, .78f, 1.28f);
    private int S(int value) => Math.Max(1, (int)Math.Round(value * UiScale));
    private void RefreshResponsiveLayout()
    {
        _content.Padding = new Padding(S(42));
        if (_page == "edit") CenterEditLayout(); else ShowCurrent();
    }
    private void BeginResizeMask()
    {
        if (IsDisposed) return;
        _resizeMask.Visible = true;
        _resizeMask.BringToFront();
    }
    private void EndResizeMask()
    {
        if (!IsDisposed) _resizeMask.Visible = false;
    }
    private void BeginInteractiveResize()
    {
        if (_suppressResizeLayout || _fullScreen) return;
        _interactiveResize = true;
        BeginResizeMask();
        AppLog.Debug("UI", "Interactive resize started; page presentation is masked until the final layout.");
    }
    private void EndInteractiveResize()
    {
        if (_suppressResizeLayout || !_interactiveResize) return;
        _interactiveResize = false;
        _suppressResizeLayout = true;
        try { EnforceAspect(); }
        finally { _suppressResizeLayout = false; }
        RefreshResponsiveLayout();
        EndResizeMask();
        AppLog.Debug("UI", $"Interactive resize finished; refreshed '{_page}' once at {ClientSize.Width}x{ClientSize.Height}.");
    }
    private void QueueResponsiveLayout()
    {
        // ResizeEnd is not sent for fullscreen/maximize transitions. Fullscreen
        // performs its own one-shot refresh, while an interactive resize remains
        // masked and is laid out only at ResizeEnd.
        if (IsDisposed || _suppressResizeLayout || _fullScreen || WindowState == FormWindowState.Minimized || ClientSize.Width < 100) return;
        if (_interactiveResize) { BeginResizeMask(); return; }
        if (_resizeRefreshQueued || !IsHandleCreated) return;
        _resizeRefreshQueued = true;
        BeginResizeMask();
        BeginInvoke((Action)(() =>
        {
            _resizeRefreshQueued = false;
            if (IsDisposed || _suppressResizeLayout || _fullScreen || WindowState == FormWindowState.Minimized) { EndResizeMask(); return; }
            RefreshResponsiveLayout();
            EndResizeMask();
            AppLog.Debug("UI", $"Programmatic resize refreshed '{_page}' once at {ClientSize.Width}x{ClientSize.Height}.");
        }));
    }
    private void CenterEditLayout()
    {
        var holder = _content.Controls.Cast<Control>().FirstOrDefault(c => Equals(c.Tag, "edit-holder"));
        if (holder is null) return;
        holder.Width = Math.Max(S(920), _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(8));
        foreach (Control child in holder.Controls) child.Left = Math.Max(0, (holder.Width - child.Width) / 2);
    }
    private void EnableWheelScroll(Control root)
    {
        root.MouseWheel -= WheelScroll;
        root.MouseWheel += WheelScroll;
        foreach (Control child in root.Controls) EnableWheelScroll(child);
    }
    private void WheelScroll(object? sender, MouseEventArgs e) => ScrollContentByWheel(e.Delta);
    private void ScrollContentByWheel(int delta)
    {
        var bar = _content.VerticalScroll;
        if (!bar.Visible) return;
        var notches = delta / 120;
        if (notches == 0) notches = Math.Sign(delta);
        var step = Math.Max(S(24), Math.Max(1, SystemInformation.MouseWheelScrollLines) * S(16));
        var next = Math.Clamp(bar.Value - notches * step, bar.Minimum, Math.Max(bar.Minimum, bar.Maximum - bar.LargeChange + 1));
        if (next != bar.Value)
        {
            bar.Value = next;
            // Setting VerticalScroll.Value programmatically does not reliably
            // raise Scroll on every WinForms host.  Tell the virtual grid now;
            // UpdateViewport itself is a no-op until a row-window boundary is
            // crossed, so this keeps scrolling cheap while preventing blank
            // areas after a direction change.
            RefreshLibraryCardViewport();
        }
    }
    private void RefreshLibraryCardViewport()
    {
        if (_page != "library" || _libraryCards is null || _libraryCards.IsDisposed) return;
        var top = Math.Max(0, _content.VerticalScroll.Value - _content.Padding.Top);
        _libraryCards.UpdateViewport(top, Math.Max(1, _content.ClientSize.Height));
    }
    private void ScheduleLibraryCardViewportRefresh()
    {
        if (!IsHandleCreated) return;
        BeginInvoke((Action)(() => { if (!IsDisposed) RefreshLibraryCardViewport(); }));
    }
    private Panel FieldCard(Control field, bool tagField = false)
    {
        var accent = tagField ? Color.FromArgb(161, 116, 211) : Color.FromArgb(133, 216, 143);
        var card = new Panel { Width = 670, Height = field is TextBox { Multiline: true } ? 120 : 78, BackColor = tagField ? Color.FromArgb(48, 39, 61) : Color.FromArgb(22, 24, 25), Padding = new Padding(10), Margin = Padding.Empty };
        card.Paint += (_, e) => { using var pen = new Pen(accent, 2); e.Graphics.DrawRectangle(pen, 1, 1, card.Width - 3, card.Height - 3); };
        field.Dock = DockStyle.Fill; field.Margin = Padding.Empty; card.Controls.Add(field); return card;
    }
    private void ShowLibrary(bool rebuildCards = true)
    {
        _page = "library"; BuildTop(!_management);
        // Reattach the previous frame first.  The post-paint reconciliation
        // below then scans paths and patches only affected card areas.
        if (RestoreCachedLibraryCards())
        {
            ScheduleLibraryCardReconciliation();
            return;
        }
        var statusChanged = _store.RefreshAllGamePathStatuses();
        if (statusChanged) _store.Save();
        PopulateLibraryCards();
    }
    private bool RestoreCachedLibraryCards()
    {
        if (_libraryCards is null || _libraryCards.IsDisposed || _libraryCardSnapshot is null) return false;
        var games = FilteredGames().OrderBy(game => game.Id).ToList();
        var presentationKey = LibraryPresentationKey();
        if (!string.Equals(_libraryCardSnapshot.PresentationKey, presentationKey, StringComparison.Ordinal) ||
            !_libraryCardSnapshot.GameIds.SequenceEqual(games.Select(game => game.Id))) return false;
        ClearContentExceptLibraryCards();
        if (_libraryCards.Parent is null) _content.Controls.Add(_libraryCards);
        else if (_libraryCards.Parent != _content) return false;
        ApplyLibraryManagementMode();
        var restoreScroll = _cachedLibraryScrollY;
        ScheduleLibraryCardViewportRefresh();
        if (restoreScroll > 0 && IsHandleCreated)
            BeginInvoke((Action)(() => { if (!IsDisposed && _page == "library") { _content.AutoScrollPosition = new Point(0, restoreScroll); RefreshLibraryCardViewport(); } }));
        AppLog.Debug("Library", "Restored cached Library card grid before reconciliation.");
        return true;
    }
    private void ScheduleLibraryCardReconciliation()
    {
        if (!IsHandleCreated) return;
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed || _page != "library" || _libraryCards is null || _libraryCardSnapshot is null) return;
            var statusChanged = _store.RefreshAllGamePathStatuses();
            if (statusChanged) _store.Save();
            var games = FilteredGames().OrderBy(game => game.Id).ToList();
            var presentationKey = LibraryPresentationKey();
            if (!string.Equals(_libraryCardSnapshot.PresentationKey, presentationKey, StringComparison.Ordinal) ||
                !_libraryCardSnapshot.GameIds.SequenceEqual(games.Select(game => game.Id)))
            {
                DiscardLibraryCardCache();
                PopulateLibraryCards();
                AppLog.Debug("Library", "Library cache presentation changed; rebuilt virtual grid.");
                return;
            }
            var fingerprints = games.Select(GameCardFingerprint).ToList();
            var changed = fingerprints.Select((fingerprint, index) => (fingerprint, index))
                .Where(item => !string.Equals(item.fingerprint, _libraryCardSnapshot.GameFingerprints[item.index], StringComparison.Ordinal))
                .Select(item => item.index).ToList();
            _libraryCards.UpdateItems(games, changed, RefreshLibraryCard);
            _libraryCardSnapshot = new LibraryCardSnapshot(presentationKey, games.Select(game => game.Id).ToList(), fingerprints);
            AppLog.Debug("Library", changed.Count == 0 ? "Library cache reconciliation found no card changes." : $"Library cache reconciliation patched {changed.Count} card slot(s).");
        }));
    }
    private void ClearContentExceptLibraryCards()
    {
        foreach (var control in _content.Controls.Cast<Control>().Where(control => control != _libraryCards).ToArray())
        {
            _content.Controls.Remove(control);
            control.Dispose();
        }
    }

    private void PopulateLibraryCards()
    {
        if (_page != "library") return;
        Clear(discardLibrary: true);
        var games = FilteredGames().OrderBy(game => game.Id).ToList();
        var grid = new VirtualGameCardPanel(games, GameCard, ConfigureLibraryCard)
        {
            Width = Math.Max(S(320), _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(8)),
            BackColor = _content.BackColor
        };
        grid.ConfigureLayout(LibraryCardSize(), new Padding(S(16)));
        _libraryCards = grid;
        _content.Controls.Add(grid);
        EnableWheelScroll(grid);
        _cachedLibraryScrollY = 0;
        _libraryCardSnapshot = new LibraryCardSnapshot(LibraryPresentationKey(), games.Select(game => game.Id).ToList(), games.Select(GameCardFingerprint).ToList());
        ScheduleLibraryCardViewportRefresh();
    }
    private string LibraryPresentationKey()
    {
        var settings = _store.Data.Settings;
        var filters = string.Join(";", settings.SelectedTagFilters.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{string.Join(',', pair.Value.Order())}"));
        var dimensions = string.Join(";", _store.Data.TagSchema.Select(dimension => $"{dimension.DimensionId}:{dimension.Name}:{dimension.IsMultiSelect}:{string.Join(',', OrderedDimensionValues(dimension).Select(pair => $"{pair.Key}={pair.Value}"))}"));
        var statuses = string.Join(";", _store.Data.PlayStatuses.Concat(_store.Data.GameStatuses).Select(status => $"{status.Id}:{status.Name}:{status.Color}:{status.IconVector}"));
        return string.Join("\u001e", ClientSize.Width, ClientSize.Height, settings.TitleSearch, settings.SelectedPlayStatusFilter, settings.SelectedGameStatusFilter, filters,
            string.Join(',', settings.HomeDisplayDimensionIds), string.Join(',', settings.HomeMultiDisplayDimensionIds), dimensions, statuses);
    }
    private string GameCardFingerprint(GameEntry game)
    {
        var imageStamp = "";
        try
        {
            var imagePath = _store.ImagePath(game);
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                var info = new FileInfo(imagePath);
                imageStamp = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }
        }
        catch { /* a changed/unavailable image simply reloads on the next full grid build */ }
        var multi = string.Join(";", game.MultiTags.Select(values => string.Join(',', values)));
        return string.Join("\u001f", game.Id, game.Title, game.ImageFile, imageStamp, game.PlayStatusId, game.GameStatusId, string.Join(',', game.Tags), multi);
    }
    private Size LibraryCardSize()
    {
        using var titleFont = new Font(Font.FontFamily, S(20), FontStyle.Bold);
        using var idFont = new Font(Font.FontFamily, S(22), FontStyle.Bold);
        var idTop = S(278); var idHeight = Math.Max(S(38), TextRenderer.MeasureText("0123456789", idFont).Height + S(4));
        var titleTop = idTop + idHeight; var titleHeight = Math.Max(S(64), TextRenderer.MeasureText("Ag\nAg", titleFont).Height + S(8));
        var singleHeight = S(80); var multiRowHeight = S(42); var multiHeight = multiRowHeight * 2 + S(6);
        var singleTop = titleTop + titleHeight + S(3); var multiTop = singleTop + singleHeight + S(4);
        return new Size(S(390), multiTop + multiHeight + S(9));
    }
    private void ConfigureLibraryCard(Control card)
    {
        if (card is Panel panel && panel.Tag is GameEntry) panel.BackColor = Color.FromArgb(38, 42, 42);
        foreach (var control in Descendants(card)) control.Cursor = _management ? Cursors.Default : Cursors.Hand;
        EnableWheelScroll(card);
    }
    private void ApplyLibraryManagementMode()
    {
        foreach (var card in Descendants(_content).OfType<Panel>().Where(control => control.Tag is GameEntry))
        {
            ConfigureLibraryCard(card);
        }
    }
    private IEnumerable<GameEntry> FilteredGames()
    {
        var settings = _store.Data.Settings;
        var search = settings.TitleSearch.Replace("\\n", "\n").Trim();
        return _store.Data.Games.Where(game =>
            (string.IsNullOrWhiteSpace(search) || DisplayTitle(game.Title).Contains(search, StringComparison.CurrentCultureIgnoreCase)) &&
            (!settings.SelectedPlayStatusFilter.HasValue || game.PlayStatusId == settings.SelectedPlayStatusFilter.Value) &&
            (!settings.SelectedGameStatusFilter.HasValue || game.GameStatusId == settings.SelectedGameStatusFilter.Value) &&
            _store.Data.TagSchema.Select((dimension, index) =>
            {
                if (!settings.SelectedTagFilters.TryGetValue(dimension.DimensionId, out var values) || values.Count == 0) return true;
                return dimension.IsMultiSelect
                    ? values.Intersect(game.MultiTags.ElementAtOrDefault(index) ?? []).Any()
                    : values.Contains(game.Tags.ElementAtOrDefault(index));
            }).All(match => match));
    }
    private Control GameCard(GameEntry game)
    {
        return BuildGameCard(game);
#if false
        var cardWidth = S(350); var cardHeight = S(390);
        var card = new Panel { Width = cardWidth, Height = cardHeight, Margin = new Padding(S(16)), BorderStyle = BorderStyle.FixedSingle, AccessibleName = game.Title, BackColor = IsDarkTheme ? Color.FromArgb(38, 42, 42) : Color.FromArgb(255, 243, 213) };
        var image = new PictureBox { Left = S(7), Top = S(7), Width = S(336), Height = S(189), SizeMode = PictureBoxSizeMode.Zoom, Image = LoadImage(game), Cursor = _management ? Cursors.Default : Cursors.Hand };
        if (!_management) image.Click += (_, _) => { _selectedId = game.Id; ShowDetail(); };
        card.Controls.Add(image);
        var title = new Label { Text = $"{game.Id}  {game.Title}", Left = S(10), Top = S(205), Width = S(330), Height = S(55), AutoEllipsis = false, Font = new Font(Font.FontFamily, S(15), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.FromArgb(181, 228, 245) : Color.FromArgb(48, 110, 132) }; card.Controls.Add(title);
        card.Controls.Add(StatusLight(StatusKind.Play, game.PlayStatusId, new Point(S(10), S(280))));
        card.Controls.Add(StatusLight(StatusKind.Game, game.GameStatusId, new Point(S(112), S(280))));
        card.Controls.Add(new Label { Text = string.Join("\n", _store.Data.TagSchema.Take(3).Select((d, i) => $"{game.Tags[i]} {d.Values.GetValueOrDefault(game.Tags[i])}")), Left = S(215), Top = S(284), Width = S(125), Height = S(90), Font = new Font(Font.FontFamily, Math.Max(10, S(12)), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.FromArgb(244, 204, 89) : Color.FromArgb(178, 102, 28) });
        if (_management) { var del = CreateIconButton("×", _t["Delete"], (_, _) => DeleteGame(game)); del.Left = cardWidth - del.Width - S(14); del.Top = S(8); card.Controls.Add(del); }
        if (!_management)
        {
            void Highlight(bool enabled) { card.BackColor = enabled ? (IsDarkTheme ? Color.FromArgb(57, 68, 62) : Color.FromArgb(255, 232, 179)) : (IsDarkTheme ? Color.FromArgb(38, 42, 42) : Color.FromArgb(255, 243, 213)); }
            card.MouseEnter += (_, _) => Highlight(true); card.MouseLeave += (_, _) => Highlight(false); image.MouseEnter += (_, _) => Highlight(true); image.MouseLeave += (_, _) => Highlight(false);
        }
        return card;
#endif
    }
    private Control BuildGameCard(GameEntry game)
    {
        var cardSize = LibraryCardSize(); var cardWidth = cardSize.Width; var baseColor = Color.FromArgb(38, 42, 42);
        using var cardTitleFont = new Font(Font.FontFamily, S(20), FontStyle.Bold);
        using var cardIdFont = new Font(Font.FontFamily, S(22), FontStyle.Bold);
        var idTop = S(278); var idHeight = Math.Max(S(38), TextRenderer.MeasureText("0123456789", cardIdFont).Height + S(4));
        var titleTop = idTop + idHeight; var titleHeight = Math.Max(S(64), TextRenderer.MeasureText("Ag\nAg", cardTitleFont).Height + S(8));
        var singleHeight = S(80); var multiRowHeight = S(42); var multiHeight = multiRowHeight * 2 + S(6);
        var singleTop = titleTop + titleHeight + S(3); var multiTop = singleTop + singleHeight + S(4);
        var card = new Panel { Width = cardWidth, Height = cardSize.Height, Margin = Padding.Empty, BorderStyle = BorderStyle.FixedSingle, AccessibleName = game.Title, BackColor = baseColor, Tag = game };
        var imageHeight = S(263); var imageWidth = imageHeight * 3 / 4; var rightLeft = S(9) + imageWidth + S(10); var rightWidth = cardWidth - rightLeft - S(9);
        var image = new PictureBox { Name = "library-cover", Tag = GameCardImageStamp(game), Left = S(9), Top = S(9), Width = imageWidth, Height = imageHeight, SizeMode = PictureBoxSizeMode.Zoom, Image = LoadImage(game), Cursor = _management ? Cursors.Default : Cursors.Hand };
        var statusGap = S(7); var statusHeight = (imageHeight - statusGap) / 2;
        var playStatus = StatusBlock(StatusKind.Play, game.PlayStatusId, new Rectangle(rightLeft, S(9), rightWidth, statusHeight)); playStatus.Name = "library-play-status";
        var gameStatus = StatusBlock(StatusKind.Game, game.GameStatusId, new Rectangle(rightLeft, S(9) + statusHeight + statusGap, rightWidth, statusHeight)); gameStatus.Name = "library-game-status";
        var id = new Label { Name = "library-id", Text = game.Id.ToString(), Left = S(12), Top = idTop, Width = cardWidth - S(24), Height = idHeight, Font = new Font(Font.FontFamily, S(22), FontStyle.Bold), ForeColor = Color.FromArgb(181, 228, 245) };
        var title = new Label { Name = "library-title", Text = DisplayTitle(game.Title), Left = S(12), Top = titleTop, Width = cardWidth - S(24), Height = titleHeight, AutoEllipsis = true, Font = new Font(Font.FontFamily, S(20), FontStyle.Bold), ForeColor = Color.White };
        var singleTags = new FlowLayoutPanel { Name = "library-single-tags", Tag = LibrarySingleTagsKey(game), Left = S(9), Top = singleTop, Width = cardWidth - S(18), Height = singleHeight, AutoScroll = true, WrapContents = true, BackColor = baseColor, Padding = Padding.Empty };
        PopulateLibrarySingleTags(singleTags, game);
        var multiTags = new Panel { Name = "library-multi-tags", Tag = LibraryMultiTagsKey(game), Left = S(9), Top = multiTop, Width = cardWidth - S(18), Height = multiHeight, BackColor = baseColor };
        PopulateLibraryMultiTags(multiTags, game);
        card.Controls.AddRange([image, playStatus, gameStatus, id, title, singleTags, multiTags]);
        void Highlight(bool enabled)
        {
            if (_management) return;
            card.BackColor = enabled ? Color.FromArgb(57, 68, 62) : baseColor;
            singleTags.BackColor = card.BackColor; multiTags.BackColor = card.BackColor;
        }
        WireLibraryCardInteractions(card, card, game);
        card.MouseEnter += (_, _) => Highlight(true); card.MouseLeave += (_, _) => Highlight(false);
        image.MouseEnter += (_, _) => Highlight(true); image.MouseLeave += (_, _) => Highlight(false);
        card.Disposed += (_, _) => { var owned = image.Image; image.Image = null; owned?.Dispose(); };
        return card;
    }
    private void WireLibraryCardInteractions(Control root, Panel card, GameEntry game)
    {
        root.MouseUp += (_, e) =>
        {
            if (_management)
            {
                if (e.Button == MouseButtons.Right) DeleteGame(game);
                return;
            }
            if (e.Button == MouseButtons.Left) { _selectedId = game.Id; ShowDetail(); }
        };
        foreach (Control child in root.Controls) WireLibraryCardInteractions(child, card, game);
    }
    private string GameCardImageStamp(GameEntry game)
    {
        try
        {
            var imagePath = _store.ImagePath(game);
            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                var info = new FileInfo(imagePath);
                return $"{game.ImageFile}|{info.Length}:{info.LastWriteTimeUtc.Ticks}";
            }
        }
        catch { }
        return game.ImageFile ?? string.Empty;
    }
    private string LibrarySingleTagsKey(GameEntry game) => string.Join(";", HomeDisplayDimensions().Select(dimension =>
    {
        var index = _store.Data.TagSchema.FindIndex(item => item.DimensionId == dimension.DimensionId);
        return $"{dimension.DimensionId}:{game.Tags.ElementAtOrDefault(index)}";
    }));
    private string LibraryMultiTagsKey(GameEntry game) => string.Join(";", HomeMultiDisplayDimensions().Select(dimension =>
    {
        var index = _store.Data.TagSchema.FindIndex(item => item.DimensionId == dimension.DimensionId);
        return $"{dimension.DimensionId}:{string.Join(',', game.MultiTags.ElementAtOrDefault(index) ?? [])}";
    }));
    private static void DisposeControls(Control parent)
    {
        foreach (var child in parent.Controls.Cast<Control>().ToArray()) { parent.Controls.Remove(child); child.Dispose(); }
    }
    private void PopulateLibrarySingleTags(FlowLayoutPanel target, GameEntry game)
    {
        DisposeControls(target);
        foreach (var dimension in HomeDisplayDimensions())
        {
            var index = _store.Data.TagSchema.FindIndex(item => item.DimensionId == dimension.DimensionId);
            var text = index >= 0 ? dimension.Values.GetValueOrDefault(game.Tags.ElementAtOrDefault(index)) ?? string.Empty : string.Empty;
            if (!string.IsNullOrWhiteSpace(text)) target.Controls.Add(FilterChip(text, SingleTagColor));
        }
    }
    private void PopulateLibraryMultiTags(Panel target, GameEntry game)
    {
        DisposeControls(target); var multiRowTop = 0; var multiRowHeight = S(42);
        foreach (var multiDimension in HomeMultiDisplayDimensions())
        {
            var index = _store.Data.TagSchema.FindIndex(item => item.DimensionId == multiDimension.DimensionId);
            var row = new FlowLayoutPanel { Left = 0, Top = multiRowTop, Width = target.Width, Height = multiRowHeight, AutoScroll = true, WrapContents = false, BackColor = target.BackColor, Padding = Padding.Empty };
            foreach (var value in OrderedSelectedValues(multiDimension, game.MultiTags.ElementAtOrDefault(index) ?? []))
            {
                var text = multiDimension.Values.GetValueOrDefault(value) ?? "";
                if (!string.IsNullOrWhiteSpace(text)) row.Controls.Add(FilterChip(text, MultiTagColor));
            }
            target.Controls.Add(row); multiRowTop += multiRowHeight + S(6);
        }
    }
    private void RefreshLibraryCard(Control control, GameEntry game)
    {
        if (control is not Panel card || card.IsDisposed) return;
        card.Tag = game; card.AccessibleName = game.Title;
        var cover = card.Controls.Find("library-cover", false).OfType<PictureBox>().FirstOrDefault();
        var imageStamp = GameCardImageStamp(game);
        if (cover is not null && !string.Equals(cover.Tag as string, imageStamp, StringComparison.Ordinal))
        {
            var old = cover.Image; cover.Image = LoadImage(game); cover.Tag = imageStamp; old?.Dispose();
        }
        var id = card.Controls.Find("library-id", false).OfType<Label>().FirstOrDefault(); if (id is not null) id.Text = game.Id.ToString();
        var title = card.Controls.Find("library-title", false).OfType<Label>().FirstOrDefault(); if (title is not null) title.Text = DisplayTitle(game.Title);
        RefreshStatusBlock(card.Controls.Find("library-play-status", false).OfType<Panel>().FirstOrDefault(), StatusKind.Play, game.PlayStatusId);
        RefreshStatusBlock(card.Controls.Find("library-game-status", false).OfType<Panel>().FirstOrDefault(), StatusKind.Game, game.GameStatusId);
        var single = card.Controls.Find("library-single-tags", false).OfType<FlowLayoutPanel>().FirstOrDefault();
        if (single is not null && !string.Equals(single.Tag as string, LibrarySingleTagsKey(game), StringComparison.Ordinal)) { PopulateLibrarySingleTags(single, game); single.Tag = LibrarySingleTagsKey(game); foreach (Control child in single.Controls) WireLibraryCardInteractions(child, card, game); }
        var multi = card.Controls.Find("library-multi-tags", false).OfType<Panel>().FirstOrDefault();
        if (multi is not null && !string.Equals(multi.Tag as string, LibraryMultiTagsKey(game), StringComparison.Ordinal)) { PopulateLibraryMultiTags(multi, game); multi.Tag = LibraryMultiTagsKey(game); foreach (Control child in multi.Controls) WireLibraryCardInteractions(child, card, game); }
        ConfigureLibraryCard(card);
    }
    private IEnumerable<TagDimension> HomeDisplayDimensions() => _store.Data.Settings.HomeDisplayDimensionIds
        .Select(id => _store.Data.TagSchema.FirstOrDefault(dimension => dimension.DimensionId == id))
        .Where(dimension => dimension is { IsMultiSelect: false }).Cast<TagDimension>();
    private IEnumerable<TagDimension> HomeMultiDisplayDimensions() => _store.Data.Settings.HomeMultiDisplayDimensionIds
        .Select(id => _store.Data.TagSchema.FirstOrDefault(dimension => dimension.IsMultiSelect && dimension.DimensionId == id))
        .Where(dimension => dimension is not null).Cast<TagDimension>();
    private IEnumerable<KeyValuePair<int, string>> OrderedDimensionValues(TagDimension dimension)
    {
        var order = dimension.ValueOrder ?? [];
        var known = order.Where(dimension.Values.ContainsKey).Select(id => new KeyValuePair<int, string>(id, dimension.Values[id]));
        var missing = dimension.Values.Where(pair => !order.Contains(pair.Key)).OrderBy(pair => pair.Key);
        return known.Concat(missing);
    }
    private IEnumerable<int> OrderedSelectedValues(TagDimension dimension, IEnumerable<int> values)
    {
        var selected = values.ToHashSet();
        return OrderedDimensionValues(dimension).Select(pair => pair.Key).Where(selected.Contains);
    }
    private static string DisplayTitle(string title) => (title ?? "").Replace("\\n", "\n");
    private Image LoadImage(GameEntry g)
    {
        var file = _store.ImagePath(g);
        try
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return ImageService.MissingImage();
            using var source = Image.FromFile(file);
            return new Bitmap(source); // do not hold a lock on managed images
        }
        catch { return ImageService.MissingImage(); }
    }
    private string StatusName(StatusKind kind, int id) => (kind == StatusKind.Play ? _store.Data.PlayStatuses : _store.Data.GameStatuses).FirstOrDefault(s => s.Id == id)?.Name ?? _t["none"];
    private Color StatusColor(StatusKind kind, int id)
    {
        var value = (kind == StatusKind.Play ? _store.Data.PlayStatuses : _store.Data.GameStatuses).FirstOrDefault(s => s.Id == id)?.Color ?? "#808080";
        try { return ColorTranslator.FromHtml(value); } catch { return Color.Gray; }
    }
    private string StatusIconVector(StatusKind kind, int id) => (kind == StatusKind.Play ? _store.Data.PlayStatuses : _store.Data.GameStatuses).FirstOrDefault(s => s.Id == id)?.IconVector ?? StatusIconVectors.DefaultFor(kind);
    private string RegionAlias(int id) => id == 0 ? _t["none"] : _store.Data.RegionAliases.GetValueOrDefault(id, $"Region {id}");
    private Label StatusLight(StatusKind kind, int id, Point location)
    {
        var text = StatusName(kind, id);
        var light = new Label { Text = "●", Width = S(90), Height = S(76), Location = location, Font = new Font("Segoe UI Symbol", S(51), FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, ForeColor = StatusColor(kind, id), AccessibleName = text };
        new ToolTip().SetToolTip(light, text);
        return light;
    }
    private Panel StatusBlock(StatusKind kind, int id, Rectangle bounds, MouseEventHandler? mouseUp = null)
    {
        var text = StatusName(kind, id);
        var block = new Panel { Bounds = bounds, BackColor = StatusColor(kind, id), AccessibleName = text, Tag = new StatusBlockState(kind, id) };
        block.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (block.Tag is StatusBlockState state) StatusIconVectors.Draw(e.Graphics, block.ClientRectangle, StatusIconVector(state.Kind, state.Id));
        };
        if (mouseUp is not null) block.MouseUp += mouseUp;
        ApplyRoundedCorners(block, S(13)); _statusToolTip.SetToolTip(block, text);
        return block;
    }
    private void RefreshStatusBlock(Panel? block, StatusKind kind, int id)
    {
        if (block is null || block.IsDisposed) return;
        block.Tag = new StatusBlockState(kind, id);
        block.BackColor = StatusColor(kind, id);
        block.AccessibleName = StatusName(kind, id);
        _statusToolTip.SetToolTip(block, block.AccessibleName);
        block.Invalidate();
    }
    private void RefreshDetailStatusBlocks(GameEntry game)
    {
        RefreshStatusBlock(_detailPlayStatusBlock, StatusKind.Play, game.PlayStatusId);
        RefreshStatusBlock(_detailGameStatusBlock, StatusKind.Game, game.GameStatusId);
    }

    private void ShowDetail(bool preserveScroll = false)
    {
        if (_selectedId is null) { ShowLibrary(); return; }
        var scrollY = preserveScroll && _page == "detail" ? _content.VerticalScroll.Value : 0;
        var retainLibrary = _page == "library";
        _page = "detail"; BuildTop(true); Clear(retainLibrary); var g = _store.GetGame(_selectedId.Value);
        if (_store.RefreshGamePathStatus(g)) _store.Save();
        BuildDetailPageV2(g);
        if (scrollY > 0 && IsHandleCreated)
            BeginInvoke((Action)(() => { if (!IsDisposed && _page == "detail") _content.AutoScrollPosition = new Point(0, scrollY); }));
        return;
#if false
        var availableWidth = Math.Max(S(420), _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(8));
        var narrow = availableWidth < S(880);
        var page = new TableLayoutPanel { ColumnCount = narrow ? 1 : 2, AutoSize = false, Width = availableWidth, Height = narrow ? S(1700) : S(1120), Padding = new Padding(S(18)), BackColor = IsDarkTheme ? Color.FromArgb(35, 38, 39) : Color.FromArgb(255, 243, 213), Margin = new Padding(0) };
        if (narrow) page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        else { page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(300))); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); }
        if (narrow) { page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(400))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(360))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(190))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(145))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(540))); }
        else { page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(400))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(180))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(500))); }
        var image = new PictureBox { Width = S(285), Height = S(380), Dock = DockStyle.Fill, Margin = new Padding(0), SizeMode = PictureBoxSizeMode.Zoom, Image = LoadImage(g) };
        page.Controls.Add(image, 0, 0);
        var headline = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(narrow ? 0 : S(28), 0, 0, 0) };
        var play = CreateIconButton("▶", _t["Play"], (_, _) => Launch(g)); play.Enabled = PathRules.IsValidGameExe(g.GamePath); new ToolTip().SetToolTip(play, play.Enabled ? _t["Play"] : _t["Missing path"]);
        play.Width = S(132); play.Height = S(92); play.Font = new Font("Segoe UI Symbol", S(36), FontStyle.Bold);
        var numberAndPlay = new FlowLayoutPanel { AutoSize = true, Height = S(105), WrapContents = false, Margin = new Padding(0) };
        numberAndPlay.Controls.Add(new Label { Text = $"#{g.Id}", Width = S(330), Height = S(92), TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, S(47), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black }); numberAndPlay.Controls.Add(play);
        headline.Controls.Add(numberAndPlay);
        headline.Controls.Add(new Label { Text = g.Title, AutoSize = true, MaximumSize = new Size(S(850), 0), Font = new Font(Font.FontFamily, S(42), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black, Margin = new Padding(0, S(18), 0, S(14)) });
        headline.Controls.Add(new Label { Text = string.Join("\n", _store.Data.TagSchema.Select((d, i) => $"{d.Name}: {g.Tags[i]} {d.Values.GetValueOrDefault(g.Tags[i])}")), AutoSize = true, Font = new Font(Font.FontFamily, S(18), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.FromArgb(244, 204, 89) : Color.FromArgb(178, 102, 28) });
        page.Controls.Add(headline, narrow ? 0 : 1, narrow ? 1 : 0);
        var note = new Label { Text = string.IsNullOrWhiteSpace(g.Note) ? " " : g.Note, Dock = DockStyle.Fill, Padding = new Padding(0, S(22), S(18), 0), Font = new Font(Font.FontFamily, S(22), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black, AccessibleName = "Note" };
        page.Controls.Add(note, 0, narrow ? 2 : 1);
        var lights = new FlowLayoutPanel { Dock = DockStyle.Fill, Height = S(150), Padding = new Padding(S(20), S(18), 0, 0), WrapContents = false };
        lights.Controls.Add(StatusLight(StatusKind.Play, g.PlayStatusId, Point.Empty)); lights.Controls.Add(StatusLight(StatusKind.Game, g.GameStatusId, Point.Empty));
        page.Controls.Add(lights, narrow ? 0 : 1, narrow ? 3 : 1);
        var metadata = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = false, Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, S(24), 0, 0) };
        if (!narrow) page.SetColumnSpan(metadata, 2);
        metadata.Controls.Add(PathLink("Game: " + (string.IsNullOrEmpty(g.GamePath) ? _t["none"] : g.GamePath), g.GamePath));
        metadata.Controls.Add(DetailLabel("Save method: " + (string.IsNullOrEmpty(g.SaveMethod) ? _t["none"] : g.SaveMethod)));
        metadata.Controls.Add(PathLink("Save: " + (string.IsNullOrEmpty(g.SavePath) ? _t["none"] : g.SavePath), g.SavePath));
        metadata.Controls.Add(DetailLabel($"Region #{g.RegionCommandId}: {_store.Data.RegionCommands.GetValueOrDefault(g.RegionCommandId, "")}"));
        metadata.Controls.Add(CreateIconButton("⇩", _t["Export"], (_, _) => ExportGame(g)));
        page.Controls.Add(metadata, 0, narrow ? 4 : 2); _content.Controls.Add(page); EnableWheelScroll(page);
#endif
    }
    private void BuildDetailPage(GameEntry g)
    {
        var gameAbsolutePath = _store.ResolveGamePath(g.GamePath); var saveAbsolutePath = _store.ResolveSavePath(g);
        var availableWidth = Math.Max(S(520), _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(8));
        var narrow = availableWidth < S(760);
        var pageWidth = narrow ? availableWidth : Math.Min(availableWidth, S(1160));
        var pageHeight = narrow ? S(1510) : S(1110);
        var holder = new Panel { Width = availableWidth, Height = pageHeight, Margin = Padding.Empty, BackColor = _content.BackColor };
        var page = new TableLayoutPanel { ColumnCount = narrow ? 1 : 2, AutoSize = false, Width = pageWidth, Height = pageHeight, Padding = new Padding(S(24)), BackColor = Color.FromArgb(35, 38, 39), Margin = Padding.Empty, Left = (availableWidth - pageWidth) / 2 };
        if (narrow) page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        else { page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); }
        if (narrow)
        {
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(420))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(400))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(220))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(470)));
        }
        else
        {
            page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(430))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(210))); page.RowStyles.Add(new RowStyle(SizeType.Absolute, S(410)));
        }
        var image = new PictureBox { Width = S(285), Height = S(380), Anchor = AnchorStyles.Top | (narrow ? AnchorStyles.Left : AnchorStyles.Right), Margin = new Padding(0), SizeMode = PictureBoxSizeMode.StretchImage, Image = LoadImage(g) };
        page.Controls.Add(image, 0, 0);
        var headline = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true, Padding = new Padding(narrow ? 0 : S(28), 0, 0, 0), BackColor = page.BackColor };
        var play = CreateIconButton("▶", "Launch game", (_, _) => ToggleGameProcess(g));
        play.Enabled = PathRules.IsValidGameExe(gameAbsolutePath); play.Width = S(132); play.Height = S(92); play.Font = new Font("Segoe UI Symbol", S(36), FontStyle.Bold);
        var numberAndPlay = new FlowLayoutPanel { AutoSize = true, Height = S(105), WrapContents = false, Margin = Padding.Empty, BackColor = page.BackColor };
        numberAndPlay.Controls.Add(new Label { Text = $"#{g.Id}", Width = S(300), Height = S(92), TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, S(47), FontStyle.Bold), ForeColor = Color.White }); if (play.Enabled) numberAndPlay.Controls.Add(play);
        headline.Controls.Add(numberAndPlay);
        headline.Controls.Add(new Label { Text = g.Title, AutoSize = true, MaximumSize = new Size(narrow ? pageWidth - S(48) : pageWidth / 2 - S(54), 0), Font = new Font(Font.FontFamily, S(38), FontStyle.Bold), ForeColor = Color.White, Margin = new Padding(0, S(16), 0, S(12)) });
        foreach (var tag in _store.Data.TagSchema.Select((d, i) => d.Values.GetValueOrDefault(g.Tags[i]) ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)))
            headline.Controls.Add(FilterChip(tag));
        page.Controls.Add(headline, narrow ? 0 : 1, narrow ? 1 : 0);

        var note = new Label { Text = string.IsNullOrWhiteSpace(g.Note) ? " " : g.Note, Dock = DockStyle.Fill, Padding = new Padding(0, S(24), S(18), 0), Font = new Font(Font.FontFamily, S(22), FontStyle.Bold), ForeColor = Color.White, AccessibleName = "Note" };
        var lights = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Padding = new Padding(narrow ? 0 : S(28), S(18), 0, 0), BackColor = page.BackColor };
        var blockWidth = narrow ? (pageWidth - S(62)) / 2 : (pageWidth / 2 - S(72)) / 2; var blockHeight = narrow ? S(82) : S(165);
        lights.Controls.Add(StatusBlock(StatusKind.Play, g.PlayStatusId, new Rectangle(0, 0, blockWidth, blockHeight))); lights.Controls.Add(StatusBlock(StatusKind.Game, g.GameStatusId, new Rectangle(0, 0, blockWidth, blockHeight)));
        page.Controls.Add(note, 0, narrow ? 2 : 1); page.Controls.Add(lights, narrow ? 0 : 1, narrow ? 2 : 1);

        var metadata = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = false, Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, S(24), 0, 0), BackColor = page.BackColor };
        if (!narrow) page.SetColumnSpan(metadata, 2);
        metadata.Controls.Add(PathLink("Game: " + (string.IsNullOrEmpty(g.GamePath) ? _t["none"] : g.GamePath), gameAbsolutePath, PathRules.IsValidGameExe(gameAbsolutePath)));
        metadata.Controls.Add(DetailLabel("Save root: " + _store.SaveRootName(g.SaveRootId)));
        metadata.Controls.Add(PathLink("Save: " + (string.IsNullOrEmpty(g.SavePath) ? _t["none"] : g.SavePath), saveAbsolutePath, PathRules.IsValidSaveTarget(saveAbsolutePath)));
        metadata.Controls.Add(DetailLabel("Region: " + RegionAlias(g.RegionCommandId)));
        var export = CreateIconButton("⇩", "Export game", (_, _) => ExportGame(g)); export.Margin = new Padding(0, S(28), 0, 0); metadata.Controls.Add(export);
        page.Controls.Add(metadata, 0, narrow ? 3 : 2);
        holder.Controls.Add(page); _content.Controls.Add(holder); EnableWheelScroll(page);
    }
    private void BuildDetailPageV2(GameEntry game)
    {
        var gamePath = _store.ResolveGamePath(game.GamePath); var savePath = _store.ResolveSavePath(game);
        var gameValid = PathRules.IsValidGameExe(gamePath); var saveValid = PathRules.IsValidSaveTarget(savePath);
        var availableWidth = Math.Max(S(520), _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(8));
        var pageWidth = Math.Min(availableWidth, S(1160)); var sectionWidth = pageWidth - S(48);
        var holder = new Panel { Width = availableWidth, Margin = Padding.Empty, BackColor = _content.BackColor };
        var page = new TableLayoutPanel { ColumnCount = 1, Width = pageWidth, Padding = new Padding(S(24)), BackColor = Color.FromArgb(35, 38, 39), Margin = Padding.Empty, Left = (availableWidth - pageWidth) / 2 };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, sectionWidth));

        // Section 1 uses an equal hidden 1A/1B split.  The 1B content determines
        // the section height; the 3:4 cover is then centred within 1A so its top
        // aligns with the number row and its bottom aligns with the tag area.
        var imageColumnWidth = sectionWidth / 2; var headlineWidth = sectionWidth - imageColumnWidth; var headlineContentWidth = headlineWidth - S(20);
        var titleFont = new Font(Font.FontFamily, S(34), FontStyle.Bold);
        var displayTitle = DisplayTitle(game.Title);
        var titleMeasure = TextRenderer.MeasureText(displayTitle, titleFont, new Size(headlineContentWidth, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl);
        var tagRows = new List<Panel>();
        var singleChips = new List<Label>();
        var multiGroups = new List<(string Name, List<string> Values)>();
        for (var index = 0; index < _store.Data.TagSchema.Count; index++)
        {
            var dimension = _store.Data.TagSchema[index];
            if (!dimension.IsMultiSelect)
            {
                var id = game.Tags.ElementAtOrDefault(index);
                var value = dimension.Values.GetValueOrDefault(id) ?? "";
                if (string.IsNullOrWhiteSpace(value)) continue;
                singleChips.Add(DetailTagChip($"{dimension.Name} : {value}", SingleTagColor, headlineContentWidth));
                continue;
            }
            var values = OrderedSelectedValues(dimension, game.MultiTags.ElementAtOrDefault(index) ?? [])
                .Select(id => dimension.Values.GetValueOrDefault(id) ?? "")
                .Where(value => !string.IsNullOrWhiteSpace(value)).ToList();
            if (values.Count > 0) multiGroups.Add((dimension.Name, values));
        }
        if (singleChips.Count > 0) tagRows.Insert(0, BuildGreedyTagRow(singleChips, headlineContentWidth));
        var multiDescriptionWidth = multiGroups.Count == 0 ? 0 : multiGroups.Max(group => DetailTagChip(group.Name + " :", MultiTagColor, headlineContentWidth / 2).Width);
        foreach (var group in multiGroups) tagRows.Add(BuildMultiTagRow(group.Name, group.Values, headlineContentWidth, multiDescriptionWidth));
        var tagHeight = Math.Max(S(38), tagRows.Sum(row => row.Height + S(6)));
        var contentFirstHeight = S(104) + titleMeasure.Height + S(16) + tagHeight;
        // A portrait cover should never collapse below this presentation size on
        // a normally sized window. On narrow windows it remains bounded by its
        // hidden 1A column so the 3:4 artwork cannot overlap 1B.
        var coverMinimumHeight = S(480);
        var coverColumnMaximumHeight = Math.Max(contentFirstHeight, (imageColumnWidth - S(24)) * 4 / 3);
        var firstHeight = Math.Max(contentFirstHeight, Math.Min(coverMinimumHeight, coverColumnMaximumHeight));
        var expandedHeight = firstHeight - contentFirstHeight;
        var numberRowHeight = S(104) + expandedHeight / 3;
        var titleRowHeight = titleMeasure.Height + S(16) + expandedHeight / 3;
        var tagRowHeight = tagHeight + expandedHeight - (expandedHeight / 3) * 2;
        var first = new TableLayoutPanel { ColumnCount = 2, Width = sectionWidth, Height = firstHeight, Margin = Padding.Empty, BackColor = page.BackColor };
        first.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, imageColumnWidth)); first.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, headlineWidth));
        var coverHeight = firstHeight; var coverWidth = coverHeight * 3 / 4;
        var coverHost = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = page.BackColor };
        coverHost.Controls.Add(new PictureBox { Width = coverWidth, Height = coverHeight, Left = (imageColumnWidth - coverWidth) / 2, Top = firstHeight - coverHeight, SizeMode = PictureBoxSizeMode.Zoom, Image = LoadImage(game), AccessibleName = "Cover" });
        first.Controls.Add(coverHost, 0, 0);
        var headline = new TableLayoutPanel { ColumnCount = 1, RowCount = 3, Dock = DockStyle.Fill, Margin = new Padding(S(20), 0, 0, 0), BackColor = page.BackColor };
        headline.RowStyles.Add(new RowStyle(SizeType.Absolute, numberRowHeight)); headline.RowStyles.Add(new RowStyle(SizeType.Absolute, titleRowHeight)); headline.RowStyles.Add(new RowStyle(SizeType.Absolute, tagRowHeight));
        var numberAndLaunch = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = Padding.Empty, BackColor = page.BackColor };
        numberAndLaunch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); numberAndLaunch.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        numberAndLaunch.Controls.Add(new Label { Text = $"#{game.Id}", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font(Font.FontFamily, S(42), FontStyle.Bold), ForeColor = Color.White }, 0, 0);
        if (gameValid)
        {
            var launch = CreateIconButton("▶", "Launch game", (_, _) => ToggleGameProcess(game));
            launch.Anchor = AnchorStyles.None; launch.Width = S(132); launch.Height = S(92); launch.Font = new Font("Segoe UI Symbol", S(36), FontStyle.Bold);
            numberAndLaunch.Controls.Add(launch, 1, 0);
        }
        headline.Controls.Add(numberAndLaunch, 0, 0);
        headline.Controls.Add(new Label { Text = displayTitle, AutoSize = true, MaximumSize = new Size(headlineContentWidth, 0), Font = titleFont, ForeColor = Color.White, Margin = new Padding(0, S(8), 0, S(8)) }, 0, 1);
        var tags = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = Padding.Empty, BackColor = page.BackColor };
        foreach (var row in tagRows) { row.Margin = new Padding(0, 0, 0, S(6)); tags.Controls.Add(row); }
        headline.Controls.Add(tags, 0, 2); first.Controls.Add(headline, 1, 0);

        // Section 2: an equal 2A/2B split, with the two lamps horizontally sharing 2B.
        var noteText = string.IsNullOrWhiteSpace(game.Note) ? " " : game.Note;
        using var noteFont = new Font(Font.FontFamily, S(16), FontStyle.Bold);
        var secondHeight = Math.Max(S(170), TextRenderer.MeasureText(noteText, noteFont, new Size(sectionWidth / 2 - S(28), 0), TextFormatFlags.WordBreak).Height + S(48));
        var second = new TableLayoutPanel { ColumnCount = 2, Width = sectionWidth, Height = secondHeight, Margin = new Padding(0, S(18), 0, S(18)), BackColor = page.BackColor };
        second.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); second.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        second.Controls.Add(new Label { Text = noteText, Dock = DockStyle.Fill, Padding = new Padding(0, S(20), S(18), 0), Font = new Font(Font.FontFamily, S(16), FontStyle.Bold), ForeColor = Color.White, AccessibleName = "Note" }, 0, 0);
        var lights = new TableLayoutPanel { ColumnCount = 2, Dock = DockStyle.Fill, Margin = new Padding(S(18), S(18), 0, S(18)), BackColor = page.BackColor };
        lights.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); lights.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        var playLight = StatusBlock(StatusKind.Play, game.PlayStatusId, Rectangle.Empty, (_, e) => HandlePlayStatusClick(game, e)); playLight.Dock = DockStyle.Fill; playLight.Margin = new Padding(0, 0, S(6), 0);
        var gameLight = StatusBlock(StatusKind.Game, game.GameStatusId, Rectangle.Empty, (_, e) => HandleGameStatusClick(game, e)); gameLight.Dock = DockStyle.Fill; gameLight.Margin = new Padding(S(6), 0, 0, 0);
        _detailPlayStatusBlock = playLight;
        _detailGameStatusBlock = gameLight;
        lights.Controls.Add(playLight, 0, 0); lights.Controls.Add(gameLight, 1, 0);
        second.Controls.Add(lights, 1, 0);

        // Section 3 uses the same full width as sections 1 and 2.
        var metadata = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, Width = sectionWidth, AutoSize = true, WrapContents = false, Padding = new Padding(0, S(16), 0, 0), BackColor = page.BackColor };
        metadata.Controls.Add(PathLink("Game: " + (string.IsNullOrEmpty(game.GamePath) ? _t["none"] : game.GamePath), gamePath, gameValid, sectionWidth));
        metadata.Controls.Add(DetailLabel("Save root: " + _store.SaveRootName(game.SaveRootId), sectionWidth));
        metadata.Controls.Add(PathLink("Save: " + (string.IsNullOrEmpty(game.SavePath) ? _t["none"] : game.SavePath), savePath, saveValid, sectionWidth));
        metadata.Controls.Add(DetailLabel("Region: " + RegionAlias(game.RegionCommandId), sectionWidth));
        var export = CreateIconButton("⇩", "Export game", (_, _) => ExportGame(game)); export.Margin = new Padding(0, S(28), 0, 0); metadata.Controls.Add(export);

        page.RowStyles.Add(new RowStyle(SizeType.Absolute, firstHeight)); page.RowStyles.Add(new RowStyle(SizeType.Absolute, secondHeight + S(36))); page.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        page.Controls.Add(first, 0, 0); page.Controls.Add(second, 0, 1); page.Controls.Add(metadata, 0, 2); page.PerformLayout();
        page.Height = firstHeight + secondHeight + metadata.PreferredSize.Height + S(84); holder.Height = page.Height; holder.Controls.Add(page); _content.Controls.Add(holder); EnableWheelScroll(page);
    }
    private Label DetailTagChip(string text, Color color, int maximumWidth = int.MaxValue)
    {
        var chip = FilterChip(text, color);
        chip.Margin = Padding.Empty;
        // Let the Label measure itself: TextRenderer alone underestimates some
        // mixed CJK/Latin glyph runs after WinForms applies label padding and
        // DPI text metrics, which was clipping the last characters.
        var natural = chip.GetPreferredSize(Size.Empty).Width + S(4);
        // Detail chips deliberately remain single-line. The row layout moves a
        // whole chip to the next row if needed; it must not split its text.
        var width = natural;
        chip.AutoEllipsis = false;
        chip.AutoSize = false;
        chip.Size = new Size(width, Math.Max(S(34), TextRenderer.MeasureText(text, chip.Font).Height + chip.Padding.Vertical + S(2)));
        return chip;
    }
    private Panel BuildGreedyTagRow(IEnumerable<Label> chips, int width)
    {
        var row = new Panel { Width = width, BackColor = Color.Transparent };
        var x = 0; var y = 0; var lineHeight = 0;
        foreach (var chip in chips)
        {
            if (x > 0 && x + chip.Width > width) { y += lineHeight + S(6); x = 0; lineHeight = 0; }
            chip.Location = new Point(x, y); row.Controls.Add(chip); x += chip.Width + S(6); lineHeight = Math.Max(lineHeight, chip.Height);
        }
        row.Height = Math.Max(S(34), y + lineHeight);
        return row;
    }
    private Panel BuildMultiTagRow(string dimensionName, IEnumerable<string> values, int width, int descriptionWidth)
    {
        var row = new Panel { Width = width, BackColor = Color.Transparent };
        var description = DetailTagChip(dimensionName + " :", MultiTagColor, descriptionWidth);
        description.Width = descriptionWidth;
        description.TextAlign = ContentAlignment.MiddleRight;
        description.Location = Point.Empty; row.Controls.Add(description);
        var indent = Math.Min(width - S(24), descriptionWidth + S(8));
        var x = indent; var y = 0; var lineHeight = description.Height;
        foreach (var value in values)
        {
            var chip = DetailTagChip(value, MultiTagColor, Math.Max(S(70), width - indent));
            if (x > indent && x + chip.Width > width) { y += lineHeight + S(6); x = indent; lineHeight = 0; }
            chip.Location = new Point(x, y); row.Controls.Add(chip); x += chip.Width + S(6); lineHeight = Math.Max(lineHeight, chip.Height);
        }
        row.Height = Math.Max(description.Height, y + lineHeight);
        return row;
    }
    private void HandlePlayStatusClick(GameEntry game, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var now = DateTime.UtcNow;
        if (_playStatusClicks.TryGetValue(game.Id, out var previous) && now - previous <= TimeSpan.FromSeconds(.8))
        {
            _playStatusClicks.Remove(game.Id); _store.SetNextPlayStatus(game.Id); RefreshDetailStatusBlocks(game); return;
        }
        _playStatusClicks[game.Id] = now;
    }
    private void HandleGameStatusClick(GameEntry game, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var now = DateTime.UtcNow;
        if (_gameStatusClicks.TryGetValue(game.Id, out var previous) && now - previous <= TimeSpan.FromSeconds(.8))
        {
            _gameStatusClicks.Remove(game.Id); _store.SetNextGameStatus(game.Id); RefreshDetailStatusBlocks(game); return;
        }
        _gameStatusClicks[game.Id] = now;
    }
    private Label DetailLabel(string text, int? maximumWidth = null) => new() { Text = text, AutoSize = true, MaximumSize = new Size(maximumWidth ?? Math.Max(S(420), _content.ClientSize.Width - S(100)), 0), Font = new Font(Font.FontFamily, S(20), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black, Margin = new Padding(0, S(8), 0, S(8)) };
    private Label PathLink(string label, string path, bool valid, int? maximumWidth = null)
    {
        var availableWidth = maximumWidth ?? Math.Max(S(420), _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(24));
        var displayLabel = WrapPathLabel(label, Math.Clamp(availableWidth / Math.Max(S(10), 12), 36, 72));
        var link = new Label { Text = displayLabel, AutoSize = true, MaximumSize = new Size(availableWidth, 0), AccessibleName = label, Font = new Font(Font.FontFamily, S(20), FontStyle.Bold), ForeColor = valid ? Color.FromArgb(161, 226, 174) : Color.FromArgb(255, 94, 94), Cursor = valid ? Cursors.Hand : Cursors.Default, Margin = new Padding(0, S(8), 0, S(8)) };
        link.Click += (_, _) => { if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); else if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); };
        return link;
    }
    private static string WrapPathLabel(string text, int maximumLineLength)
    {
        var result = new System.Text.StringBuilder(text.Length + text.Length / maximumLineLength); var lineLength = 0;
        foreach (var character in text)
        {
            result.Append(character); lineLength++;
            if ((character is '\\' or '/') && lineLength >= maximumLineLength) { result.AppendLine(); lineLength = 0; }
            else if (lineLength >= maximumLineLength * 2) { result.AppendLine(); lineLength = 0; }
        }
        return result.ToString();
    }
    private async void ToggleGameProcess(GameEntry game)
    {
        _store.NormalizeAndValidatePaths(); _store.Save();
        var resolvedGamePath = _store.ResolveGamePath(game.GamePath);
        if (!PathRules.IsValidGameExe(resolvedGamePath)) { AppLog.Warning("Launcher", $"Launch rejected for game {game.Id}: executable path is unavailable."); MessageBox.Show(_t["Missing path"]); return; }
        try
        {
            AppLog.Information("Launcher", $"Launching game {game.Id} ({game.Title}).");
            await Task.Run(() =>
            {
                var gameDirectory = Path.GetDirectoryName(resolvedGamePath) ?? Environment.CurrentDirectory;
                if (game.RegionCommandId == 0)
                {
                    using var direct = Process.Start(new ProcessStartInfo(resolvedGamePath) { UseShellExecute = true, WorkingDirectory = gameDirectory });
                    return;
                }

                var parts = CommandLine.Split(_store.Data.RegionCommands[game.RegionCommandId]);
                if (parts.Count == 0) throw new InvalidOperationException("The region command is empty.");
                var start = new ProcessStartInfo(parts[0]) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = gameDirectory };
                foreach (var argument in parts.Skip(1)) start.ArgumentList.Add(argument);
                start.ArgumentList.Add(resolvedGamePath);
                using var region = Process.Start(start);
            });
            AppLog.Information("Launcher", $"Launch request completed for game {game.Id}.");
        }
        catch (Exception ex) { AppLog.Error("Launcher", $"Could not launch game {game.Id}.", ex); MessageBox.Show("Could not launch game: " + ex.Message, "GameShelf", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void ShowEdit()
    {
        if (_selectedId is null) { ShowLibrary(); return; }
        _page = "edit"; BuildTop(true); Clear(); var original = _store.GetGame(_selectedId.Value); var draft = PackageService.Clone(original);
        var holderWidth = Math.Max(S(920), _content.ClientSize.Width - _content.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - S(8));
        var holder = new Panel { Width = holderWidth, Tag = "edit-holder", BackColor = _content.BackColor, Margin = Padding.Empty };
        var form = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Width = Math.Min(S(1120), holderWidth), Padding = new Padding(20), BackColor = Color.FromArgb(35, 38, 39) };
        form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(190))); form.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); form.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(105)));
        void Row(string title, Control field, Action reset)
        {
            var row = form.RowCount++; var tall = field is TextBox { Multiline: true }; var panelField = field is FlowLayoutPanel; var dimension = _store.Data.TagSchema.FirstOrDefault(d => title == d.Name || title == $"{d.Name} (multi-select)"); var tagField = dimension is not null; form.RowStyles.Add(new RowStyle(SizeType.Absolute, tall ? 190 : panelField ? 140 : 132));
            var label = new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font.FontFamily, 16, FontStyle.Bold), ForeColor = tagField ? (dimension?.IsMultiSelect == true ? MultiTagColor : Color.FromArgb(190, 151, 235)) : Color.FromArgb(244, 204, 89) };
            field.Width = 670; field.Anchor = AnchorStyles.Left | AnchorStyles.Right; field.Margin = new Padding(0, tall ? 20 : panelField ? 12 : 26, 12, tall ? 20 : panelField ? 12 : 26); field.Font = new Font(Font.FontFamily, 15, FontStyle.Bold);
            if (field is TextBox input) { input.BackColor = IsDarkTheme ? Color.FromArgb(22, 24, 25) : Color.White; input.ForeColor = IsDarkTheme ? Color.FromArgb(181, 228, 245) : Color.FromArgb(48, 110, 132); }
            if (field is ComboBox choice && !Equals(choice.Tag, "status-selector")) { choice.BackColor = tagField ? Color.FromArgb(48, 39, 61) : Color.FromArgb(22, 24, 25); choice.ForeColor = Color.FromArgb(181, 228, 245); }
            if (field is FlowLayoutPanel panel) { panel.AutoSize = false; panel.WrapContents = false; panel.Height = 106; panel.Padding = Padding.Empty; }
            var resetButton = CreateIconButton("↺", "Reset to default", (_, _) => reset()); resetButton.Anchor = AnchorStyles.None; resetButton.Margin = new Padding(10, 24, 10, 24);
            var view = field is TextBox or ComboBox ? FieldCard(field, tagField) : field;
            view.Anchor = AnchorStyles.Left | AnchorStyles.Right; view.Margin = new Padding(0, tall ? 20 : panelField ? 12 : 26, 12, tall ? 20 : panelField ? 12 : 26);
            form.Controls.Add(label, 0, row); form.Controls.Add(view, 1, row); form.Controls.Add(resetButton, 2, row);
        }
        var title = new TextBox { Text = draft.Title }; Row("Title", title, () => title.Text = "unknown");
        var note = new TextBox { Text = draft.Note, Multiline = true, Height = 95 }; Row("Note", note, () => note.Text = "");
        var gamePath = new TextBox { Text = draft.GamePath, ReadOnly = true, Width = 555 };
        var saveRoot = ChoiceCombo(_store.Data.SaveRoots.Select(root => new Selection<int>(root.Id, root.Name)).ToList(), draft.SaveRootId); Row("Save root", saveRoot, () => SelectChoice(saveRoot, Defaults.SaveRootGameDirectoryId));
        var gamePick = CreateIconButton("▣", "Choose game executable", (_, _) => { using var d = new OpenFileDialog { Filter = "Executable (*.exe)|*.exe" }; if (d.ShowDialog() == DialogResult.OK) { try { gamePath.Text = _store.ToRcRelativePath(d.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message); } } }); var gamePanel = new FlowLayoutPanel(); gamePanel.Controls.Add(gamePath); gamePanel.Controls.Add(gamePick); Row("Game path (relative to rc)", gamePanel, () => gamePath.Text = "");
        var savePath = new TextBox { Text = draft.SavePath, ReadOnly = true, Width = 450 }; var savePick = CreateIconButton("▣", "Choose save file", (_, _) => { using var d = new OpenFileDialog(); if (d.ShowDialog() == DialogResult.OK) { try { savePath.Text = _store.ToSaveRelativePath(ChoiceId(saveRoot, Defaults.SaveRootGameDirectoryId), gamePath.Text, d.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message); } } }); var saveFolder = CreateIconButton("▤", "Choose save folder", (_, _) => { using var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) { try { savePath.Text = _store.ToSaveRelativePath(ChoiceId(saveRoot, Defaults.SaveRootGameDirectoryId), gamePath.Text, d.SelectedPath); } catch (Exception ex) { MessageBox.Show(ex.Message); } } }); var savePanel = new FlowLayoutPanel(); savePanel.Controls.Add(savePath); savePanel.Controls.Add(savePick); savePanel.Controls.Add(saveFolder); Row("Save path (relative)", savePanel, () => savePath.Text = "");
        var regionChoices = _store.Data.RegionCommands.Keys.OrderBy(id => id).Select(id => new Selection<int>(id, RegionAlias(id))).ToList(); var region = ChoiceCombo(regionChoices, draft.RegionCommandId); Row("Region command", region, () => SelectChoice(region, 0));
        var multiTagPickers = new Dictionary<int, FlowLayoutPanel>();
        // Keep first-level selection controls in the same grouped order as
        // global dimension management, while retaining each dimension's
        // original schema index for the persisted Tags/MultiTags arrays.
        foreach (var entry in _store.Data.TagSchema.Select((dimension, index) => (dimension, index)).OrderBy(entry => entry.dimension.IsMultiSelect ? 1 : 0))
        {
            var pos = entry.index; var dim = entry.dimension;
            if (dim.IsMultiSelect)
            {
                var picker = MultiTagPicker(dim, draft.MultiTags.ElementAtOrDefault(pos) ?? [0]); multiTagPickers[pos] = picker;
                Row($"{dim.Name} (multi-select)", picker, () => SetMultiTagPicker(picker, [0]));
            }
            else
            {
                var tag = ChoiceCombo(dim.Values.OrderBy(x => x.Key).Select(x => new Selection<int>(x.Key, x.Value)).ToList(), draft.Tags[pos]); Row(dim.Name, tag, () => SelectChoice(tag, 0)); tag.Tag = pos;
            }
        }
        var imageButton = CreateIconButton("▣", _t["Choose image"], (_, _) => { using var dialog = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" }; if (dialog.ShowDialog() == DialogResult.OK) { try { _store.SetImage(draft.Id, dialog.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message); } } });
        var applySave = CreateIconButton("✓", "Save game", (_, _) => { try { draft.Title = title.Text; draft.Note = note.Text; draft.GamePath = gamePath.Text; draft.SavePath = savePath.Text; draft.SaveRootId = ChoiceId(saveRoot, Defaults.SaveRootGameDirectoryId); draft.RegionCommandId = ChoiceId(region, 0); foreach (var combo in Descendants(form).OfType<ComboBox>()) if (combo.Tag is int tagPos) draft.Tags[tagPos] = ChoiceId(combo, 0); foreach (var (tagPos, picker) in multiTagPickers) draft.MultiTags[tagPos] = picker.Controls.OfType<CheckBox>().Where(check => check.Checked && check.Tag is int).Select(check => (int)check.Tag!).ToList(); _store.UpdateGame(draft); ShowDetail(); } catch (Exception ex) { MessageBox.Show(ex.Message); } });
        var preview = new PictureBox { Left = S(20), Top = S(20), Width = S(220), Height = S(293), SizeMode = PictureBoxSizeMode.StretchImage, Image = LoadImage(_store.GetGame(draft.Id)), BorderStyle = BorderStyle.FixedSingle, AccessibleName = "Current cover" };
        var cropImageButton = CreateIconButton("✂", "Choose and crop cover", (_, _) => { using var dialog = new OpenFileDialog { Filter = "Images (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg" }; if (dialog.ShowDialog() != DialogResult.OK) return; try { if (SelectAndSaveCover(draft.Id, dialog.FileName)) { preview.Image?.Dispose(); preview.Image = LoadImage(_store.GetGame(draft.Id)); } } catch (Exception ex) { MessageBox.Show(ex.Message); } });
        var actions = new Panel { Width = form.Width, Height = S(345), BackColor = form.BackColor }; actions.Controls.Add(preview); cropImageButton.Location = new Point(S(270), S(34)); applySave.Location = new Point(S(376), S(34)); actions.Controls.Add(cropImageButton); actions.Controls.Add(applySave);
        holder.Height = form.PreferredSize.Height + actions.Height; form.Top = 0; actions.Top = form.PreferredSize.Height; holder.Controls.Add(form); holder.Controls.Add(actions);
        _content.Controls.Add(holder); CenterEditLayout(); EnableWheelScroll(holder);
    }
    private FlowLayoutPanel MultiTagPicker(TagDimension dimension, IEnumerable<int> selectedValues)
    {
        var selected = selectedValues.Where(dimension.Values.ContainsKey).Distinct().ToHashSet();
        if (selected.Count == 0) selected.Add(0);
        if (selected.Count > 1) selected.Remove(0);
        var picker = new FlowLayoutPanel { AutoScroll = true, WrapContents = false, BackColor = Color.FromArgb(46, 32, 23), Padding = new Padding(S(6)), Tag = dimension.DimensionId };
        var checks = new List<CheckBox>(); var changing = false;
        foreach (var value in OrderedDimensionValues(dimension))
        {
            var id = value.Key; var font = new Font(Font.FontFamily, S(13), FontStyle.Bold); var size = TextRenderer.MeasureText(value.Value, font);
            var check = new CheckBox { Tag = id, Text = value.Value, Checked = selected.Contains(id), Appearance = Appearance.Button, AutoSize = false, Width = size.Width + S(28), Height = Math.Max(S(42), size.Height + S(16)), TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, ForeColor = Color.White, BackColor = Color.FromArgb(83, 62, 46), Font = font, Margin = new Padding(S(4)) };
            check.FlatAppearance.CheckedBackColor = MultiTagColor; check.FlatAppearance.BorderColor = Color.FromArgb(244, 178, 104);
            check.CheckedChanged += (_, _) =>
            {
                if (changing) return;
                changing = true;
                try
                {
                    if (id == 0 && check.Checked) foreach (var other in checks.Where(other => other != check)) other.Checked = false;
                    else if (id != 0 && check.Checked) checks.FirstOrDefault(other => Equals(other.Tag, 0))?.Checked = false;
                    if (!checks.Any(other => other.Checked)) checks.First(other => Equals(other.Tag, 0)).Checked = true;
                }
                finally { changing = false; }
            };
            checks.Add(check); picker.Controls.Add(check);
        }
        return picker;
    }
    private static void SetMultiTagPicker(FlowLayoutPanel picker, IEnumerable<int> values)
    {
        var selected = values.ToHashSet(); if (selected.Count == 0) selected.Add(0);
        foreach (var check in picker.Controls.OfType<CheckBox>()) check.Checked = check.Tag is int id && selected.Contains(id);
    }
    private bool SelectAndSaveCover(int gameId, string sourcePath)
    {
        using var cropped = CropCover(sourcePath);
        if (cropped is null) return false;
        var temporary = Path.Combine(Path.GetTempPath(), "gameshelf-cover-" + Guid.NewGuid().ToString("N") + ".png");
        try
        {
            cropped.Save(temporary, System.Drawing.Imaging.ImageFormat.Png);
            _store.SetImage(gameId, temporary);
            return true;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private Bitmap? CropCover(string sourcePath)
    {
        using var loaded = Image.FromFile(sourcePath); using var source = new Bitmap(loaded);
        using var dialog = new Form { Text = "Crop cover", StartPosition = FormStartPosition.CenterParent, Size = new Size(760, 680), MinimumSize = new Size(640, 580), BackColor = Color.FromArgb(35, 38, 39), ForeColor = Color.White, Font = Font };
        var viewport = new Panel { Left = 32, Top = 24, Width = 360, Height = 480, BackColor = Color.FromArgb(15, 16, 17), BorderStyle = BorderStyle.FixedSingle };
        var picture = new PictureBox { Image = new Bitmap(source), SizeMode = PictureBoxSizeMode.StretchImage, Cursor = Cursors.SizeAll };
        var zoom = new TrackBar { Left = 430, Top = 100, Width = 265, Minimum = 100, Maximum = 300, Value = 100, TickFrequency = 25 };
        var help = new Label { Left = 430, Top = 32, Width = 275, Height = 60, Text = "Drag the cover to choose its position.\nUse the slider to zoom.", ForeColor = Color.FromArgb(181, 228, 245), Font = new Font(Font.FontFamily, 13, FontStyle.Bold) };
        var done = CreateIconButton("✓", "Use this crop", (_, _) => dialog.DialogResult = DialogResult.OK); done.Left = 430; done.Top = 180;
        var cancel = CreateIconButton("×", "Cancel", (_, _) => dialog.DialogResult = DialogResult.Cancel); cancel.Left = 536; cancel.Top = 180;
        var dragging = false; Point dragStart = Point.Empty; Point imageStart = Point.Empty;
        void Constrain()
        {
            picture.Left = Math.Clamp(picture.Left, Math.Min(0, viewport.Width - picture.Width), 0);
            picture.Top = Math.Clamp(picture.Top, Math.Min(0, viewport.Height - picture.Height), 0);
        }
        void LayoutImage(bool center)
        {
            var scale = Math.Max((float)viewport.Width / source.Width, (float)viewport.Height / source.Height) * zoom.Value / 100f;
            var oldCenter = new PointF(picture.Left + picture.Width / 2f, picture.Top + picture.Height / 2f);
            picture.Size = new Size((int)Math.Ceiling(source.Width * scale), (int)Math.Ceiling(source.Height * scale));
            if (center) picture.Location = new Point((viewport.Width - picture.Width) / 2, (viewport.Height - picture.Height) / 2);
            else picture.Location = new Point((int)(oldCenter.X - picture.Width / 2f), (int)(oldCenter.Y - picture.Height / 2f));
            Constrain();
        }
        viewport.Controls.Add(picture); LayoutImage(true);
        picture.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { dragging = true; dragStart = e.Location; imageStart = picture.Location; } };
        picture.MouseMove += (_, e) => { if (!dragging) return; picture.Location = new Point(imageStart.X + e.X - dragStart.X, imageStart.Y + e.Y - dragStart.Y); Constrain(); };
        picture.MouseUp += (_, _) => dragging = false; zoom.ValueChanged += (_, _) => LayoutImage(false);
        dialog.Controls.AddRange([viewport, help, zoom, done, cancel]);
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        var scaleResult = picture.Width / (float)source.Width;
        var sourceRect = new RectangleF(-picture.Left / scaleResult, -picture.Top / scaleResult, viewport.Width / scaleResult, viewport.Height / scaleResult);
        var output = new Bitmap(600, 800); using var graphics = Graphics.FromImage(output); graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(source, new Rectangle(0, 0, output.Width, output.Height), sourceRect.X, sourceRect.Y, sourceRect.Width, sourceRect.Height, GraphicsUnit.Pixel);
        return output;
    }
    private static IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child => new[] { child }.Concat(Descendants(child)));
    private ComboBox ChoiceCombo(IReadOnlyList<Selection<int>> choices, int selectedId)
    {
        var combo = new ScrollSafeComboBox { DropDownStyle = ComboBoxStyle.DropDownList, DisplayMember = "Text", ValueMember = "Value", WheelScrollRequested = ScrollContentByWheel };
        combo.Items.AddRange(choices.Cast<object>().ToArray()); SelectChoice(combo, selectedId); return combo;
    }
    private static void SelectChoice(ComboBox combo, int id)
    {
        var index = combo.Items.Cast<object>().Select((item, i) => (item, i)).FirstOrDefault(x => x.item is Selection<int> choice && choice.Value == id).i;
        combo.SelectedIndex = index;
    }
    private static int ChoiceId(ComboBox combo, int fallback) => combo.SelectedItem is Selection<int> choice ? choice.Value : fallback;
    private ComboBox StatusCombo(List<GameStatus> statuses, int id)
    {
        var colors = statuses.ToDictionary(s => s.Id, s => StatusColorValue(s.Color));
        var c = ChoiceCombo(statuses.Select(s => new Selection<int>(s.Id, s.Name)).ToList(), id); c.DrawMode = DrawMode.OwnerDrawFixed; c.Tag = "status-selector";
        c.DrawItem += (_, e) => { if (e.Index < 0 || c.Items[e.Index] is not Selection<int> item) return; var color = colors.GetValueOrDefault(item.Value, Color.Gray); using var brush = new SolidBrush(color); e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, item.Text, c.Font, e.Bounds, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); };
        void ApplyColor() { c.BackColor = colors.GetValueOrDefault(ChoiceId(c, id), Color.Gray); c.ForeColor = Color.White; }
        c.SelectedIndexChanged += (_, _) => ApplyColor(); ApplyColor(); return c;
    }
    private static Color StatusColorValue(string value) { try { return ColorTranslator.FromHtml(value); } catch { return Color.Gray; } }

    private void ShowGlobal(bool preserveScroll = false)
    {
        var scrollY = preserveScroll && _page == "global" ? _content.VerticalScroll.Value : 0;
        _page = "global"; BuildTop(false); Clear();
        var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Width = Math.Max(900, _content.ClientSize.Width - 80), Padding = new Padding(S(8)) };
        stack.Controls.Add(RcRootSection());
        stack.Controls.Add(ArraySection("Save roots", _store.Data.SaveRoots.Select(root => (root.Id, root.Name)), "＋", AddSaveRootTile, SaveRootContext));
        stack.Controls.Add(ArraySection(_t["Region commands"], _store.Data.RegionCommands.Keys.Where(id => id != 0).Select(id => (id, RegionAlias(id))), "＋", AddRegionTile, RegionContext));
        stack.Controls.Add(ArraySection("Play " + _t["Statuses"], _store.Data.PlayStatuses.Select(x => (x.Id, x.Name)), "＋", () => AddStatusTile(StatusKind.Play), id => StatusContext(StatusKind.Play, id)));
        stack.Controls.Add(ArraySection("Game " + _t["Statuses"], _store.Data.GameStatuses.Select(x => (x.Id, x.Name)), "＋", () => AddStatusTile(StatusKind.Game), id => StatusContext(StatusKind.Game, id)));
        stack.Controls.Add(ButtonIconSection());
        stack.Controls.Add(TagVectorSection());
        _content.Controls.Add(stack); EnableWheelScroll(stack);
        if (scrollY > 0 && IsHandleCreated)
            BeginInvoke((Action)(() =>
            {
                if (!IsDisposed && _page == "global") _content.AutoScrollPosition = new Point(0, scrollY);
            }));
    }
    private Control RcRootSection()
    {
        var sectionWidth = Math.Max(860, _content.ClientSize.Width - 100);
        var section = new Panel { Width = sectionWidth, Height = S(245), Margin = new Padding(0, 0, 0, S(32)), BackColor = Color.FromArgb(35, 38, 39) };
        section.Controls.Add(new Label { Text = "rc root folder", Left = S(22), Top = S(16), Width = section.Width - S(44), Height = S(40), Font = new Font(Font.FontFamily, S(21), FontStyle.Bold), ForeColor = Color.White });
        var stored = string.IsNullOrWhiteSpace(_store.Data.RcRootPath) ? "Set rc root folder" : _store.Data.RcRootPath;
        var tile = ElementTile(stored, false, ChooseRcRoot, null); tile.Left = S(30); tile.Top = S(75); tile.Width = Math.Min(section.Width - S(60), S(680));
        section.Controls.Add(tile);
        return section;
    }
    private void ChooseRcRoot()
    {
        using var dialog = new FolderBrowserDialog { Description = "Choose the shared rc game-resource folder" };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try { _store.SetRcRootPath(dialog.SelectedPath); ShowGlobal(true); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private void AddSaveRootTile()
    {
        var name = Prompt("Save root name"); if (name is null) return;
        var path = Prompt("Save root path (. or environment-variable Windows path)", "%USERPROFILE%\\Documents"); if (path is null) return;
        try { _store.AddSaveRoot(name, path); ShowGlobal(true); }
        catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private ContextMenuStrip? SaveRootContext(int id)
    {
        var root = _store.Data.SaveRoots.Single(item => item.Id == id);
        var menu = CreateDarkContextMenu();
        menu.Items.Add("Edit", null, (_, _) =>
        {
            var name = Prompt("Save root name", root.Name); if (name is null) return;
            var path = Prompt("Save root path (. or environment-variable Windows path)", root.PathTemplate); if (path is null) return;
            try { _store.UpdateSaveRoot(id, name, path); ShowGlobal(true); }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        });
        if (_store.Data.SaveRoots.Count > 1)
            menu.Items.Add("Delete", null, (_, _) => { if (MessageBox.Show(_t["Confirm deletion"], "GameShelf", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { try { _store.DeleteSaveRoot(id); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); } } });
        return menu;
    }
    private Control ArraySection(string title, IEnumerable<(int id, string text)> items, string addGlyph, Action add, Func<int, ContextMenuStrip?> menu)
    {
        var values = items.ToList(); var sectionWidth = Math.Max(860, _content.ClientSize.Width - 100); var columns = Math.Max(1, (sectionWidth - S(44)) / S(200)); var rows = (int)Math.Ceiling((values.Count + 1d) / columns);
        var section = new Panel { Width = sectionWidth, Height = S(85) + rows * S(152), Margin = new Padding(0, 0, 0, S(32)), BackColor = Color.FromArgb(35, 38, 39) };
        section.Controls.Add(new Label { Text = title, Left = S(22), Top = S(16), Width = section.Width - S(44), Height = S(40), Font = new Font(Font.FontFamily, S(21), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black });
        var tiles = new FlowLayoutPanel { Left = S(22), Top = S(75), Width = section.Width - S(44), Height = section.Height - S(83), AutoScroll = false, Padding = new Padding(S(8)), BackColor = section.BackColor };
        foreach (var item in values) tiles.Controls.Add(ElementTile(item.text, false, () => { }, menu(item.id)));
        tiles.Controls.Add(ElementTile(addGlyph, true, add, null)); section.Controls.Add(tiles); EnableWheelScroll(tiles); return section;
    }
    private static readonly (string Key, string Name, string Glyph)[] ButtonIconCatalog =
    [
        ("glyph:⌂", "Library", "⌂"), ("glyph:✎", "Edit", "✎"), ("glyph:←", "Back / cancel", "←"), ("glyph:＋", "Add", "＋"),
        ("glyph:⇧", "Import", "⇧"), ("glyph:⇩", "Export", "⇩"), ("glyph:✓", "Save / confirm", "✓"), ("glyph:×", "Delete / clear", "×"),
        ("glyph:▣", "Choose file", "▣"), ("glyph:▤", "Choose folder", "▤"), ("glyph:✂", "Crop cover", "✂"), ("glyph:↻", "Reset", "↻"),
        ("glyph:▶", "Launch game", "▶"), ("glyph:☷", "Library dimensions", "☷"), ("filter", "Filter", "")
    ];
    private Control ButtonIconSection()
    {
        return ArraySection("Button icons", ButtonIconCatalog.Select((item, index) => (index, item.Name)), "↻", () => { _store.Data.Settings.ButtonIcons.Clear(); _store.Save(); ShowGlobal(true); }, ButtonIconContext);
    }
    private ContextMenuStrip? ButtonIconContext(int index)
    {
        var item = ButtonIconCatalog[index]; var menu = CreateDarkContextMenu();
        menu.Items.Add("Edit", null, (_, _) =>
        {
            _store.Data.Settings.ButtonIcons.TryGetValue(item.Key, out var current);
            // Button glyphs are font icons by default, not stored vectors. Pass
            // the default glyph to the editor so an empty/custom-cleared canvas
            // still shows the original button icon as a visual reference.
            var fallbackGlyph = string.IsNullOrWhiteSpace(item.Glyph) ? "▼" : item.Glyph;
            var vector = EditStatusIcon(current ?? "", ColorTranslator.ToHtml(ButtonColor(item.Glyph)), fallbackGlyph); if (vector is null) return;
            _store.Data.Settings.ButtonIcons[item.Key] = vector; _store.Save(); ShowGlobal(true);
        });
        if (_store.Data.Settings.ButtonIcons.ContainsKey(item.Key)) menu.Items.Add("Restore glyph", null, (_, _) => { _store.Data.Settings.ButtonIcons.Remove(item.Key); _store.Save(); ShowGlobal(true); });
        return menu;
    }
    private sealed class DarkContextColorTable : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(25, 27, 28);
        private static readonly Color Highlight = Color.FromArgb(52, 84, 84);
        public override Color ToolStripDropDownBackground => Background;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color MenuItemSelected => Highlight;
        public override Color MenuItemSelectedGradientBegin => Highlight;
        public override Color MenuItemSelectedGradientEnd => Highlight;
        public override Color MenuItemBorder => Color.FromArgb(181, 228, 245);
    }
    private ContextMenuStrip CreateDarkContextMenu()
    {
        var menu = new ContextMenuStrip
        {
            AutoSize = false,
            Width = Math.Max(S(300), 250),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            BackColor = Color.FromArgb(25, 27, 28),
            ForeColor = Color.White,
            Font = new Font(Font.FontFamily, S(16), FontStyle.Bold),
            Padding = new Padding(S(6)),
            Renderer = new ToolStripProfessionalRenderer(new DarkContextColorTable())
        };
        menu.ItemAdded += (_, args) =>
        {
            if (args.Item is null) return;
            args.Item.AutoSize = false;
            args.Item.Width = menu.Width - S(12);
            args.Item.Height = S(52);
            args.Item.Padding = new Padding(S(18), S(6), S(18), S(6));
            args.Item.ForeColor = Color.White;
            args.Item.Font = menu.Font;
            // The menu itself has AutoSize disabled so it can keep the intended
            // dark, wide presentation. Resize its height as actions are added;
            // otherwise WinForms leaves a one-line drop-down and clips every
            // item except the tiny scroll indicator.
            menu.Height = menu.Items.Cast<ToolStripItem>().Sum(item => item.Height) + menu.Padding.Vertical + S(4);
        };
        return menu;
    }
    private Panel ElementTile(string text, bool add, Action click, ContextMenuStrip? context)
    {
        var tile = new Panel { Width = S(176), Height = S(128), Margin = new Padding(S(12)), BackColor = add ? (IsDarkTheme ? Color.FromArgb(43, 133, 91) : Color.FromArgb(47, 157, 100)) : (IsDarkTheme ? Color.FromArgb(52, 61, 62) : Color.FromArgb(255, 232, 179)), Cursor = Cursors.Hand, AccessibleName = add ? "Add" : text };
        tile.Paint += (_, e) => { using var pen = new Pen(add ? Color.FromArgb(133, 216, 143) : (IsDarkTheme ? Color.FromArgb(181, 228, 245) : Color.FromArgb(178, 102, 28)), 2); e.Graphics.DrawRectangle(pen, 1, 1, tile.Width - 3, tile.Height - 3); };
        var label = new Label { Text = text, Dock = DockStyle.Fill, Padding = new Padding(S(9)), TextAlign = ContentAlignment.MiddleCenter, AutoEllipsis = true, Font = new Font(add ? new FontFamily("Segoe UI Symbol") : Font.FontFamily, add ? S(42) : S(16), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black };
        tile.Controls.Add(label);
        void Activate(object? _, MouseEventArgs e) { if (e.Button == MouseButtons.Left) click(); }
        tile.MouseUp += Activate; label.MouseUp += Activate;
        if (context is not null)
        {
            void ShowMenu(object? sender, MouseEventArgs e) { if (e.Button == MouseButtons.Right) context.Show((Control)sender!, e.Location); }
            tile.MouseUp += ShowMenu; label.MouseUp += ShowMenu;
        }
        new ToolTip().SetToolTip(tile, add ? "Add" : "Right-click to edit or delete"); return tile;
    }
    private void AddRegionTile()
    {
        var alias = Prompt("Region command alias"); if (alias is null) return;
        var command = Prompt("Region command", ""); if (command is null) return;
        try { _store.AddRegionCommand(alias, command); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private ContextMenuStrip? RegionContext(int id)
    {
        var menu = CreateDarkContextMenu(); menu.Items.Add("Edit", null, (_, _) => { var alias = Prompt("Region command alias", RegionAlias(id)); if (alias is null) return; var command = Prompt("Region command", _store.Data.RegionCommands[id]); if (command is not null) { try { _store.UpdateRegionCommand(id, alias, command); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); } } });
        menu.Items.Add("Delete", null, (_, _) => { if (MessageBox.Show(_t["Confirm deletion"], "GameShelf", MessageBoxButtons.YesNo) == DialogResult.Yes) { _store.DeleteRegionCommand(id); ShowGlobal(true); } }); return menu;
    }
    private static bool TryParseRgb(string value, out string hex)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries); hex = "";
        if (parts.Length != 3 || !int.TryParse(parts[0], out var r) || !int.TryParse(parts[1], out var g) || !int.TryParse(parts[2], out var b) || r is < 0 or > 255 || g is < 0 or > 255 || b is < 0 or > 255) return false;
        hex = $"#{r:X2}{g:X2}{b:X2}"; return true;
    }
    private string? PromptRgb(string currentHex)
    {
        var color = StatusColorValue(currentHex); var value = Prompt("Status color (R,G,B)", $"{color.R},{color.G},{color.B}");
        if (value is null) return null;
        if (!TryParseRgb(value, out var hex)) { MessageBox.Show("Enter three RGB values from 0 to 255, for example: 83,180,107."); return null; }
        return hex;
    }
    private string? EditStatusIcon(string vector, string color, string? fallbackGlyph = null)
    {
        using var dialog = new Form { Text = "Vector icon editor", StartPosition = FormStartPosition.CenterParent, Size = new Size(760, 700), MinimumSize = new Size(680, 640), BackColor = Color.FromArgb(35, 38, 39), ForeColor = Color.White, Font = Font };
        var canvas = new StatusIconCanvas(StatusColorValue(color), vector, fallbackGlyph) { Left = 30, Top = 32, Width = 390, Height = 390, AccessibleName = "Status icon drawing canvas" };
        var help = new Label { Left = 455, Top = 32, Width = string.IsNullOrWhiteSpace(fallbackGlyph) ? 255 : 155, Height = 150, Text = string.IsNullOrWhiteSpace(fallbackGlyph)
            ? "Draw white vectors. The fill tool stores a seed point and fills its enclosed region using all white shapes as boundaries."
            : "The original button glyph is shown whenever there is no custom artwork (including after Clear). It is a reference preview; draw white vectors to replace it.", Font = new Font(Font.FontFamily, 12, FontStyle.Bold), ForeColor = Color.FromArgb(181, 228, 245) };
        Control? originalPreview = null;
        if (!string.IsNullOrWhiteSpace(fallbackGlyph))
        {
            originalPreview = new Label { Text = fallbackGlyph, Left = 630, Top = 42, Width = 72, Height = 72, TextAlign = ContentAlignment.MiddleCenter, BackColor = StatusColorValue(color), ForeColor = Color.White, Font = new Font("Segoe UI Symbol", 32, FontStyle.Bold), AccessibleName = "Original button icon preview" };
        }
        var tools = new FlowLayoutPanel { Left = 450, Top = 200, Width = 270, Height = 245, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = dialog.BackColor };
        Button ToolButton(string text, string tool)
        {
            var button = new Button { Text = text, Width = 124, Height = 46, Margin = new Padding(5), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, BackColor = Color.FromArgb(45, 119, 115), ForeColor = Color.White, Font = new Font(Font.FontFamily, 10, FontStyle.Bold) };
            button.FlatAppearance.BorderColor = Color.FromArgb(181, 228, 245); button.Click += (_, _) => canvas.Tool = tool; return button;
        }
        tools.Controls.AddRange([ToolButton("Line", "line"), ToolButton("Straight line", "straight"), ToolButton("Hollow circle", "ellipse"), ToolButton("Hollow triangle", "triangle"), ToolButton("Hollow rectangle", "rectangle"), ToolButton("Fill enclosed region", "flood")]);
        var clear = CreateIconButton("×", "Clear vector canvas", (_, _) => canvas.Clear()); clear.Left = 450; clear.Top = 380;
        var save = CreateIconButton("✓", "Save status icon", (_, _) => dialog.DialogResult = DialogResult.OK); save.Left = 556; save.Top = 380;
        var cancel = CreateIconButton("←", "Cancel", (_, _) => dialog.DialogResult = DialogResult.Cancel); cancel.Left = 662; cancel.Top = 380;
        // Keep the descriptive text unobstructed; action controls live below the tool palette.
        clear.Top = save.Top = cancel.Top = 470;
        dialog.Controls.AddRange([canvas, help, tools, clear, save, cancel]);
        if (originalPreview is not null) dialog.Controls.Add(originalPreview);
        return dialog.ShowDialog(this) == DialogResult.OK ? StatusIconVectors.Serialize(canvas.Shapes) : null;
    }
    private void AddStatusTile(StatusKind kind)
    {
        var name = Prompt("Status name"); if (name is null) return; var color = PromptRgb("#808080"); if (color is null) return;
        var icon = EditStatusIcon(StatusIconVectors.DefaultFor(kind), color); if (icon is null) return;
        try { _store.AddStatus(kind, name, color, icon); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private ContextMenuStrip? StatusContext(StatusKind kind, int id)
    {
        var item = (kind == StatusKind.Play ? _store.Data.PlayStatuses : _store.Data.GameStatuses).Single(x => x.Id == id);
        var systemGameStatus = kind == StatusKind.Game && !string.IsNullOrWhiteSpace(item.SystemRole);
        var menu = CreateDarkContextMenu(); menu.Items.Add("Edit", null, (_, _) => { var name = Prompt("Status name", item.Name); if (name is null) return; var color = systemGameStatus ? item.Color : PromptRgb(item.Color); if (color is null) return; var icon = EditStatusIcon(item.IconVector, color); if (icon is null) return; try { _store.UpdateStatus(kind, id, name, color, icon); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); } });
        if (kind == StatusKind.Play)
        {
            var index = _store.Data.PlayStatuses.FindIndex(status => status.Id == id);
            if (index > 0) menu.Items.Add("Move earlier", null, (_, _) => { _store.MovePlayStatus(id, -1); ShowGlobal(true); });
            if (index < _store.Data.PlayStatuses.Count - 1) menu.Items.Add("Move later", null, (_, _) => { _store.MovePlayStatus(id, 1); ShowGlobal(true); });
        }
        if (!systemGameStatus && (kind == StatusKind.Play ? _store.Data.PlayStatuses : _store.Data.GameStatuses).Count > 1) menu.Items.Add("Delete", null, (_, _) => { if (MessageBox.Show(_t["Confirm deletion"], "GameShelf", MessageBoxButtons.YesNo) == DialogResult.Yes) { try { _store.DeleteStatus(kind, id); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); } } });
        return menu;
    }
    private Control TagVectorSection()
    {
        var sectionHeight = S(132) + (_store.Data.TagSchema.Count + 1) * S(154);
        var section = new Panel { Width = Math.Max(860, _content.ClientSize.Width - 100), Height = sectionHeight, Margin = new Padding(0, 0, 0, S(32)), BackColor = Color.FromArgb(35, 38, 39) };
        section.Controls.Add(new Label { Text = _t["Dimensions"] + " (single / multi-select)", Left = S(22), Top = S(16), Width = section.Width - S(44), Height = S(40), Font = new Font(Font.FontFamily, S(21), FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black });
        section.Controls.Add(new Label { Text = "Right-click a dimension to rename, change its selection type, or delete it. Left-click a value to edit it.", Left = S(24), Top = S(58), Width = section.Width - S(48), Height = S(28), Font = new Font(Font.FontFamily, S(12), FontStyle.Bold), ForeColor = Color.FromArgb(181, 228, 245) });
        var rows = new FlowLayoutPanel { Left = S(22), Top = S(105), Width = section.Width - S(44), Height = section.Height - S(113), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = false, Padding = new Padding(S(8)), BackColor = section.BackColor };
        // Keep the two selection models in contiguous groups so a global
        // editor scan never alternates single/multi/single rows.
        foreach (var dimension in _store.Data.TagSchema.OrderBy(dimension => dimension.IsMultiSelect ? 1 : 0))
        {
            var row = new FlowLayoutPanel { Width = rows.Width - S(36), Height = S(154), WrapContents = false, AutoScroll = true, Padding = new Padding(0, 0, 0, S(6)) };
            row.Controls.Add(ElementTile(dimension.Name + (dimension.IsMultiSelect ? " (multi)" : ""), false, () => { }, DimensionContext(dimension.DimensionId)));
            foreach (var value in OrderedDimensionValues(dimension)) row.Controls.Add(ElementTile(value.Value, false, () => { if (value.Key != 0) EditTagValue(dimension.DimensionId, value.Key); }, ValueContext(dimension.DimensionId, value.Key)));
            row.Controls.Add(ElementTile("＋", true, () => AddValueTile(dimension.DimensionId), null)); rows.Controls.Add(row); EnableWheelScroll(row);
        }
        rows.Controls.Add(ElementTile("＋", true, AddDimensionTile, null)); section.Controls.Add(rows); EnableWheelScroll(rows); return section;
    }
    private void AddDimensionTile()
    {
        var name = Prompt("Dimension name"); if (name is null) return;
        var type = Prompt("Dimension type: single or multi", "single"); if (type is null) return;
        var multi = type.Trim().Equals("multi", StringComparison.OrdinalIgnoreCase);
        if (!multi && !type.Trim().Equals("single", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show("Dimension type must be single or multi."); return; }
        try { _store.AddDimension(name, multi); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private void AddValueTile(int dimensionId) { var name = Prompt("Value display text"); if (name is not null) { try { _store.AddTagValue(dimensionId, name); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); } } }
    private void EditTagValue(int dimensionId, int value)
    {
        var dimension = _store.Data.TagSchema.Single(x => x.DimensionId == dimensionId); var text = Prompt("Value display text", dimension.Values[value]);
        if (text is not null) { try { _store.SetTagValue(dimensionId, value, text); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); } }
    }
    private ContextMenuStrip DimensionContext(int id)
    {
        var dimension = _store.Data.TagSchema.Single(x => x.DimensionId == id); var menu = CreateDarkContextMenu();
        menu.Items.Add("Rename", null, (_, _) => { var name = Prompt("Dimension name", dimension.Name); if (name is not null) { _store.RenameDimension(id, name); ShowGlobal(true); } });
        menu.Items.Add(dimension.IsMultiSelect ? "Change to single-select" : "Change to multi-select", null, (_, _) => { _store.SetDimensionMultiSelect(id, !dimension.IsMultiSelect); ShowGlobal(true); });
        menu.Items.Add("Delete", null, (_, _) => { if (MessageBox.Show(_t["Confirm deletion"], "GameShelf", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) { _store.DeleteDimension(id); ShowGlobal(true); } }); return menu;
    }
    private ContextMenuStrip? ValueContext(int dimensionId, int value)
    {
        if (value == 0) return null;
        var dimension = _store.Data.TagSchema.Single(x => x.DimensionId == dimensionId); var menu = CreateDarkContextMenu();
        menu.Items.Add("Edit", null, (_, _) => { var text = Prompt("Value display text", dimension.Values[value]); if (text is not null) { _store.SetTagValue(dimensionId, value, text); ShowGlobal(true); } });
        if (dimension.IsMultiSelect)
        {
            var index = dimension.ValueOrder.IndexOf(value);
            if (index > 1) menu.Items.Add("Move earlier", null, (_, _) => { _store.MoveMultiTagValue(dimensionId, value, -1); ShowGlobal(true); });
            if (index >= 1 && index < dimension.ValueOrder.Count - 1) menu.Items.Add("Move later", null, (_, _) => { _store.MoveMultiTagValue(dimensionId, value, 1); ShowGlobal(true); });
        }
        menu.Items.Add("Delete", null, (_, _) => { if (MessageBox.Show(_t["Confirm deletion"], "GameShelf", MessageBoxButtons.YesNo) == DialogResult.Yes) { _store.DeleteTagValue(dimensionId, value); ShowGlobal(true); } }); return menu;
    }
    private void ExpandGlobalSection(GroupBox group, int width)
    {
        group.Width = width - 20; group.Height = Math.Max(500, ClientSize.Height - _top.Height - 115); group.Margin = new Padding(0, 0, 0, 28);
        var list = group.Controls.OfType<ListBox>().FirstOrDefault();
        if (list is not null) { list.Left = 26; list.Top = 58; list.Width = group.Width - 52; list.Height = group.Height - 190; list.Font = new Font(Font.FontFamily, 17, FontStyle.Bold); }
        var buttons = group.Controls.OfType<Button>().ToList();
        for (var i = 0; i < buttons.Count; i++) buttons[i].Location = new Point(26 + i * 112, group.Height - 104);
    }
    private GroupBox GlobalGroup(string title, Action add, IEnumerable<Selection<int>> options, Action<int> edit)
    {
        var group = new GroupBox { Text = title, Width = 540, Height = 285, Margin = new Padding(16), Font = new Font(Font.FontFamily, 16, FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black }; var list = new ListBox { Left = 14, Top = 38, Width = 510, Height = 165, DisplayMember = "Text", DataSource = options.ToList(), Font = new Font(Font.FontFamily, 14, FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black, BackColor = IsDarkTheme ? Color.FromArgb(25, 25, 25) : Color.White }; group.Controls.Add(list);
        var addButton = TextButton(_t["Add"], (_, _) => add()); addButton.Location = new Point(14, 215); group.Controls.Add(addButton);
        var editButton = TextButton(_t["Edit"], (_, _) => { if (list.SelectedItem is Selection<int> s) edit(s.Value); }); editButton.Location = new Point(116, 215); group.Controls.Add(editButton);
        var deleteButton = TextButton(_t["Delete"], (_, _) => { if (list.SelectedItem is not Selection<int> s) return; if (MessageBox.Show(_t["Confirm deletion"], "GameShelf", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return; try { if (title == _t["Dimensions"]) _store.DeleteDimension(s.Value); else _store.DeleteRegionCommand(s.Value); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); } }); deleteButton.Location = new Point(218, 215); group.Controls.Add(deleteButton);
        return group;
    }
    private void ManageDimension(int id)
    {
        var d = _store.Data.TagSchema.Single(x => x.DimensionId == id); var action = Prompt("Dimension: rename, add, edit, or delete a value", "rename"); if (action is null) return;
        try
        {
            if (action == "rename") { var n = Prompt("New name", d.Name); if (n is not null) _store.RenameDimension(id, n); }
            else if (action == "add") { var n = Prompt("Value display text"); if (n is not null) _store.AddTagValue(id, n); }
            else if (action.StartsWith("edit ") && int.TryParse(action[5..], out var e)) { var n = Prompt("Value display text", d.Values.GetValueOrDefault(e, "")); if (n is not null) _store.SetTagValue(id, e, n); }
            else if (action.StartsWith("delete ") && int.TryParse(action[7..], out var x) && MessageBox.Show(_t["Confirm deletion"], "GameShelf", MessageBoxButtons.YesNo) == DialogResult.Yes) _store.DeleteTagValue(id, x);
            ShowGlobal(true);
        } catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private GroupBox StatusGroup(StatusKind kind)
    {
        var title = kind == StatusKind.Play ? "Play " + _t["Statuses"] : "Game " + _t["Statuses"]; var statuses = kind == StatusKind.Play ? _store.Data.PlayStatuses : _store.Data.GameStatuses;
        var group = new GroupBox { Text = title, Width = 540, Height = 285, Margin = new Padding(16), Font = new Font(Font.FontFamily, 16, FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black }; var list = new ListBox { Left = 14, Top = 38, Width = 510, Height = 165, DisplayMember = "Text", DataSource = statuses.Select(x => new Selection<int>(x.Id, $"{x.Id}: {x.Name} ({x.Color})")).ToList(), Font = new Font(Font.FontFamily, 14, FontStyle.Bold), ForeColor = IsDarkTheme ? Color.White : Color.Black, BackColor = IsDarkTheme ? Color.FromArgb(25, 25, 25) : Color.White }; group.Controls.Add(list);
        var addButton = TextButton(_t["Add"], (_, _) => { var name = Prompt("Status name"); var color = name is null ? null : Prompt("Color (#RRGGBB)", "#808080"); if (name is not null && color is not null) { _store.AddStatus(kind, name, color); ShowGlobal(true); } }); addButton.Location = new Point(14, 215); group.Controls.Add(addButton);
        var deleteButton = TextButton(_t["Delete"], (_, _) => { if (list.SelectedItem is not Selection<int> s || MessageBox.Show(_t["Confirm deletion"], "GameShelf", MessageBoxButtons.YesNo) != DialogResult.Yes) return; try { _store.DeleteStatus(kind, s.Value); ShowGlobal(true); } catch (Exception ex) { MessageBox.Show(ex.Message); } }); deleteButton.Location = new Point(116, 215); group.Controls.Add(deleteButton);
        return group;
    }
    private void AddGame()
    {
        var text = Prompt("Game ID"); if (!int.TryParse(text, out var id)) { if (text is not null) MessageBox.Show("ID must be an integer."); return; } try { _store.AddGame(id); ShowLibrary(); } catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private void DeleteGame(GameEntry game)
    {
        if (!ConfirmDeleteGame(game)) return;
        _store.DeleteGame(game.Id);
        ShowLibrary();
    }
    private bool ConfirmDeleteGame(GameEntry game)
    {
        using var dialog = new Form { Text = "GameShelf", StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(S(620), S(260)), MinimumSize = new Size(S(500), S(230)), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Color.FromArgb(35, 38, 39), ForeColor = Color.White, Font = Font };
        var prompt = new Label { Text = $"\u662f\u5426\u522a\u9664No{game.Id} {DisplayTitle(game.Title)}?", AutoSize = true, MaximumSize = new Size(S(550), 0), Left = S(28), Top = S(34), Font = new Font(Font.FontFamily, S(19), FontStyle.Bold), ForeColor = Color.White };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = S(92), FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(S(18), S(12), S(18), S(12)), BackColor = dialog.BackColor };
        Button ButtonFor(string text, Color color, DialogResult result)
        {
            var button = new Button { Text = text, Width = S(140), Height = S(58), Margin = new Padding(S(8), 0, 0, 0), DialogResult = result, Font = new Font(Font.FontFamily, S(15), FontStyle.Bold), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, BackColor = color, ForeColor = Color.White };
            button.FlatAppearance.BorderColor = Color.FromArgb(181, 228, 245);
            ApplyRoundedCorners(button);
            return button;
        }
        var cancel = ButtonFor("\u9084\u662f\u7b97\u4e86", Color.FromArgb(181, 74, 91), DialogResult.Cancel);
        var confirm = ButtonFor("\u78ba\u5b9a", Color.FromArgb(45, 139, 94), DialogResult.OK);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(confirm);
        dialog.Controls.AddRange([prompt, buttons]);
        dialog.AcceptButton = cancel;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) == DialogResult.OK;
    }
    private void ExportGame(GameEntry game)
    {
        using var d = new SaveFileDialog { Filter = "GameShelf game (*.gspkg)|*.gspkg", FileName = game.Id + ".gspkg" }; if (d.ShowDialog() != DialogResult.OK) return; try { _packages.Export(game, d.FileName); } catch (Exception ex) { MessageBox.Show(ex.Message); }
    }
    private void ImportGame()
    {
        using var d = new OpenFileDialog { Filter = "GameShelf game (*.gspkg)|*.gspkg" }; if (d.ShowDialog() != DialogResult.OK) return;
        try
        {
            var manifest = _packages.Inspect(d.FileName);
            if (_store.Data.Games.Any(g => g.Id == manifest.Game.Id)) { var local = _store.GetGame(manifest.Game.Id); MessageBox.Show($"Import refused: ID {manifest.Game.Id} exists.\nLocal: {local.Title}; note: {local.Note}; image: {!string.IsNullOrEmpty(local.ImageFile)}\nImport: {manifest.Game.Title}; note: {manifest.Game.Note}; image: {!string.IsNullOrEmpty(manifest.ImageEntry)}\nPaths, statuses, region command and each tag are retained only for comparison."); return; }
            var mappings = new Dictionary<int, (int, int)>();
            foreach (var dim in manifest.Dimensions)
            {
                var target = _store.Data.TagSchema.FirstOrDefault(x => x.Name == dim.Name); // UI confirmation, never creates a definition
                IEnumerable<int> sourceValues = dim.IsMultiSelect ? dim.GameValues : [dim.GameValue];
                if (target is not null)
                {
                    var compatible = sourceValues.Where(target.Values.ContainsKey).ToList();
                    if (compatible.Count > 0 && MessageBox.Show($"Map imported tag dimension '{dim.Name}' to the same named local dimension?", "Import mapping", MessageBoxButtons.YesNo) == DialogResult.Yes)
                        mappings[dim.SourceDimensionId] = (target.DimensionId, compatible[0]);
                }
            }
            var play = ChooseStatus(_store.Data.PlayStatuses, "Choose local play-status ID (or cancel for default)", Defaults.PlayDefaultId);
            var region = ChooseRegion();
            _packages.Import(d.FileName, manifest, mappings, play, region); MessageBox.Show("Imported. Paths were reset and need local selection."); ShowLibrary();
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Import failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
    private int ChooseStatus(List<GameStatus> statuses, string prompt, int fallback)
    {
        var choices = string.Join(", ", statuses.Select(x => $"{x.Id}={x.Name}"));
        var text = Prompt(prompt + "\n" + choices, fallback.ToString());
        return int.TryParse(text, out var id) && statuses.Any(x => x.Id == id) ? id : fallback;
    }
    private int ChooseRegion()
    {
        var choices = string.Join(", ", _store.Data.RegionCommands.Keys.OrderBy(id => id).Select(id => $"{id}={RegionAlias(id)}"));
        var text = Prompt("Choose local region command ID (or 0 for none)\n" + choices, "0");
        return int.TryParse(text, out var id) && _store.Data.RegionCommands.ContainsKey(id) ? id : 0;
    }
    private string? Prompt(string text, string value = "")
    {
        using var f = new Form { Text = "GameShelf", Width = 560, Height = 205, StartPosition = FormStartPosition.CenterParent, Font = new Font("Segoe UI", 14f), BackColor = Color.FromArgb(35, 38, 39), ForeColor = Color.White };
        var label = new Label { Text = text, Left = 18, Top = 18, Width = 510, Height = 55, ForeColor = Color.FromArgb(181, 228, 245) };
        var box = new TextBox { Text = value, Left = 18, Top = 82, Width = 510, Height = 35, BorderStyle = BorderStyle.FixedSingle, BackColor = Color.FromArgb(22, 24, 25), ForeColor = Color.FromArgb(181, 228, 245), Font = new Font("Segoe UI", 14f, FontStyle.Bold) };
        var ok = new Button { Text = "✓", Left = 405, Top = 125, Width = 58, Height = 50, DialogResult = DialogResult.OK, AccessibleName = "Confirm" };
        StyleButton(ok); ApplyRoundedCorners(ok); new ToolTip().SetToolTip(ok, "Confirm");
        f.Controls.AddRange([label, box, ok]); f.AcceptButton = ok;
        return f.ShowDialog() == DialogResult.OK ? box.Text : null;
    }
/// <summary>
/// Fixed-grid Library surface that realizes only card rows around its visible
/// viewport. This prevents a large Library from allocating a native control
/// handle and cover bitmap for every game at once.
/// </summary>
public sealed class VirtualGameCardPanel : Panel
{
    private IReadOnlyList<GameEntry> _games;
    private readonly Func<GameEntry, Control> _createCard;
    private readonly Action<Control> _configureCard;
    private readonly Dictionary<int, Control> _realized = [];
    private Size _cardSize;
    private Padding _cardMargin;
    private int _columns = 1;
    private int _rows;
    private int _rowStride = 1;
    private int _viewportTop = -1;
    private int _viewportHeight = -1;
    private int _realizedFirst = -1;
    private int _realizedLast = -1;
    private const int OverscanRows = 2;

    public VirtualGameCardPanel(IReadOnlyList<GameEntry> games, Func<GameEntry, Control> createCard, Action<Control> configureCard)
    {
        _games = games; _createCard = createCard; _configureCard = configureCard;
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
    }

    /// <summary>Updates the source list without reconstructing unchanged cards.</summary>
    public void UpdateItems(IReadOnlyList<GameEntry> games, IEnumerable<int> changedIndices, Action<Control, GameEntry> refreshCard)
    {
        _games = games;
        foreach (var index in changedIndices.Distinct().Where(_realized.ContainsKey).ToArray())
        {
            refreshCard(_realized[index], _games[index]);
        }
    }

    public void ConfigureLayout(Size cardSize, Padding cardMargin)
    {
        _cardSize = cardSize; _cardMargin = cardMargin;
        _columns = Math.Max(1, (Math.Max(1, Width) - _cardMargin.Left - _cardMargin.Right) / Math.Max(1, _cardSize.Width + _cardMargin.Horizontal));
        _rows = (int)Math.Ceiling(_games.Count / (double)_columns);
        _rowStride = Math.Max(1, _cardSize.Height + _cardMargin.Vertical);
        Height = _rows == 0 ? 1 : _cardMargin.Top + _rows * _rowStride + _cardMargin.Bottom;
        _viewportTop = -1; _viewportHeight = -1;
        _realizedFirst = -1; _realizedLast = -1;
        ClearRealized();
    }

    public void UpdateViewport(int top, int height)
    {
        if (_rows == 0) return;
        top = Math.Max(0, top); height = Math.Max(1, height);
        if (_viewportTop == top && _viewportHeight == height) return;
        _viewportTop = top; _viewportHeight = height;
        var firstRow = Math.Max(0, top / _rowStride - OverscanRows);
        var lastRow = Math.Min(_rows - 1, (top + height) / _rowStride + OverscanRows);
        var first = firstRow * _columns;
        var last = Math.Min(_games.Count - 1, (lastRow + 1) * _columns - 1);
        // Wheel messages may move a few pixels at a time.  If the virtual
        // row window has not crossed a boundary, there is no control work to
        // do: Windows can scroll the already painted surface directly.
        if (first == _realizedFirst && last == _realizedLast) return;
        SuspendLayout();
        try
        {
            foreach (var index in _realized.Keys.Where(index => index < first || index > last).ToArray())
            {
                var card = _realized[index]; _realized.Remove(index); Controls.Remove(card); card.Dispose();
            }
            for (var index = first; index <= last; index++)
            {
                if (_realized.ContainsKey(index)) continue;
                var row = index / _columns; var column = index % _columns;
                var card = _createCard(_games[index]);
                card.Margin = Padding.Empty;
                card.Bounds = new Rectangle(_cardMargin.Left + column * (_cardSize.Width + _cardMargin.Horizontal), _cardMargin.Top + row * _rowStride, _cardSize.Width, _cardSize.Height);
                _realized[index] = card; Controls.Add(card); _configureCard(card);
            }
        }
        finally { ResumeLayout(false); }
        _realizedFirst = first; _realizedLast = last;
    }

    private void ClearRealized()
    {
        foreach (var card in _realized.Values) { Controls.Remove(card); card.Dispose(); }
        _realized.Clear();
        _realizedFirst = -1; _realizedLast = -1;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) ClearRealized();
        base.Dispose(disposing);
    }
}

}

public sealed record Selection<T>(T Value, string Text)
{
    public override string ToString() => Text;
}

/// <summary>Flow layout with buffered painting to avoid card tearing while scrolling.</summary>
public sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
{
    public BufferedFlowLayoutPanel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        UpdateStyles();
    }
}


/// <summary>Prevents a closed native ComboBox from consuming the page scroll wheel.</summary>
public sealed class ScrollSafeComboBox : ComboBox
{
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Action<int>? WheelScrollRequested { get; set; }
    protected override void WndProc(ref Message m)
    {
        const int WmMouseWheel = 0x20A;
        if (m.Msg == WmMouseWheel && !DroppedDown)
        {
            var delta = (short)(((long)m.WParam >> 16) & 0xffff);
            WheelScrollRequested?.Invoke(delta);
            return;
        }
        base.WndProc(ref m);
    }
}

public static class CommandLine
{
    /// <summary>Small Windows-style argument splitter; values are passed back through ArgumentList, never through a shell.</summary>
    public static List<string> Split(string input)
    {
        var output = new List<string>(); var current = new System.Text.StringBuilder(); var quote = false;
        foreach (var ch in input)
        {
            if (ch == '"') { quote = !quote; continue; }
            if (char.IsWhiteSpace(ch) && !quote) { if (current.Length > 0) { output.Add(current.ToString()); current.Clear(); } }
            else current.Append(ch);
        }
        if (quote) throw new InvalidOperationException("The region command contains an unmatched quote.");
        if (current.Length > 0) output.Add(current.ToString());
        return output;
    }
}
