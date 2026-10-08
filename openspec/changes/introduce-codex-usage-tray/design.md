## Context

Reviewed on 2026-10-08. `codex_trayapp` contains only `.git` before OpenSpec initialization. The reference is a .NET 9 WPF app with a provider-independent core, generated tray artwork, custom chart controls, SQLite history, and self-contained x64/Arm64 releases. Its screenshots and renderer were inspected; no live Codex account request or private credential file was read.

Reference evidence:

- `../claude_trayapp/README.md`: functionality, defaults, packaging, and operating behavior.
- `../claude_trayapp/src/ClaudeTrayApp/Tray/TrayIconRenderer.cs`: native 16/20/24/32 px ring-and-number rendering.
- `../claude_trayapp/src/ClaudeTrayApp/Tray/TrayIconState.cs`: rounding, unavailable `?`, and full `!` at 99.5 percent.
- `../claude_trayapp/src/ClaudeTrayApp/Themes/Palette.Dark.xaml` and `docs/screenshots/`: current warm palette and layout.
- `../claude_trayapp/docs/architecture.md` and `docs/data-sources.md`: core/UI separation and incremental analytics design.
- Reference license permits reuse with copyright and license preservation.

Installed tools: OpenSpec 1.13.0, Codex CLI 0.161.0, stable .NET SDK 10.0.303 (plus .NET 9). Neither project has an existing graphify graph.

## Goals / Non-Goals

**Goals:** Preserve tray behavior and reference feature breadth, while isolating Codex-specific sources and creating a polished native blue interface. Account limits must remain authoritative; detailed local analytics must remain useful offline.

**Non-goals:** Chat conversations, starting model turns for monitoring, managing billing or buying credits, redeeming earned resets, API organization billing dashboards, arbitrary ChatGPT message-limit monitoring, and automatic download/install of updates. ChatGPT inspiration is a visual direction; this is an unofficial Codex usage utility.

## Decisions

### 1. Native WPF and a provider-independent core

Use stable .NET 10 LTS, WPF/MVVM, H.NotifyIcon.Wpf, SQLite, hosted background services, and structured redacted logging. Pin compatible package versions during implementation. Adapt the reference's tray renderer, placement, charts, history, settings, and lifecycle patterns; write Codex provider and session parser explicitly. Retain MIT notices for copied portions.

WPF matches the proven native tray behavior and permits selective reuse. WinUI 3 adds migration work; Electron/Tauri adds a second rendering stack without improving this small Windows utility. Updating the runtime avoids starting a new app on the reference's older runtime.

Suggested structure:

```text
src/CodexTrayApp/          App, Views, ViewModels, Tray, Themes, Controls, Hosting, Startup, Diagnostics
src/CodexTrayApp.Core/     Domain, Codex, Analytics, Storage, Polling, Settings, Pricing, Notifications
tests/CodexTrayApp.Core.Tests/
tests/CodexTrayApp.Tests/
docs/                     data-sources, architecture, screenshots, packaging
packaging/                portable release metadata and later winget manifests
```

### 2. Read account limits through Codex app-server

Use a hidden app-owned `codex app-server --listen stdio://` child and its newline-delimited request/response transport. Locate the executable through a user override, PATH, then the installed Codex app location. Set an app-owned working directory, honor `CODEX_HOME`, and use structured process arguments. Initialize once per connection, await the reply, send `initialized`, then invoke `account/read` and `account/rateLimits/read`; accept account/limit update notifications. Never call thread/turn start, login/logout, credit purchase, reset redemption, or owner-email methods.

Codex owns file/keyring credential discovery and any refresh it performs. The tray app does not copy, parse, persist, or rewrite tokens. Verify startup/config side effects with the installed version before integration; disable child telemetry through supported configuration and do not start project tools or MCP servers for monitoring.

Prefer `rateLimitsByLimitId` when present; otherwise use the legacy `rateLimits` object once. Normalize bucket id, optional label, primary/secondary role, `usedPercent`, `windowDurationMins`, Unix-second `resetsAt`, availability, and observed time. Stable window ids combine bucket and role. Never duplicate the legacy and multi-bucket views. Labels derive from supplied names/duration; 300 minutes can display as 5-hour and 10080 as 7-day, but neither duration is assumed. Missing windows or reset timestamps stay unavailable.

Optional credit fields are displayed according to the verified schema: distinguish remaining credits, unlimited status, reset-credit counts, and money. Do not convert credits into dollars or reproduce Claude's overage budget without matching fields. API-key-only mode supports local analytics and explains that subscription percentages are unavailable.

The installed CLI was checked for version and app-server help only. First implementation milestone generates its protocol schema and verifies supported account methods, required experimental capabilities, credit fields, config effects, and a redacted real response. An unsupported account method produces a compatible unavailable state, not fabricated percentages. Do not silently fall back to an undocumented HTTP endpoint.

Sources: [official app-server documentation](https://learn.chatgpt.com/docs/app-server), [official authentication documentation](https://learn.chatgpt.com/docs/auth). Documentation describes account limit reads and file/keyring storage; exact installed-version compatibility remains an implementation verification task.

### 3. Polling, storage, and isolation

Default poll 300 seconds, configurable floor 180 seconds as a conservative application policy, not a claimed Codex requirement. Manual refresh shares debounce/backoff. Honor provider retry delays; otherwise exponential retry capped at 30 minutes with jitter. Bound each RPC to 15 seconds, use one reconnect loop, and retry child startup at most three times before remaining unavailable until a later poll/manual retry. Closing the app cancels work and terminates only its own child.

Cache last successful normalized snapshot without secrets; mark restored data stale until a fresh response. Store timestamps in UTC, render local reset time and nonnegative countdown. Reaching a reset does not infer a new 0-percent reading. Partition cache, analytics, and history by verified account identity; account switch clears displayed data until the new identity is established. Local sessions without account provenance remain explicitly local/unattributed, rather than assigned to a signed-in account.

SQLite stores quota snapshots, token events, scan offsets, and notification period keys. Default history retention 90 days. Clear history deletes only app history/derived caches, resets scan cursors, and starts future ingestion at the current file ends so existing sessions do not instantly repopulate it. Original Codex logs remain intact. Writes are atomic and serialized; scanning never blocks the UI dispatcher.

### 4. Modern blue design

Direction: a calm developer utility with ChatGPT-like restraint, Codex naming, precise numerical typography, cool surfaces, and blue as the primary interactive/usage color. Signature: the same numeric blue ring in the tray and flyout, beside an aligned reset-time column. Avoid ornamental gradients, repeated nested cards, and continuous motion.

| Token | Dark | Light |
|---|---|---|
| Background | `#101820` | `#F4F7FB` |
| Raised surface | `#18232F` | `#FFFFFF` |
| Border | `#334354` | `#D4DDE8` |
| Primary text | `#F3F7FC` | `#162334` |
| Secondary text | `#ADBCCD` | `#536579` |
| Accent / normal usage | `#69A9FF` | `#175CD3` |

Semantic warning amber and critical red remain available for risk states; orange is not the brand accent. Default risk boundaries match the reference's 70/90 percent, separately from notification thresholds. Taskbar palette follows actual taskbar appearance independently of the chosen flyout theme; contrast and high-contrast mode take priority.

Typography: Segoe UI Variable Display for heading, Segoe UI Variable Text for controls/body, Cascadia Mono for small data labels with Segoe UI fallbacks. Tray numerals retain the reference's bold Segoe UI for tiny-size legibility. Use tabular digits, 4 px spacing scale, 20 px outer padding, 12 px main corner radius, 8 px controls, and a subtle native shadow. Body 14 px, secondary 12 px, title 18 px, main value 36 px. Test text and controls for WCAG AA contrast; do not encode state by color alone.

Flyout target width 380 device-independent pixels; height fits the current monitor work area with scrolling. Primary ring about 104 px, smaller than the reference's dominant ring so more information stays visible. Simplified wireframe (synthetic values):

```text
+------------------------------------------------+
| Codex usage                 Plan  account •••   |
|                                                |
|   ( 59% )   Current window                      |
|    used     Room to work                        |
|             Resets in 3h 05m · 15:49            |
|                                                |
| Weekly                         16% · 4d 03h     |
| [======-------------------------------------]  |
| Other available windows / credits              |
|------------------------------------------------|
| Today               1.2M tokens   ~$3.40 est.   |
| Pace and qualified limit projection            |
| Model / project details                        |
|------------------------------------------------|
| [This window] [History] [Daily]                 |
| Chart + textual summary                        |
|------------------------------------------------|
| Updated just now          Refresh   Settings   |
+------------------------------------------------+
```

Blue active underline for chart selection, quiet separators, rounded buttons, and visible focus. Error banners occupy the relevant data area and retain valid cached/local information. No oversized product logo replaces the tray numbers. Optional 100-150 ms opacity transitions obey Windows reduced-motion preferences; no video or animation deliverable is part of this planning change.

### 5. Preserve analytical value without false precision

Discover local session files under resolved Codex home, including archived sessions where supported. Parse only metadata and token-count records, without persisting prompts or tool output. Codex schemas must be verified rather than borrowing Claude field names. Handle partial lines, file rotation/truncation, cumulative totals, repeated events, resumed/forked sessions, model changes, cached input, and reasoning tokens without double counting. Persist offsets and normalized events; document local-only coverage.

Today/per-model/per-project totals and 14-day daily bars come from verified local events. Optional `account/usage/read`, if supported, can provide a separately labeled account-wide summary; it must never be added to overlapping local totals. Cost uses a dated, versioned pricing table; cached input is a subset of input and reasoning is not an additional charge when already included in output. Unknown model/price gives cost unknown. Display every cost as estimated API-equivalent cost, never a subscription bill.

Quota history provides this-window and 24 h/7 d/30 d charts. Use only fresh samples in the same bucket/reset period for quota slope; require at least three distinct samples spanning ten minutes and positive slope. Suppress projection on missing duration/reset, stale data, zero pace, a percent drop, or changed quota identity. Dash projected segments, label them as estimates, and never derive quota percentages from token totals. Expose token/hour independently where verified event coverage exists.

### 6. Feature parity and availability

| Reference feature | Codex plan | Verification |
|---|---|---|
| Ring + numeral, `?`, `!`, stale treatment | Same semantics with blue normal state | Native icon sheets and state tests |
| Flyout, reset countdowns, plan, masked email | Provider-driven windows and account fields | Fixture-driven views and live redacted check |
| Extra usage / budget | Show verified Codex credits; unsupported money says unavailable | Credit/missing-field fixtures |
| Burn rate / limit projection | Local token pace + separate quota-sample projection | Reset and sparse-sample tests |
| Today / model / project costs | Local Codex session adapter + dated prices | Deduplication and pricing fixtures |
| This block / history / daily charts | This window / history / daily, same ranges | Deterministic chart fixtures |
| Settings and hot reload | Equivalent settings plus CLI/home overrides | Invalid-file and live-change tests |
| Notifications | Opt-in, once per window/reset/threshold | Fake-clock and restart tests |
| Startup and single instance | Per-user startup; second launch opens existing flyout | Windows integration check |
| Logs / probe / screenshots / releases | Redacted diagnostics, synthetic capture, portable x64/Arm64 | Smoke checks and artifact inspection |
| Weekly update check | Optional check for this Codex repository after its remote is configured | Fake update responses |

Settings preserve reference defaults: system theme, mask email on, local analytics/credits/inactive windows off, notifications off with 80/95 thresholds, 24 h history range, weekly update check enabled when a release remote exists. Startup is established once on first launch and never re-enabled after opt-out. Keep startup registration separate from JSON as in the reference. Include pricing override, retention, clear history, reset defaults, logs/folder access, CLI path, and Codex-home override.

## Risks / Trade-offs

- Protocol evolution or experimental methods -> isolate adapters, verify CLI schema, tolerate unknown fields, and show version-specific unavailable guidance.
- Different quota/credit concepts -> preserve provider labels/units and never claim Claude-specific semantics.
- Detailed analytics depend on local event coverage -> show scope, missing models/prices, and partial-data states.
- Native tiny artwork can lose legibility -> inspect native-size dark/light sheets at 16/20/24/32 px and multi-monitor DPI transitions.
- App-server can refresh its own credentials -> document ownership and verify startup behavior; the tray app never writes credentials itself.
- Reuse may leak Claude branding, network destinations, or registry keys -> audit names, domains, assets, paths, and attribution before packaging.

## Migration Plan

Build in the task order: verify source contracts, scaffold, account provider, tray, flyout, analytics, settings/lifecycle, release. No Claude cache, history, credentials, autostart keys, or settings are imported. Both utilities can run side by side with distinct mutexes, app data, and registry entries. Preserve raw logs through upgrades; make app database upgrades versioned and transactional. Rollback replaces the app binary, with documented backup/restore if a database migration cannot be read by the previous version.

Release gate: all six capability scenarios verified, blue dark/light screenshots reviewed, native tray sizes checked, account-limit/local-analytics separation demonstrated, no Claude network calls or credentials in outputs, and self-contained x64/Arm64 artifacts smoke-tested. App-owned `--probe`, `--render-icons`, and `--capture-flyout` use redacted or synthetic output.
