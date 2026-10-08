## Purpose

Let users configure the same practical monitoring, appearance, history, and startup behavior as the reference with predictable persistence and quiet alerts.

## ADDED Requirements

### Requirement: Persistent live preferences
Settings SHALL include poll interval, tray window, theme, chart range, email masking, local analytics, credits, inactive windows, notifications/thresholds, retention/clear history, pricing override, update checks, and Codex executable/home overrides. Changes SHALL apply live and persist atomically; external valid edits SHALL reload and invalid edits SHALL leave active settings intact with an explanation. Reset defaults SHALL restore documented defaults.

#### Scenario: Invalid external settings
- **WHEN** settings.json is externally replaced with malformed data
- **THEN** the last valid settings remain active and the settings view identifies the error without overwriting that file.

#### Scenario: Interval below floor
- **WHEN** the user selects a poll interval below 180 seconds
- **THEN** the app applies the 180-second minimum and explains the adjustment.

### Requirement: Once-only startup default
Start with Windows SHALL use per-user registration without administrator rights, be enabled once on first launch, and stay disabled after opt-out. Tray menu and settings SHALL reflect the same registration state.

#### Scenario: Startup opt-out persists
- **WHEN** the user disables startup and later relaunches the app
- **THEN** the startup entry remains absent and both controls show disabled.

### Requirement: Quiet threshold alerts
Usage notifications SHALL default off, with default configured thresholds 80 and 95 percent. When enabled, a fresh crossing SHALL notify at most once per account/window/reset period/threshold, including across restarts. Cached stale samples SHALL not trigger notifications.

#### Scenario: Restart above threshold
- **WHEN** an 80-percent crossing was already announced and the app restarts with 85 percent in the same period
- **THEN** another 80-percent notification is not sent.

### Requirement: Optional release checks
The app SHALL check the configured Codex utility release repository at most weekly when enabled, report new versions, and open their release page on request without downloading or installing automatically. An unconfigured release repository SHALL show update checks unavailable.

#### Scenario: Updates disabled
- **WHEN** the user disables update checks
- **THEN** subsequent monitoring does not request release metadata.
