using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace GameShelf;

public sealed class PackageService(DataStore store)
{
    private readonly DataStore _store = store;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public void Export(GameEntry game, string destination)
    {
        var manifest = new ImportedGame
        {
            Game = Clone(game),
            RegionCommandDisplay = _store.Data.RegionAliases.GetValueOrDefault(game.RegionCommandId, ""),
            PlayStatusDisplay = _store.Data.PlayStatuses.FirstOrDefault(s => s.Id == game.PlayStatusId)?.Name ?? "",
            GameStatusDisplay = _store.Data.GameStatuses.FirstOrDefault(s => s.Id == game.GameStatusId)?.Name ?? ""
        };
        for (var i = 0; i < _store.Data.TagSchema.Count; i++)
        {
            var d = _store.Data.TagSchema[i];
            manifest.Dimensions.Add(new ImportedDimension { SourceDimensionId = d.DimensionId, Name = d.Name, Values = new Dictionary<int, string>(d.Values), GameValue = game.Tags[i] });
        }
        var sourceImage = _store.ImagePath(game);
        if (!string.IsNullOrEmpty(sourceImage) && File.Exists(sourceImage)) manifest.ImageEntry = "image.png";
        using var zip = ZipFile.Open(destination, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
        using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false))) writer.Write(JsonSerializer.Serialize(manifest, Json));
        if (manifest.ImageEntry.Length > 0) zip.CreateEntryFromFile(sourceImage, manifest.ImageEntry, CompressionLevel.Optimal);
    }

    public ImportedGame Inspect(string packagePath)
    {
        using var zip = ZipFile.OpenRead(packagePath);
        if (zip.Entries.Count > 3 || zip.Entries.Any(e => e.FullName.Contains("..") || e.FullName.Contains('/') && e.FullName.StartsWith('/'))) throw new InvalidOperationException("Unsafe import package.");
        var entry = zip.GetEntry("manifest.json") ?? throw new InvalidOperationException("Package does not contain manifest.json.");
        ImportedGame? manifest;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) manifest = JsonSerializer.Deserialize<ImportedGame>(reader.ReadToEnd());
        if (manifest is null || manifest.FormatVersion != 1 || manifest.Game is null) throw new InvalidOperationException("Unsupported or corrupt import package.");
        if (!string.IsNullOrEmpty(manifest.ImageEntry) && zip.GetEntry(manifest.ImageEntry) is null) throw new InvalidOperationException("Package image is missing.");
        return manifest;
    }

    public void Import(string packagePath, ImportedGame manifest, IReadOnlyDictionary<int, (int dimensionId, int value)> mappings, int playStatus, int gameStatus, int regionCommand)
    {
        if (_store.Data.Games.Any(g => g.Id == manifest.Game.Id)) throw new InvalidOperationException("A game with this ID already exists.");
        var game = Clone(manifest.Game);
        game.GamePath = ""; game.SavePath = ""; game.SaveRootId = Defaults.SaveRootGameDirectoryId; game.ImageFile = "";
        game.Tags = Enumerable.Repeat(0, _store.Data.TagSchema.Count).ToList();
        foreach (var item in manifest.Dimensions)
            if (mappings.TryGetValue(item.SourceDimensionId, out var map))
            {
                var position = _store.Data.TagSchema.FindIndex(d => d.DimensionId == map.dimensionId);
                if (position >= 0 && _store.Data.TagSchema[position].Values.ContainsKey(map.value)) game.Tags[position] = map.value;
            }
        game.PlayStatusId = _store.Data.PlayStatuses.Any(s => s.Id == playStatus) ? playStatus : Defaults.PlayDefaultId;
        game.GameStatusId = _store.Data.GameStatuses.Any(s => s.Id == gameStatus) ? gameStatus : Defaults.GameDefaultId;
        game.RegionCommandId = _store.Data.RegionCommands.ContainsKey(regionCommand) ? regionCommand : 0;
        if (string.IsNullOrEmpty(manifest.ImageEntry)) { _store.Data.Games.Add(game); _store.Save(); return; }

        using var zip = ZipFile.OpenRead(packagePath);
        var entry = zip.GetEntry(manifest.ImageEntry)!;
        var temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png");
        var imageName = $"{game.Id}-{Guid.NewGuid():N}.png";
        var target = Path.Combine(_store.Paths.ImagesDirectory, imageName);
        try
        {
            entry.ExtractToFile(temp);
            ImageService.ProcessToCard(temp, target); // stage image before changing the database
            game.ImageFile = imageName;
            _store.Data.Games.Add(game);
            try { _store.Save(); }
            catch { _store.Data.Games.Remove(game); File.Delete(target); throw; }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static GameEntry Clone(GameEntry game) => new() { Id = game.Id, Title = game.Title, ImageFile = game.ImageFile, Note = game.Note, GamePath = game.GamePath, SaveMethod = game.SaveMethod, SaveRootId = game.SaveRootId, SavePath = game.SavePath, PlayStatusId = game.PlayStatusId, GameStatusId = game.GameStatusId, RegionCommandId = game.RegionCommandId, Tags = [.. game.Tags] };
}
