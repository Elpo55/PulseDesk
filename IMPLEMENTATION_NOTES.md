# Implementation notes: Monitor → Detect → Explain → Understand

This release turns PulseDesk from a monitoring dashboard into a tool that explains what happens on the PC.
Five functions were added, all built on real measurements and all local:

| Function | Question it answers | Page |
| --- | --- | --- |
| PC Diagnosis | Why is my PC slow? | Diagnosis |
| Performance Replay | What happened in the last minutes? | Replay |
| Change Detection | What changed on my PC recently? | Changes |
| App Impact | Which application weighs the most on my PC? | App Impact |
| Intelligent Alerts | Is something lasting or unusual going on? | Alerts |

The Dashboard was reorganized around them (status, metric tiles with mini-graphs, insights, quick actions), and
the navigation now lists Dashboard, Diagnosis, Replay, App Impact, Changes and Alerts first, then the existing
detail pages (Performance, Processes, Storage, Network, System) and Settings. No existing feature was removed;
the only page removed is the "History — coming soon" placeholder, replaced by Replay (`--page=History` still
works and opens Replay).

## Architecture

The existing layering is unchanged: rules and analysis in **Core** (plain `net10.0`, no Windows API),
Windows access and storage in **Infrastructure**, views and view models in **App**. View models never touch
SQLite: they call Core services, which use `IHistoryRepository`.

```
MetricsMonitor (existing loop)
  ├─ PerformanceHistory   one MetricSnapshot per CPU sample (ring buffer) + SystemEvents
  │    ├─ HistoryRecorder  minute aggregates + events → background writer → IHistoryRepository (SQLite)
  │    ├─ AlertService     evaluates AlertEngine every 5 s (thread pool)
  │    └─ DiagnosisService / ReplayService / DashboardInsights (on demand)
  └─ ProcessHistory       per-application usage of the session + 5-minute buckets → HistoryRecorder
BaselineService           usual behavior from 7 days of minute history (every 30 min)
ChangeDetectionService    system snapshot 1 min after start then every 6 h → changes + daily reference
```

`App/Services/AnalysisServices` starts these services before the monitor and stops them (flushing the minute and
the five-minute bucket in progress) after it, in `App.ExitAsync`.

## New services

| Service | Project | Role |
| --- | --- | --- |
| `IPerformanceHistory` / `PerformanceHistory` | Core/History | In-memory, high-frequency history (ring buffer) and event detection (`SystemEventDetector`) |
| `HistoryRecorder`, `SystemUsageAggregator` | Core/History | Minute aggregation, bounded write queue, hourly maintenance |
| `IHistoryRepository` / `HistoryRepository` | Core/Interfaces, Infrastructure/Storage | SQLite storage (file, or in-memory in demo mode) |
| `ProcessHistory`, `AppGrouper` | Core/Analysis | Per-application usage (session + five-minute buckets) |
| `IAppImpactAnalyzer` / `AppImpactAnalyzer`, `AppImpactService` | Core | Explainable impact score and ranking per period |
| `BaselineService`, `BaselineCalculator` | Core/Analysis | Usual behavior (percentiles) once 4 hours of history exist |
| `IDiagnosisEngine` / `DiagnosisEngine`, `DiagnosisRule` (10 rules), `DiagnosisService`, `DiagnosisReportBuilder` | Core/Diagnosis | Deterministic rules, results merged from every registered engine |
| `IAlertEngine` / `AlertEngine`, `AlertRule` (7 rules), `AlertService` | Core/Alerts | Duration- and baseline-aware alerts, deduplication, hysteresis, cooldown, hourly limit, persistence |
| `IChangeDetectionService` / `ChangeDetectionService`, `BaselineComparer`, `FrequentAppDetector` | Core/Changes | Snapshots, comparisons, timeline |
| `ISystemInventoryProvider` / `WindowsSystemInventoryProvider` | Core/Interfaces, Infrastructure/SystemInfo | Installed apps, startup programs, devices (read-only) |
| `ReplayService`, `ReplayNarrator` | Core/Analysis | Replay data (memory or minute history) and "what happened" |
| `DashboardInsights` | Core/Analysis | Overall state and insights for the dashboard |
| `InsightNavigator`, `AnalysisServices` | App/Services | Links findings to pages; service life cycle |

## New models

- History: `MetricSnapshot`, `AppSample`, `SystemEvent` (+ `SystemEventKind`), `SystemUsageAggregate`,
  `AggregateValue`, `AppUsageAggregate`, `AppUsageBucket`, `HistoryBatch`, `HistoryRetention`, `HistoryStorageInfo`.
- Analysis: `AnalysisEvidence` (the shared explainability record), `ConfidenceLevel`, `AppIdentity`, `AppGroup`,
  `AppUsageStatistics`, `AppImpactScore`, `ImpactComponent`, `AppImpactResult`, `AppImpactReport`, `UsageTrend`,
  `UsageBaseline`, `MetricBaseline`, `ReplayMoment`, `ReplayStory`, `ReplayData`, `Insight`.
- Diagnosis: `DiagnosisResult`, `DiagnosisSeverity` (Normal, Info, Warning, Critical), `DiagnosisContext`,
  `DiagnosisReport`, `PcHealthState` (Healthy, Attention, Problem), `DiagnosisAction`.
- Alerts: `Alert`, `AlertSeverity`, `AlertStatus` (New, Seen, Resolved), `AlertCondition`, `AlertContext`, `AlertEvaluation`.
- Changes: `SystemBaseline`, `SystemInventory`, `InstalledApp`, `StartupProgram`, `DeviceInfo`, `UsageSummary`,
  `DetectedChange`, `ChangeType`, `ChangeImportance`, `BaselineReference`, `ChangeComparison`, `KnownApp`.
- Settings: `SmartAlertSettings` (every alert rule is configurable) and `HistorySettings` (recording, replay buffer, retention).

All records are immutable with explicit UTC timestamps. Alerts, snapshots and changes are stored as JSON through a
source-generated context (`AnalysisJson`).

Two names from the request map to existing or shared types, to avoid duplicates:
`ProcessSnapshot` already existed (all processes at one instant) and is reused as the input; history keeps the compact
`AppSample`. `DiagnosisEvidence` is `AnalysisEvidence`, used by diagnoses, alerts, scores and changes alike.

## New views

`DiagnosisPage`, `ReplayPage`, `AppImpactPage`, `ChangesPage`, `AlertsPage` (each with its view model), a new
Dashboard layout, new Settings sections (Alerts, History) and controls: `Sparkline`, `LevelBadge`, `StatusIcon`,
plus a cursor, event markers and click-to-select-time on `TimeSeriesChart`. The Alerts menu item shows a badge with
the number of new alerts. An optional Windows notification (off by default) is shown for new warnings.

## Explainability

Every diagnosis, alert, score and change carries: what was measured, the value, what it was compared with (threshold
or usual range), the period, the number of measurements, the source and a confidence level. The UI shows the
conclusion first and the evidence on demand ("Why this result?", "Data used"). Hypotheses are worded as such
("a possibility, not confirmed"); a change whose cause is unknown says "Change detected, origin unknown."

The App Impact score is documented in the UI (`AppImpactScore.Formula`): 45% CPU + 40% memory + 15% disk I/O, each
being the application's average load over the period (usage while running × share of time running) relative to a
reference level; levels low / moderate / high / very high. It is presented as a relative indicator, never alone.

## Windows data sources added

| Data | Source |
| --- | --- |
| Executable path of each process (application identity) | `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageName`, once per new process, PID reuse checked with `GetProcessTimes` |
| Installed applications | Uninstall registry keys (HKLM, HKLM WOW6432Node, HKCU), excluding system components and updates |
| Store / MSIX applications | `PackageManager.FindPackagesForUser("")` (frameworks, resource and system packages excluded) |
| Startup programs | HKCU/HKLM `Run` keys, user and common Startup folders, enabled state from `Explorer\StartupApproved` |
| Graphics adapters | DXGI (`IDXGIFactory1::EnumAdapters1`), already used by System |
| Network adapters | IP Helper (`NetworkInterface.GetAllNetworkInterfaces`), physical types only |
| Windows build, BIOS, installed memory | Existing `ISystemInfoProvider` (registry, firmware) |

Everything else (CPU, memory, disk, network, GPU, processes) comes from the existing providers.

## Local storage

`%LOCALAPPDATA%\PulseDesk\history.db` (SQLite through `Microsoft.Data.Sqlite`, WAL, incremental vacuum). Demo mode
uses an in-memory database: simulated data never reaches the real history.

| Table | Content | Retention (default) |
| --- | --- | --- |
| `system_usage` | Minute buckets (avg/max/count per metric), rolled up to hours | minutes 7 days, hours 90 days |
| `app_usage`, `app_buckets` | Five-minute application buckets (top applications by CPU, memory and I/O), rolled up to hours | 7 days / 90 days |
| `apps` | Applications seen, first and last time | 90 days after last seen |
| `events` | Timeline events | 90 days |
| `documents` | Alerts, daily snapshots, detected changes (JSON) | 90 days |

Maintenance runs 2 minutes after start and then hourly: roll-up of complete hours, retention, and a 256 MB size cap
(the oldest detailed day is removed first). Writes go through a bounded queue drained by one background task; a
damaged database is set aside and recreated. Settings › History allows turning recording off, changing retention and
deleting the history. Nothing is sent anywhere.

## Performance of PulseDesk itself

- No new timer per metric: the history and the per-application tracking hang off the existing monitoring loop and
  only append to memory; SQLite writes are batched once a minute on a background task.
- Alerts are evaluated every 5 s on the thread pool; diagnosis, impact and replay are computed only when their page
  (or the dashboard) is visible, off the UI thread.
- Ring buffers bound memory: the replay buffer covers the replay setting (15 min by default) and at least the longest
  alert window (25 min by default) — about 3,000 snapshots.
- Process paths are read once per new process; inventory snapshots run every 6 hours in the background.
- Measured (Release build, live data, window hidden in the tray, Ryzen 9 7845HX with 24 logical processors, 6 minutes
  after the first minute): **0.027% of total CPU capacity (0.66% of one core)**, working set about 220 MB (mostly the
  WinUI runtime). The first system snapshot (registry, Store packages, devices) takes a few hundred milliseconds in
  the background.

## Tests

Added: history (ring buffer, period retrieval, gaps, events, minute aggregation, SQLite round trip, merges,
roll-up, retention, recorder), App Impact (formula, short vs long processes, aggregation across processes,
processes that come and go, launches, same name with different paths, five-minute buckets, period summaries without
double counting), Diagnosis (normal activity, high CPU, saturation, short spikes, dips, high memory, busy application,
memory growth, unusual activity vs baseline, no baseline, unavailable metrics, baseline computation), Alerts
(temporary spike ignored, prolonged problem, unusual behavior, usual-but-high, unusual activity, hysteresis and
reopening, resolution, hourly limit, memory rise, escalation, disabling, persistence), Change Detection (no change, new
application, updates, removal, memory usage increase, system and startup changes, disk space, first snapshot, frequent
new application, reference selection, service deduplication, parsing helpers), Replay (narration, quiet periods,
recoveries, data sources) and dashboard insights. All tests are deterministic (fixed data, fake time, in-memory SQLite).

## Limitations

- **Network per application**: not available (Windows does not expose it without administrator-level event
  tracing). Per-process I/O combines files, devices and network and cannot be split by disk.
- **Temperatures**: still not available (no documented API without a kernel driver).
- **Long-term application history** keeps the most significant applications of each five-minute period (up to about
  75); figures for minor applications over days are lower bounds. The current five-minute period appears once it ends.
- **Baseline** needs 4 hours of history (7-day window, last 15 minutes excluded). Until then nothing is called "unusual".
- **First run of Changes**: before two snapshots exist, only desktop applications whose installer recorded a date in the
  last 30 days are listed, as "installed or updated" (many installers rewrite that date on update). Store apps are left
  out of this list because their date is always the date of their latest update.
- **Change detection** does not cover drivers, services, scheduled tasks or individual files. Device detection is limited
  to graphics adapters, physical network adapters and fixed volumes. A change is dated between two snapshots (every 6 hours,
  plus a reference kept per day), except installs whose installer recorded a date. A product reinstalled with the same
  version after being removed is not reported a second time.
- **Application identity**: protected processes (SYSTEM services, other users' processes when not elevated) are identified
  by name only, and the UI says so.
- **Replay**: the per-second data lives in memory (it is lost when PulseDesk exits); longer periods use minute averages
  without per-second application data (applications are shown per five minutes).
- **Uptime rule**: with Fast Startup, "Shut down" does not reset the uptime counter; the explanation says so.

## Possible next steps

1. Per-application network usage through ETW (optional, administrator), and per-disk process I/O.
2. Change detection for drivers, services and scheduled tasks (read-only), and Windows Update history.
3. Export of a diagnosis or a replay period (JSON/CSV/HTML report) for support requests.
4. Alerts for a specific application (user-defined), and alert snoozing.
5. GPU per application (`GPU Engine` counters carry the PID).
6. A statistical diagnosis engine next to the rule engine (the `IDiagnosisEngine` extension point is ready).
