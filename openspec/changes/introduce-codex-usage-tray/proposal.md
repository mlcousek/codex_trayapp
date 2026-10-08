## Why

Codex usage should be visible directly in the Windows tray, with the same numbered indicator and practical functionality as the existing Claude Usage Tray. This empty repository will become a dedicated Codex app with a modern blue visual identity inspired by ChatGPT/Codex.

## What Changes

- Create a Windows 11 tray app with a live ring-and-number usage icon, instant flyout, and context menu.
- Show Codex account limits, reset countdowns, plan information, and credit information when the provider exposes it; preserve unavailable states honestly.
- Match the reference app's history charts, local token analytics, estimated API-equivalent costs, pace projections, settings, notifications, startup, single-instance behavior, diagnostics, and portable releases.
- Replace warm orange styling with blue accents, cool neutral surfaces, restrained depth, accessible typography, and dark/light/system themes.
- Use Codex's app-server account interface for live limits and a separate read-only local session adapter for detailed analytics.
- Keep account quota percentages separate from locally observed tokens and cost estimates.

## Capabilities

### New Capabilities

- `codex-account-usage`: Account connection, authoritative quota windows, credits, caching, refresh, and failure states.
- `numbered-tray`: Pixel-sharp numbered ring icon, tooltip, context menu, and flyout activation.
- `usage-flyout`: Blue visual system, account overview, quota rows, reset countdowns, and accessible interactions.
- `usage-analytics`: Local token/cost breakdowns, history, charts, and qualified pace projections.
- `preferences-and-alerts`: Settings persistence, themes, startup preferences, and threshold notifications.
- `desktop-lifecycle`: Single instance, process cleanup, privacy boundaries, diagnostics, and release packaging.

### Modified Capabilities

None. There are no existing application files or capability specifications.

## Impact

New `CodexTrayApp` WPF application, `CodexTrayApp.Core` library, test projects, CI, and release artifacts. Reuse provider-independent patterns and eligible MIT-licensed code from `C:/Git/Projects/claude_trayapp`, retaining attribution. Codex credentials remain managed by Codex; new app data belongs to `%LOCALAPPDATA%/CodexTrayApp` and `%APPDATA%/CodexTrayApp`.

This change contains planning artifacts only. Implementation tasks remain unchecked. Full parity means equivalent user-facing behavior where Codex supplies the underlying data, with explicit unavailable states elsewhere; it does not mean inventing Claude-specific windows or spending figures.
