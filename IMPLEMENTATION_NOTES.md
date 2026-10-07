# Implementation notes: Monitor → Detect → Explain → Understand

This release turns Sysora from a monitoring dashboard into a tool that explains what happens on the PC.
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

`%LOCALAPPDATA%\Sysora\history.db` (SQLite through `Microsoft.Data.Sqlite`, WAL, incremental vacuum). Demo mode
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

## Performance of Sysora itself

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
- **Replay**: the per-second data lives in memory (it is lost when Sysora exits); longer periods use minute averages
  without per-second application data (applications are shown per five minutes).
- **Uptime rule**: with Fast Startup, "Shut down" does not reset the uptime counter; the explanation says so.

## Possible next steps

1. Per-application network usage through ETW (optional, administrator), and per-disk process I/O.
2. Change detection for drivers, services and scheduled tasks (read-only), and Windows Update history.
3. Export of a diagnosis or a replay period (JSON/CSV/HTML report) for support requests.
4. Alerts for a specific application (user-defined), and alert snoozing.
5. GPU usage per application on the App Impact page (the per-process GPU data now collected for games could feed it).
6. A statistical diagnosis engine next to the rule engine (the `IDiagnosisEngine` extension point is ready).
7. Frame rates for games through PresentMon-style event tracing, as an explicit, optional, administrator-level feature.

---

# Finalisation: gaming sessions, usual-activity comparisons, sleep and resume

## Gaming sessions and recap

| Piece | Project | Role |
| --- | --- | --- |
| `GameClassifier` | Core/Gaming | Is an executable a game? Evidence only: the user's lists, Windows' list, game library folders (helpers such as launchers, anti-cheat and crash reporters excluded). Never inferred from resource usage |
| `IGameLibrary` / `WindowsGameLibrary` | Core/Interfaces, Infrastructure/Gaming | Executables Windows recognizes as games: `HKCU\System\GameConfigStore\Children\*\MatchedExeFullPath` (Game Bar's list, read-only), product names from version resources |
| `GameSessionTracker` | Core/Gaming | Pure state machine fed by the monitor's snapshots: session start, end (absent for 15 s; a game restarting itself stays one session), averages and peaks, the game's processes, associated processes (started by the game or in its folder), background applications, Sysora's own usage, resource limits reached, a bounded timeline |
| `GameRecapBuilder` | Core/Gaming | Pure recap: measurements (average / maximum), anomalies, analysis worded as hypotheses ("Likely limiting factor", "Possible cause", "Potential contributor", "Observed change, cause unknown"), comparison with previous sessions of the same game, what was not measured |
| `GameSessionService` | Core/Gaming | Wires the tracker to the monitor, keeps sessions (documents table, kind `game`), raises `RecapReady`, timeline events (game started/closed), "Not a game" / "Mark as a game" |
| `GamingPage`, `GamingViewModel` | App | Live session, list of sessions, recap with charts; Settings › Gaming; dashboard quick action and insight; notification when a recap is ready (click opens it) |

**FPS is never estimated.** Windows exposes frame rates only through administrator-level event tracing (what
PresentMon and overlays use) or by hooking into the game; Sysora does neither, so every recap shows
"FPS: Not available" with the reason. Temperatures and per-application network usage are reported as not available
for the same kind of reason. Video memory is the dedicated memory in use on the game's adapter (all applications):
Windows does not give the game's own share without another counter set.

**Cost during a game.** No new timer, no new collection: the tracker reads the snapshots already taken, only for the
metrics refreshed by each update. Per process sample, the work is one dictionary lookup per process (classification
results are cached per executable path). Per-process GPU usage comes from the `GPU Engine` instances Sysora already
read (their name carries the PID). Memory is bounded: at most 240 timeline points per session (neighbors are merged as
the session grows), capped application lists. While a game runs and Sysora is not the active window, the window's
live updates stop and detailed metrics are sampled 5× less often ("Lighter monitoring during games", on by default);
the recap shows Sysora's own CPU usage during the session.

Measured (Release, live data, window in the tray, Ryzen 9 7845HX with 24 logical processors, about 355 processes,
3-minute window after a 4.5-minute warm-up, both builds run back to back): the committed version used 0.040% of total
CPU capacity (0.96% of one core, 143 MB private memory); this version 0.039% (0.93% of one core, 141 MB). The tracker
itself, benchmarked on a 300-process snapshot: about 21 µs per process sample with no game running, 28 µs with a game,
0.1 µs per CPU or memory update, a few hundred bytes allocated per sample.

## Usual activity: hour, day, 7 and 30 days

`UsageComparer` (pure) and `UsageComparisonService` compare the last 15 minutes with the last hour, today, yesterday,
the last 7 and 30 days, and the same hour on previous days, from the minute history (last two days) and the hourly
summaries (older), minute data replacing the summary of the same hour so no time is counted twice. Each period has a
minimum of measured time (30 min, 1 h, 4 h over 2 days, 24 h over 8 days, 45 min over 3 days at this hour); below it
the table shows a dash and how much data exists. Shown on the Diagnosis page ("Compared with your usual activity").

## Sleep and resume

`WindowsPowerEvents` (`PowerRegisterSuspendResumeNotification`, no window needed) and `PowerTransitionService`:
before sleep, the minute and the application bucket in progress are written (bounded wait, Windows allows about two
seconds) and a "PC going to sleep" event is recorded; on resume, "PC resumed from sleep (asleep 42m)" is recorded and
every metric is refreshed at once. Game sessions exclude the time asleep from their measured time.

## Fixes

- `GamingSettings` compared its lists by reference: settings read back from disk never equaled the saved ones (failing
  test). Lists now compare by content.
- A lasting state alert (low disk space) was resolved when Sysora closed and raised again, with a new notification,
  at every start. Alerts of persistent-state rules now continue when the condition is still met at the first evaluation.
- `% Processor Utility` has no valid value for one sample every ~1 h 55 min (counter wrap): the CPU was reported
  "not available" for a second each time (visible in the logs). Such samples are now skipped (`MetricSampleSkippedException`);
  only repeated skips count as a failure. Same for transient empty disk counter samples.
- A history database damaged while open is now set aside and recreated (previously only detected at open).
- The game classifier no longer mistakes titles containing "crash" (for example *Crash Bandicoot*) for crash reporters.

---

# Advanced diagnostics, insights and storage analysis

This round adds twelve functions on top of the existing pipeline. Each one reuses what the existing services already
collect and keep (in-memory history, minute and hourly history, events, alerts, snapshots, game sessions); the only new
collection is the on-demand large-file scan, and the only changes to the sampling rate are the ones the user chooses
(monitoring intensity) or starts (troubleshooting).

| Function | Question it answers | Core types | Page |
| --- | --- | --- | --- |
| PC Health | In what state is my PC? | `PcHealthScorer`, `PcHealthService` | PC Health, Dashboard |
| Insights | What is worth knowing now? | `DashboardInsights` (extended) | Dashboard |
| Why now? | Why is this happening now? | `WhyNowAnalyzer`, `WhyNowService` | Diagnosis (also from Alerts and App Impact) |
| Before / after | How does this period compare with that one? | `StateComparer`, `StateComparisonService` | Compare (also from Timeline, Replay, Gaming) |
| Since yesterday | What changed since yesterday? | `SinceYesterdayBuilder`, `SinceYesterdayService` | Changes, Dashboard |
| Recurring problems | Does this keep happening? | `RecurringProblemDetector`, `RecurringProblemService` | PC Health, Diagnosis, Insights |
| Timeline | What happened, in order? | `TimelineBuilder`, `TimelineService` | Timeline |
| Troubleshooting | What happens while the problem occurs? | `TroubleshootingRecorder`, `TroubleshootingService` | Troubleshooting |
| Sysora impact | Is Sysora itself a problem? | `SelfUsage` (extended), `SelfImpactAssessor` | Settings, PC Health |
| Monitoring intensity | How much should Sysora collect? | `MonitoringIntensity`, `MonitoringProfile` | Settings |
| Large files | What takes the most space? | `LargeFileScanEngine`, `ILargeFileScanner`, `LargeFileService` | Large files (also from Storage and PC Health) |
| Report export | Can I keep or share this? | `ReportBuilder`, `ReportWriter` | Export on every analysis page |

## Explainability: observed, inferred, unknown

`Finding` (label, text, `FindingBasis` Observed / Inferred / Unknown, optional confidence) is the shared statement record of
Why now, recurring problems and investigations, rendered with a badge in the UI and in reports. Correlations are worded as
such ("Likely contributor", "Possible contributor", "Associated with", "Evidence suggests"); when no single application
accounts for a change the analysis says "Cause unknown"; when Windows cannot provide something (per-application network,
GPU per application in the history, temperatures, FPS) it says "Not available" and why.

## PC Health score

Starts at 100; each area takes off points from fixed, displayed thresholds; areas that cannot be measured are not counted.

| Area | Measured | Points taken off |
| --- | --- | --- |
| CPU | 15-minute average | 0 below 50%, linear up to 15 at 100% |
| Memory | 15-minute average, commit charge | 0 below 60%, linear up to 20 at 95%; +3 when the commit charge stays above 90% |
| Storage | Free space of fixed volumes | 16 when the Windows volume is above the critical level, 8 above the warning level (Settings › Health thresholds); 4 per other volume above critical |
| Disk activity | 15-minute average of the busiest disk | 0 below 40%, linear up to 10 at 90% |
| GPU | Usage (informational), dedicated video memory | 5 when video memory is 95% full (a busy GPU alone is normal while gaming) |
| Temperatures | Only if a driver reports them | 5 at 85 °C, 10 at 95 °C; otherwise "Not available" and not counted |
| Stability | Recurring problems over 7 days | 4 per recurring problem (max 12); "Not available" until 2 days of history |
| Recent anomalies | Alerts raised in the last 24 hours | 2 per warning, 4 per critical (max 10); alerts "usual for this PC" take nothing off |
| Usual behavior | 15-minute averages vs the baseline | 3 per metric above max(P95, median + margin) (max 6); "Not available" until the baseline is ready |

A score needs 2 minutes of measurements; before that the page says how much data exists.

## Why now?

The in-memory history (per second) and one hour of per-minute averages before it are averaged into 30-second steps.
The current level (last minute) is compared with the 20th percentile of the earlier steps; a change counts from 15 points
(CPU, disk, GPU), 8% of physical memory (memory) or 5 Mbit/s and twice the earlier level (network). The start is the first
step of the run above halfway between both levels (one dip tolerated). Applications are compared over the three minutes
before the start and the last minute: the one whose usage rose the most is a "likely contributor" when it accounts for 35%
or more of the rise (high confidence from 60%), "possible" from 15%, otherwise the cause is reported unknown. The analysis
also lists events from two minutes before the start (launches, games, alerts, connectivity, devices, resume), other metrics
that rose at the same time, the usual range and similar alerts over 7 days.

## Before / after, since yesterday

`StateComparisonService` loads each period from per-second measurements when the in-memory buffer covers it, else per-minute
history (within the detail retention), else hourly summaries. Rows show both values, the difference (percentage points for
percentages, amounts otherwise), the relative change for amounts and an importance level. Presets: now vs 1 hour ago, now vs
yesterday at this time, today vs yesterday, before vs during / before vs after a game, a game vs the previous session, before
vs after a moment (timeline entry, replay cursor) and two moments.

To compare free space over time, the free space of the Windows volume is now part of each history snapshot and of the
minute and hourly aggregates (`sysfree_*` columns). Existing databases get the columns when opened (`ALTER TABLE`; existing
rows keep a count of 0 and read as "not measured").

"Since yesterday" combines the comparison with yesterday's snapshot (applications, startup programs, Windows, firmware,
memory, devices, disk space), today's activity vs yesterday's, and the alert counts of both days. An area is called
unchanged only when both snapshots could compare it; otherwise it is listed as not compared.

## Recurring problems

Alerts of the last 7 days are grouped by rule (and by application for per-application CPU alerts); losses of Internet access
reported by Windows are merged into episodes. A problem is recurring with at least 3 episodes on 2 different days, in a
history covering at least 2 days (high confidence from 5 episodes on 3 days). A time-of-day pattern is given when 60% of at
least 4 episodes fall in the same 3-hour slot; an associated application when it took part (alert or high-usage event in the
10 minutes before) in at least half of the episodes. With less history: "Not enough historical data."

## Timeline

Merges stored and in-memory events, detected changes (dated between snapshots, marked ≈, with their earliest time) and
spikes derived from the per-minute history ("CPU usage high" from a 60% minute average, "back to normal" below 40%; memory
at 90% / 85%; a gap in the data ends a spike without claiming a return to normal). New event kinds: `AppStarted` (an
application whose first process started within 2 minutes, confirmed at the next process sample, in a user session, outside
the Windows folder, not a background helper, at most once per 30 minutes per application; its closing is reported with how
long it ran), `InvestigationStarted`, `InvestigationEnded`. Each entry opens Replay at that time, a 15-minute before / after
comparison, or the page with more context.

## Troubleshooting mode

`IMetricsMonitor.SetInvestigationMode(true)` applies the Detailed profile and disables the background slowdown (the CPU
budget still applies). The recorder keeps running statistics, a timeline of 10-second steps (at most 400, merged two by two
beyond), at most 300 applications and 500 events: constant cost per snapshot. The investigation ends by itself (2 to 30
minutes, at most 60), when stopped, or when Sysora exits (report kept as "ended when Sysora closed"); collection returns to the
user's intensity first. The report (anomalies, correlations such as "the busiest application in 12 of the 15 high-CPU
moments", likely explanations, applications, events, unknowns, recommendations) is saved in the `documents` table (kind
`troubleshooting`) when history recording is on, appears on the Timeline and can be opened in Replay or exported. A Windows
notification says when it is complete if Sysora is not in front.

## Monitoring intensity and Sysora's own impact

| Intensity | CPU, memory, network | Processes, GPU, disks | Free space | Applications per sample (per criterion) | Alert evaluation | Dashboard insights |
| --- | --- | --- | --- | --- | --- | --- |
| Minimal | ×2 | ×3 | ×4 | 3 | 10 s | 30 s |
| Balanced (default) | ×1 | ×1 | ×1 | 5 | 5 s | 15 s |
| Detailed | ×0.5 (≥ 0.5 s) | ×0.5 (≥ 1 s) | ×0.5 (≥ 5 s) | 8 | 5 s | 10 s |

Multipliers apply to the configured intervals; the background slowdown and the CPU budget still apply on top.
`MetricsMonitor.ScheduleInfo` exposes the intervals in effect. Every 10 seconds (the existing governor period) Sysora also
records its .NET heap, allocation rate, garbage collections, collection rounds per minute, its own write rate and threads
(from its own entry in the process list) and whether it has stayed above its CPU budget for a minute (logged once).
Settings › Diagnostics shows "Sysora impact" with these values and any warning.

## Large files

Read-only by construction: the engine only lists folders (`FileSystemEnumerable` returns name, size, date and attributes from
the listing; no file is opened, nothing is written). Runs only on request, one scan at a time, cancellable (partial results
are kept and labeled), on a dedicated thread in Windows' background mode (`THREAD_MODE_BACKGROUND_BEGIN`: lower CPU and I/O
priority). Memory is bounded: the 500 largest files in a fixed-size heap, at most 5,000 folders and 1,000 extensions in the
groups. Junctions and symbolic links are not followed; online-only cloud files are skipped (they are not stored on the PC);
`System Volume Information` and the component store (`WinSxS`, hard links counted twice) are excluded with the reason shown.
Folders Windows refuses to list are counted with examples ("their content is unknown, not absent"). Categories come from the
name, location and extension; page file, hibernation file, Windows Installer cache, game data and virtual disks carry a
caution. Actions: show in File Explorer, copy the path. Demo mode scans a made-up tree.

## Report export

`ReportDocument` (title, period, PC description without user or computer name, summary, sections with facts, tables and
findings, missing data, notes) plus `data`: the complete analysis as source-generated JSON (typed values, timestamps,
enumerations as names; replay points without per-sample applications to keep files small). HTML reports are self-contained
(inline styles, light and dark, print-friendly; only `& < > " '` are escaped so the page stays readable UTF-8) and print to
PDF from any browser; no PDF writer was added (it would need a third-party library). The user picks HTML or JSON in the
save dialog; the file is written off the UI thread and a message offers to open its folder.

## Navigation and integration

The menu is grouped: Dashboard, PC Health, Timeline; *Investigate*: Diagnosis, Troubleshooting, Replay, Compare, App Impact,
Changes, Alerts, Gaming; *Details*: Performance, Processes, Storage, Large files, Network, System. Cross-links go through
`InsightNavigator` and `NavigationRequests` (a request handed to the target page when it opens, so page view models do not
depend on each other): Diagnosis → Why now and Timeline; Alert → Replay and Why now; Gaming → Compare and export; App Impact
→ Why now; Timeline → Replay, Compare and the page of each entry; Replay → Compare; PC Health → Alerts, Large files,
Diagnosis; Troubleshooting → Replay; Storage → Large files; Dashboard → PC Health, Since yesterday, Timeline, Troubleshooting.

## Performance

Nothing new runs on the monitoring loop except constant work per sample (launch detection compares the applications with
the previous sample; free space is one more value per snapshot) and, every 10 seconds, a few counter reads for Sysora's own
impact. Every advanced analysis runs on demand, off the UI thread, only while its page or the dashboard is visible, with
caches where the data changes slowly (recurring problems 10 minutes, since yesterday 30 minutes, the large-file result for
the session). Lists that can grow are bounded (events, applications and steps of an investigation, files and folders of a
scan, timeline entries); long lists are virtualized (Timeline, Large files).

Measured on the same PC (Ryzen 9 7845HX, 24 logical processors), Release builds, demo mode hidden in the tray, 60-second
warm-up then 3 minutes, previous commit and this version run back to back:

| Build | CPU (share of total capacity) | CPU (one core) | Private memory | Working set |
| --- | --- | --- | --- | --- |
| Previous commit | 0.016% | 0.37% | 114 MB | 202 MB |
| This version | 0.011% | 0.27% | 116 MB | 204 MB |

The CPU difference is within run-to-run noise: no measurable increase. During a troubleshooting investigation, collection
follows the Detailed intensity (CPU and memory every 0.5 s, processes every second) for its duration only.

## Fixes

- **Settings added in a newer version were read as zero.** Settings are records with `init` properties, which the
  source-generated serializer sets all at once: a property missing from a section present in the file got `default(T)`
  (false, 0, the first enum value) instead of its declared default. For example the CPU budget would have been 0 (no limit)
  for anyone upgrading from a version without it, and the new monitoring intensity would have been Minimal. The stored file
  is now laid over the serialized defaults before it is read (`SettingsSerializer`), with a regression test.
- Tests now run with the invariant culture (`xunit.runner.json`), so formatted values do not depend on the machine's region.

## Limitations

- PC Health, Why now and Before / after can only use what Sysora measured: no temperature without a driver that reports it,
  no per-application network, no GPU usage per application in the history (only the game's, in gaming sessions).
- Free space history starts with this version; comparisons over older periods show it as not available.
- Application launches are detected from process samples (every 2 s by default, 6 s with Minimal): an application that ran
  for less than one sampling interval is not reported, by design.
- "Since yesterday" and Changes depend on snapshots; a change between two snapshots is dated by that interval.
- Large files reports logical sizes (as Explorer); compressed or sparse files may use less space on disk.
- Diagnoses themselves are not stored (they are recomputed); investigations are the stored form of a diagnosis over time.
- Analysis documents in the history (alerts, sessions, changes) are also records with `init` properties: a property added
  to them in a future version should be given a value when old documents are read (none was added in this round).
