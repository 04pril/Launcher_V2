# Pixelmon Launcher recovered source

Recovered from the uploaded `LL.exe` single-file .NET/WPF build.

## Included

- `PixelmonLauncher_Recovered.sln`
- WPF project file
- `App.xaml` / `App.xaml.cs`
- `MainWindow.xaml` / `MainWindow.xaml.cs`
- `LauncherSettings.cs`
- `MinecraftLaunchService.cs`

## Recovery notes

- The executable was not obfuscated.
- The embedded assembly contained `PixelmonLauncher.MainWindow` and `MinecraftLaunchService` metadata and IL.
- The recovered executable is an earlier build than the later download/progress code fragments supplied separately. The executable did not contain `MinecraftLaunchProgress`, asset-index downloading, asset-object downloading, or SHA-1 download verification logic.
- `MainWindow.xaml` was reconstructed from the compiled WPF/BAML resource and connection metadata; it is functionally reconstructed rather than guaranteed byte-for-byte identical to the original XAML.
- The original PNG and BAML binary resources were extracted locally, but this connector upload contains the editable text source. Add the three recovered images under `pixelmon-launcher/Assets/Images/` before building if they are not present.
- The project targets `net8.0-windows` with WPF.

## Expected image names

- `launcher-cutout.png`
- `pixelmon-logo.png`
- `pixelmon-logo-hq-cutout.png`

## Build

Open `PixelmonLauncher_Recovered.sln` with Visual Studio 2022 or run:

```powershell
dotnet build "PixelmonLauncher_Recovered.sln"
```

The source was structurally checked during recovery, but a full build was not run in the recovery environment because the .NET SDK was unavailable there.
