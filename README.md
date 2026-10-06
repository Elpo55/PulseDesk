<p align="center">
  <img src="assets/logo.png" alt="Sysora logo" width="96" height="96" />
</p>

<h1 align="center">Sysora</h1>

<p align="center">
  <strong>Your PC, Explained.</strong><br />
  A lightweight, local-first Windows system dashboard.
</p>

<p align="center">
  <a href="https://github.com/Elpo55/Sysora/actions/workflows/ci.yml"><img src="https://github.com/Elpo55/Sysora/actions/workflows/ci.yml/badge.svg" alt="CI" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D4" alt="Windows 10 | 11" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4" alt=".NET 10" />
  <img src="https://img.shields.io/badge/license-MIT-green" alt="MIT license" />
</p>

Sysora is a modern, local-first Windows dashboard for monitoring system performance, processes,
storage, network activity and system health in real time. It is a native WinUI 3 application: no
account, no server, no telemetry, and it works offline.

![Sysora dashboard (dark theme, demo data)](docs/images/dashboard-dark.png)

> Screenshots use demo mode (`--demo`): every value is simulated, and the app shows a "DEMO MODE" badge.

## Features

Sysora does more than show numbers: it detects, explains and helps you understand what happens on your PC.

- **Diagnosis**: "Why is my PC slow?" Deterministic rules check CPU, memory, disks, GPU, network, applications and
  uptime, and explain each finding with the data behind it, a comparison with your usual activity and a confidence level
- **Replay**: look back at the last minutes (every second) or hours (per minute): charts with a cursor, the values and
  applications at any moment, events on the timeline, and a summary of what happened
- **App Impact**: which applications weigh the most on your PC, this session or over days, with an explainable impact
  score (CPU, memory and disk I/O weighted by running time), trends and per-application history
- **Changes**: what changed recently (applications installed, removed or updated, startup programs, Windows build,
  devices, disk space, average usage), from daily snapshots of the PC
- **Intelligent alerts**: only for problems that last or are unusual for this PC, one alert per condition, never spam;
  statuses New, Seen and Resolved; optional notifications
- **Gaming recap**: game sessions are detected automatically when the game is identifiable (Windows' own list of your
  games, or a game library folder such as Steam or Epic; you can also mark or unmark an application). When the game
  closes, a recap shows averages and peaks (CPU, GPU, video memory, RAM, disk, network, the game's own usage), the
  limits reached, the likely limiting factor, the busiest background applications and a comparison with your previous
  sessions of the same game. **FPS is never estimated**: Windows offers no reliable source, so it is shown as
  "Not available". While a game is in front, Sysora pauses its own window updates and samples less often
- **Usual activity**: the diagnosis compares the current activity with the last hour, today, yesterday, the last 7 and
  30 days and the usual level at this hour, once enough history exists (otherwise it says how much data is missing)
- **CPU monitoring**: overall and per-logical-processor usage, effective clock speed, base and peak speed, cores, threads, caches
- **Memory monitoring**: used, available, committed, system cache, kernel pools
- **GPU monitoring**: usage per adapter (busiest engine, the way Task Manager computes it), dedicated and shared memory, busiest engines
- **Storage monitoring**: capacity of every local volume, plus active time and read/write throughput
- **Network monitoring**: interfaces, local addresses, download/upload rates, and the connectivity Windows reports
- **Process monitoring**: a simplified task manager with live sorting, search, details, top consumers, and ending a process after confirmation
- **System information**: Windows edition and build, processor, graphics, memory, motherboard, BIOS, uptime
- **Health indicators**: CPU and memory alerts must persist for a configurable time before they're reported, so short spikes don't trigger them; thresholds are configurable
- **Local history**: aggregated history in a local SQLite database with automatic retention (configurable, deletable)
- **Real-time charts**: 30 seconds to 30 minutes of history, kept in fixed-size in-memory ring buffers
- **Windows tray support**: close to the notification area, pause/resume from the tray, start with Windows; pending
  history is written before the PC sleeps, and every metric is refreshed when it resumes
- **Light, dark and system themes** with the Windows 11 look (Mica, Fluent controls)
- **No account**
- **No telemetry by default**: nothing is ever uploaded

<details>
<summary>More screenshots</summary>

![Performance page](docs/images/performance-dark.png)
![Processes page](docs/images/processes-dark.png)
![Dashboard in the light theme](docs/images/dashboard-light.png)

</details>

## Privacy

Sysora is local-first.

- No telemetry is enabled by default.
- No account is required.
- No system metrics are uploaded.

Sysora never opens a network connection. "Internet available" comes from Windows' own
connectivity checks, so Sysora generates no network traffic to measure it. Settings, logs and the local history
(performance, application names, alerts, detected changes, game sessions) are stored in `%LOCALAPPDATA%\Sysora` and stay on
your PC. History recording can be turned off and the history deleted from Settings.

## Accuracy

Sysora only shows values Windows actually provides. Anything that isn't available on a given machine
appears as **"Not available"**, never as an invented number. For example, CPU temperature can't be read
through any documented Windows API without a third-party kernel driver, which Sysora deliberately does not
install. See [docs/architecture.md](docs/architecture.md#where-the-numbers-come-from) for the data source
of every metric.

## Install

Download the installer from the [latest release](https://github.com/Elpo55/Sysora/releases/latest):
`Sysora-<version>-setup-x64.exe` (or `-arm64` for Windows on ARM), run it, then start Sysora from the Start
menu. It installs for all users by default; choose **Install for me only** on the first screen to install without
administrator rights. Uninstall it from Settings › Apps.

The installer is not code-signed yet: if SmartScreen shows "Windows protected your PC", click **More info**, then
**Run anyway**, and compare the file with `SHA256SUMS.txt` from the release.

## Requirements

- Windows 10 version 1809 (build 17763) or later, Windows 11 recommended; x64 or ARM64
- To build: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
  Visual Studio is optional. Everything else (Windows App SDK, WinUI 3) comes from NuGet.

## Build

```powershell
git clone https://github.com/Elpo55/Sysora.git
cd Sysora
dotnet build Sysora.slnx -c Release
```

## Run

```powershell
dotnet run --project src/Sysora.App -c Release
```

The app is unpackaged and bundles the Windows App SDK runtime, so no runtime installation or signing
certificate is needed. Useful options:

| Option | Effect |
| --- | --- |
| `--demo` | Simulated metrics (clearly labeled), for UI work and screenshots |
| `--tray` | Start hidden in the notification area |
| `--page=Processes` | Open on a given page (`Dashboard`, `Diagnosis`, `Replay`, `AppImpact`, `Changes`, `Alerts`, `Gaming`, `Performance`, `Processes`, `Storage`, `Network`, `System`, `Settings`) |

Pass them after `--` with `dotnet run`, for example `dotnet run --project src/Sysora.App -- --demo`.

## Test

```powershell
dotnet test --project src/Sysora.Tests
```

The unit tests cover the Core logic (ring buffers, history, formatting, trends, thresholds, anomaly
detection, health, settings serialization, the monitoring loop, diagnosis, alerts, app impact, change detection,
replay), the SQLite history (in memory) and the pure parsing helpers of the Windows layer. They use simulated data
and never depend on the machine's hardware.

## Project structure

```
src/
  Sysora.App/             WinUI 3 application: views, view models, controls, UI services
  Sysora.Core/            Models, interfaces, monitoring loop, health, history, analysis, diagnosis,
                             alerts, change detection, settings (no Windows dependency)
  Sysora.Infrastructure/  Windows implementations: performance counters, native APIs, registry, files,
                             local SQLite history
  Sysora.Tests/           Unit tests
docs/                        Architecture and development guides
```

Read [docs/architecture.md](docs/architecture.md) for the design and [docs/development.md](docs/development.md)
to contribute.

## Roadmap

Done: local history with retention, notifications for lasting problems, Diagnosis, Replay, App Impact, Changes and
intelligent alerts (see [IMPLEMENTATION_NOTES.md](IMPLEMENTATION_NOTES.md)). Planned next:

1. **Advanced GPU metrics**: temperature and clock speed through the documented D3DKMT adapter statistics, where drivers expose them
2. **Storage analyzer**: on-demand, read-only folder sizes
3. Per-process network usage, export of a diagnosis or a replay period, change detection for drivers and services,
   monitoring profiles, an optional local API (for example for Windows Orchestrator)

## License

[MIT](LICENSE)
