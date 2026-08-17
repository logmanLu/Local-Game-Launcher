namespace GameShelf;

public sealed class AppPaths
{
    public required string Root { get; init; }
    public required string DataDirectory { get; init; }
    public required string ImagesDirectory { get; init; }
    public required string DatabaseFile { get; init; }
    public required string LogDirectory { get; init; }

    public static AppPaths FromExecutable()
    {
        var root = AppContext.BaseDirectory;
        var data = Path.Combine(root, "savedata");
        return new AppPaths
        {
            Root = root,
            DataDirectory = data,
            ImagesDirectory = Path.Combine(data, "images"),
            DatabaseFile = Path.Combine(data, "gameshelf.json"),
            LogDirectory = Path.Combine(root, "log")
        };
    }
}
