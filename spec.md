# GameShelf architecture specification

**Current application specification:** 2.1.0a3 (alpha)
**Savedata format:** v4
**Scope:** This document describes the implemented application architecture and its persistent-data contract. It is the source of truth for future maintenance; `MAINTENANCE.md` contains the chronological patch history and troubleshooting record.

## 1. Product boundary and platform

GameShelf is a local, single-user Windows game-library launcher. It stores game metadata, cover images, tags, paths, status definitions, button artwork, and UI state beside the executable. It does not require a server, account, cloud service, Steam integration, or Internet connection to run.

The application is a .NET 10 Windows Forms application targeting 64-bit Windows:

- Target framework: `net10.0-windows`
- Runtime: `win-x64`
- Distribution: self-contained, single-file Windows executable
- NuGet dependency: `System.Management` (used for optional Windows process diagnostics)

A target computer needs 64-bit Windows and write permission to the executable directory. A separately installed .NET runtime is not required by a published build.

## 2. Deliverables and application root

The compiled assembly name is `GameShelf.exe`. The distributable launcher convention is:

- `Launcher.exe` is the fixed local launch target; a shortcut may point to it.
- `Launcher_<version>.exe` is the versioned copy, for example `Launcher_2_0_1.exe`.
- An alpha version has an `a` suffix. Alpha packages are Debug diagnostics builds; a version with no suffix is a stable Release package.

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
| `Program` | Initializes WinForms, paths and logging; owns top-level exception reporting and the `DataStore`/`MainForm` lifetime. |
| `AppPaths` | Resolves the application-root, savedata, image, database and log locations. |
| `AppLog` | Dependency-free daily rolling diagnostics. Logging failures never stop the launcher. |
| `Domain` | Persistent DTOs, default statuses/save roots, and data-format version constants. |
| `DataStore` | Loads, migrates, normalizes, validates and atomically writes `gameshelf.json`; all state changes pass through this layer. |
| `MainForm` | Owns all pages, native menu integration, responsive layouts, navigation, input, edit dialogs and UI-state persistence. |
| `ImageService` | Imports cover images, stores them under `savedata/images`, and supports the image crop workflow. |
| `PackageService` (`ImportExport.cs`) | Exports/imports individual-game packages and reconciles imported schema entries. |
| `GameProcessTracker` | Records launched process identities and performs bounded Windows/WMI child-process diagnostics. It does not drive a Stop-game UI. |
| `Localizer` | Provides the four UI-language dictionaries. Game data remains Unicode and is not translated. |
| `StatusIconVectors` / icon editor | Stores and renders white vector artwork over coloured button/status backgrounds. |

## 4. Persistent data contract

`savedata/gameshelf.json` serializes `AppData`. Its current `Version` is `4`.

| Area | Stored data |
| --- | --- |
| `Settings` | UI language, persisted launcher-version policy, last supported page and selected game, normal/fullscreen window state and bounds, title/status/tag filters, Library-card dimension choices, custom button vectors, and transient process identities. |
| `RcRootPath` | Absolute path of the common `rc` game-resource root. |
| `SaveRoots` | Named Windows path templates used as save roots. |
| `RegionCommands` / `RegionAliases` | Region-launch commands and their short display aliases. |
| `TagSchema` | Ordered single-select or multi-select dimensions, each with an integer ID and mapped display values. |
| `PlayStatuses` / `GameStatuses` | Ordered status definitions: ID, display name, RGB/hex colour, white vector icon, default flag, and (for system game statuses) role. |
| `Games` | Individual game records: ID, title, managed cover filename, note, relative game/save paths, selected save root, statuses, region command, one value per single-select dimension, and one-or-more values per multi-select dimension. |

Every persistent DTO supports `JsonExtensionData`. Therefore unknown properties from a newer launcher are round-tripped rather than intentionally discarded. Older formats are backed up in `savedata/backups/` before in-place normalization. A newer savedata version is not downgraded.

The legacy `GameEntry.SaveMethod` field is retained only so older data can be read; current UI and logic use `SaveRootId` and `SavePath`.

### 4.1 Path rules

- `GamePath` is stored relative to `RcRootPath`. Selecting a game executable outside the configured `rc` root is rejected.
- `SavePath` is stored relative to the selected `SaveRoot`.
- A save root of `.` means the directory containing the resolved game executable.
- Built-in portable save roots are `.` (Game directory), `%USERPROFILE%\\Documents`, and `%USERPROFILE%\\AppData`. Environment variables are expanded on the current computer, supporting a different Windows user profile.
- Absolute legacy paths are readable for compatibility, but new selections are normalized to their corresponding relative form.

## 5. Permanent visual and window model

The presentation is permanently dark; there is no day/night theme or runtime theme selection. Standard UI languages are English (`en`), Traditional Chinese (`zh-Hant`), Simplified Chinese (`zh-Hans`) and Japanese (`ja`). All stored game text supports Unicode independently of the UI language.

GameShelf uses the normal native Windows title bar. Windows owns caption dragging, system menu, Snap layouts, resize borders and minimize/maximize/close controls. Directly below it is a native Windows menu bar:

- The menu is collapsed by default and reveals when the pointer reaches the top reveal band. It retracts after the pointer leaves the menu area; this works in normal and F11 fullscreen windows.
- **Version** (click or `Alt+V` after revealing the menu) stores one launch policy and restarts into its resolved executable. It offers **Automatically select latest version**, **Automatically select latest stable version**, each available stable *major.minor* series, and at most one alpha.
- A stable series entry such as `2.0` resolves to the highest available stable patch in that series at the moment it is selected, for example `Launcher_2_0_1.exe`. The resulting exact patch is pinned, so even a later `2.0.2` does not replace it automatically.
- An alpha is shown only when its core version is strictly newer than the highest available stable release. For example, `2.0.1a` is shown with `2.0.0`, but is hidden when stable `2.0.1` exists. Selecting an alpha pins that exact alpha version.
- The persisted launcher policy is resolved during form loading, before the first paint. A fixed `Launcher.exe` can therefore hand off to the chosen versioned executable without a visible start/close/start flash.
- **Language** (click or `Alt+L`) persists the chosen UI language and restarts the application.
- The same native menu and reveal behaviour remain available in F11 fullscreen.

Normal resizing is constrained to 16:9 by the native `WM_SIZING` path, with a minimum size of 720 × 405. During an interactive resize, a dark presentation mask hides intermediate redraws; a single responsive layout refresh occurs when sizing ends. This avoids an animation/frame loop and the previous fullscreen redraw flicker.

All normal pages are scrollable where needed. Buttons use coloured rounded square/rectangle backgrounds and explanatory tooltips. If an action is unavailable on a page, its button is omitted and the remaining header controls close the gap.

## 6. Navigation and persisted page state

| Input | Result |
| --- | --- |
| `F2` | Library / Home |
| `F3` | Game management or global management, according to context |
| `F4` | Back to Library or the preceding supported page |
| `F11` | Toggle fullscreen |
| `Esc` | Exit fullscreen |
| `Alt+F4` | Close the launcher |

The last page is persisted only for Library and game detail. If the launcher is closed on detail, it reopens on that game detail; closing while in first-level edit reopens at that game detail instead. Other management pages reopen at Library to avoid restoring incomplete edit state.

## 7. Library / Home

Library shows responsive game cards. In normal mode, only a left click opens a game detail page. In game-management mode, right-clicking a card performs the management context action, including deletion; cards do not open games in that mode.

Each card has a fixed portrait 3:4 cover at upper-left, enlarged by 1.2× from the prior card layout. Its two status lamps stack vertically to the cover's right and together match the cover height. The decimal game number is below the cover, followed by a title that supports two lines and renders a literal `\\n` as a line break. The compact single-select strip sits under the title; the larger multi-select strip sits below it, and the card height provides room for its chips. The Library may show up to three selected single-select dimensions and two selected multi-select dimensions; every multi value uses its own orange chip, while single chips use purple. When no multi-display selection has been stored, the first two multi-select dimensions are selected automatically. Filtering always considers every dimension.

The title search field sits directly left of the rightmost filter control and combines with every other condition using AND; its adjacent clear button removes the search text. The filter control opens a modal filter:

- `none` values cannot be selected.
- Multiple values within one single-select dimension are ORed; different dimensions are ANDed.
- A multi-select dimension matches when the selected values intersect that game's selected values.
- Play status and game status are independent single-choice filters.
- Tag, play-status and game-status selection tiles use distinct colours.
- Clear removes all conditions.
- After applying, active selection chips appear immediately to the left of the filter control, right-aligned and wrapping when necessary.

In Library management, the dimension selector chooses up to three displayed single-select dimensions plus two displayed multi-select dimensions. Its choice tiles measure to the dimension text rather than using fixed-size controls.

## 8. Game detail page

Entering game detail always refreshes the selected game path state. The page is horizontally centred and consists of three equal-width stacked sections; no child may cross its invisible section boundary.

1. **Section 1** — left **1A** is a 3:4 portrait cover; right **1B** contains number/Launch, title, and tags. `1B1` splits number and Launch evenly, and `1B1`, `1B2`, and `1B3` may grow to their content. The right side determines the section height; the cover follows that height at 3:4, is vertically aligned to it, and has a 480-pixel minimum where the available column permits.
2. **Section 2** — note (**2A**) and play/game lamps (**2B**) split at the horizontal centre. The lamps appear side-by-side, retain their artwork aspect ratio, and match the note reservation height.
3. **Section 3** — game path, save root, save path, region command and export action. Long paths wrap at Windows path separators instead of creating horizontal scrolling.

Single-select tag chips use a greedy left-to-right layout and wrap only before exceeding the right-side section width. Multi-select values group by dimension: one orange `Dimension :` chip is followed by its individual orange value chips on the same line when possible; values that wrap are indented to the description-chip value column. Their widths measure to their complete text.

### 8.1 Path-derived state

- A valid game executable forces the built-in green **Installed locally** game status.
- If the game executable becomes invalid, only that green installed status changes to purple **Data missing**. Red **In other machine**, purple **Data missing**, and blue **Storaged** remain as chosen.
- Save-path availability does not change the game status and does not enable or hide Launch.
- Valid game/save paths are light green and clickable to open the resolved folder. Invalid paths are bright red and are not clickable.
- Launch is shown only when the resolved game executable is valid. It is always the normal Launch icon; the Stop-game feature was intentionally removed.

The play-status lamp accepts a double click within 0.8 seconds to move to the next configured play status, cycling at the end. The game-status lamp accepts the same double click only while its executable path is invalid; it cycles **In other machine**, **Data missing**, and **Storaged**. A valid path forms a wall: the forced green installed state never cycles to an invalid state. Status icon meanings come from the stored vectors; defaults include hollow/half/filled squares for play states and filled circle/hollow circle/cross/hollow cloud for game states.

## 9. Editing modes

### 9.1 First-level game edit

First-level edit changes one game. It provides boxed interactive text controls for title, note, paths and selectable properties. Image and save/game path selection controls are placed below the normal properties in the scrollable page. Choosing a cover opens a crop/zoom surface before the managed image is saved.

The selected save root, region-command alias and tag values are shown as mapped text; persisted IDs remain internal. Every multi-select dimension has its own clearly labelled `(<name> multi-select)` first-level selection row, using orange mutually-aware checkbox tiles: at least one value is required, and `none` cannot coexist with another value. Play status is not set here because it is changed from the detail lamp. Game status is also absent: it is path-derived and can only be cycled from the detail lamp while unavailable.

### 9.2 Second-level global management

Global management owns:

- the `rc` root;
- save-root names and Windows path templates;
- region commands (full command plus required short alias);
- play and game statuses, including RGB/hex colour and vector artwork;
- play-status ordering through adjacent two-way swaps;
- a labelled single-select/multi-select dimension management area, including mapped values and a right-click type conversion action;
- the Library-card display-dimension selection; and
- all logical action-button icons.

Property collections are tiles rather than a fixed list: simple properties use one tile per value plus an Add tile; tag dimensions are scrollable rows with an Add-value tile at the row end and an Add-dimension tile at the column end. A dimension's context menu can change it between single-select and multi-select while preserving a compatible game value. Left click edits a value. Right click opens only the edit/delete context menu (it must not also open the edit dialog). Every edit/delete context menu uses the enlarged dark GameShelf menu renderer, with an explicit menu width sized for complete labels, rather than the default light menu appearance. Management sections shrink to their content height instead of claiming an entire viewport. A management-page refresh caused by any add, edit, delete, reorder, reset or type-change preserves its current vertical scroll position.

The vector icon editor previews existing custom artwork or the logical built-in glyph when no custom vector is saved. It supports freehand line, straight line, hollow circle, hollow triangle, hollow rectangle, and Paint-style flood fill of an enclosed region. New geometry can be adjusted immediately after creation. Vectors render as white marks over the selected coloured background.

## 10. Launching and process diagnostics

Launch resolves the stored relative game path against `RcRootPath`. A direct launch uses the game directory as working directory. A configured region command is invoked with the game executable as its final argument.

`GameProcessTracker` records a launched process ID and start time, then may observe/adopt an exact child executable for region-launcher workflows. WMI event monitoring and two bounded post-launch checks are diagnostics only. The persisted identity lets a restarted launcher inspect an already-running tracked process safely. Process tracking does not alter the current UI into a Stop action and must never cause a page-rebuild loop.

## 11. Logging and reliability

Logs are written to `log/gameshelf-YYYY-MM-DD.log` next to the executable. Files older than 30 days are removed at startup. Levels are `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical`, and `None`.

- Alpha builds default to `Debug`.
- Stable builds default to `Information`.
- The `GAMESHELF_LOG_LEVEL` environment variable overrides the threshold for a diagnostic run.

Unhandled UI, runtime and task exceptions are logged. Database writes use a temporary JSON file followed by replace, so a completed save is atomic. Before diagnosing any defect, inspect the relevant daily log first.

## 12. Repository and release maintenance rule

**Before every `git commit`, `git push`, PR update, merge, or release publication, review and update this `spec.md` so it matches the implementation.** Update `MAINTENANCE.md` with the patch-specific history and troubleshooting note as well.

The normal workflow is:

1. Work on a development branch.
2. Update implementation, tests/checks, `spec.md`, and `MAINTENANCE.md` together.
3. Commit and push the branch, then open/update a pull request to `main`.
4. Build the requested package only after checking and terminating a prior `Launcher` process if necessary.
5. Produce `Launcher.exe` first, then copy it to `Launcher_<version>.exe`.
6. Merge only after the requested review/approval. Publish only when expressly requested; never include `savedata/` in source control or release assets.
