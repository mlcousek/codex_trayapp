## Purpose

Run reliably as an independent Windows tray utility with clear data ownership, bounded background work, and verifiable portable release artifacts.

## ADDED Requirements

### Requirement: Single instance and clean shutdown
Only one app instance SHALL run per Windows user session. A second launch SHALL open the existing flyout and exit. Quit SHALL remove the icon, flush app-owned state, and stop only app-owned background processes.

#### Scenario: Second launch
- **WHEN** another app instance starts in the same user session
- **THEN** the original flyout opens and only the original instance continues running.

### Requirement: Privacy and independent ownership
The app SHALL write its own cache/history/logs under LocalAppData/CodexTrayApp and settings under AppData/CodexTrayApp, use distinct Codex tray startup/single-instance identities, and leave Claude files and entries unchanged. It SHALL not persist credentials, prompt content, or raw session payloads. Logs SHALL redact secrets and mask identifying values; monitoring SHALL have no app telemetry and SHALL make only required Codex-provider and enabled release-check requests.

#### Scenario: Coexistence
- **WHEN** Claude Usage Tray and Codex Usage Tray run together
- **THEN** their data, startup registrations, single-instance activation, and tray controls remain independent.

#### Scenario: Failure diagnostics
- **WHEN** a provider returns an authorization failure
- **THEN** logs record a useful redacted failure without credentials or raw account/session payloads.

### Requirement: Diagnostic and visual capture modes
The app SHALL support a bounded redacted provider probe with success/failure exit codes and synthetic tray/flyout captures for reproducible review. Captures SHALL cover both palettes and required icon sizes without disclosing real account identifiers.

#### Scenario: Synthetic capture
- **WHEN** visual capture mode runs
- **THEN** output contains deterministic sample usage and no live account email or token.

### Requirement: Portable Windows releases
Release artifacts SHALL include self-contained Windows x64 and Arm64 executables, pricing data, license attribution, installation/uninstall guidance, version information, and SHA256 checksums. Installation SHALL not require a separate .NET runtime or administrator privileges. Signing status SHALL be stated truthfully.

#### Scenario: Clean-machine launch
- **WHEN** a user unpacks the artifact on supported Windows without .NET installed
- **THEN** the app launches and explains any missing Codex connection without requiring runtime installation.
