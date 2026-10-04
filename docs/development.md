# Development guide

## Prerequisites

- Windows 10 1809+ or Windows 11 (WinUI 3 and the Windows performance APIs only exist on Windows)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). `global.json` accepts any 10.0 feature band.
- Optional: Visual Studio 2026 (with the *WinUI application development* workload) or VS Code with C# Dev Kit.

No other setup is needed: the Windows App SDK comes from NuGet, and the app runs unpackaged with its
runtime bundled (`WindowsAppSDKSelfContained`), so no MSIX, certificate or runtime installer is required.

## Everyday commands

```powershell
dotnet build PulseDesk.slnx                     # build everything (Debug)
dotnet run --project src/PulseDesk.App          # run with live data
dotnet run --project src/PulseDesk.App -- --demo --page=Processes
dotnet test --project src/PulseDesk.Tests       # run the unit tests
```

`global.json` opts into Microsoft.Testing.Platform for `dotnet test` (required by xUnit v3 on .NET 10).

In Visual Studio, open `PulseDesk.slnx`, set **PulseDesk.App** as the startup project and pick the
*PulseDesk* or *PulseDesk (demo)* launch profile.

### Command-line options

| Option | Effect |
| --- | --- |
| `--demo` | Use `SimulatedMachine` instead of the Windows providers. The title bar shows "DEMO MODE". |
| `--tray` | Start hidden in the notification area. |
| `--startup` | Added by the Run key: applies "Start minimized" / "Start in tray". |
| `--page=Name` | Open on a page (`Dashboard`, `Performance`, `Processes`, `Storage`, `Network`, `System`, `History`, `Settings`). |

Demo mode uses its own single-instance key, so it can run next to a normal instance.

### Local data

| What | Where |
| --- | --- |
| Settings | `%LOCALAPPDATA%\PulseDesk\settings.json` (delete it to reset) |
| Logs | `%LOCALAPPDATA%\PulseDesk\Logs\pulsedesk-YYYYMMDD.log` |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\PulseDesk` (only when enabled) |

Set *Settings › Diagnostics › Log level* to **Debug** to see every collection failure.

## Publishing

```powershell
dotnet publish src/PulseDesk.App -c Release -r win-x64 --self-contained -o artifacts/win-x64
```

The output folder runs on any Windows 10 1809+ PC without installing .NET or the Windows App SDK.
Use `-r win-arm64` for ARM64.

## Conventions

- **Layers**: Core stays platform-agnostic. Windows APIs belong in Infrastructure. WinUI types belong in App.
  View models use services and Core interfaces, never Windows APIs directly.
- **No invented values**: a metric that can't be read is `null` and is displayed as "Not available"
  (`MetricFormatter.NotAvailable`). Simulated values exist only in tests and demo mode.
- **Strong typing**: immutable `record` models, nullable for optional metrics, no `dynamic`.
- **MVVM**: CommunityToolkit.Mvvm with `[ObservableProperty]` on partial properties and `[RelayCommand]`.
  Compiled bindings (`x:Bind`) only.
- **Threading**: providers run on the monitoring loop; `UiMetricsHub` is the only bridge to the UI thread.
  Never block the UI thread; pass `CancellationToken`s through async calls.
- **Expected failures are values**: operations that can fail for normal reasons return `OperationResult`
  (for example "access denied" when ending a process).
- **Comments** explain *why*, not *what*. Public types have XML documentation summaries.
- **Style**: enforced by `.editorconfig` and the .NET analyzers. CI treats warnings as errors.

## Adding a metric

See [architecture.md](architecture.md#adding-a-metric). Add tests for any new logic in Core. Keep
Windows-specific parsing in small pure functions (like `GpuCounterInstance`) so they can be unit tested too.

## Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request (Windows runner):
restore → build (Release, warnings as errors) → test.

## Troubleshooting

- **Some metrics show "Not available"**: check the log. Performance counters can be disabled or corrupted
  on some systems; `lodctr /R` (in an elevated prompt) rebuilds them.
- **The window doesn't appear**: PulseDesk may already be running in the notification area. Launching it
  again brings the existing window to the front.
- **Ending a process fails with "Access denied"**: PulseDesk never elevates itself. Processes of other users
  or of the system require running PulseDesk as administrator. Critical Windows processes are always refused.
