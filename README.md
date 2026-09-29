# Input Switcher

A small Windows system-tray app that switches external monitor inputs over **DDC/CI** (VCP feature 60 — Input Source), similar to Linux `ddcutil`.

Left-click the tray icon for a compact flyout (like the Windows sound or network panel). Right-click for **Refresh** and **Exit**.

## Features

- Lives in the tray (no taskbar button; single instance)
- Discovers DDC-capable monitors via the Windows Physical Monitor API
- Lists **only** inputs each monitor reports in its DDC capabilities string
- Highlights the active input; clicking it again does nothing (avoids a blanking refresh)
- Shows how this PC is linked (`Via HDMI` / `Via DisplayPort`) from Windows DisplayConfig

No brand-specific tables: any monitor that answers DDC/CI and reports feature 60 values should work.

## Requirements

- Windows 10/11
- .NET **not** required to run a published single-file build
- External monitor with **DDC/CI enabled** in the OSD (laptop panels usually do not support this)

## Run (published build)

```
publish\InputSwitcher.exe
```

Build that file yourself with the command below, or attach it to a GitHub Release.

## Build

```powershell
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -o publish
```

Requires the .NET 10 SDK (or adjust `TargetFramework` in `InputSwitcher.csproj`).

Self-test (no UI):

```powershell
dotnet run -c Release -- --self-test
```

## Notes

- After you switch the monitor to another device, this PC may lose the picture until the input is switched back. That is expected.
- Some monitors report incorrect or incomplete input lists; the app never invents HDMI/DP buttons for those cases.
- Monitors often map HDMI to legacy VESA codes (for example DVI). For the **active** input, Windows cable type is preferred when the VESA label looks wrong.

## License

MIT — see [LICENSE](LICENSE).
