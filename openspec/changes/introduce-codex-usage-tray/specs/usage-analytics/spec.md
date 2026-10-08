## Purpose

Explain observed Codex token activity and quota history with useful charts and estimates while preserving source boundaries and avoiding double counting.

## ADDED Requirements

### Requirement: Incremental local token analytics
The app SHALL incrementally ingest supported Codex session and archived-session usage records and show today's tokens, model/project breakdowns, and token pace offline. It SHALL handle cumulative counts, repeats, resumed sessions, partial lines, truncation, and model changes without counting the same activity twice. Missing provenance or fields SHALL be explicit; prompts and tool output SHALL not be stored.

#### Scenario: Repeated cumulative usage
- **WHEN** a session records cumulative totals 100, 150, and 150 tokens and is scanned again
- **THEN** the normalized session total remains 150 tokens rather than 400 or 800.

#### Scenario: Partial appended line
- **WHEN** the newest record ends mid-line
- **THEN** completed records are ingested and the partial record waits for completion without losing or duplicating events.

### Requirement: Honest cost estimates
Costs SHALL use a dated configurable per-model pricing table, separate cached and uncached input correctly, and avoid counting reasoning tokens twice. They SHALL be labeled estimated API-equivalent costs; unsupported model pricing SHALL show cost unknown and partial totals SHALL be identified.

#### Scenario: Unknown model price
- **WHEN** a model has no matching pricing entry
- **THEN** its tokens remain visible with cost unknown and the aggregate cost is identified as partial.

### Requirement: Charts and history
The app SHALL provide this-window utilization, 24-hour/7-day/30-day quota history, and 14-day daily token charts with optional model splits, hover readings, and text summaries. Gaps SHALL remain gaps. Local totals SHALL be scoped to local logs; any optional account-wide summary SHALL be labeled separately and never summed into overlapping local totals.

#### Scenario: Offline gap
- **WHEN** quota sampling is interrupted for several hours
- **THEN** history preserves the missing interval and its summary does not claim measurements during that gap.

### Requirement: Qualified pace projection
The app SHALL estimate quota exhaustion only from fresh increasing authoritative samples in the same bucket/reset period, using at least three samples across ten minutes. Projections SHALL be visually distinct estimates and SHALL be suppressed on stale data, missing reset/duration, percent decreases, or insufficient pace. Local tokens SHALL never manufacture quota percentages.

#### Scenario: Window resets
- **WHEN** a reset identity changes or utilization drops
- **THEN** the old projection is removed and a new estimate waits for sufficient samples in the new period.

### Requirement: Retention and clear history
The app SHALL retain derived history for the configured duration, defaulting to 90 days. Clear history SHALL remove app-owned history and derived scan state without deleting source sessions or immediately importing the same past activity again.

#### Scenario: Clear with logs present
- **WHEN** the user clears history while old session files remain
- **THEN** history stays empty until new activity or fresh account samples arrive and all Codex source files remain intact.
