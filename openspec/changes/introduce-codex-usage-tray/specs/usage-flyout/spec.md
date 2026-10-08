## Purpose

Offer a compact, accessible Codex usage overview with a modern blue identity and equivalent information density to the reference app.

## ADDED Requirements

### Requirement: Blue visual identity
The flyout and settings SHALL use blue primary accents, cool neutral surfaces, consistent spacing, restrained rounding, and dark/light/system themes. They SHALL identify the utility as Codex usage and preserve readable numerical data rather than replacing the tray number with a logo.

#### Scenario: Theme switches
- **WHEN** Windows appearance changes with system theme selected
- **THEN** the flyout and settings update their palette immediately while maintaining blue accents and readable text.

### Requirement: Usage overview
The flyout SHALL display plan/account information when available, mask email by default, show a primary usage ring, and list other available windows with percentages, bars, countdowns, and optional sparklines. It SHALL show fresh/stale status, Refresh and Settings actions, and optional supported credits/local analytics.

#### Scenario: Complete account snapshot
- **WHEN** a connected account supplies primary and secondary windows
- **THEN** the flyout shows their actual usage and reset information plus account/plan information, with email masked until revealed.

#### Scenario: Quota unavailable with local data
- **WHEN** account limits are unavailable but local token activity exists
- **THEN** the flyout explains the quota state and continues to show local activity with its source and scope.

### Requirement: Work-area placement and accessibility
The flyout SHALL anchor near the tray on the active monitor, remain inside its work area, support scrolling when necessary, dismiss on Escape or outside click, and provide keyboard access and visible focus. Text SHALL meet WCAG AA contrast and usage status SHALL have a textual equivalent. Reduced-motion and high-contrast preferences SHALL be respected.

#### Scenario: Small high-DPI monitor
- **WHEN** the flyout opens on a small monitor at 200 percent scaling
- **THEN** all controls remain reachable within the work area using scrolling and keyboard navigation.

#### Scenario: Reduced motion
- **WHEN** Windows animation is disabled
- **THEN** the flyout opens without animated transitions and remains fully operable.
