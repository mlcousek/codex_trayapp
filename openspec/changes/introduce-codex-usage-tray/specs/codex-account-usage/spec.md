## Purpose

Provide trustworthy Codex account usage, reset information, and clear connection states without deriving quota percentages from local activity.

## ADDED Requirements

### Requirement: Account connection without model activity
The app SHALL use Codex-managed authentication, honor the configured Codex home, and read account information without starting model turns or copying credentials. Missing CLI, signed-out, API-key-only, and incompatible-version states SHALL explain the recovery action while allowing local analytics.

#### Scenario: Missing Codex installation
- **WHEN** no configured or discoverable Codex executable exists
- **THEN** the app shows usage unavailable and installation guidance while continuing local analytics where logs exist.

#### Scenario: API key account
- **WHEN** Codex reports API-key-only authentication
- **THEN** subscription quota is unavailable and the app does not substitute API request-rate limits.

### Requirement: Provider-driven quota windows
The app SHALL display distinct authoritative windows with stable identities, used percentages, actual durations, optional labels, and reset timestamps. It SHALL prefer a supplied multi-bucket response over the overlapping legacy view and tolerate omitted or unknown fields. Credits SHALL preserve supplied units; unavailable spend amounts SHALL never be inferred from credits.

#### Scenario: Multiple limits
- **WHEN** the provider returns legacy and multi-bucket representations of the same quota
- **THEN** the app displays each bucket/window once and uses the supplied durations rather than hardcoded five-hour or weekly assumptions.

#### Scenario: Incomplete response
- **WHEN** a window lacks a reset timestamp or the account lacks monetary fields
- **THEN** reset time or spend is displayed as unavailable without inventing a countdown or dollar balance.

### Requirement: Resilient refresh and caching
The app SHALL refresh on startup and at the configured interval, coalesce concurrent refreshes, honor retry delays, and retain the last good snapshot with visible age/staleness on failures. Reset passage SHALL trigger a refresh opportunity without assuming zero utilization. Restored cache SHALL be stale until confirmed.

#### Scenario: Provider failure
- **WHEN** refresh fails after a successful snapshot
- **THEN** the last snapshot remains visible with a stale marker and recovery status, and repeated refresh clicks do not bypass backoff.

#### Scenario: Reset time passes offline
- **WHEN** a cached window's reset passes without a fresh response
- **THEN** the countdown does not become negative and the app does not report a new zero-percent window.

### Requirement: Account data isolation
The app SHALL partition verified account history and cache by account identity and SHALL label unassociated local activity as local/unattributed.

#### Scenario: Account switches
- **WHEN** the verified account changes
- **THEN** the previous account's cached usage is removed from the current view before displaying the new account and histories remain separate.
