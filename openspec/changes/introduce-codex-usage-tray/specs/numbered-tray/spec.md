## Purpose

Keep Codex quota visible through the same legible numbered ring indicator and native tray interactions as the reference utility.

## ADDED Requirements

### Requirement: Numbered usage ring
The tray icon SHALL draw a clockwise usage arc from twelve o'clock around a centered bold numeral at native 16, 20, 24, and 32 pixel sizes. Values below 99.5 percent SHALL use reference-compatible rounding; values at or above 99.5 SHALL show `!`; absent values SHALL show `?`. Normal usage SHALL use blue, with warning/critical colors and visibly reduced arc emphasis for stale data.

#### Scenario: Typical and full usage
- **WHEN** authoritative utilization is 45 percent and later 100 percent
- **THEN** the icon first shows `45` inside a partial blue ring and later `!` inside a full critical ring.

#### Scenario: Unknown and stale usage
- **WHEN** no snapshot exists or an existing snapshot becomes stale
- **THEN** the icon shows `?` for unknown usage or retains the cached numeral with stale arc treatment.

### Requirement: Selected window and tooltip
The icon SHALL follow the selected quota window; auto SHALL prefer the Codex primary window and then the first supported window in deterministic order. A missing explicitly selected window SHALL show unknown until selected again or changed. The tooltip SHALL identify the window, used percentage, reset availability, and stale state.

#### Scenario: Selected window disappears
- **WHEN** a manually selected window is absent from a fresh response
- **THEN** the icon becomes unknown and its tooltip explains that this window is unavailable.

### Requirement: Native tray interactions
Left-click SHALL toggle the flyout. Right-click SHALL offer Refresh, Settings, Open logs, Start with Windows, About, and Quit. Icons SHALL redraw for DPI/taskbar appearance changes and restore after Windows Explorer restarts.

#### Scenario: Display and shell change
- **WHEN** the taskbar DPI changes or Explorer restarts
- **THEN** the icon reappears at the correct native size and contrast without restarting the app.
