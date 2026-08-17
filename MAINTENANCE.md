# GameShelf maintenance and troubleshooting guide

**Current application patch:** `2.0.0a4` (alpha)

## Patch history

### 2.0.0a4 (alpha)

- Fixed the remaining fullscreen flashing path when the launcher is closed while fullscreen and later restored fullscreen. During construction, the original fullscreen transition occurs before a native form handle exists, so its post-transition callback cannot run; Windows then sends resize notifications after the normal resize handler is attached. Fullscreen resize notifications are now excluded from the interactive drag-layout timer, and the `Shown` event performs one explicit first-show layout refresh with a diagnostic log entry.

### 2.0.0a3 (alpha)

- Corrected the detail Section 1 sizing rule: its right half now expands to the full measured height of the number, title, and every one-per-line tag, with no internal tag scrollbar. The 3:4 cover directly follows that resulting height rather than limiting the right-side content to the cover column's maximum height.

### 2.0.0a2 (alpha)

- Detail cover sizing now follows the actual height of the number, title, and tag area instead of using half the page width as its source. The cover remains 3:4, is centred in the left half, and aligns vertically with the corresponding headline/tag section rather than expanding to an unnecessary fullscreen height. Detail tags now appear one per row and use the spaced form `Dimension : value`.
- The Library-card dimension selector now measures each dimension name and assigns its tile enough width and height for the text, rather than relying on the unreliable auto-size behaviour of appearance-button checkboxes.

### 2.0.0a1 (alpha)

- Fixed continuous fullscreen flashing after `F11`. Changing `FormBorderStyle` and `WindowState` programmatically raises `Resize` but does not raise `ResizeEnd`; the prior resize-throttling timer could therefore remain active and rebuild the complete page every 33 ms. Fullscreen transitions now suspend that timer, ignore their intermediate resize events, and perform exactly one responsive layout refresh after Windows commits the final client size.
- Debug logs now record the start and completion of each fullscreen transition, including the final client size and page, so a later resize-loop report can be diagnosed directly from `log/gameshelf-YYYY-MM-DD.log`.

### 2.0.0a (alpha)

- Game-path availability is now the sole automatic source for the game-status lamp. An existing registered EXE always selects the locked green **Installed locally** system status. When that EXE is unavailable, only green Installed locally changes to locked purple **Data missing**; red **In other machine**, purple Data missing, and blue **Storaged** remain selected. Save-path availability never changes the game-status lamp or the availability of Launch.
- Entering Library refreshes this rule for every game. Entering game detail refreshes the selected game, independently colours game and save paths, shows Launch only for a valid game EXE (or an already tracked running process), and continues to show invalid save paths without disabling launch.
- Game detail has three equally wide stacked sections: portrait 3:4 cover / headline and tags, note / side-by-side status lamps, then game path, save root, save path, region command, and export. Detail tag chips read `Dimension: value`; a double-click (within 0.8 seconds) on the play-status lamp advances it cyclically.
- Play status is no longer edited in the first-level game editor. In second-level management, right-click a play-status tile to move it earlier or later by swapping neighbouring status positions; this order is also the detail-page cycle order.
- Library cards show up to three administrator-selected dimensions. In Library management, use the dimension-selector control to choose which three dimensions are displayed; filtering still covers every dimension.
- The vector icon editor is shared by status lamps and all logical header/action button glyphs. Its tools are freehand line, straight line, hollow circle, hollow triangle, hollow rectangle, and Paint-style enclosed-region white fill. Vectors are stored as white artwork over the status/button colour background. Built-in system game-status colours are deliberately fixed to preserve the path-availability rules.
- There is no global rendering frame loop. Responsive page reconstruction while the user resizes a window is throttled to at most 30 layouts per second, followed by one final layout at the end of the resize gesture. This reduces resize stutter without lowering normal interaction or process-event responsiveness.

### 1.4

- Stable release of the new diagnostic logging system, distinguishing it from 1.3. GameShelf now records operational diagnostics in `log/gameshelf-YYYY-MM-DD.log` next to the executable and automatically removes its own logs older than 30 days.
- Stable builds default to the `Information` threshold; `GAMESHELF_LOG_LEVEL` may raise detail temporarily for investigation without rebuilding.

### 1.4.0a (alpha)

- Added daily rolling diagnostic logs in `log/` next to the executable. Log files use `gameshelf-YYYY-MM-DD.log` and startup deletes GameShelf log files older than 30 days.
- Added `Trace`, `Debug`, `Information`, `Warning`, `Error`, and `Critical` levels. Alpha versions (an `a` suffix) default to `Debug`, while stable versions without that suffix default to `Information`. Set `GAMESHELF_LOG_LEVEL` to any level name (for example `Debug` or `Trace`) before starting the application to override that minimum.
- Startup, persistence, process tracking, launch/stop operations, and unhandled exceptions now emit diagnostic events. Log write failures are deliberately ignored so an unavailable log directory cannot prevent GameShelf from running.
- Local shortcut policy: `Launcher.exe` is the fixed launch target. Each new build replaces that file first, then copies it to `Launcher_<version>.exe` for a versioned release asset. Existing shortcuts only need to be changed once to point to `Launcher.exe`.

### 1.3

- First stable `1.3` package. Published executables use the portable filename convention `Launcher_<version>.exe`; this release is `Launcher_1_3.exe`.
- Includes the Library back control for game-management mode and the game-detail back control introduced in the 1.3 preview patches.

### 1.3a1

- Game detail now also presents Back. Clicking it, or pressing `F4`, returns directly to Library.

### 1.3a

- Game-management mode now presents the normal Back button. Clicking it, or pressing `F4`, exits management mode and returns to the standard Library; Add and Import shift right as usual.

### 1.2.2a2

- The filter control is now custom-drawn as a shallow solid white downward triangle with a white vertical stem extending from its lower point, rather than depending on a font glyph.

### 1.2.2a1

- Status-light vectors now render inside a centred square viewport. Library status blocks retain their full-width background colour, while circles, squares, clouds, and custom vectors preserve their proportions instead of stretching to the rectangular light.

### 1.2.2a

- The header filter control now uses a solid white downward triangle.
- Status lights now render a persisted white vector icon on their status-colour background, both in Library cards and game detail. Built-in game icons are filled circle (Installed locally), outline circle (In other machine), cross (Data missing), and outline cloud (Storaged). Built-in play icons are outline square (Not played), half-filled square (Playing), and filled square (Completed).
- Added the blue `Storaged` game status. Existing data migrates `In progress` to `Playing`, `Not local, available elsewhere` to `In other machine`, and supplies missing built-in status icons without overwriting an existing custom icon.
- Adding or editing either status type now opens the Status vector editor after its RGB colour is chosen. The editor stores a compact JSON vector list and provides line, outline/filled circle, outline/filled square, cloud, clear, save, and cancel controls. Vectors are always white; the selected status colour is the canvas background.

### 1.2.1a2

- Opening a game detail page now performs a single native Windows process-image-path query for that game's configured executable. It enumerates current process IDs through Tool Help and calls `QueryFullProcessImageName` for each accessible process, then compares the full normalized path. A match immediately attaches exit tracking and presents the blue Stop control.
- This is the authoritative recovery path after GameShelf restarts. It does not use WMI, .NET `MainModule`, saved launcher PID, or an interval timer, so it remains available on systems where WMI subscriptions are denied.

### 1.2.1a1

- Fixed recovery for games whose configured EXE is a short-lived launcher. A direct launch now makes one Windows Tool Help process-tree snapshot after startup and also at the launcher's exit event, then transfers tracking to the live descendant process. This is event-driven plus bounded one-shot discovery, never an interval poll.
- The tracked descendant's PID and exact start time replace the launcher identity in `Settings.RunningGameProcesses`. A later GameShelf restart first recovers that child directly. For an older stale launcher entry, startup performs one bounded same-game-directory/start-time recovery attempt before clearing the stale record.

### 1.2.1a

- Fixed a direct-launch tracking race. The executable returned by GameShelf's own direct launch is now attached immediately, without waiting to inspect its module path. This prevents a real running game from incorrectly leaving the detail control in green Play state.
- A tracked launch now persists the process ID and exact UTC start time in `Settings.RunningGameProcesses`. On restart GameShelf first validates that stored identity and reattaches, so correct Play/Stop state and the later exit event no longer depend on WMI access.
- The WMI process-start watcher remains an optional event-driven enhancement for region-launcher children. On systems that reject WMI event subscriptions, direct launches and restart recovery continue to work fully; the denial is logged once for diagnostics.

### 1.2.0a

- GameShelf now tracks registered game executables without periodic polling. It makes one recovery scan at startup, then attaches to `Process.Exited` for each matched game and uses the Windows WMI process-start event to detect games created by a region launcher.
- On a game detail page, a tracked running game changes the launch button into a blue stop button. Stop sends the game's normal window-close request; the button changes back only when Windows reports that the game process actually exited.
- Restarting GameShelf while a registered game is running reattaches to the matching executable and continues to receive its exit event. If the Windows WMI service is unavailable, direct launches and already-running recovered games still work through `Process.Exited`; region-launcher child-process diagnostics are recorded in `log/`.

### 1.1.0a

- The normal window now uses the native Windows title bar and window manager. Windows supplies caption buttons, the system menu, snapping, drag behavior, and resize cursors; GameShelf only enforces the 16:9 normal-window aspect ratio. `F11` remains a separate borderless fullscreen mode.
- Startup restores either Library or the selected game detail page. Closing while editing intentionally restores the selected detail page rather than an unfinished editor; closing in second-level management restores Library.
- Game executable paths are stored relative to a configurable `rc` root. Existing absolute paths migrate automatically when an ancestor folder named `rc` is found. Detail shows the short relative path but resolves it for launch and Explorer.
- Save methods are now selectable save roots. A root maps a friendly name to `.` (the game directory) or a Windows environment-variable path template. The defaults are Game directory, User Documents, and User AppData; save paths are stored relative to the selected root.

### 1.0.3a

- Detail-path rendering now inserts actual newline characters after path separators once a line reaches the calculated limit. This is required because the Windows Forms vertical `FlowLayoutPanel` does not reliably honour only soft-break opportunities for its auto-sized child labels.

### 1.0.2

- Game and save paths in game detail insert invisible break opportunities after path separators and use the current content width as their maximum width. Long paths wrap onto multiple lines instead of widening the layout or causing horizontal scrolling.

### 1.0.1

- Invalid or missing game/save paths are rendered bright red in game detail; valid paths remain light green.
- Custom border resize areas explicitly handle `WM_SETCURSOR`, so edges and corners show the standard horizontal, vertical, or diagonal resize cursor while normal-window resizing is available.

## Purpose and runtime

GameShelf is a personal Windows game-library manager. It is a Windows Forms application targeting `win-x64` and is published as a self-contained, single-file executable. A target computer needs 64-bit Windows and write permission in the directory that contains `GameShelf.exe`; it does **not** need a separately installed .NET runtime.

The executable always uses its own directory as the application root. Do not run it from a read-only directory, archive, or protected installation location.

## Files and formats

```text
GameShelf.exe
savedata/
  gameshelf.json       # UTF-8 JSON database
  images/              # managed PNG covers
log/
  gameshelf-YYYY-MM-DD.log # daily diagnostic log; retained for 30 days
```

Copy both `GameShelf.exe` and `savedata/` to migrate an existing library. Copying only the executable starts an empty library.

### `gameshelf.json`

The top-level document (`AppData`) contains:

| Field | Format / purpose |
| --- | --- |
| `Version` | Database format version; currently `1`. |
| `Settings` | Window state, last safe page/selected game, selected tag filters, the up-to-three Library-card dimensions, custom button vectors, and tracked running-process identities. Library and game-detail page state are restored at launch. |
| `RcRootPath` | Absolute shared `rc` folder. `GamePath` values are relative to this folder. |
| `SaveRoots` | Save-root mappings: an ID, display name, and `.` or Windows environment-variable path template. |
| `RegionCommands` | `int -> command line` mapping. ID `0` means no region command. |
| `RegionAliases` | `int -> display alias` mapping for region commands. UI displays aliases, not command lines. |
| `TagSchema` | Ordered tag dimensions. Each dimension has an ID, a Unicode name, and `int -> Unicode string` values. Value `0` is the reserved `none` value. |
| `PlayStatuses`, `GameStatuses` | Status objects with ID, display name, `#RRGGBB` color, default flag, and a persisted white vector icon. System game statuses additionally have `SystemRole`; their colours are fixed by the application. |
| `Games` | Game records. |

A game record contains an integer `Id`, Unicode `Title`, managed `ImageFile`, `Note`, executable `GamePath`, `SaveRootId`, file/folder `SavePath`, play/game status IDs, region-command ID, and a `Tags` list. `GamePath` is relative to `RcRootPath`; `SavePath` is relative to the root selected by `SaveRootId`. The legacy `SaveMethod` field is retained only for backwards-compatible JSON reads and is no longer used by the UI. The position in `Tags` corresponds to the position of the dimension in `TagSchema`.

On startup, `DataStore.Normalize()` repairs missing reserved values, invalid IDs, missing aliases, tag-list length, and invalid filter values. In particular, `none` (`0`) is removed from active filters because it is not a selectable filter condition.

### Covers

Input covers may be PNG, JPG, or JPEG. The selection flow opens a crop window: drag the image to position it and use the slider to zoom. The result is stored as a managed PNG in `savedata/images/` at **480×640 (3:4 portrait)**. The input limit is 20 MB and 40 million pixels.

### Export packages

Export uses `.gspkg`, a ZIP archive containing `manifest.json` and, when present, `image.png`. Imports never restore local game/save paths; these must be chosen again on the receiving computer. Existing game IDs are intentionally refused during import.

## User interface and controls

The interface uses a permanent dark palette and bold English UI text. Data strings remain Unicode and can contain Chinese or other languages.

- `F2`: Library / action mode.
- `F3`: Enter management mode from Library; open first-level edit from a game detail page; open second-level management from first-level edit.
- `F4`: Go back one edit level.
- `F11`: Toggle fullscreen.
- `Esc`: Leave fullscreen.
- `Alt+F4`: Close.

The standard Windows title bar supplies minimize, maximize/restore, close, the system menu, snapping, and resize cursors. Normal-window resizing is locked to **16:9 during the drag** through `WM_SIZING`; this is not merely corrected after mouse release. The minimum normal size is 720×405. `F11` is the only borderless mode.

Buttons that are unavailable for the current page are not created. Remaining header buttons shift left rather than leaving disabled gaps. Tooltips describe buttons and status blocks.

### Library

- Normal mode: only left click on a game card opens its detail page. Right click does nothing.
- Management mode: right click on a card asks to delete it.
- Cards show a large ID/title, compact tag chips from up to three configured dimensions, and two status-color blocks at the bottom. Library management exposes the three-dimension selection dialog; filters are not limited by this card-display setting.
- The filter button is at the top right. The filter popup permits multiple values per tag dimension; values in one dimension are ORed, while different dimensions are ANDed. `none` is excluded. Clear removes all filters. Active filter chips are right aligned to the left of the filter button.

### Game detail and first-level edit

The detail presentation is horizontally centered and uses three full-width sections. Its cover is 3:4 portrait, the headline is aligned beside it, the note/status section splits equally down the centre, and the two status blocks are side-by-side. Detail tag chips show `Dimension: mapped value`. Valid game/save path text is light green and clickable; invalid or missing paths are bright red and not clickable. Long paths receive actual line breaks after separators, so they use multiple lines rather than horizontal scrolling even inside the vertical `FlowLayoutPanel`.

Each time a detail page opens, the game path is checked. A valid EXE forces the locked green Installed locally lamp and makes Launch available. An invalid EXE changes only Installed locally to the locked purple Data missing lamp and hides Launch (unless a previously tracked process is still running). Save-path validity only affects its own path-text colour; it does not change the game lamp or Launch. Double-click the play-status lamp within 0.8 seconds to advance to the next configured play status.

Launching resolves the stored relative executable against `RcRootPath`, then uses the executable's directory as `WorkingDirectory`, matching the behavior of double-clicking that EXE in Explorer. If a region command is selected, GameShelf starts that command with the resolved executable as its final argument and still uses the game directory as the working directory.

The detail launch control is event-driven: it is blue with a stop glyph while the tracked executable is running and returns to Play on its Windows exit event. GameShelf does not poll every few seconds. Opening a detail page performs one native process-image-path query for that exact executable and attaches when it is found; WMI process-start events remain an optional enhancement for games spawned by region launchers after GameShelf starts. Stop requests a normal application close, not a forced process kill.

The edit page shows the current cover and places cover selection/cropping and save actions after the other settings. Choice controls are populated from direct `Selection<int>` items rather than delayed data binding, so the saved play status, game status, region alias, and tags are retained when edit opens. Status dropdowns display their current status color.

### Second-level management

Each management section grows only enough to contain its tiles. The overall page scrolls.

- `rc root folder`: left click its tile to choose the shared folder that contains all game resources. Changing it is the only path change needed after moving the `rc` tree to another computer.
- Save roots: right-click a tile to edit its friendly name and its path template, or delete it. `.` means the selected game's directory; `%USERPROFILE%\Documents` and `%USERPROFILE%\AppData` resolve for the current Windows user.
- Region commands: add/edit requires both alias and command line; delete is available by right click. Only aliases are presented in UI.
- Statuses: left click/edit and right-click context menu allow editing name, RGB color (`R,G,B`, each 0–255), and the white vector icon. The four system game statuses (Installed locally, In other machine, Data missing, Storaged) retain their fixed semantic colours and cannot be deleted; custom statuses can be managed normally. Play-status context menus also offer Move earlier / Move later, swapping adjacent values.
- Button icons: a dedicated section uses the same vector editor for every logical header/action glyph. The tools are freehand line, straight line, hollow circle, hollow triangle, hollow rectangle, and seed-fill of a closed transparent region bounded by white artwork.
- Tag dimensions: right click a dimension to rename/delete it. Left click a non-`none` value to edit it; right click it for Edit/Delete. Deleting a value resets affected games to `none`.

## Storage and validation behavior

- At run time `GamePath` resolves to a fully qualified existing `.exe` beneath `RcRootPath`; the persisted value is relative. A legacy absolute path remains readable until an `rc` root can be assigned.
- At run time `SavePath` resolves to an existing file or directory beneath its selected save root; the persisted value is relative.
- Invalid/missing resolved paths are retained for repair. An invalid game EXE changes only the Installed locally game status to Data missing; save-path validity does not alter game status.
- Database writes are serialized to a temporary file and atomically moved into place. A failed write leaves the original database intact.
- A game image is only replaced after its managed PNG has been successfully staged.

## Source map for maintainers

| File | Responsibility |
| --- | --- |
| `GameShelf/MainForm.cs` | All Windows Forms UI, responsive layout, input handling, launch behavior, crop UI, filtering. |
| `GameShelf/DataStore.cs` | JSON persistence, normalization, validation, mutations, and log writing. |
| `GameShelf/Domain.cs` | Persisted data classes and defaults. |
| `GameShelf/ImageService.cs` | Cover validation and 480×640 PNG processing. |
| `GameShelf/ImportExport.cs` | `.gspkg` ZIP package export/import. |
| `GameShelf/AppPaths.cs` | Paths rooted next to the executable. |
| `GameShelf/Program.cs` | Application entry point and top-level error dialog. |
| `GameShelf/GameProcessTracker.cs` | Event-driven executable recovery, WMI start-event subscription, process exit tracking, and stop requests. |
| `GameShelf/StatusIconVectors.cs` | Persisted status-vector format, white icon renderer, built-in icon definitions, and the drawing canvas used by the status editor. |

## Diagnostics checklist

1. **A game is missing from Library:** inspect `savedata/gameshelf.json` to verify that the game record exists, then inspect `Settings.SelectedTagFilters`. Restarting the current build clears invalid `0`/`none` selections. Use the filter popup's Clear button if real filters remain.
2. **A selector shows the wrong value:** compare the relevant game ID field with the available status/region/tag IDs in JSON. The edit page uses direct choice items; an invalid ID is normalized to a default/`none` value on startup.
3. **A game does not launch:** verify `RcRootPath`, then combine it with the game's relative `GamePath` and verify the resulting executable works when double-clicked. GameShelf uses the EXE directory as working directory. For a region command, check the stored command line and its executable/arguments.
4. **A save path is missing after moving machines:** verify its `SaveRootId` points to the intended `SaveRoots` entry and that the root's template expands correctly for the current user. If the game's resources moved, update only `RcRootPath`.
5. **Data cannot save:** ensure the folder containing `GameShelf.exe` and its `savedata` subfolder are writable; inspect the newest file in `log/`.
6. **Migration or backup:** close GameShelf first, then copy `GameShelf.exe` and the full `savedata/` tree. Do not edit JSON while the application is running.
7. **A running game is not detected:** confirm the registered executable is the actual process rather than only a launcher. Opening its detail page asks Windows once for every accessible process's full image path; if the configured EXE is not the process that remains alive, choose the remaining game EXE as the game path or inspect the launcher/child-process note in the patch history. GameShelf performs no repeated scan by design.

## Build and publish

The project is `GameShelf/GameShelf.csproj`. Publish a self-contained executable for 64-bit Windows with:

```powershell
dotnet publish .\GameShelf\GameShelf.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

The published executable is `publish/GameShelf.exe`. Do not delete `publish/savedata/` when publishing updates; it contains user data.
