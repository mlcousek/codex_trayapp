# Desktop improvements (2026-10-08)

Windows resume wakes the polling loop. Healthy monitoring refreshes after the
60-second attempt floor; failed requests retain their retry deadline. Resume
signals are coalesced and rechecked after an in-flight fetch completes.

Tray rendering uses the current taskbar window's DPI, falling back to system
DPI. Shell recreation and DPI messages request a redraw; H.NotifyIcon owns
Explorer restart recovery. Queued tray/theme/view-model updates stop doing
work after disposal, and the power event subscription is removed at exit.

The flyout distinguishes missing CLI, unsupported sign-in, incompatible CLI,
connection failure and rate limiting. Its status banner appears even with no
cached snapshot, includes recovery guidance and retry time, and offers Settings
for connection failures. Cached readings remain marked during refresh. Long
tray tooltips preserve the status prefix and suffix.

Connection settings have executable/folder pickers. Apply rejects relative,
missing or non-executable paths before saving either field. Empty fields
restore discovery/environment defaults. Applying valid paths schedules a
refresh through the same limiter. A home override also controls local scanning
after restart; the scanner is not changed during an active session.

Quota lines and sparklines break at gaps longer than 15 minutes, changed reset
identities, passed resets and decreasing percentages. Both sides of breaks
survive downsampling. Discontinuities can therefore increase the point count
beyond the usual downsampling budget. The 15-minute rule is conservative;
unusually long configured poll intervals can yield disconnected observations.

High contrast uses Windows surface/text colors and a solid flyout backdrop.
Existing keyboard focus styles and reduced-motion behavior are retained.
Connection fields now use the themed text-box style. Missing Settings section
styles that caused a runtime XAML exception have been replaced with existing
styles. Clear history confirmation now describes future-only ingestion.

Validation: Release build has zero warnings/errors; all 505 tests pass on
Windows x64. Regression coverage includes resume cadence/backoff, distinct
failure messages, invalid settings preservation, downsampling across gaps and
actual disconnected WPF strokes. Isolated synthetic flyout and Settings
captures were launched with separate app-data/Codex-home directories and
inspected; both exited normally after the Settings resource fix.

Physical sleep/wake, real Explorer restart, high-contrast switching, constrained
200-percent desktop interaction and Arm64 still need manual device checks.
