# OpenSpec implementation map

Audited 2026-10-08. Active change: `introduce-codex-usage-tray`.

Desktop continuation: see [desktop improvements](desktop-improvements.md)
for resume handling, connection status, chart discontinuities, path pickers,
high contrast and the Settings runtime resource fix. The updated Windows x64
suite has 505 passing tests. Manual device/release gates below remain open.

## Recovered handoff

The previous session adapted a substantial native implementation from the
neighboring MIT reference, but left all 30 OpenSpec tasks unchecked. There
are no Git commits on `main`; source and tests were untracked. OpenSpec's
four completed artifacts mean planning is complete, not implementation.
No existing graphify graph was available; this map uses source inspection,
the specifications, actual builds/tests and the installed CLI schema.

## Architecture

| Flow | Locations | Current behavior |
|---|---|---|
| Composition/lifetime | App `App.xaml.cs`, `Hosting/`, `Startup/` | Generic host owns services, startup and shutdown. |
| Account quota | Core `Codex/`, `Domain/`, `Polling/` | Hidden stdio child, handshake/account reads, normalized windows and backoff. |
| Persistence | Core `Storage/`, `Cache/`, `History/` | SQLite events/cursors, explicitly scoped quota reads and sanitized account-partitioned snapshot cache. |
| Local analytics | Core `Analytics/`, `Pricing/`; app `Charts/` | Incremental numerical JSONL ingestion, totals, costs and charts. |
| Presentation | App `Tray/`, `Views/`, `ViewModels/`, `Themes/`, `Controls/` | Blue numbered ring, flyout, charts and settings. |
| Preferences/alerts | Core `Settings/`, `Notifications/`, `Updates/`; app `Startup/` | Atomic settings/reload, autostart, in-memory alerts and GitHub checks. |
| Verification | Projects in `tests/`; `.github/workflows/ci.yml` | Release build and 494 passing tests on Windows x64. |

## Task map

Partial means code exists but required behavior or full verification remains
unfinished. Only fully verified tasks have checked boxes.

| Task | State | Evidence / remaining work |
|---|---|---|
| 1.1 | Complete | Generated 0.161.0 schema; `data-sources.md` records methods, gates, units and supported baseline. |
| 1.2 | Partial | A bounded live account probe passed in the prior run. A new synthetic process integration test verifies account-only arguments, case-insensitive removal of inherited thread/API-key variables, explicit Codex home selection, and cleanup of a real helper process tree. Verify the installed CLI's config, telemetry, and MCP side effects before completion. |
| 1.3 | Complete | The bounded local inspector prints field names only; the 2026-10-08 shape check records session metadata, turn context and cumulative/last token fields in `data-sources.md`. Synthetic fixtures cover repeats, model changes, resume, fork identity, archives, partial records and truncation; sentinel checks confirm prompt/account metadata never enter SQLite or persisted cursor context. |
| 2.1 | Complete | .NET 10 WPF/core/test projects, central packages, formatting and CI; Release build and real test discovery pass. |
| 2.2 | Partial | MIT preserved; source/test audit found no Claude HTTP destinations. Finish reuse/asset audit and inherited semantic names/examples. |
| 2.3 | Partial | Legacy/multi-bucket/missing-field/credit/malformed-field tests pass. Review complete normalized availability/account contracts. |
| 3.1 | Partial | RPC tests cover ordering, malformed replies, EOF, cancellation, deadlines, account-only process arguments and full owned-tree cleanup; transport notifications are cloned safely. The handshake explicitly disables experimental APIs. Provider event wiring and bounded reconnect remain; each poll currently uses a short-lived connection. |
| 3.2 | Partial | Injectable RPC factory and auth-mode/identity tests plus live quota pass. Missing email now maps to a stable per-home identity; JSON-RPC method-not-found maps to actionable CLI compatibility guidance. Remaining account scenarios and provider integration still require verification. |
| 3.3 | Partial | Refresh now revalidates queued requests against current backoff; race regression passes. Verify retry-delay policy, jitter, cache age and reset opportunities. |
| 3.4 | Complete | Versioned SQLite schema migrations run in one transaction and reject a newer schema. Tests verify v0 creation, v1 upgrade, failed-migration rollback/retry, corrupted-file salvage, account-partitioned snapshot/history reads, secret-free cache, and 90-day pruning. |
| 4.1 | Partial | Blue renderer/state tests exist. Generate/review native icon sheets for all sizes/themes. |
| 4.2 | Partial | Controller, placement, tooltip and interactions exist/tests pass. Run DPI/monitor/Explorer checklist. |
| 4.3 | Partial | Independent single-instance/shutdown code and tests exist. Verify actual second launch, Quit and owned-child/icon cleanup. |
| 5.1 | Partial | Blue theme tokens/control styles exist. Contrast, high contrast, reduced motion and screenshot review remain. |
| 5.2 | Partial | Provider rows/masking/credits/status exist; account changes immediately clear quota charts and reject obsolete async loads. Verify full synthetic view state matrix and real dispatcher integration. |
| 5.3 | Partial | Dismissal/constrained sizing/actions exist. Keyboard-only and constrained 200-percent display checks remain. |
| 6.1 | Complete | Incremental offsets and cumulative deduplication cover active/archive copies, model changes, truncation, resume, forked sessions and partial lines across restart and re-scan; source bytes remain unchanged and synthetic prompt/account sentinels are absent from derived SQLite/cursor state. |
| 6.2 | Complete | Synthetic SQLite ledger verifies model/project totals, partial cost, local token pace, half-open boundaries, local midnight and 23/25-hour DST days. |
| 6.3 | Complete | Dated standard-tier `gpt-5.3-codex` rates ship with official pricing/cache provenance. Tests load the actual JSON and verify cached-input invoice arithmetic and unknown-model behavior; API-equivalent and partial labels are implemented. |
| 6.4 | Partial | Charts/ranges/model split/hover summaries exist. Verify missing intervals/reset boundaries and deterministic captures. |
| 6.5 | Complete | Qualified distinct same-period samples plus current authoritative observation; sparse/stale/decreasing/zero-slope/missing-reset-or-duration/changed-bucket suppression and real SQLite account isolation tests pass. |
| 6.6 | Complete | Clear atomically replaces scan cursors with baselines from every active/archive source, including unseen and retention-skipped files, while deleting derived token/quota history. Tests cover clear/restart/new-file appends, partial-line completion, truncation/replacement/rename, cancellation rollback, concurrent scans, stale in-flight quota rejection, fresh samples, source preservation and chart notification. |
| 7.1 | Partial | Settings persistence/reload/defaults tested. Complete CLI/home override UI and live analytics-root handling; verify every preference. |
| 7.2 | Partial | Startup/opt-out/shared-toggle tests exist. Real relaunch/coexistence checks remain. |
| 7.3 | Complete | App registration now uses the SQLite claim ledger; claims store only hashed account/window keys, reset and threshold. Tests cover crossing order, account/window/reset partitions, disabled/stale/future samples, process restarts, concurrent claims and rollback. Synthetic capture retains its in-memory store to keep capture runs read-only. |
| 7.4 | Complete | Build metadata selects the release repository; default matches origin. Empty/invalid destinations and disabled checks send no requests. Weekly state and announcements are repository-scoped; fake HTTP/clock tests and STA settings command tests verify user-requested release links with no automatic installation. Default, custom and unconfigured Release builds pass. |
| 8.1 | Partial | Diagnostics switches exist; live probe succeeded with 45-second bound. Isolate captures from real stores/logs, freeze time, add state variants and output scan. |
| 8.2 | Partial | Data sources and this map documented. Complete architecture/privacy/install/signing/troubleshooting and parity audit. |
| 8.3 | Complete | Tag-triggered workflow builds/tests Release, packages self-contained untrimmed win-x64/win-arm64 archives with README, MIT license, dated pricing and manifests, then creates a GitHub release. Local packages passed archive-content checks and SHA256 verification. The workflow awaits a published tag. |
| 8.4 | Partial | Windows x64 Release, portable per-user installation and launch on the current PC pass; the app remained running and its startup entry points to the installed executable. Arm64, clean-machine/no-runtime and remaining desktop integration/screenshots are open. |

## Initial recovery changes

- Fixed six Release compilation/analyzer errors without weakening enforcement.
- Enabled Microsoft Testing Platform in both xUnit projects and corrected the
  test command to `dotnet test --solution CodexTrayApp.sln -c Release`.
  The earlier positional invocation with `--nologo` reported zero tests.
  Configuration follows [xUnit documentation](https://xunit.net/docs/getting-started/v3/microsoft-testing-platform).
- Fixed Refresh bypassing transient failure backoff, with four regression cases.
- Updated auth tests to assert previous-account usage is cleared on auth failure.
- Replaced inherited fixed five-hour chart labels with current-window wording.
- Hardened malformed limits parsing and added five parser cases.
- Aligned Clear history tests with preserved ingestion cursor semantics.
- Added CI, source-contract docs and an overall 45-second probe deadline.
- Removed the inherited OpenSpec ignore so plans/progress can be committed.

## Parallel continuation and review

Three subagents ran RPC implementation, analytics qualification and code
review/hardening while the primary agent implemented account/cache isolation.
The review findings and dispositions are recorded in `code-review.md`.

- Bounded RPC queue/write/reply lifetimes; robust malformed envelope handling,
  notifications, fail-fast EOF, idempotent disposal and handshake cleanup.
- Revalidated queued Refresh requests after a failed scheduled attempt.
- Added sanitized cache partitions, source/identity fingerprints, safe legacy
  cache rejection, malformed-state validation and atomic/cancellation checks.
- Cleared previous account quota before awaiting new limits; cancellation
  independently flushes cache deactivation. Confirmed same-account cached data
  remains stale but available when the quota read fails.
- Scoped SQLite/calculator/chart reads to the captured snapshot account and
  rejected async chart/header work from obsolete generations.
- Qualified quota projection samples and added synthetic calculator ledger,
  timezone/DST, account isolation and UI generation tests.

Historical samples still do not carry duration metadata. A duration change
that preserves exactly the same bucket/reset identity cannot be detected in
old samples; a missing current duration always suppresses projection.

## Recorded verification

```powershell
dotnet build CodexTrayApp.sln -c Release --no-restore
dotnet test --solution CodexTrayApp.sln -c Release --no-build --minimum-expected-tests 1
```

Build: zero warnings/errors. Tests: 494 passed, zero failed/skipped on Windows
x64. The CI definition uses these commands; hosted CI has not run because
changes have not been committed/pushed. `tools/package-release.ps1 -Version
0.1.0` also published both self-contained RIDs locally. The archives contain
only the single-file executable, pricing JSON, README, MIT license and a
per-file SHA256 manifest; the release-level manifest hashes both archives.
All archive entries and release-level hashes were inspected successfully.

The updated account-only live probe used temporary LOCALAPPDATA/APPDATA and retained
the configured Codex home for Codex-managed sign-in. It returned three
normalized windows and exited 0; no remaining direct child of the app process
was found afterward. Only a numerical
summary was inspected; no credentials or raw account response were copied
into the repository. This verifies the happy path, not all process or config
side effects. No model turns were requested.

## Release-check continuation

Task 7.4 is now verified. `ReleaseRepository` is an explicit MSBuild property
embedded in the app assembly; Core no longer assumes a destination. The
default matches the configured `origin`, and empty/invalid build destinations
leave checks unavailable with the Releases command disabled. Changing the
destination resets the weekly deadline and remembered announcement, while
repository casing alone does not.

Fake HTTP and clock tests cover enabled/disabled/unconfigured checks,
repository changes, weekly checks and restart deduplication. STA settings
tests verify that constructing the view model never opens a browser and that
the command opens the offered release or configured fallback only on request,
including with checks turned off. Default, custom (`sample/custom-tray`) and
empty repository Release builds were compiled and their generated assembly
metadata inspected. Strict OpenSpec validation passes.

The current checkout also contained newer Clear history tests with cancellation
analyzer errors. Those calls now use the xUnit cancellation token; Release
enforcement remains enabled. The complete suite passes with 494 tests. Other
tasks remain open until their full verification gates are satisfied.

## Next work in dependency order

1. Finish 1.2/3.1: full owned-process tree and configuration side-effect
   checks; keep notification wiring aligned with connection lifetime.
2. Finish override preferences, chart gaps and remaining flyout state checks.
3. Finish isolated capture variants, remaining desktop checks and docs. The
   release workflow is ready but still needs a completed implementation and
   published version tag before a public release.

Do not archive the change or declare release readiness while these gates remain open.
