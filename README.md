# GameShelf

A self-contained Windows game-library launcher built with Windows Forms and .NET 10.

The published application keeps mutable personal data next to `GameShelf.exe` in `savedata/`. That directory is deliberately excluded from Git: it can contain local executable paths, save locations, covers, and game metadata.

## Build

```powershell
dotnet publish .\GameShelf\GameShelf.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

The result is `publish/GameShelf.exe`. The repository intentionally excludes `publish/`; create releases locally or through a release workflow.

## Documentation

[MAINTENANCE.md](MAINTENANCE.md) is the authoritative technical reference. It documents the JSON format, path portability model, game-process tracking, UI controls, migration, diagnostics, and publish process.
