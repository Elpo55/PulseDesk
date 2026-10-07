# Architecture

Sysora is split into three projects with a strict dependency rule, plus tests.

```mermaid
flowchart TB
    App["Sysora.App<br/>WinUI 3 · views · view models · UI services"]
    Core["Sysora.Core<br/>models · interfaces · monitoring · history · analysis · diagnosis · alerts · changes · settings"]
    Infra["Sysora.Infrastructure<br/>Windows implementations · SQLite history"]
    Tests["Sysora.Tests"]
    App --> Core
    App --> Infra
    Infra --> Core
    Tests --> Core
    Tests --> Infra
```

- **Core** depends on nothing Windows-specific (it targets plain `net10.0`): no WinUI, no P/Invoke, no registry.
  It defines the interfaces (`ICpuMetricProvider`, `IMetricsMonitor`, `IProcessManager`...) and contains
  all logic that can be tested without hardware.
- **Infrastructure** implements the Core interfaces with Windows APIs. It never references WinUI.
- **App** is the only place that knows both. `Services/AppHost.cs` is the composition root: the single
  place that decides which implementation backs each interface (Windows providers normally, simulated
  ones with `--demo`).

The UI never reads system counters itself:

```
View (XAML) → ViewModel → UiMetricsHub / service → Core interface → Infrastructure → Windows
```

Views contain no logic beyond wiring. View models never call `System.Diagnostics.Process` or any Windows API,
and never touch the history database: they go through Core services, which use `IHistoryRepository`.

## Monitoring pipeline

```mermaid
sequenceDiagram
    participant Loop as MetricsMonitor (background loop)
    participant P as Providers (CPU, memory, GPU...)
    participant H as MetricHistory (ring buffers)
    participant Health as HealthService
    participant Hub as UiMetricsHub
    participant VM as Active view model (UI thread)
    Loop->>P: collect due metrics concurrently
    P-->>Loop: immutable records
    Loop->>H: append chart samples
    Loop-->>Health: MetricsUpdated (snapshot)
    Loop-->>Hub: MetricsUpdated (snapshot)
    Hub->>VM: one coalesced update at low priority
```

`MetricsMonitor` (Core) is a single background loop. It:

1. asks `MetricSchedule` which metrics are due (each has its own interval: CPU 1 s, processes 2 s,
   storage 15 s...), coalescing those due within 30 ms;
2. collects them concurrently, each with a timeout, and never calls a provider while its previous call
   is still running;
3. publishes a new immutable `SystemSnapshot`, records chart samples in `MetricHistory`, and raises
   `MetricsUpdated`;
4. sleeps until the next metric is due. There's no busy loop and no timer per metric, and `StopAsync`
   leaves nothing running.

A provider that throws doesn't stop the others: its metric is flagged in `SystemSnapshot.Unavailable` and
the UI shows "Not available". The first failure is logged in full; repeats are logged at debug level only.

### Keeping Sysora light

| Mechanism | Where |
| --- | --- |
| One loop, per-metric intervals, coalesced wake-ups | `MetricSchedule`, `MetricsMonitor` |
| Background mode: processes, GPU, disk and network sampled 5× less often while the window is hidden or minimized | `MetricsMonitor.SetActivity`, `ApplicationShell` |
| History and per-application tracking only append to memory on the loop; SQLite writes are batched once a minute by one background task | `PerformanceHistory`, `ProcessHistory`, `HistoryRecorder` |
| Alerts evaluated every 5 s on the thread pool; diagnosis, impact and replay computed only while their page is visible | `AlertService`, page view models |
| No UI work at all while hidden; at most one queued UI update at a time, at low priority | `UiMetricsHub` |
| CPU budget: if Sysora exceeds it (default 2% of total capacity), every interval is stretched automatically | `SelfUsageGovernor` |
| Bounded memory: fixed-size ring buffers sized for 30 minutes at the fastest interval | `RingBuffer<T>`, `MetricSeries` |
| One kernel call for all processes (`NtQuerySystemInformation`), reusable native buffers, allocation-free counter parsing | `ProcessSnapshotReader`, `PdhCounter.VisitInstances` |
| Charts downsampled to about one point per two pixels (LTTB) | `Downsampler`, `TimeSeriesChart` |
| Lightweight bars (render transform, no layout) and stable list rows (re-sorting changes content, not items) | `UsageBar`, `ProcessesViewModel.Rows` |
| Only the visible page listens to updates | `PageViewModel.Activate/Deactivate` |
| While a game runs and Sysora is not the active window: no UI updates, background sampling | `ApplicationShell.UpdateActivity`, `GamingSettings.ReduceMonitoringDuringGames` |
| Game sessions read the existing snapshots (no extra collection); classification cached per executable; bounded timeline | `GameSessionTracker` |
| Monitoring intensity: Minimal multiplies intervals (×2 real time, ×3 processes/GPU/disks, ×4 free space), keeps 3 applications per sample instead of 5 and evaluates alerts every 10 s; Detailed halves them (never below 0.5 s, 1 s for processes) and keeps 8 | `MonitoringProfile`, `MetricsMonitor`, `PerformanceHistory`, `AlertService` |
| Sysora measures its own CPU, memory, allocations, garbage collections, writes and collection rounds every 10 s (counter reads only) and warns once if it stays above its CPU budget for a minute | `MetricsMonitor.MeasureSelf`, `SelfImpactAssessor` |
| Advanced analyses run only when their page or the dashboard is visible, off the UI thread, with caches (recurring problems 10 min, since yesterday 30 min) | `PcHealthService`, `RecurringProblemService`, `SinceYesterdayService` |
| Large-file scans run only on request, one at a time, cancellable, at background priority; the last result is kept in memory | `LargeFileService`, `FileSystemLargeFileScanner` |
| A counter with no valid value for one sample (wrap-around) skips that sample instead of reporting the metric unavailable | `MetricSampleSkippedException`, `MetricsMonitor` |

Measured on a Ryzen 9 7845HX laptop (24 logical processors), Release build: in the background
Sysora uses about 1% of one core (0.05% of total capacity). With the window open, a page uses
1.5–4% of one core, including rendering.

## Where the numbers come from

| Metric | Source | Notes |
| --- | --- | --- |
| CPU usage | PDH `\Processor Information(*)\% Processor Utility` | Same counter as Task Manager; capped at 100% |
| CPU speed | PDH `Processor Frequency` × `% Processor Performance` | Task Manager's method; reflects boost clocks |
| CPU base speed | `CallNtPowerInformation(ProcessorInformation)` | Highest rated frequency (hybrid CPUs) |
| Cores, sockets, caches | `GetLogicalProcessorInformationEx` | |
| CPU temperature | — | Not available: no documented API without a kernel driver |
| Memory | `GlobalMemoryStatusEx`, `GetPerformanceInfo` | Commit charge, system cache, kernel pools |
| GPU usage | PDH `\GPU Engine(*)\Utilization Percentage` | Sum per engine, max over engines (Task Manager's method) |
| GPU memory | PDH `\GPU Adapter Memory(*)\Dedicated/Shared Usage`, DXGI | Names and totals from `IDXGIAdapter1::GetDesc1` |
| Volumes | `GetDiskFreeSpaceEx` / `GetVolumeInformation` (`DriveInfo`) | Network drives skipped (can block) |
| Disk activity | PDH `\LogicalDisk(*)\% Idle Time`, `Disk Read/Write Bytes/sec` | Active time = 100 − idle |
| Network | IP Helper (`System.Net.NetworkInformation`), WinRT `NetworkInformation` | Filter drivers and WAN miniports excluded; Internet status as determined by Windows |
| Processes | `NtQuerySystemInformation(SystemProcessInformation)` | CPU, private working set, I/O, threads, handles without opening any process |
| Process path, version | `QueryFullProcessImageName`, `FileVersionInfo` | "Access denied" shown for protected processes |
| OS, firmware | Registry (`CurrentVersion`, `HARDWARE\DESCRIPTION\System\BIOS`) | "Windows 10" in the registry is corrected to "Windows 11" from the build number; OEM placeholder strings are hidden |
| Uptime | `GetTickCount64` | Includes sleep, like Task Manager |
| Application identity | `QueryFullProcessImageName` (once per new process) | PID reuse checked with `GetProcessTimes`; name only when access is denied |
| Installed applications | Uninstall registry keys, `PackageManager.FindPackagesForUser` | Read-only; system components and updates excluded |
| Startup programs | `Run` keys, Startup folders, `StartupApproved` | Enabled state as set in Settings › Apps › Startup |
| Network adapters (Changes) | `NetworkInterface.GetAllNetworkInterfaces` | Physical types only; virtual adapters excluded |
| Per-application network | — | Not available: requires administrator-level event tracing |
| GPU usage per process (games) | PDH `\GPU Engine(*)\Utilization Percentage` (instance names carry `pid_N`) | Busiest engine per process, like Task Manager |
| Games recognized by Windows | `HKCU\System\GameConfigStore\Children\*\MatchedExeFullPath` | Game Bar's list for the user; read-only |
| Frame rate (FPS) | — | Not available: requires administrator-level event tracing or hooking into the game |
| Sleep and resume | `PowerRegisterSuspendResumeNotification` | Pending history written before sleep; all metrics refreshed on resume |
| Free space over time (Windows volume) | `GetDiskFreeSpaceEx`, kept in the per-minute history | Used by Compare; history written by earlier versions has no value (shown as not available) |
| Application launches | Process start time and session from `NtQuerySystemInformation` | Reported when confirmed at the next sample, in a user session, outside the Windows folder |
| Large files | Folder listings (`FindFirstFileEx` through `FileSystemEnumerable`) | Logical size, as Explorer shows it; nothing is opened |
| Sysora's own usage | `Environment.CpuUsage`, `GC` counters, its own entry in the process list | Measured every 10 seconds |

All performance counters are added by their English names (`PdhAddEnglishCounter`), so Sysora works
on every Windows display language.

## Health and anomaly detection

`HealthEvaluator` turns snapshots into indicators using the user's thresholds. CPU, memory and
per-application CPU conditions go through `SustainedThresholdDetector`, which avoids false positives:

- the value must stay at or above the threshold for the whole duration (30 s for CPU by default);
- any dip below the threshold restarts the wait;
- an active condition clears only below `threshold − hysteresis`, so a value hovering at the limit doesn't flap;
- a gap in the samples (sleep, pause) restarts the wait.

`HealthService` runs the evaluator on every update in the background, independently of the UI. These instant
indicators feed the dashboard's System health panel; the intelligent alerts (see above) are a separate, slower layer
with their own configurable rules.

## From measurements to explanations

Sysora's value is *Monitor → Detect → Explain → Understand*. Five functions sit on top of the monitoring loop:

```mermaid
flowchart LR
    Monitor["MetricsMonitor"] --> PH["PerformanceHistory<br/>1 snapshot per CPU sample<br/>(ring buffer) + events"]
    Monitor --> Proc["ProcessHistory<br/>per-application usage"]
    PH --> Rec["HistoryRecorder<br/>minute aggregates"]
    Proc --> Rec
    Rec --> DB[("history.db<br/>(SQLite, local)")]
    DB --> Base["BaselineService<br/>usual behavior"]
    PH --> Diag["DiagnosisService"]
    PH --> Alerts["AlertService"]
    PH --> Replay["ReplayService"]
    Base --> Diag
    Base --> Alerts
    Proc --> Impact["AppImpactService"]
    DB --> Impact
    DB --> Replay
    Inv["ISystemInventoryProvider"] --> Changes["ChangeDetectionService"]
    DB --> Changes
```

| Function | Core types | Page |
| --- | --- | --- |
| Diagnosis: "why is my PC slow?" | `IDiagnosisEngine`, `DiagnosisEngine`, `DiagnosisRule`, `DiagnosisService` | Diagnosis |
| Replay: "what happened?" | `IPerformanceHistory`, `ReplayService`, `ReplayNarrator` | Replay |
| App Impact: "which application weighs the most?" | `ProcessHistory`, `IAppImpactAnalyzer`, `AppImpactService` | App Impact |
| Alerts: lasting or unusual conditions | `IAlertEngine`, `AlertEngine`, `AlertRule`, `AlertService` | Alerts |
| Changes: "what changed?" | `IChangeDetectionService`, `BaselineComparer`, `ISystemInventoryProvider` | Changes |
| Gaming: "how did my game session go?" | `GameClassifier`, `IGameLibrary`, `GameSessionTracker`, `GameRecapBuilder`, `GameSessionService` | Gaming |
| Usual activity: now vs hour, day, 7 and 30 days | `UsageComparer`, `UsageComparisonService` | Diagnosis |
| PC Health: "in what state is my PC?" | `PcHealthScorer`, `PcHealthService` | PC Health, Dashboard |
| Why now: "why is this happening now?" | `WhyNowAnalyzer`, `WhyNowService` | Diagnosis |
| Before / after | `StateComparer`, `StateComparisonService` | Compare |
| Since yesterday | `SinceYesterdayBuilder`, `SinceYesterdayService` | Changes, Dashboard |
| Recurring problems | `RecurringProblemDetector`, `RecurringProblemService` | PC Health, Diagnosis |
| Global timeline | `TimelineBuilder`, `TimelineService` | Timeline |
| Troubleshooting mode | `TroubleshootingRecorder`, `TroubleshootingService` | Troubleshooting |
| Large files | `LargeFileScanEngine`, `ILargeFileScanner`, `LargeFileService` | Large files |
| Reports | `ReportBuilder`, `ReportWriter` | every analysis page (Export) |

Principles shared by all of them:

- **Deterministic and explainable.** Rules are plain code with documented thresholds. Every result carries
  `AnalysisEvidence` (what was measured, value, reference, period, sample count, source) and a `ConfidenceLevel`.
  Hypotheses are worded as such; an unknown cause is reported as "Change detected, origin unknown."
- **Never invented.** A metric that is not available is not analyzed (the diagnosis lists it under "Not analyzed").
  Nothing is described as "usual" before the baseline has 4 hours of history.
- **Duration and context, not instant thresholds.** Alerts require a condition to last (5 minutes of high CPU by
  default), compare with the usual range, keep one alert per condition with hysteresis, reopen within a cooldown
  instead of creating a new one, and are limited per hour.
- **Applications are identified by executable path.** Processes sharing only a name (several `Update.exe`) are kept
  apart; protected processes whose path is not accessible are identified by name and flagged as such.

### Advanced analysis

The advanced analysis functions compute on demand from what the services above already keep: none of them adds a
collection or a timer of its own (troubleshooting temporarily raises the collection rate, see below).

```mermaid
flowchart LR
    PH["PerformanceHistory"] --> Health["PcHealthScorer"]
    Alerts["AlertService"] --> Health
    Base["BaselineService"] --> Health
    DB[("history.db")] --> Rec["RecurringProblemDetector"]
    Rec --> Health
    PH --> Why["WhyNowAnalyzer"]
    DB --> Why
    PH --> Cmp["StateComparer"]
    DB --> Cmp
    Changes["ChangeDetectionService"] --> Since["SinceYesterdayBuilder"]
    Cmp --> Since
    DB --> TL["TimelineBuilder"]
    PH --> TL
    PH --> TS["TroubleshootingRecorder"]
    Health & Why & Cmp & Since & TL & TS --> Rep["ReportBuilder → HTML / JSON"]
```

- **Observed, inferred, unknown.** Every statement of Why now, recurring problems and investigations is a `Finding` with a
  `FindingBasis`. Correlations ("Likely contributor", "Associated with") carry a confidence; what cannot be observed says so
  ("Cause unknown", "Not available: Windows does not report network usage per application…").
- **PC Health** starts at 100 and each area takes off documented points (shown next to it). Time-based areas use 15-minute
  averages, so a short spike barely moves the score; an area that cannot be measured is not counted.
- **Why now** buckets the in-memory history (and one hour of per-minute averages before it) into 30-second steps, finds where
  the metric left its earlier level, and compares the top applications before and after that moment.
- **Before / after** reads each period from the most detailed source available: per-second measurements in memory, then the
  per-minute history, then hourly summaries. Only metrics measured in both periods are compared.
- **Timeline** merges stored and in-memory events (application launches and closings, games, alerts, connectivity, devices,
  sleep, investigations), detected changes (dated between snapshots, marked ≈) and spikes derived from the per-minute history.
- **Troubleshooting** switches the monitor to the Detailed intensity (`IMetricsMonitor.SetInvestigationMode`) without the
  background slowdown, records each snapshot at a constant cost (bounded timeline, applications and events), and returns to
  the user's intensity when the time is up, when stopped, or when Sysora exits. Reports are kept in the `documents` table.
- **Large files** walks folder listings only (`FileSystemEnumerable`: no file is opened) on a dedicated thread in Windows'
  background mode (lower CPU and I/O priority). Links and junctions are not followed, online-only cloud files are skipped,
  unreadable folders are counted and reported. The largest files are kept in a fixed-size heap.
- **Reports** have readable sections and a `data` element with the complete analysis (source-generated JSON). HTML reports are
  self-contained (no external resource) and print to PDF from any browser.

### History and storage

- **Short term, in memory**: `PerformanceHistory` keeps one `MetricSnapshot` per CPU sample in a ring buffer sized
  for the replay setting (15 minutes by default) and at least the longest alert window, plus a ring buffer of
  `SystemEvent`s (applications starting to use a lot of CPU or memory, notable applications exiting, connectivity
  changes, volumes, gaps, pauses, alerts).
- **Long term, local**: `HistoryRecorder` aggregates snapshots per minute and application usage per five minutes, and
  a single background task writes them to `%LOCALAPPDATA%\Sysora\history.db` through `IHistoryRepository`
  (`HistoryRepository`, SQLite). Hourly maintenance rolls complete hours up, applies retention (minutes 7 days,
  hours 90 days by default) and keeps the file under 256 MB. Demo mode uses an in-memory database.

### Extending the analysis

- **A diagnosis rule**: derive from `DiagnosisRule`, return a `Normal` result when the area is fine, and add it to
  `DiagnosisEngine.CreateDefaultRules`. Another engine (statistical, for example) implements `IDiagnosisEngine` and is
  registered next to the rule engine in `AppHost`: `DiagnosisService` merges their results.
- **An alert rule**: derive from `AlertRule`, give each condition a stable `Key`, use `Condition(...)` for hysteresis,
  and add the rule to `AlertEngine.CreateDefaultRules` (and its thresholds to `SmartAlertSettings`).
- **A change type**: add it to `ChangeType` and to `BaselineComparer` with a deterministic `ChangeId`.

## Settings

`AppSettings` is an immutable record tree serialized with source-generated `System.Text.Json`
(`SettingsSerializer`). `SettingsValidator` clamps every value into a safe range, so a hand-edited or
outdated file can never make Sysora sample every millisecond. `SettingsService` validates each change,
raises `Changed` and writes the file 500 ms after the last change. `JsonFileSettingsStore` writes the file
atomically (temporary file, then replace). A corrupt file is set aside, and the defaults are used.

## Application shell

- `Program.cs` enforces a single instance per user (`AppInstance`): launching Sysora again brings the
  running window to the front.
- `ApplicationShell` owns the window life cycle: initial visibility (`--tray`, "Start minimized" and
  "Start in tray" when launched at sign-in), close behavior (quit or minimize to tray), background mode,
  and the tray icon (`Services/Tray`, a small `Shell_NotifyIcon` wrapper).
- Start with Windows uses the documented per-user Run key, writing only Sysora's own value. The
  setting also reflects whether the user disabled it in *Settings › Apps › Startup*.
- Shutdown is ordered: stop the monitoring loop, flush settings, dispose providers (PDH queries, native
  buffers), flush logs, exit.

## Theme

All colors are defined once, in `Themes/Colors.xaml` (light, dark and high-contrast dictionaries).
Pages use `{ThemeResource}` keys or the standard Fluent brushes. `ThemeService` applies the System,
Light or Dark preference to the window content and the caption buttons.

## Logging

`FileLoggerProvider` (Infrastructure) writes daily files to `%LOCALAPPDATA%\Sysora\Logs` through a
bounded queue drained by a background writer, so logging never blocks the caller. Files are kept for
14 days and capped at 10 MB each. The level (Debug, Information, Warning, Error) can be changed in Settings.

## Extending Sysora

### Adding a metric

1. Add a model record in `Core/Models` and a property to `SystemSnapshot`.
2. Declare a provider interface in `Core/Interfaces/IMetricProvider.cs`.
3. Implement it in Infrastructure, plus a simulated version in `Core/Simulation`.
4. Add the provider to `MetricProviders`, give it a `MetricKind` and a `MetricSource` in `MetricsMonitor`,
   and an interval in `ConfigureSchedule`.
5. Register it in `AppHost`, then display it from a view model.

The dashboard and the other pages don't need to change to keep working.

### Planned modules

The architecture leaves room for plugins or a local API (export and monitoring profiles now exist). A future integration with Windows Orchestrator ("if CPU > 90% for
30 s, run a scenario") would consume `AlertService.AlertRaised` or `IMetricsMonitor.MetricsUpdated`, without touching
the UI. See `IMPLEMENTATION_NOTES.md` for the limitations and next steps of the analysis modules.

## Deviations from the initial folder plan

- `Sysora.Infrastructure/System` is named `SystemInfo`: a namespace ending in `.System` would shadow
  the `System` namespace in every Infrastructure file.
- Core has extra folders: `Settings`, `Formatting` and `Simulation`. Infrastructure has extra `Logging`
  and `Settings` folders. App has a `Themes` folder.
