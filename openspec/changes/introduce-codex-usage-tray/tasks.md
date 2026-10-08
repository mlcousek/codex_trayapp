## 1. Verify Codex data contracts

- [x] 1.1 Generate the installed CLI's app-server schema in a temporary directory; document account methods, notifications, experimental gates, credit units, and minimum supported version in docs/data-sources.md; verify against the generated schema and official documentation.
- [ ] 1.2 Run a bounded account-only probe without model turns, checking initialization, hidden process startup, configuration/telemetry/MCP side effects, auth modes, and account identity; verify a redacted response and process cleanup, recording unavailable features explicitly.
- [x] 1.3 Inspect only local session metadata/token-count record shapes using a redacting extractor; create synthetic fixtures for repeats, cumulative counts, model changes, resume/fork, archived sessions, partial lines, and truncation; verify no prompts or credentials enter fixtures.

## 2. Scaffold and reuse audit

- [x] 2.1 Create the .NET 10 WPF app, independent core, two test projects, central package versions, formatting rules, and CI; verify Release build and initial test discovery on stable SDK.
- [ ] 2.2 Adapt provider-independent reference components and retain MIT notices; verify a repository audit contains no Claude-specific provider requests, account paths, runtime identities, or bundled orange assets.
- [ ] 2.3 Define normalized account/window/credit models and data-source contracts; verify synthetic legacy/multi-bucket and missing-field fixtures map to the intended domain values without duplication.

## 3. Account provider, polling, and storage

- [ ] 3.1 Implement hidden Codex executable discovery and stdio request lifecycle with handshake, RPC deadlines, notifications, supported capabilities, and cancellation; verify fake-process tests for out-of-order replies, malformed records, exits, and timeout cleanup.
- [ ] 3.2 Implement account/limits adapter and unsupported/signed-out/API-key states; verify all codex-account-usage scenarios using synthetic responses and one redacted live check.
- [ ] 3.3 Implement 300-second polling, 180-second floor, debounce, backoff, cache age, and reset handling; verify fake-clock tests demonstrate manual refresh cannot bypass backoff and reset passage cannot manufacture zero usage.
- [x] 3.4 Add account-partitioned SQLite history, transactional migrations, secret-free snapshot cache, and 90-day pruning; verify account switches isolate data and a interrupted write leaves recoverable state.

## 4. Numbered tray and desktop shell

- [ ] 4.1 Adapt ring/numeral rendering, state selection, blue palettes, reference rounding, question/full markers, and stale treatment; verify state tests and native 16/20/24/32 px sheets on dark/light taskbars.
- [ ] 4.2 Implement tooltip, left/right-click interactions, context actions, work-area placement, DPI redraw, and Explorer recovery; verify a Windows manual checklist across monitor scaling and shell restart.
- [ ] 4.3 Implement independent single-instance activation and clean Quit; verify a second launch opens the existing flyout and Quit leaves no owned child or icon behind.

## 5. Blue flyout and settings design

- [ ] 5.1 Build theme tokens, typography, focus/control styles, high-contrast fallback, and reduced-motion handling from design.md; verify contrast calculations and blue dark/light screenshots.
- [ ] 5.2 Build primary ring, quota rows, masked account/plan, optional credits, reset countdowns, and contextual unavailable/stale status; verify synthetic normal, exhausted, signed-out, partial, and API-key snapshots.
- [ ] 5.3 Add keyboard navigation, Escape/outside dismissal, scrollable constrained height, Refresh and Settings actions; verify keyboard-only use and 200-percent scaling on a constrained work area.

## 6. Analytics and charts

- [x] 6.1 Implement incremental Codex session ingestion, offsets, stable deduplication, and local/unattributed scope; verify every fixture from 1.3 plus restart/re-scan yields identical totals and source files remain unchanged.
- [x] 6.2 Implement today, model/project breakdowns and local token pace; verify totals with a known synthetic event ledger and timezone/day-boundary cases.
- [x] 6.3 Implement a dated pricing table, overrides, cached-input accounting, and unknown/partial estimates; verify known invoice-equivalent fixture arithmetic and unknown models do not acquire guessed prices.
- [ ] 6.4 Build this-window, 24-hour/7-day/30-day history, 14-day daily/model charts, hover values, and text summaries; verify deterministic captures preserve missing intervals and quota reset boundaries.
- [x] 6.5 Implement qualified quota projections separate from token pace; verify sparse, stale, decreasing, zero-slope, missing-reset, and changed-bucket fixtures suppress estimates.
- [x] 6.6 Implement retention and Clear history with future-only ingestion after clearing; verify historical source files do not immediately repopulate derived history and new records still ingest.

## 7. Preferences, alerts, and updates

- [ ] 7.1 Implement complete settings UI, atomic persistence, hot reload, invalid-file recovery, and reset defaults; verify every preference applies live and malformed external edits preserve active values.
- [ ] 7.2 Implement first-run per-user startup and shared tray/settings toggle; verify opt-out persists through relaunch and Claude startup entries remain unchanged.
- [x] 7.3 Implement opt-in threshold notifications and persisted account/window/reset deduplication; verify 80/95 crossings, restarts, stale samples, new periods, and disabled notification cases.
- [x] 7.4 Implement optional weekly checks for the configured Codex utility repository; verify fake release responses, no requests when disabled/unconfigured, and release-link behavior without automatic installation.

## 8. Diagnostics and release readiness

- [ ] 8.1 Add --probe, --render-icons, and --capture-flyout with deterministic synthetic data and redaction; verify exit codes, bounded timeout, capture variants, and secret/identifier scan of outputs.
- [ ] 8.2 Document architecture, source coverage, defaults, privacy, coexistence, install/uninstall, schema compatibility, troubleshooting, and signing status; verify docs against actual behavior and all parity-table rows.
- [x] 8.3 Add self-contained untrimmed x64/Arm64 release workflow with pricing/license/readme/checksums and prepare package metadata for the actual release remote; verify artifact contents and hashes.
- [ ] 8.4 Run Release build, relevant automated tests, and Windows integration checks for both architectures, DPI/taskbar themes, Explorer restart, startup, offline mode, account switch, no-runtime launch, and clean shutdown; record capability-by-capability results and final blue screenshots before declaring implementation complete.
