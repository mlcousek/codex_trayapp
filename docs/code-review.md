# Parallel continuation code review

Reviewed 2026-10-08 by a dedicated read-only subagent, followed by integration
review rounds. All findings below were fixed and covered by regressions.

| Finding | Disposition |
|---|---|
| Queued manual Refresh could bypass a newly established retry backoff. | Recheck current polling state when consuming the signal; queued-before-failure test. |
| Malformed RPC error codes could fault the reader and disposal. | Validate envelope kinds and use bounded numeric parsing; `1e100`, malformed and subsequent-reply tests. |
| Cancellation after account change could leave the old cache active on disk. | Independently bounded cache deactivation before propagating cancellation. |
| Same-account restored quota was unnecessarily discarded before limits returned. | Confirm hashed source/read identity, preserving stale same-account cache on quota failure. |
| Startup with a different Codex home could retain the old home's quota if connection failed. | Persist/check source fingerprint before connecting; A/B home failure regressions. |
| Valid JSON containing null/malformed cache entries could crash restoration. | Validate partitions/windows and sanitize retained siblings; 30 cache tests. |
| Mutable active account could mix projection/chart history across accounts. | Explicit snapshot account scope in SQLite, calculator and chart reads; real A/B SQLite test. |
| Outstanding chart/header loads could restore previous-account UI data. | Immediate quota chart clear, generation/account guards, disposal invalidation; four WPF isolation tests. |
| Initialized-notification timeout could escape provider/probe failure handling. | Internal timeout becomes TimeoutException; caller cancellation and EOF remain distinct; three regressions. |

The final review found no remaining high-priority regression in the reviewed
changes. This does not close the outstanding OpenSpec integration/release
gates. Final Windows x64 Release verification: zero warnings/errors and
430 passing tests, none failed or skipped. The isolated live account-only
probe exited 0 with no remaining direct child process.
