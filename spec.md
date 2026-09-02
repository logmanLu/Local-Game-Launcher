# GameShelf architecture specification

**Current application specification:** 2.2.0b (beta)
**Savedata format:** v5
**Scope:** This document describes the implemented application architecture and its persistent-data contract. It is the source of truth for future maintenance; `MAINTENANCE.md` contains the chronological patch history and troubleshooting record.

## 1. Product boundary and platform

GameShelf is a local, single-user Windows game-library launcher. It stores game metadata, cover images, tags, paths, status definitions, button artwork, and UI state beside the executable. It does not require a server, account, cloud service, Steam integration, or Internet connection to run.

The application is a .NET 10 Windows Forms application targeting 64-bit Windows:

- Target framework: `net10.0-windows`
- Runtime: `win-x64`
- Distribution: self-contained, single-file Windows executable

A target computer needs 64-bit Windows and write permission to the executable directory. A separately installed .NET runtime is not required by a published build.

## 2. Deliverables and application root

The compiled assembly name is `GameShelf.exe`. The distributable launcher convention is:

- `Launcher.exe` is the fixed local launch target; a shortcut may point to it.
- `Launcher_<version>.exe` is the versioned copy, for example `Launcher_2_0_1.exe`.
- Alpha, beta and release-candidate versions have an `a`, `b` or `rc` suffix. Every preview package retains Debug diagnostics; a version with no suffix is a stable Release package.

`AppContext.BaseDirectory` is the application root. The executable must remain beside its data folders:

```text
<application root>/
+-- Launcher.exe                         fixed local launcher (when distributed)
+-- Launcher_<version>.exe               versioned launcher(s)
+-- savedata/
|   +-- gameshelf.json                    primary database
|   +-- images/                           managed cover images
|   +-- backups/                          automatic pre-migration backups
+-- log/
    +-- gameshelf-YYYY-MM-DD.log          daily rolling diagnostics
```

The user-owned `savedata/` directory is never included in source-control or release assets. `SteamEmu/`, game folders, and game saves are external resources and are not managed application files.

## 3. Main components

| Component | Responsibility |
| --- | --- |
| `Program` | Initializes WinForms, paths and logging; owns the per-application-root single-instance mutex, top-level exception reporting and the `DataStore`/`MainForm` lifetime. |
| `AppPaths` | Resolves the application-root, savedata, image, database and log locations. |
| `AppLog` | Dependency-free daily rolling diagnostics. Logging failures never stop the launcher. |
| `Domain` | Persistent DTOs, default statuses/save roots, and data-format version constants. |
| `DataStore` | Loads, migrates, normalizes, validates and atomically writes `gameshelf.json`; all state changes pass through this layer. |
| `MainForm` | Owns all pages, native menu integration, responsive layouts, navigation, input, edit dialogs and UI-state persistence. Its rebuilt control trees dispose retired controls, resize subscriptions, rounded-corner regions and owned Library cover bitmaps. The Library uses a virtual fixed grid so only cards near the viewport own controls and bitmaps. |
| `ImageService` | Imports cover images, stores them under `savedata/images`, and supports the image crop workflow. |
| `PackageService` (`ImportExport.cs`) | Exports/imports individual-game packages and reconciles imported schema entries. |
| `Localizer` | Provides the four UI-language dictionaries. Game data remains Unicode and is not translated. |
| `StatusIconVectors` / icon editor | Stores and renders white vector artwork over coloured button/status backgrounds. |

## 4. Persistent data contract

`savedata/gameshelf.json` serializes `AppData`. Its current `Version` is `5`.

| Area | Stored data |
| --- | --- |
| `Settings` | UI language, persisted launcher-version policy and last versioned executable, last supported page and selected game, normal/fullscreen window state and bounds, title/status/tag filters, Library-card dimension choices, and custom button vectors. |
| `RcRootPath` / `DefaultImageFile` | Absolute path of the common `rc` game-resource root and the managed fallback cover filename used for games without an individual cover. |
| `SaveRoots` | Named Windows path templates used as save roots. |
| `RegionCommands` / `RegionAliases` | Region-launch commands and their short display aliases. |
| `TagSchema` | Ordered single-select or multi-select dimensions, each with an integer ID, mapped display values, and a persisted value order (multi-select values can be moved earlier/later). |
| `PlayStatuses` / `GameStatuses` | Ordered status definitions: ID, display name, RGB/hex colour, white vector icon, default flag, and (for system game statuses) role. |
| `Games` | Individual game records: ID, title, managed cover filename, note, relative game/save paths, selected save root, statuses, region command, one value per single-select dimension, and one-or-more values per multi-select dimension. |

Every persistent DTO supports `JsonExtensionData`. Therefore unknown properties from a newer launcher are round-tripped rather than intentionally discarded, including when first-level editing replaces a `GameEntry` with its draft copy. Older formats are backed up in `savedata/backups/` before in-place normalization. A newer savedata version is not downgraded.

The legacy `GameEntry.SaveMethod` field is retained only so older data can be read; current UI and logic use `SaveRootId` and `SavePath`. Normalization clears the legacy field and JSON ignores its default value, so it is not emitted again. The retired `Settings.RunningGameProcesses` field is removed during normalization so it cannot be re-emitted from old savedata.

### 4.1 Path rules

- `GamePath` is stored relative to `RcRootPath`. Selecting a game executable outside the configured `rc` root is rejected.
- `SavePath` is stored relative to the selected `SaveRoot`.
- A save root of `.` means the directory containing the resolved game executable.
- Built-in portable save roots are `.` (Game directory), `%USERPROFILE%\\Documents`, and `%USERPROFILE%\\AppData`. Environment variables are expanded on the current computer, supporting a different Windows user profile.
- Absolute legacy paths are readable for compatibility, but new selections are normalized to their corresponding relative form.
- The game-file picker starts in the configured `rc` directory. Save file/folder pickers receive the resolved directory for the currently selected save root exactly (game directory for `.`, or the current user's AppData/Documents root). The Save file dialog uses an isolated native-dialog client state, and the Save folder dialog sets both its `InitialDirectory` and `SelectedPath`, so a previously visited folder cannot override that requested root.
- For a non-`.` save root, first-level edit can create a Windows directory Junction at the resolved `SavePath`. It first creates `Save_Junction_<game-id>` beside the game executable as the target, then invokes PowerShell's `New-Item -ItemType Junction -Path` with the resolved save path as the Junction path. Paths are UTF-8/base64 passed into an encoded PowerShell command, so spaces and Unicode filenames are not re-parsed by `cmd.exe`. The Save path must not already exist; failures are surfaced to the user and written to the log. This does not alter the stored portable path representation.

## 5. Permanent visual and window model

The presentation is permanently dark; there is no day/night theme or runtime theme selection. Standard UI languages are English (`en`), Traditional Chinese (`zh-Hant`), Simplified Chinese (`zh-Hans`) and Japanese (`ja`). All stored game text supports Unicode independently of the UI language.

GameShelf uses the normal native Windows title bar. Windows owns caption dragging, system menu, Snap layouts, resize borders and minimize/maximize/close controls. Directly below it is a native Windows menu bar:

- The menu is collapsed by default and reveals when the pointer reaches the top reveal band. It retracts after the pointer leaves the menu area; this works in normal and F11 fullscreen windows.
- **Version** (click or `Alt+V` after revealing the menu) stores one launch policy. It offers **Automatically select latest version**, **Automatically select latest stable version**, each available stable *major.minor* series, and at most one eligible preview (alpha, beta or release candidate).
- A stable series entry such as `2.0` resolves to the highest available stable patch in that series at the moment it is selected, for example `Launcher_2_0_1.exe`. The resulting exact patch is pinned, so even a later `2.0.2` does not replace it automatically.
- Automatic policies are passive: startup first opens the last versioned executable that actually ran, without enumerating release files. An updated fixed `Launcher.exe` may retain itself only when its embedded version is newer than that remembered auto-latest preview and its matching versioned executable is present; this prevents an old parser from hiding a newly introduced preview suffix. After the first page is shown, a background scan discovers the eligible automatic target. If it is newer, the Version menu exposes **Update to <version>**; only choosing that item restarts the launcher. A first installation with no remembered version performs one bootstrap scan.
- A preview is shown only when its core version is strictly newer than the highest available stable release. For example, `2.0.1a` is shown with `2.0.0`, but is hidden when stable `2.0.1` exists. When alpha, beta and release candidate share a core version, `rc` wins over beta, and beta wins over alpha. Selecting a preview pins that exact executable.
- The persisted launcher policy is resolved during form loading, before the first paint. All persisted window-state restoration is deliberately deferred until that resolution succeeds in the final executable. The fixed `Launcher.exe` skips saving state while handing off, so it cannot overwrite a persisted fullscreen request or briefly enter fullscreen/maximized before the chosen version takes over.
- **Language** (click or `Alt+L`) persists the chosen UI language and restarts the application.
- The same native menu and reveal behaviour remain available in F11 fullscreen.

Normal resizing is constrained to 16:9 by the native `WM_SIZING` path, with a minimum size of 720 × 405. During an interactive resize, a dark presentation mask hides intermediate redraws; a single responsive layout refresh occurs when sizing ends. This avoids an animation/frame loop and the previous fullscreen redraw flicker.

All normal pages are scrollable where needed. Buttons use coloured rounded square/rectangle backgrounds and explanatory tooltips. If an action is unavailable on a page, its button is omitted and the remaining header controls close the gap.

## 6. Navigation and persisted page state

| Input | Result |
| --- | --- |
| `F2` | Return to Library from a non-Library page. It is intentionally inert while already on Library, including Library management mode. |
| `F3` | Game management or global management, according to context |
| `F4` | Back to Library or the preceding supported page |
| `F5` | In normal Library or game detail only, refresh resolved path availability and path-derived game status. It is inert in management/edit pages. Library uses its existing background card reconciliation; detail preserves its vertical scroll while recomputing Save-path colour and Launch visibility. |
| `F11` | Toggle fullscreen |
| `Esc` | Exit fullscreen |
| `Alt+F4` | Close the launcher |

The last page is persisted only for Library and game detail. If the launcher is closed on detail, it reopens on that game detail; closing while in first-level edit reopens at that game detail instead. Other management pages reopen at Library to avoid restoring incomplete edit state.

Only one GameShelf process may run for a given application root at a time. A normal second start reports that the launcher is already running and exits, preventing concurrent savedata writes. An orderly version/language handoff passes a private command-line marker to its successor; that successor waits up to eight seconds for the predecessor to relinquish the same mutex.

## 7. Library / Home

Library shows responsive game cards in ascending numeric game-ID order. In normal mode, only a left click opens a game detail page. In game-management mode, right-clicking a card performs the management context action, including deletion; cards do not open games in that mode. Deletion uses a dark GameShelf confirmation dialog that names the game number and title, offers Confirm and a default/cancel "never mind" action, and treats Enter as cancel.

Switching into or out of Library management mode does not recreate the card grid. Existing cards evaluate their click behaviour against the current management flag, preserving the Library scroll position while preventing game entry in management mode. The content and card grid use double-buffered surfaces. Wheel scrolling explicitly notifies the virtual grid after a programmatic scrollbar update, because Windows Forms does not reliably raise `Scroll` for that operation; it never forces a full page layout for each wheel tick. The fixed Library grid is virtualized: only the rows visible in the viewport plus six rows of overscan above and below are realized as WinForms card controls. This intentionally trades a bounded amount of memory for smoother rapid scrolling. Offscreen cards are disposed with their owned display bitmaps, then recreated on demand when they re-enter the buffered viewport. A 72-entry LRU cache retains decoded 480×640 cover bitmaps; decoding happens on a worker thread and UI controls receive only a cloned completed bitmap. A wheel movement which remains inside the same realized row window performs no control creation, disposal, or layout work. Entering any detail or editing page detaches (rather than disposes) the current virtual grid and records an in-memory presentation snapshot plus its scroll offset. Returning to Library reattaches that cached frame immediately, then reconciles it after the first UI frame. Path availability checks, status derivation, title/tag filtering and numeric sorting use an immutable Library snapshot on a worker thread; only the final status updates and card-region patches run on the UI thread. Matching IDs and presentation layout are retained; only changed realized card regions (cover, title/number, lamps, single tags, or multi tags) are patched. A filter/order/schema/window-layout change safely rebuilds the virtual grid.

Each card has a fixed portrait 3:4 cover at upper-left, enlarged by 1.2× from the prior card layout. Its two status lamps stack vertically to the cover's right and together match the cover height. The decimal game number is below the cover, with a measured height that safely holds large multi-digit IDs. Every card reserves exactly two title lines even when its title uses only one, so all card heights remain uniform. The compact two-line single-select strip follows immediately, followed by two separate compact, single-line rows for the chosen multi-select dimensions. An individual multi row scrolls horizontally on overflow and never consumes the next dimension's row. The title renders a literal `\\n` as a line break. Every multi value uses its own orange chip, while single chips use purple. When no multi-display selection has been stored, the first two multi-select dimensions are selected automatically. Filtering always considers every dimension.

The title search field sits directly left of the rightmost filter control and combines with every other condition using AND; its adjacent clear button removes the search text. The filter control opens a modal filter:

- `none` values cannot be selected.
- Multiple values within one single-select dimension are ORed; different dimensions are ANDed.
- A multi-select dimension matches when the selected values intersect that game's selected values.
- Single-select and multi-select tag-dimension sections are contiguous groups in that order, with their existing purple/orange tile colours.
- Play status and game status are independent single-choice filters.
- Tag, play-status and game-status selection tiles use distinct colours.
- Clear removes all conditions.
- After applying, active selection chips appear immediately to the left of the filter control, right-aligned and wrapping when necessary.

In Library management, the dimension selector chooses up to three displayed single-select dimensions plus two displayed multi-select dimensions. Its choice tiles measure to the dimension text rather than using fixed-size controls.

## 8. Game detail page

**Current Section 3 rule:** There is no separate Save root line. Its `Save:` line combines the root indicator and stored relative path: it begins with `.` / `AppData` / `Documents`, never the current user's full profile path, followed by the relative save path.

Entering game detail always refreshes the selected game path state. The page is horizontally centred and consists of three equal-width stacked sections; no child may cross its invisible section boundary.

1. **Section 1** — left **1A** is a 3:4 portrait cover; right **1B** contains number/Launch, title, and tags. `1B1` splits number and Launch evenly, and `1B1`, `1B2`, and `1B3` may grow to their content. The right side determines the section height; the cover follows that height at 3:4, is vertically aligned to it, and has a 480-pixel minimum where the available column permits.
2. **Section 2** — note (**2A**) and play/game lamps (**2B**) split at the horizontal centre. The lamps appear side-by-side, retain their artwork aspect ratio, and match the note reservation height.
3. **Section 3** — game path, the combined save-path indicator/path, region command and export action. Long paths wrap at Windows path separators instead of creating horizontal scrolling.

Double-clicking either detail status lamp changes and repaints only the corresponding lamp; it neither rebuilds the detail page nor changes its vertical scroll position.
Every detail tag chip is a measured single-line box: its width grows to contain its complete dimension/value string, with no internal text wrapping or ellipsis. Single-select chips use a greedy left-to-right layout and move an entire chip to the next row only before exceeding the right-side section width. Multi-select values group by dimension: one orange `Dimension :` chip is right-aligned in a common description column, followed by its individual orange value chips in a common left-aligned value column. Values that do not fit beside a preceding chip move as whole chips to an indented next row.

### 8.1 Path-derived state

- Built-in game statuses are **Installed locally** (green filled circle), **In other machine** (red hollow circle), **Data missing** (purple cross), **Storaged** (blue hollow cloud) and **Backuped** (blue filled cloud). They are protected from deletion and their system colours are fixed.
- When a game executable is invalid, **Installed locally** becomes **In other machine** and **Backuped** becomes **Storaged**. **In other machine**, **Data missing** and **Storaged** otherwise remain unchanged.
- When a game executable is valid, **In other machine** and **Data missing** become **Installed locally**; **Storaged** becomes **Backuped**. **Installed locally** and **Backuped** otherwise remain unchanged.
- Save-path availability does not change the game status and does not enable or hide Launch.
- Valid game paths and ordinary valid save paths are light green and clickable to open the resolved folder. A valid save path that is specifically an NTFS Junction is light blue; its target remains opened through the Junction path. Invalid paths are bright red and are not clickable.
- Launch is shown only when the resolved game executable is valid. It is always the normal Launch icon; the Stop-game feature was intentionally removed.

The play-status lamp accepts a double click within 0.8 seconds to move to the next configured play status, cycling at the end. On a valid executable path, the game-status lamp alternates **Installed locally** and **Backuped**; on an invalid path it cycles **In other machine**, **Data missing**, and **Storaged**. The valid and invalid groups never cross through double-clicking. Status icon meanings come from the stored vectors; defaults include hollow/half/filled squares for play states and filled circle/hollow circle/cross/hollow cloud/filled cloud for game states.

## 9. Editing modes

### 9.1 First-level game edit

First-level edit changes one game. It provides boxed interactive text controls for title, note, paths and selectable properties. Image and save/game path selection controls are placed below the normal properties in the scrollable page. Choosing a cover opens a crop/zoom surface before the managed image is saved. The executable picker opens at `rc`; save selectors open at the active save root.

The selected save root, region-command alias and tag values are shown as mapped text; persisted IDs remain internal. When the selected Save root is not **Game directory**, its row exposes a Junction action. It validates the unsaved current game/save fields, creates the `Save_Junction_<game-id>` target beside the resolved game executable, and creates the selected absolute save-path Junction to that target; the button is hidden for the Game-directory root. Every multi-select dimension has its own clearly labelled `(<name> multi-select)` first-level selection row, using orange mutually-aware checkbox tiles: at least one value is required, and `none` cannot coexist with another value. Play status is not set here because it is changed from the detail lamp. Game status is also absent: it is path-derived and can only be cycled from the detail lamp within its current valid or invalid path group.

### 9.2 Second-level global management

Global management owns:

- the `rc` root;
- a default managed game cover, selected with the same crop/zoom workflow as an individual cover and used wherever a game has no individual cover;
- save-root names and Windows path templates;
- region commands (full command plus required short alias);
- play and game statuses, including RGB/hex colour and vector artwork;
- play-status ordering through adjacent two-way swaps;
- a labelled single-select/multi-select dimension management area, including mapped values and a right-click type conversion action; multi-select values additionally support adjacent earlier/later order swaps;
- the Library-card display-dimension selection; and
- all logical action-button icons.

Property collections are tiles rather than a fixed list: simple properties use one tile per value plus an Add tile; tag dimensions are scrollable rows with an Add-value tile at the row end and an Add-dimension tile at the column end. A dimension's context menu can change it between single-select and multi-select while preserving a compatible game value. Left click edits a value. Right click opens only the edit/delete context menu (it must not also open the edit dialog). Every edit/delete context menu uses the enlarged dark GameShelf menu renderer, with an explicit menu width sized for complete labels, rather than the default light menu appearance. Management sections shrink to their content height instead of claiming an entire viewport. A management-page refresh caused by any add, edit, delete, reorder, reset or type-change preserves its current vertical scroll position.

The vector icon editor previews existing custom artwork or the logical built-in glyph when no custom vector is saved. It supports freehand line, straight line, hollow circle, hollow triangle, hollow rectangle, and Paint-style flood fill of an enclosed region. New geometry can be adjusted immediately after creation. Vectors render as white marks over the selected coloured background.

## 10. Launching

Launch resolves the stored relative game path against `RcRootPath`. A direct launch uses the game directory as working directory. A configured region command is invoked with the game executable as its final argument. After Windows accepts the launch request, GameShelf disposes only its temporary `Process` handle; it does not enumerate, monitor, persist, recover, signal, or otherwise track the game process.

## 11. Logging and reliability

Logs are written to `log/gameshelf-YYYY-MM-DD.log` next to the executable. Files older than 30 days are removed at startup. Levels are `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, and `None`.

- Alpha, beta and release-candidate builds default to `Debug`.
- Stable builds default to `Information`.
- The `GAMESHELF_LOG_LEVEL` environment variable overrides the threshold for a diagnostic run.

Unhandled UI, runtime and task exceptions are logged. Database writes use a temporary JSON file followed by replace, so a completed save is atomic. Before diagnosing any defect, inspect the relevant daily log first.

## 12. Repository and release maintenance rule

**Before every `git commit`, `git push`, PR update, merge, or release publication, review and update this `spec.md` so it matches the implementation.** Update `MAINTENANCE.md` with the patch-specific history and troubleshooting note as well.

The branch and release workflow is:

1. `develop` is the single long-lived integration branch. Create each alpha, beta or release-candidate work line as `feature/<version>` from `develop`. Git cannot host both a `develop` branch and `develop/<version>` branches, so the `feature/` prefix is intentional.
2. Before every commit, push, PR update, merge, or release publication, reread this specification and correct any statement that no longer matches the implementation. Update `MAINTENANCE.md` with the patch-specific history and troubleshooting note at the same time.
3. Commit and push `feature/<version>`, then open or update its pull request to `develop`.
4. At the end of each alpha, beta or release-candidate version cycle, merge `feature/<version>` into `develop` and delete that feature branch. A beta prerelease, when requested, is built and published from `develop`.
5. A stable release contains no new implementation work: it is the exact final tested beta commit. When the user designates that beta as stable, merge `develop` into `main` and publish the stable package from that unchanged commit.
6. Build a requested package only after checking and terminating a prior `Launcher` process if necessary. Produce `Launcher.exe` first, then copy it to `Launcher_<version>.exe`.
7. Never include `savedata/` in source control or release assets. Publish only when expressly requested.
