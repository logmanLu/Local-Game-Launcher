using System.Text.Json.Serialization;

namespace GameShelf;

public enum StatusKind { Play, Game }

public sealed class AppData
{
    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public Dictionary<int, string> RegionCommands { get; set; } = new() { [0] = "" };
    public Dictionary<int, string> RegionAliases { get; set; } = new() { [0] = "none" };
    public string RcRootPath { get; set; } = "";
    public List<SaveRoot> SaveRoots { get; set; } = Defaults.SaveRoots();
    public List<TagDimension> TagSchema { get; set; } = [];
    public List<GameStatus> PlayStatuses { get; set; } = Defaults.PlayStatuses();
    public List<GameStatus> GameStatuses { get; set; } = Defaults.GameStatuses();
    public List<GameEntry> Games { get; set; } = [];
}

public sealed class AppSettings
{
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "system";
    public string Page { get; set; } = "library";
    public int? SelectedGameId { get; set; }
    public bool IsMaximized { get; set; }
    public bool IsFullscreen { get; set; }
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public Dictionary<int, int?> Filters { get; set; } = [];
    public Dictionary<int, List<int>> SelectedTagFilters { get; set; } = [];
    public List<int> HomeDisplayDimensionIds { get; set; } = [];
    public Dictionary<string, string> ButtonIcons { get; set; } = [];
    public Dictionary<int, RunningGameProcess> RunningGameProcesses { get; set; } = [];
}

/// <summary>Volatile process identity persisted only so a restarted GameShelf can reattach safely.</summary>
public sealed class RunningGameProcess
{
    public int ProcessId { get; set; }
    public long StartTimeUtcTicks { get; set; }
}

public sealed class TagDimension
{
    public int DimensionId { get; set; }
    public string Name { get; set; } = "";
    public Dictionary<int, string> Values { get; set; } = new() { [0] = "none" };
}

public sealed class GameStatus
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Color { get; set; } = "#808080";
    public string IconVector { get; set; } = "";
    public bool IsDefault { get; set; }
    /// <summary>Reserved role for the four path-aware game states; empty for user-defined states.</summary>
    public string SystemRole { get; set; } = "";
}

public sealed class GameEntry
{
    public int Id { get; set; }
    public string Title { get; set; } = "unknown";
    public string ImageFile { get; set; } = "";
    public string Note { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string SaveMethod { get; set; } = "";
    public int SaveRootId { get; set; } = Defaults.SaveRootGameDirectoryId;
    public string SavePath { get; set; } = "";
    public int PlayStatusId { get; set; }
    public int GameStatusId { get; set; }
    public int RegionCommandId { get; set; }
    public List<int> Tags { get; set; } = [];
}

public sealed class SaveRoot
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string PathTemplate { get; set; } = "";
}

public sealed class ImportedGame
{
    public int FormatVersion { get; set; } = 1;
    public GameEntry Game { get; set; } = new();
    public List<ImportedDimension> Dimensions { get; set; } = [];
    public string RegionCommandDisplay { get; set; } = "";
    public string PlayStatusDisplay { get; set; } = "";
    public string GameStatusDisplay { get; set; } = "";
    public string ImageEntry { get; set; } = "";
}

public sealed class ImportedDimension
{
    public int SourceDimensionId { get; set; }
    public string Name { get; set; } = "";
    public Dictionary<int, string> Values { get; set; } = [];
    public int GameValue { get; set; }
}

public static class Defaults
{
    public const int PlayDefaultId = 1;
    public const int GameDefaultId = 3;
    public const int MissingGameStatusId = 2;
    public const int SaveRootGameDirectoryId = 1;
    public const string InstalledRole = "installed";
    public const string OtherMachineRole = "other-machine";
    public const string MissingRole = "missing";
    public const string StoragedRole = "storaged";
    public static string GameStatusColor(string role) => role switch
    {
        InstalledRole => "#53b46b",
        OtherMachineRole => "#d35b5b",
        MissingRole => "#9a72d0",
        StoragedRole => "#4c91d9",
        _ => "#808080"
    };
    public static List<SaveRoot> SaveRoots() =>
    [
        new() { Id = 1, Name = "Game directory", PathTemplate = "." },
        new() { Id = 2, Name = "User Documents", PathTemplate = "%USERPROFILE%\\Documents" },
        new() { Id = 3, Name = "User AppData", PathTemplate = "%USERPROFILE%\\AppData" }
    ];
    public static List<GameStatus> PlayStatuses() =>
    [
        new() { Id = 1, Name = "Not played", Color = "#53b46b", IconVector = StatusIconVectors.OutlineSquare, IsDefault = true },
        new() { Id = 2, Name = "Playing", Color = "#d7ae42", IconVector = StatusIconVectors.HalfSquare },
        new() { Id = 3, Name = "Completed", Color = "#4c91d9", IconVector = StatusIconVectors.FilledSquare }
    ];
    public static List<GameStatus> GameStatuses() =>
    [
        new() { Id = 1, Name = "Installed locally", Color = "#53b46b", IconVector = StatusIconVectors.FilledCircle, SystemRole = InstalledRole },
        new() { Id = 2, Name = "In other machine", Color = "#d35b5b", IconVector = StatusIconVectors.OutlineCircle, SystemRole = OtherMachineRole },
        new() { Id = 3, Name = "Data missing", Color = "#9a72d0", IconVector = StatusIconVectors.Cross, IsDefault = true, SystemRole = MissingRole },
        new() { Id = 4, Name = "Storaged", Color = "#4c91d9", IconVector = StatusIconVectors.OutlineCloud, SystemRole = StoragedRole }
    ];
}
