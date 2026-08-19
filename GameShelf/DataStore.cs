using System.Text;
using System.Text.Json;

namespace GameShelf;

/// <summary>Owns all persistent state. Every mutating operation validates then atomically replaces the JSON file.</summary>
public sealed class DataStore : IDisposable
{
    private readonly AppPaths _paths;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public AppData Data { get; private set; }
    public AppPaths Paths => _paths;

    public DataStore(AppPaths paths)
    {
        _paths = paths;
        Directory.CreateDirectory(paths.DataDirectory);
        Directory.CreateDirectory(paths.ImagesDirectory);
        AppLog.Debug("DataStore", "Opening local database.");
        Data = Load();
        UpgradeFormatIfNeeded();
        NormalizeAndValidatePaths();
        Save();
    }

    private AppData Load()
    {
        if (!File.Exists(_paths.DatabaseFile)) return new AppData();
        try
        {
            var data = JsonSerializer.Deserialize<AppData>(File.ReadAllText(_paths.DatabaseFile, Encoding.UTF8), _json);
            return data ?? new AppData();
        }
        catch (Exception ex)
        {
            AppLog.Error("DataStore", "Could not read database.", ex);
            throw new InvalidOperationException("The savedata database is unreadable. See the log folder next to GameShelf.exe.", ex);
        }
    }

    /// <summary>
    /// Applies safe, in-place schema upgrades. Unknown JSON properties are kept by
    /// JsonExtensionData, and a copy is made before the first write of an older
    /// format so a launcher from another release cannot silently destroy data.
    /// </summary>
    private void UpgradeFormatIfNeeded()
    {
        if (Data.Version <= 0) Data.Version = 1;
        if (Data.Version == AppData.CurrentFormatVersion) return;
        if (Data.Version > AppData.CurrentFormatVersion)
        {
            AppLog.Warning("DataStore", $"Savedata format v{Data.Version} is newer than this launcher (v{AppData.CurrentFormatVersion}); preserving unknown fields without downgrading it.");
            return;
        }

        var backupDirectory = Path.Combine(_paths.DataDirectory, "backups");
        try
        {
            Directory.CreateDirectory(backupDirectory);
            var backup = Path.Combine(backupDirectory, $"gameshelf-before-v{Data.Version}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            if (File.Exists(_paths.DatabaseFile)) File.Copy(_paths.DatabaseFile, backup, overwrite: false);
            AppLog.Information("DataStore", $"Backed up savedata format v{Data.Version} before migration: '{backup}'.");
        }
        catch (Exception ex)
        {
            // The loaded data remains usable; log this prominently because a later
            // Save will still normalize the schema.
            AppLog.Warning("DataStore", "Could not create the savedata migration backup.", ex);
        }

        var sourceVersion = Data.Version;
        // v1 -> v2: permanent dark UI removes the obsolete Theme field.
        // v2 -> v3: multi-select dimension collections are normalized below.
        // v3 -> v4: Library supports two multi-select display dimensions.
        // Future fields remain in JsonExtensionData and are round-tripped.
        Data.Settings ??= new AppSettings();
        Data.Settings.UnknownFields?.Remove("Theme");
        Data.Version = AppData.CurrentFormatVersion;
        AppLog.Information("DataStore", $"Migrated savedata format v{sourceVersion} to v{Data.Version}.");
    }

    public void Save()
    {
        Normalize();
        var temporary = _paths.DatabaseFile + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(Data, _json), new UTF8Encoding(false));
            File.Move(temporary, _paths.DatabaseFile, true);
            AppLog.Debug("DataStore", "Saved local database.");
        }
        catch (Exception ex)
        {
            AppLog.Error("DataStore", "Could not save database.", ex);
            throw new InvalidOperationException("GameShelf cannot write savedata. No change was confirmed.", ex);
        }
    }

    public void AddGame(int id)
    {
        if (Data.Games.Any(g => g.Id == id)) throw new InvalidOperationException("A game with this ID already exists.");
        Data.Games.Add(new GameEntry
        {
            Id = id,
            Tags = Enumerable.Repeat(0, Data.TagSchema.Count).ToList(),
            MultiTags = Data.TagSchema.Select(dimension => dimension.IsMultiSelect ? new List<int> { 0 } : []).ToList(),
            PlayStatusId = Defaults.PlayDefaultId,
            GameStatusId = Defaults.GameDefaultId
        });
        Save();
    }

    public void DeleteGame(int id)
    {
        var game = GetGame(id);
        var image = ImagePath(game);
        Data.Games.Remove(game);
        if (!string.IsNullOrEmpty(image) && File.Exists(image)) File.Delete(image);
        Save();
    }

    public GameEntry GetGame(int id) => Data.Games.FirstOrDefault(g => g.Id == id) ?? throw new InvalidOperationException("Game not found.");
    public string ImagePath(GameEntry game) => string.IsNullOrWhiteSpace(game.ImageFile) ? "" : Path.Combine(_paths.ImagesDirectory, Path.GetFileName(game.ImageFile));
    public string ResolveGamePath(string storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return "";
        if (Path.IsPathFullyQualified(storedPath)) return storedPath;
        return string.IsNullOrWhiteSpace(Data.RcRootPath) ? "" : Path.GetFullPath(Path.Combine(Data.RcRootPath, storedPath));
    }
    public string ToRcRelativePath(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(Data.RcRootPath)) throw new InvalidOperationException("Set the rc root folder in second-level management before choosing a game executable.");
        var relative = Path.GetRelativePath(Data.RcRootPath, absolutePath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidOperationException("The game executable must be inside the configured rc root folder.");
        return relative;
    }
    public string SaveRootName(int id) => Data.SaveRoots.FirstOrDefault(root => root.Id == id)?.Name ?? "Game directory";
    public string ResolveSaveRoot(int saveRootId, string storedGamePath)
    {
        var root = Data.SaveRoots.FirstOrDefault(item => item.Id == saveRootId) ?? Data.SaveRoots.First(item => item.Id == Defaults.SaveRootGameDirectoryId);
        if (root.PathTemplate == ".") { var game = ResolveGamePath(storedGamePath); return string.IsNullOrWhiteSpace(game) ? "" : Path.GetDirectoryName(game) ?? ""; }
        return Environment.ExpandEnvironmentVariables(root.PathTemplate);
    }
    public string ResolveSavePath(GameEntry game)
    {
        if (string.IsNullOrWhiteSpace(game.SavePath)) return "";
        if (Path.IsPathFullyQualified(game.SavePath)) return game.SavePath;
        var root = ResolveSaveRoot(game.SaveRootId, game.GamePath);
        return string.IsNullOrWhiteSpace(root) ? "" : Path.GetFullPath(Path.Combine(root, game.SavePath));
    }
    public string ToSaveRelativePath(int saveRootId, string storedGamePath, string absolutePath)
    {
        var root = ResolveSaveRoot(saveRootId, storedGamePath);
        if (string.IsNullOrWhiteSpace(root)) throw new InvalidOperationException("Choose a valid game path before selecting a save location for this save root.");
        var relative = Path.GetRelativePath(root, absolutePath);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)) throw new InvalidOperationException("The save location must be inside the selected save root.");
        return relative;
    }
    public void SetRcRootPath(string path)
    {
        if (!Directory.Exists(path)) throw new InvalidOperationException("The rc root folder must exist.");
        Data.RcRootPath = Path.GetFullPath(path); NormalizeAndValidatePaths(); Save();
    }
    public void AddSaveRoot(string name, string pathTemplate)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pathTemplate)) throw new InvalidOperationException("A save root name and path are required.");
        var id = Data.SaveRoots.Select(root => root.Id).DefaultIfEmpty(0).Max() + 1; Data.SaveRoots.Add(new SaveRoot { Id = id, Name = name.Trim(), PathTemplate = pathTemplate.Trim() }); Save();
    }
    public void UpdateSaveRoot(int id, string name, string pathTemplate)
    {
        var root = Data.SaveRoots.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("Save root not found.");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(pathTemplate)) throw new InvalidOperationException("A save root name and path are required.");
        root.Name = name.Trim(); root.PathTemplate = pathTemplate.Trim(); Save();
    }
    public void DeleteSaveRoot(int id)
    {
        if (Data.SaveRoots.Count <= 1) throw new InvalidOperationException("At least one save root is required.");
        var root = Data.SaveRoots.FirstOrDefault(item => item.Id == id) ?? throw new InvalidOperationException("Save root not found.");
        Data.SaveRoots.Remove(root); var fallback = Data.SaveRoots.First().Id;
        foreach (var game in Data.Games.Where(game => game.SaveRootId == id)) game.SaveRootId = fallback;
        Save();
    }

    public void UpdateGame(GameEntry proposed)
    {
        var current = GetGame(proposed.Id);
        var index = Data.Games.IndexOf(current);
        proposed.ImageFile = current.ImageFile; // only SetImage owns managed files
        // Game availability is determined solely from the executable path.  A
        // first-level edit must never overwrite that derived state.
        proposed.GameStatusId = current.GameStatusId;
        proposed.Title = TextRules.TrimGraphemes(proposed.Title, 50, "unknown");
        proposed.Note = TextRules.TrimGraphemes(proposed.Note, 150, "");
        proposed.SaveMethod = TextRules.TrimGraphemes(proposed.SaveMethod, 20, "");
        Data.Games[index] = proposed;
        NormalizeAndValidatePaths();
        Save();
    }

    public void SetImage(int gameId, string source)
    {
        var game = GetGame(gameId);
        var targetName = $"{game.Id}-{Guid.NewGuid():N}.png";
        var target = Path.Combine(_paths.ImagesDirectory, targetName);
        ImageService.ProcessToCard(source, target);
        var old = ImagePath(game);
        game.ImageFile = targetName;
        try { Save(); }
        catch { game.ImageFile = Path.GetFileName(old); if (File.Exists(target)) File.Delete(target); throw; }
        if (!string.IsNullOrEmpty(old) && File.Exists(old)) File.Delete(old);
    }

    public void AddDimension(string name, bool isMultiSelect = false)
    {
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Dimension name is required.");
        var next = Data.TagSchema.Count == 0 ? 1 : Data.TagSchema.Max(x => x.DimensionId) + 1;
        Data.TagSchema.Add(new TagDimension { DimensionId = next, Name = name, IsMultiSelect = isMultiSelect });
        foreach (var game in Data.Games) { game.Tags.Add(0); game.MultiTags.Add(isMultiSelect ? [0] : []); }
        Save();
    }

    public void RenameDimension(int id, string name)
    {
        var dimension = Dimension(id); dimension.Name = name.Trim();
        if (dimension.Name.Length == 0) throw new InvalidOperationException("Dimension name is required.");
        Save();
    }

    public int DeleteDimension(int id)
    {
        var position = Data.TagSchema.FindIndex(d => d.DimensionId == id);
        if (position < 0) throw new InvalidOperationException("Dimension not found.");
        var affected = Data.Games.Count(g => Data.TagSchema[position].IsMultiSelect ? g.MultiTags.ElementAtOrDefault(position)?.Any(value => value != 0) == true : g.Tags.ElementAtOrDefault(position) != 0);
        Data.TagSchema.RemoveAt(position);
        foreach (var game in Data.Games)
        {
            if (game.Tags.Count > position) game.Tags.RemoveAt(position);
            if (game.MultiTags.Count > position) game.MultiTags.RemoveAt(position);
        }
        Save();
        return affected;
    }

    public void AddTagValue(int dimensionId, string text)
    {
        var d = Dimension(dimensionId);
        var next = d.Values.Keys.DefaultIfEmpty(0).Max() == int.MaxValue ? throw new InvalidOperationException("Index limit reached; explicitly reuse a deleted index.") : d.Values.Keys.DefaultIfEmpty(0).Max() + 1;
        d.Values[next] = text.Trim();
        Save();
    }

    public void SetTagValue(int dimensionId, int value, string text)
    {
        if (value == 0) throw new InvalidOperationException("The none value cannot be edited.");
        var d = Dimension(dimensionId);
        if (!d.Values.ContainsKey(value)) throw new InvalidOperationException("Value not found.");
        d.Values[value] = text.Trim(); Save();
    }

    public void DeleteTagValue(int dimensionId, int value)
    {
        if (value == 0) throw new InvalidOperationException("The none value cannot be deleted.");
        var pos = Data.TagSchema.FindIndex(d => d.DimensionId == dimensionId);
        var d = Dimension(dimensionId);
        if (!d.Values.Remove(value)) throw new InvalidOperationException("Value not found.");
        foreach (var game in Data.Games)
        {
            if (d.IsMultiSelect)
            {
                var values = game.MultiTags.ElementAtOrDefault(pos);
                values?.Remove(value);
                if (values is null || values.Count == 0) { while (game.MultiTags.Count <= pos) game.MultiTags.Add([]); game.MultiTags[pos] = [0]; }
            }
            else if (game.Tags.ElementAtOrDefault(pos) == value) game.Tags[pos] = 0;
        }
        Save();
    }

    public void SetDimensionMultiSelect(int id, bool isMultiSelect)
    {
        var position = Data.TagSchema.FindIndex(dimension => dimension.DimensionId == id);
        if (position < 0) throw new InvalidOperationException("Dimension not found.");
        var dimension = Data.TagSchema[position];
        if (dimension.IsMultiSelect == isMultiSelect) return;
        dimension.IsMultiSelect = isMultiSelect;
        foreach (var game in Data.Games)
        {
            while (game.MultiTags.Count <= position) game.MultiTags.Add([]);
            if (isMultiSelect)
            {
                game.MultiTags[position] = [game.Tags.ElementAtOrDefault(position)];
                game.Tags[position] = 0;
            }
            else
            {
                game.Tags[position] = game.MultiTags[position].FirstOrDefault(value => value != 0);
                game.MultiTags[position] = [];
            }
        }
        Save();
    }

    public void AddRegionCommand(string alias, string command)
    {
        if (string.IsNullOrWhiteSpace(alias)) throw new InvalidOperationException("Region command alias is required.");
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Region command is required.");
        var max = Data.RegionCommands.Keys.DefaultIfEmpty(0).Max();
        if (max == int.MaxValue) throw new InvalidOperationException("Index limit reached; explicitly reuse a deleted index.");
        Data.RegionCommands[max + 1] = command.Trim(); Data.RegionAliases[max + 1] = alias.Trim(); Save();
    }

    public void UpdateRegionCommand(int id, string alias, string command)
    {
        if (id == 0) throw new InvalidOperationException("The default region command cannot be changed.");
        if (!Data.RegionCommands.ContainsKey(id)) throw new InvalidOperationException("Region command not found.");
        if (string.IsNullOrWhiteSpace(alias)) throw new InvalidOperationException("Region command alias is required.");
        if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("Region command is required.");
        Data.RegionCommands[id] = command.Trim(); Data.RegionAliases[id] = alias.Trim(); Save();
    }

    public void DeleteRegionCommand(int id)
    {
        if (id == 0) throw new InvalidOperationException("The default region command cannot be deleted.");
        if (!Data.RegionCommands.Remove(id)) throw new InvalidOperationException("Region command not found.");
        Data.RegionAliases.Remove(id);
        foreach (var game in Data.Games.Where(g => g.RegionCommandId == id)) game.RegionCommandId = 0;
        Save();
    }

    public void AddStatus(StatusKind kind, string name, string color, string? iconVector = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Status name is required.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) throw new InvalidOperationException("Status color must be #RRGGBB.");
        var statuses = Statuses(kind); var next = statuses.Select(s => s.Id).DefaultIfEmpty(0).Max() + 1;
        statuses.Add(new GameStatus { Id = next, Name = name.Trim(), Color = color, IconVector = string.IsNullOrWhiteSpace(iconVector) ? StatusIconVectors.DefaultFor(kind) : iconVector }); Save();
    }

    public void UpdateStatus(StatusKind kind, int id, string name, string color, string? iconVector = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Status name is required.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) throw new InvalidOperationException("Status color must be #RRGGBB.");
        var status = Statuses(kind).FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("Status not found.");
        if (kind == StatusKind.Game && !string.IsNullOrWhiteSpace(status.SystemRole) && !string.Equals(color, Defaults.GameStatusColor(status.SystemRole), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The color of a built-in path-aware game status is locked.");
        status.Name = name.Trim(); status.Color = color; if (iconVector is not null) status.IconVector = iconVector; Save();
    }

    public void DeleteStatus(StatusKind kind, int id)
    {
        var statuses = Statuses(kind); var status = statuses.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("Status not found.");
        if (kind == StatusKind.Game && !string.IsNullOrWhiteSpace(status.SystemRole)) throw new InvalidOperationException("Built-in path-aware game statuses cannot be deleted.");
        if (statuses.Count <= 1) throw new InvalidOperationException("At least one status is required.");
        if (status.IsDefault) statuses.First(s => s.Id != id).IsDefault = true;
        statuses.Remove(status);
        var fallback = statuses.Single(s => s.IsDefault).Id;
        foreach (var game in Data.Games)
            if (kind == StatusKind.Play && game.PlayStatusId == id) game.PlayStatusId = fallback;
            else if (kind == StatusKind.Game && game.GameStatusId == id) game.GameStatusId = fallback;
        Save();
    }

    public void NormalizeAndValidatePaths()
    {
        Normalize();
        RefreshAllGamePathStatuses();
        Normalize();
    }

    /// <summary>
    /// Only the executable path controls the automatic game-status lamp. A valid
    /// executable selects Installed locally. An invalid path only changes Installed
    /// locally into Data missing, preserving user-selected red, purple, and blue states.
    /// Save-path validity intentionally has no effect here.
    /// </summary>
    public bool RefreshGamePathStatus(GameEntry game)
    {
        var installed = GameStatusByRole(Defaults.InstalledRole);
        var missing = GameStatusByRole(Defaults.MissingRole);
        if (installed is null || missing is null) return false;
        var valid = PathRules.IsValidGameExe(ResolveGamePath(game.GamePath));
        var desired = valid ? installed.Id : game.GameStatusId == installed.Id ? missing.Id : game.GameStatusId;
        if (desired == game.GameStatusId) return false;
        AppLog.Debug("DataStore", $"Game {game.Id} path availability changed game status from {game.GameStatusId} to {desired}.");
        game.GameStatusId = desired;
        return true;
    }

    public bool RefreshAllGamePathStatuses()
    {
        var changed = false;
        foreach (var game in Data.Games) changed |= RefreshGamePathStatus(game);
        return changed;
    }

    public void SetNextPlayStatus(int gameId)
    {
        var game = GetGame(gameId); var statuses = Data.PlayStatuses;
        var index = statuses.FindIndex(status => status.Id == game.PlayStatusId);
        game.PlayStatusId = statuses[(index + 1 + statuses.Count) % statuses.Count].Id;
        Save();
    }

    public void SetNextInvalidGameStatus(int gameId)
    {
        var game = GetGame(gameId);
        if (PathRules.IsValidGameExe(ResolveGamePath(game.GamePath))) return;
        var invalidStatuses = new[] { Defaults.OtherMachineRole, Defaults.MissingRole, Defaults.StoragedRole }
            .Select(GameStatusByRole).OfType<GameStatus>().ToList();
        if (invalidStatuses.Count == 0) return;
        var index = invalidStatuses.FindIndex(status => status.Id == game.GameStatusId);
        game.GameStatusId = invalidStatuses[(index + 1 + invalidStatuses.Count) % invalidStatuses.Count].Id;
        Save();
    }

    public void MovePlayStatus(int id, int direction)
    {
        var index = Data.PlayStatuses.FindIndex(status => status.Id == id);
        var target = index + direction;
        if (index < 0) throw new InvalidOperationException("Play status not found.");
        if (target < 0 || target >= Data.PlayStatuses.Count) return;
        (Data.PlayStatuses[index], Data.PlayStatuses[target]) = (Data.PlayStatuses[target], Data.PlayStatuses[index]);
        Save();
    }

    public void SetHomeDisplayDimensions(IEnumerable<int> dimensionIds, IEnumerable<int> multiDimensionIds)
    {
        var single = new HashSet<int>(Data.TagSchema.Where(dimension => !dimension.IsMultiSelect).Select(dimension => dimension.DimensionId));
        var multi = new HashSet<int>(Data.TagSchema.Where(dimension => dimension.IsMultiSelect).Select(dimension => dimension.DimensionId));
        Data.Settings.HomeDisplayDimensionIds = dimensionIds.Where(single.Contains).Distinct().Take(3).ToList();
        Data.Settings.HomeMultiDisplayDimensionIds = multiDimensionIds.Where(multi.Contains).Distinct().Take(2).ToList();
        // Preserve the compatible v3 scalar so a v3 launcher can still show the
        // first selected multi dimension if the data is opened accidentally.
        Data.Settings.HomeMultiDisplayDimensionId = Data.Settings.HomeMultiDisplayDimensionIds.Count > 0 ? Data.Settings.HomeMultiDisplayDimensionIds[0] : null;
        Normalize(); Save();
    }

    private void Normalize()
    {
        Data.RegionCommands ??= [];
        Data.RegionCommands[0] = "";
        Data.RegionAliases ??= [];
        Data.RegionAliases[0] = "none";
        foreach (var item in Data.RegionCommands.Where(x => x.Key != 0))
            if (!Data.RegionAliases.TryGetValue(item.Key, out var alias) || string.IsNullOrWhiteSpace(alias)) Data.RegionAliases[item.Key] = $"Region {item.Key}";
        foreach (var id in Data.RegionAliases.Keys.Where(id => !Data.RegionCommands.ContainsKey(id)).ToList()) Data.RegionAliases.Remove(id);
        Data.TagSchema ??= [];
        Data.RcRootPath ??= "";
        Data.SaveRoots = Data.SaveRoots?.Any() == true ? Data.SaveRoots : Defaults.SaveRoots();
        if (!Data.SaveRoots.Any(root => root.Id == Defaults.SaveRootGameDirectoryId)) Data.SaveRoots.Insert(0, Defaults.SaveRoots().First());
        foreach (var d in Data.TagSchema) { d.Values ??= []; d.Values[0] = "none"; }
        Data.Settings ??= new AppSettings();
        Data.Settings.Language = Data.Settings.Language?.Trim().ToLowerInvariant() switch
        {
            "ja" or "ja-jp" => "ja",
            "zh-hans" or "zh-cn" or "zh-sg" => "zh-Hans",
            "zh-hant" or "zh-tw" or "zh-hk" or "zh" => "zh-Hant",
            _ => "en"
        };
        var launcherSelection = Data.Settings.LauncherSelection?.Trim().ToLowerInvariant() ?? "";
        Data.Settings.LauncherSelection = launcherSelection is "auto-latest" or "auto-stable"
            || System.Text.RegularExpressions.Regex.IsMatch(launcherSelection, "^exact:[0-9]+\\.[0-9]+\\.[0-9]+(?:a[0-9]*)?$")
                ? launcherSelection
                : "auto-latest";
        Data.Settings.SelectedTagFilters ??= [];
        Data.Settings.TitleSearch ??= "";
        Data.Settings.HomeDisplayDimensionIds ??= [];
        Data.Settings.HomeMultiDisplayDimensionIds ??= [];
        Data.Settings.ButtonIcons ??= [];
        Data.Settings.RunningGameProcesses ??= [];
        var dimensionsById = Data.TagSchema.ToDictionary(d => d.DimensionId);
        foreach (var dimensionId in Data.Settings.SelectedTagFilters.Keys.ToList())
        {
            if (!dimensionsById.TryGetValue(dimensionId, out var dimension)) { Data.Settings.SelectedTagFilters.Remove(dimensionId); continue; }
            var values = Data.Settings.SelectedTagFilters[dimensionId] ??= [];
            values.RemoveAll(value => value == 0 || !dimension.Values.ContainsKey(value));
            if (values.Count == 0) Data.Settings.SelectedTagFilters.Remove(dimensionId);
        }
        Data.Settings.HomeDisplayDimensionIds = Data.Settings.HomeDisplayDimensionIds
            .Where(id => dimensionsById.TryGetValue(id, out var dimension) && !dimension.IsMultiSelect).Distinct().Take(3).ToList();
        foreach (var dimension in Data.TagSchema.Where(dimension => !dimension.IsMultiSelect))
        {
            if (Data.Settings.HomeDisplayDimensionIds.Count >= 3) break;
            if (!Data.Settings.HomeDisplayDimensionIds.Contains(dimension.DimensionId)) Data.Settings.HomeDisplayDimensionIds.Add(dimension.DimensionId);
        }
        if (Data.Settings.HomeMultiDisplayDimensionIds.Count == 0 && Data.Settings.HomeMultiDisplayDimensionId is int legacyMulti)
            Data.Settings.HomeMultiDisplayDimensionIds.Add(legacyMulti);
        Data.Settings.HomeMultiDisplayDimensionIds = Data.Settings.HomeMultiDisplayDimensionIds
            .Where(id => dimensionsById.TryGetValue(id, out var multiDimension) && multiDimension.IsMultiSelect).Distinct().Take(2).ToList();
        Data.Settings.HomeMultiDisplayDimensionId = Data.Settings.HomeMultiDisplayDimensionIds.Count > 0 ? Data.Settings.HomeMultiDisplayDimensionIds[0] : null;
        Data.Games ??= [];
        if (string.IsNullOrWhiteSpace(Data.RcRootPath))
        {
            var existingRoot = Data.Games.Select(game => InferRcRoot(game.GamePath)).FirstOrDefault(path => path is not null);
            if (existingRoot is not null) Data.RcRootPath = existingRoot;
        }
        Data.PlayStatuses = Data.PlayStatuses?.Any() == true ? Data.PlayStatuses : Defaults.PlayStatuses();
        Data.GameStatuses = Data.GameStatuses?.Any() == true ? Data.GameStatuses : Defaults.GameStatuses();
        NormalizeStatusIcons();
        if (Data.Settings.SelectedPlayStatusFilter is int playFilter && !Data.PlayStatuses.Any(status => status.Id == playFilter))
            Data.Settings.SelectedPlayStatusFilter = null;
        if (Data.Settings.SelectedGameStatusFilter is int gameFilter && !Data.GameStatuses.Any(status => status.Id == gameFilter))
            Data.Settings.SelectedGameStatusFilter = null;
        foreach (var g in Data.Games)
        {
            if (Path.IsPathFullyQualified(g.GamePath) && !string.IsNullOrWhiteSpace(Data.RcRootPath))
            {
                var relative = Path.GetRelativePath(Data.RcRootPath, g.GamePath);
                if (relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar)) g.GamePath = relative;
            }
            if (!Data.SaveRoots.Any(root => root.Id == g.SaveRootId)) g.SaveRootId = Defaults.SaveRootGameDirectoryId;
            if (Path.IsPathFullyQualified(g.SavePath))
            {
                var matching = Data.SaveRoots.Select(root => (root, basePath: ResolveSaveRoot(root.Id, g.GamePath))).Where(item => !string.IsNullOrWhiteSpace(item.basePath)).Select(item => (item.root, relative: Path.GetRelativePath(item.basePath, g.SavePath))).Where(item => item.relative != ".." && !item.relative.StartsWith(".." + Path.DirectorySeparatorChar)).OrderBy(item => item.relative.Length).FirstOrDefault();
                if (matching.root is not null) { g.SaveRootId = matching.root.Id; g.SavePath = matching.relative; }
            }
            g.Tags ??= [];
            g.MultiTags ??= [];
            while (g.Tags.Count < Data.TagSchema.Count) g.Tags.Add(0);
            while (g.Tags.Count > Data.TagSchema.Count) g.Tags.RemoveAt(g.Tags.Count - 1);
            while (g.MultiTags.Count < Data.TagSchema.Count) g.MultiTags.Add([]);
            while (g.MultiTags.Count > Data.TagSchema.Count) g.MultiTags.RemoveAt(g.MultiTags.Count - 1);
            for (var i = 0; i < g.Tags.Count; i++)
            {
                var dimension = Data.TagSchema[i];
                if (!dimension.IsMultiSelect)
                {
                    if (!dimension.Values.ContainsKey(g.Tags[i])) g.Tags[i] = 0;
                    g.MultiTags[i] = [];
                    continue;
                }
                var values = g.MultiTags[i] ??= [];
                values.RemoveAll(value => !dimension.Values.ContainsKey(value));
                values = values.Distinct().ToList();
                if (values.Count == 0) values.Add(0);
                if (values.Count > 1) values.RemoveAll(value => value == 0);
                g.MultiTags[i] = values;
                g.Tags[i] = 0;
            }
            if (!Data.RegionCommands.ContainsKey(g.RegionCommandId)) g.RegionCommandId = 0;
            if (!Data.PlayStatuses.Any(s => s.Id == g.PlayStatusId)) g.PlayStatusId = Data.PlayStatuses.Single(s => s.IsDefault).Id;
            if (!Data.GameStatuses.Any(s => s.Id == g.GameStatusId)) g.GameStatusId = Data.GameStatuses.Single(s => s.IsDefault).Id;
        }
    }

    private void NormalizeStatusIcons()
    {
        var playDefaults = Defaults.PlayStatuses().ToDictionary(status => status.Id);
        foreach (var status in Data.PlayStatuses)
        {
            if (status.Id == 2 && status.Name == "In progress") status.Name = "Playing";
            if (string.IsNullOrWhiteSpace(status.IconVector) && playDefaults.TryGetValue(status.Id, out var fallback)) status.IconVector = fallback.IconVector;
            else if (string.IsNullOrWhiteSpace(status.IconVector)) status.IconVector = StatusIconVectors.DefaultFor(StatusKind.Play);
        }

        var gameDefaults = Defaults.GameStatuses().ToDictionary(status => status.Id);
        foreach (var status in Data.GameStatuses)
        {
            if (status.Id == 2 && status.Name == "Not local, available elsewhere") status.Name = "In other machine";
            if (string.IsNullOrWhiteSpace(status.SystemRole) && gameDefaults.TryGetValue(status.Id, out var systemFallback)) status.SystemRole = systemFallback.SystemRole;
            if (string.IsNullOrWhiteSpace(status.IconVector) && gameDefaults.TryGetValue(status.Id, out var fallback)) status.IconVector = fallback.IconVector;
            else if (string.IsNullOrWhiteSpace(status.IconVector)) status.IconVector = StatusIconVectors.DefaultFor(StatusKind.Game);
            if (!string.IsNullOrWhiteSpace(status.SystemRole)) status.Color = Defaults.GameStatusColor(status.SystemRole);
        }
        foreach (var required in Defaults.GameStatuses())
        {
            if (Data.GameStatuses.Any(status => string.Equals(status.SystemRole, required.SystemRole, StringComparison.Ordinal))) continue;
            var id = Data.GameStatuses.Any(status => status.Id == required.Id) ? Data.GameStatuses.Select(status => status.Id).DefaultIfEmpty(0).Max() + 1 : required.Id;
            Data.GameStatuses.Add(new GameStatus { Id = id, Name = required.Name, Color = required.Color, IconVector = required.IconVector, IsDefault = required.IsDefault, SystemRole = required.SystemRole });
        }
    }

    private static string? InferRcRoot(string storedPath)
    {
        if (!Path.IsPathFullyQualified(storedPath)) return null;
        for (DirectoryInfo? directory = new DirectoryInfo(Path.GetDirectoryName(storedPath) ?? ""); directory is not null; directory = directory.Parent)
        {
            if (string.Equals(directory.Name, "rc", StringComparison.OrdinalIgnoreCase)) return directory.FullName;
        }
        return null;
    }

    private TagDimension Dimension(int id) => Data.TagSchema.FirstOrDefault(d => d.DimensionId == id) ?? throw new InvalidOperationException("Dimension not found.");
    private List<GameStatus> Statuses(StatusKind kind) => kind == StatusKind.Play ? Data.PlayStatuses : Data.GameStatuses;
    private GameStatus? GameStatusByRole(string role) => Data.GameStatuses.FirstOrDefault(status => string.Equals(status.SystemRole, role, StringComparison.Ordinal));
    public void Log(string message) => AppLog.Error("DataStore", message);
    public void Dispose() { }
}

public static class PathRules
{
    public static bool IsValidGameExe(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
    public static bool IsValidSaveTarget(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && (File.Exists(path) || Directory.Exists(path));
}

public static class TextRules
{
    public static string TrimGraphemes(string? value, int maximum, string fallback)
    {
        value ??= "";
        var elements = System.Globalization.StringInfo.ParseCombiningCharacters(value);
        return elements.Length == 0 && fallback.Length > 0 ? fallback : elements.Length <= maximum ? value : value[..elements[maximum]];
    }
}
