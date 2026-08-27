# GameShelf

A self-contained Windows game-library launcher built with Windows Forms and .NET 10.

The published application keeps mutable personal data next to `GameShelf.exe` in `savedata/`. That directory is deliberately excluded from Git: it can contain local executable paths, save locations, covers, and game metadata. Diagnostic logs are written separately to `log/` and are also excluded.

## Build

```powershell
dotnet publish .\GameShelf\GameShelf.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

The local launch target is always `publish/Launcher.exe`; point Windows shortcuts at this stable filename. Each release additionally copies it to `Launcher_<version>.exe` for the GitHub Release attachment. The repository intentionally excludes `publish/`; create releases locally or through a release workflow.

## Documentation

[spec.md](spec.md) is the authoritative current architecture and persistent-data contract. [MAINTENANCE.md](MAINTENANCE.md) is the chronological patch and troubleshooting record. GameShelf deliberately does not track or control game processes after launch.
