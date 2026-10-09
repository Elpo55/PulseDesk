# Platforms

Sysora is a Windows application. Its logic (analysis, diagnosis, alerts, history, settings, translations) is shared
code that also builds and passes its tests on Linux and macOS, and the first Linux and macOS data sources exist, but
**there is no Sysora application for Linux or macOS yet**. This page says exactly what works where, and how it was
checked.

## How each status was checked

| Status | Meaning |
| --- | --- |
| **Verified** | Used on a real Windows PC, and covered by the automated tests. |
| **Tested in CI** | Implemented and run by the automated tests on a GitHub Actions runner of that system (a virtual machine): it reads real values from the runner, but has not been tried on a real computer. |
| **Implemented, not verified** | Written and unit-tested with sample data, never run on that system. |
| **Partial** | Works, with documented gaps. |
| **Permission needed** | Needs rights Sysora does not ask for (Sysora never elevates itself). |
| **Planned** | Not implemented yet. |
| **Not supported** | Not possible with a documented API of that system. |

A successful build or test run is not proof that the application works on a real computer: only the Windows x64 column
has been used on real hardware, on Windows 11. Windows 10 (version 1809 or later) uses the same APIs and is supported,
but version 1.3 has not been tried on it.

## Compatibility matrix

| Feature | Windows x64 | Windows ARM64 | Linux | macOS |
| --- | --- | --- | --- | --- |
| Application (interface) | Verified (WinUI 3) | Implemented, not verified | Planned ([evaluation](#a-linux-and-macos-interface)) | Planned ([evaluation](#a-linux-and-macos-interface)) |
| Analysis, diagnosis, alerts, history, reports, settings | Verified | Implemented, not verified | Tested in CI (no interface uses it yet) | Tested in CI (no interface uses it yet) |
| French and English | Verified | Implemented, not verified | Tested in CI (texts and formats) | Tested in CI (texts and formats) |
| CPU usage | Verified (PDH, per processor) | Implemented, not verified | Tested in CI (`/proc/stat`, per processor) | Tested in CI (Mach host statistics, overall only) |
| CPU clock speed | Verified (effective, base, peak) | Implemented, not verified | Partial: current speed from `/proc/cpuinfo`, usually absent on ARM | Planned |
| CPU temperature | Partial: only when a driver reports it | Partial | Planned (depends on hardware sensors under `/sys`) | Planned |
| Memory | Verified (used, available, commit, cache, pools) | Implemented, not verified | Tested in CI (`/proc/meminfo`: used, available, cache; commit left out, see below) | Tested in CI (total, available = free + inactive pages) |
| Storage capacity | Verified (every local volume) | Implemented, not verified | Tested in CI (disk file systems from `/proc/self/mounts`) | Planned |
| Disk activity | Verified (PDH) | Implemented, not verified | Planned (`/proc/diskstats`) | Planned |
| Network | Verified (interfaces, rates, Windows connectivity) | Implemented, not verified | Planned | Planned |
| Processes and App Impact | Verified | Implemented, not verified | Planned (`/proc/<pid>`) | Planned |
| Ending a process | Verified; other users' and system processes: Permission needed | Implemented, not verified | Planned | Planned |
| GPU usage and video memory | Verified (per adapter, as Task Manager) | Implemented, not verified | Planned (driver-specific) | Not supported for now (no public per-GPU usage API) |
| System information (OS, processor, board, BIOS) | Verified | Implemented, not verified | Planned | Planned |
| Uptime | Verified | Implemented, not verified | Tested in CI (`/proc/uptime`) | Tested in CI (`kern.boottime`) |
| Game sessions (process to game matching) | Verified for games Windows recognizes and library folders; matching through launcher files is unit-tested but not yet seen in a real session | Implemented, not verified | Planned | Planned |
| Games recognized by Windows (Game Bar list) | Verified | Implemented, not verified | Not supported (Windows only) | Not supported (Windows only) |
| Games installed with Steam | Verified (registry and library manifests) | Implemented, not verified | Implemented, not verified (native, Flatpak and Snap folders) | Implemented, not verified |
| Games installed with Epic Games | Partial: the test PC's real manifests are read, but they point to a deleted folder and are correctly left out; never seen with an installed game | Implemented, not verified | Not supported (no Linux launcher) | Implemented, not verified |
| Games installed with Riot Client | Verified (VALORANT) | Implemented, not verified | Not supported (no Linux launcher) | Implemented, not verified |
| Games installed with GOG Galaxy | Implemented, not verified (no GOG game on the test PC) | Implemented, not verified | Not supported | Planned |
| FPS | Not supported (needs administrator event tracing or hooking the game) | Not supported | Not supported | Not supported |
| Per-application network usage | Permission needed (administrator event tracing); not implemented | Same | Planned | Planned |
| Large files (read-only scan) | Verified | Implemented, not verified | Tested in CI (scan of a real temporary folder; no interface) | Tested in CI (scan of a real temporary folder; no interface) |
| Start with the system, notification area | Verified | Implemented, not verified | Planned | Planned |

### Known limits of the Linux and macOS data sources

- **Linux memory commit** is not reported: with the default overcommit policy the kernel does not enforce
  `CommitLimit`, so comparing it with `Committed_AS` would raise false alarms.
- **macOS available memory** follows the estimate most tools use (free + inactive pages). Compressed memory and swap
  are not reported yet.
- **macOS CPU** is the overall usage only; per-processor usage and clock speed are not read.
- **Launcher folders on Linux and macOS** are the documented defaults. A library moved elsewhere is found through
  Steam's own `libraryfolders.vdf`, like on Windows, but none of these paths has been tried on a real Linux or macOS
  computer yet.

## Project layout

| Project | Target | Contents |
| --- | --- | --- |
| `Sysora.Core` | `net10.0` (any system) | Models, interfaces, monitoring loop, analysis, diagnosis, alerts, games, settings |
| `Sysora.Localization` | `net10.0` | English and French texts (`.resx`), language selection, plural rules |
| `Sysora.Infrastructure` | `net10.0` | Settings file, SQLite history, logs, large-file scan, data folders; Linux and macOS adapters (`Linux/`, `MacOS/`), launcher folders for Linux and macOS |
| `Sysora.Infrastructure.Windows` | `net10.0-windows` | Windows adapters: performance counters, native APIs, registry, Game Bar and launchers |
| `Sysora.App` | `net10.0-windows` | WinUI 3 application |
| `Sysora.Tests` | `net10.0-windows` on Windows, `net10.0` elsewhere | Unit tests; `WindowsOnly/` is compiled on Windows only |

The Linux and macOS adapters implement the same Core interfaces as the Windows ones (`ICpuMetricProvider`,
`IMemoryMetricProvider`, `IStorageMetricProvider`, `ISystemMetricProvider`), are marked with
`[SupportedOSPlatform]`, read only, and never need administrator rights.

## Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request:

| Runner | What runs |
| --- | --- |
| `windows-latest` | Build of the whole solution (warnings as errors), then every test |
| `ubuntu-latest` | Build of the shared projects and the tests on `net10.0`, then every test; the Linux adapters read the runner itself |
| `macos-latest` | Same as Ubuntu; the macOS adapters read the runner itself |

Each job adds its totals (passed, failed, skipped) and every failed test as annotations on the run. Tests that only
make sense on one system skip themselves elsewhere and say so: on Linux and macOS, the 18 test cases whose data are
Windows paths (`C:\...`, where a backslash is not a path separator) and the other system's live test.

## A Linux and macOS interface

The WinUI 3 interface only runs on Windows. Running Sysora on Linux or macOS needs a second interface, and the
candidate is [Avalonia](https://avaloniaui.net/) (XAML, MVVM, runs on Windows, Linux and macOS). It has **not** been
added yet. The reasoning:

**What would be shared as is**: Core, Localization and Infrastructure (more than half of the code), the texts in both
languages, and most of the view models' logic: 20 of the 26 view models use no WinUI type; 5 others only use the
WinUI dispatcher, which can be put behind a small interface, and one uses a WinUI message severity.

**What would have to be written again**: the 18 pages and the controls (about 4,700 lines of XAML, with WinUI-specific
controls such as `NavigationView`, `InfoBar` and Mica), the charts, the notification area and notifications, start
with the system, the file pickers and the single-instance handling, for each system.

**Why not now**: replacing WinUI on Windows would lose the native Windows 11 look and bring no benefit to the existing
Windows users. A second interface doubles the interface work for every feature, and its value depends on Linux and
macOS data sources that are still incomplete (processes, network, GPU, disk activity). The order chosen is:

1. Shared code and adapters testable on every system, checked by CI (done).
2. Processes, disk activity and network on Linux and macOS, behind the existing interfaces.
3. Move the view models to a WinUI-free project (with the dispatcher behind an interface).
4. Then an Avalonia application (`Sysora.Desktop`) for Linux and macOS, released as a preview until it has been used on
   real computers. The WinUI application stays the Windows version.

Until then, no Linux or macOS build is published, and nothing claims those systems are supported.
