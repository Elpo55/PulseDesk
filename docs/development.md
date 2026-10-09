# Development guide

## Prerequisites

- Windows 10 1809+ or Windows 11 to build and run the application (WinUI 3 and the Windows performance APIs only
  exist on Windows). On Linux and macOS, the shared projects and the tests build and run (see below).
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). `global.json` accepts any 10.0 feature band.
- Optional: Visual Studio 2026 (with the *WinUI application development* workload) or VS Code with C# Dev Kit.

No other setup is needed: the Windows App SDK comes from NuGet, and the app runs unpackaged with its
runtime bundled (`WindowsAppSDKSelfContained`), so no MSIX, certificate or runtime installer is required.

## Everyday commands

```powershell
dotnet build Sysora.slnx                     # build everything (Debug)
dotnet run --project src/Sysora.App          # run with live data
dotnet run --project src/Sysora.App -- --demo --page=Processes
dotnet test --project src/Sysora.Tests       # run the unit tests
dotnet run --project src/Sysora.App -- --demo --language=fr
```

`global.json` opts into Microsoft.Testing.Platform for `dotnet test` (required by xUnit v3 on .NET 10).

In Visual Studio, open `Sysora.slnx`, set **Sysora.App** as the startup project and pick the
*Sysora* or *Sysora (demo)* launch profile.

### Command-line options

| Option | Effect |
| --- | --- |
| `--demo` | Use `SimulatedMachine` instead of the Windows providers. The title bar shows "DEMO MODE". |
| `--tray` | Start hidden in the notification area. |
| `--startup` | Added by the Run key: applies "Start minimized" / "Start in tray". |
| `--language=fr` | Use a language for this run only (`en` or `fr`), without changing the setting. |
| `--page=Name` | Open on a page (`Dashboard`, `PcHealth`, `Timeline`, `Diagnosis`, `Troubleshooting`, `Replay`, `Compare`, `AppImpact`, `Changes`, `Alerts`, `Gaming`, `Performance`, `Processes`, `Storage`, `LargeFiles`, `Network`, `System`, `Settings`). `History` opens Replay. |

Demo mode uses its own single-instance key, so it can run next to a normal instance. Its history is kept in
memory only: simulated data never reaches the real history database, and Large files scans a made-up folder tree
instead of the real disk. It starts from your settings, but changes made during a demo (including "Start with
Windows") last only for that session.

### Local data

| What | Where |
| --- | --- |
| Settings | `%LOCALAPPDATA%\Sysora\settings.json` (delete it to reset) |
| Logs | `%LOCALAPPDATA%\Sysora\Logs\sysora-YYYYMMDD.log` |
| History (performance, application usage, events, alerts, snapshots, changes, game sessions, investigations) | `%LOCALAPPDATA%\Sysora\history.db` (SQLite; delete it, or use Settings › History › Delete, to reset) |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Sysora` (only when enabled) |

Set *Settings › Diagnostics › Log level* to **Debug** to see every collection failure.

## Publishing

```powershell
dotnet publish src/Sysora.App -c Release -r win-x64 --self-contained -o artifacts/win-x64
```

The output folder runs on any Windows 10 1809+ PC without installing .NET or the Windows App SDK.
Use `-r win-arm64` for ARM64. Release publishes are precompiled (ReadyToRun), which makes Sysora start faster.

### Installer and release

`installer/Sysora.iss` ([Inno Setup 6](https://jrsoftware.org/isinfo.php)) packages that folder into a setup program
(Program Files or per-user install, Start menu shortcut, uninstaller in Settings › Apps):

```powershell
dotnet publish src/Sysora.App -c Release -r win-x64 --self-contained -o artifacts/publish/win-x64
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /DAppVersion=1.3.0 /DArch=x64 installer\Sysora.iss
```

The installer lands in `artifacts/installer`. To publish a release, set `<Version>` in `Directory.Build.props`, add
`docs/release-notes/v<version>.md`, then push a `v<version>` tag: `.github/workflows/release.yml` runs the tests, builds the
x64 and ARM64 installers with their SHA-256 checksums and creates the GitHub release.

## Conventions

- **Layers**: Core and Infrastructure stay platform-agnostic. Windows APIs belong in Infrastructure.Windows, Linux and
  macOS APIs in `Infrastructure/Linux` and `Infrastructure/MacOS` (marked `[SupportedOSPlatform]`). WinUI types
  belong in App. View models use services and Core interfaces, never system APIs directly.
- **Texts**: every text shown to the user comes from `Sysora.Localization`, never from a literal in C# or XAML (a test
  checks the XAML pages). See [Translating Sysora](#translating-sysora).
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
- **Observed, inferred, unknown**: an analysis labels its statements with `FindingBasis`. A correlation is worded as such
  ("Likely contributor", "Associated with", "Evidence suggests"); what cannot be observed says "Cause unknown" or
  "Not available".
- **Settings evolve safely**: a new setting gets its declared default when an older file lacks it (the file is laid over the
  serialized defaults before it is read). Add a test when adding a setting to an existing section.
- **Style**: enforced by `.editorconfig` and the .NET analyzers. CI treats warnings as errors.

## Adding a metric

See [architecture.md](architecture.md#adding-a-metric). Add tests for any new logic in Core. Keep
system-specific parsing in small pure functions (like `GpuCounterInstance` or `ProcFiles`) so they can be unit tested
on every system.

## Translating Sysora

Texts are in `src/Sysora.Localization`:

| File | Contents |
| --- | --- |
| `Strings.resx` / `Strings.fr.resx` | Texts produced by the analysis: diagnoses, alerts, events, reports, units |
| `UiStrings.resx` / `UiStrings.fr.resx` | The interface: pages, buttons, settings, messages |

- Edit a text in Visual Studio's resource editor or in any text editor (they are XML). Keep the placeholders (`{0}`,
  `{1:N0}`...): the tests fail when a translation uses a placeholder the English text does not have.
- A French text may reorder the placeholders, and must use French typography: a space before `: ; ? !` (it becomes
  non-breaking automatically), « » quotes, and a decimal comma comes from the regional format, not from the text.
- Plurals: texts used with `Text.Plural` have a singular and a plural version (`..._One` and `..._Other`). In French,
  0 and 1 are singular.
- To add a language: add `Strings.<code>.resx` and `UiStrings.<code>.resx` with every text, add the language to
  `AppLanguage.Supported`, and run the tests, which check that every text is translated.
- Run `dotnet run --project src/Sysora.App -- --demo --language=fr` to see the result without changing your settings.

Texts saved in the history (events, alerts, detected changes) keep the language they were recorded in.

## Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request:

- **Windows**: restore → build of the whole solution (Release, warnings as errors) → every test.
- **Ubuntu and macOS**: build of the shared projects and the tests on `net10.0` → every test. Tests of the Linux or
  macOS adapters read the runner itself; the Windows-only tests (`src/Sysora.Tests/WindowsOnly`) are not compiled.

Each job adds its totals and every failed test as annotations on the run (`.github/scripts/report-tests.ps1`).
See [platforms.md](platforms.md) for what this does and does not prove.

## Troubleshooting

- **Some metrics show "Not available"**: check the log. Performance counters can be disabled or corrupted
  on some systems; `lodctr /R` (in an elevated prompt) rebuilds them.
- **The window doesn't appear**: Sysora may already be running in the notification area. Launching it
  again brings the existing window to the front.
- **Ending a process fails with "Access denied"**: Sysora never elevates itself. Processes of other users
  or of the system require running Sysora as administrator. Critical Windows processes are always refused.
