# Data sources and compatibility

Verified on 2026-10-08 against installed `codex-cli 0.161.0` and the
[official app-server documentation](https://learn.chatgpt.com/docs/app-server).
The supported baseline for this initial implementation is 0.161.0; older
versions have not been tested. Schema generation validates the wire contract,
not successful access to any particular account or service.

## Reproduce the schema check

Generate into a temporary directory, outside the repository:

```powershell
$schemaDir = Join-Path ([IO.Path]::GetTempPath()) ('codex-tray-schema-' + [guid]::NewGuid())
codex --version
codex app-server generate-json-schema --out $schemaDir
```

Inspect `ClientRequest.json`, `ServerNotification.json`,
`v1/InitializeParams.json`, `v2/GetAccountParams.json`,
`v2/GetAccountResponse.json`, and `v2/GetAccountRateLimitsResponse.json`.
No credentials or session content are needed for schema generation.

## Account usage

The utility starts a hidden, owned `codex app-server --listen stdio://`
process in its application data directory. It sends `initialize`, waits for
the response, sends `initialized`, then reads `account/read` and
`account/rateLimits/read`. Each response wait is bounded to 15 seconds.
The current implementation opens a new connection per poll and kills its
owned process tree when the fetch finishes. A synthetic child-process
integration test verifies the account-only arguments, secret-variable
filtering, explicit home selection, and termination of both parent and
grandchild on disposal. It also verifies startup creates only the app working
directory and does not create the isolated Codex home. The utility does not
start model turns.
The initialize handshake explicitly advertises `experimentalApi=false`;
account reads remain within the stable generated schema.

These reads are included in the default generated schema without opting into
experimental APIs. `initialize.capabilities.experimentalApi` defaults to
false. Account notifications are `account/updated` and
`account/rateLimits/updated`; the transport exposes these notifications with
owned JSON payloads, but the provider does not yet consume them. Whole-request
deadlines include the write queue/write/response; synthetic transport tests
cover malformed records, out-of-order replies, EOF, cancellation and handshake
cleanup. Persistent connections, provider notification wiring and bounded
reconnect remain unfinished. Method-not-found RPC errors produce explicit CLI
compatibility guidance instead of a generic server failure.

`account` can be absent/null, `apiKey`, `chatgpt`, or `amazonBedrock` in this
schema. ChatGPT account data includes nullable email and a plan type. Only
ChatGPT mode currently requests subscription limits. Other modes display
subscription limits as unavailable; local analytics do not require sign-in.
Codex owns credential discovery and refresh. The utility requests
`refreshToken=true` and never reads a credential file itself.

Prefer the nonempty `rateLimitsByLimitId` map over the legacy `rateLimits`
view. Normalize each bucket's primary and secondary windows exactly once.
`usedPercent` is an integer; `windowDurationMins` and `resetsAt` are nullable
integers. Reset timestamps are Unix seconds. Duration is supplied by the
provider; five-hour and weekly durations are display labels, not assumptions.
Unknown fields are ignored. A missing reset never manufactures zero usage.

`credits.balance` is a nullable string, with boolean `hasCredits` and
`unlimited` fields. It is shown in credits, with no conversion to dollars.
`rateLimitResetCredits.availableCount` is the authoritative earned-reset
count; detail rows may be null or capped. The utility never redeems resets.
Spend-control fields and `ordinaryUsageAllowed` exist in this version's
schema but are not yet modeled. `account/usage/read` is optional account-wide
token activity and is not called or combined with local session totals.

The child is started with `analytics.enabled=false` and `otel.exporter="none"`.
The process integration test asserts both overrides are passed. A bounded live
account probe has succeeded with the installed CLI, but the installed CLI's
config writes, telemetry behavior and MCP startup side effects have not yet
been independently audited; the flags alone do not prove their absence.

## Local analytics

The scanner reads `sessions/**/*.jsonl` and `archived_sessions/**/*.jsonl`
under resolved `CODEX_HOME` (default `~/.codex`). It consumes metadata,
model context, and numerical token-count records. Prompts and tool output
are not stored. Cursor offsets and numerical context survive restart in
SQLite. Trailing partial lines wait for the next scan; decreased counters
establish a new baseline. Stable cumulative identities deduplicate repeats.

The bounded local shape audit records only field names. Observed
`session_meta` keys include `id`, `session_id`, `forked_from_id`,
`creator_account_id`, `cwd`, and `base_instructions`; `turn_context` includes
`model`, `cwd`, and context metadata. Token events contain `total_token_usage`
and `last_token_usage`; the former includes input, output, cached input,
cache-write input, reasoning output, and total counters. The parser retains
the session id, project path, model, and numerical cumulative baselines only.
Creator account ids and instruction text are ignored.

All event totals are currently local/unattributed, including records with
account metadata. They are not an account-wide billing summary. Cached input
is a subset of input; reasoning output is not added again to output. The
bundled `pricing.json` is dated 2026-10-08 and currently prices the exact
`gpt-5.3-codex` model at standard API rates: $1.75 per million uncached input,
$0.175 cached input, and $14 output. Cache writes are zero-priced for this
model family. The table source is the [OpenAI API pricing page](https://developers.openai.com/api/docs/pricing);
the [prompt caching guide](https://developers.openai.com/api/docs/guides/prompt-caching)
documents cache-write charges for later model families. The estimate assumes
standard service tier; local logs do not report fast tier or regional
processing, and the result is not a subscription bill. Every unlisted model
remains unpriced; add verified entries through the supported pricing override
instead of inferring rates from a related model. The fixture test loads the
actual bundled file and checks invoice-equivalent arithmetic and unknown-model
behavior. Synthetic session fixtures cover fork/resume/truncation, model
changes, archives, partial records, restart/re-scan and source preservation.
Sentinel tests verify prompt and account identifiers do not enter derived
SQLite records or cursor context.

## Isolation limits

Quota history keys are prefixed with the normalized account hash when
available. Calculator and chart queries pass the captured account explicitly,
independently of the legacy mutable active-account property. Cache snapshots
are now partitioned by account hash and exclude account email. Additional
hashed source/account-read fingerprints validate restored data before new
quota arrives. Invalid/legacy cache records are ignored. Account/source changes
clear previous quota and obsolete chart work, including cancellation during
the new quota request; confirmed same-account quota may remain visibly stale
on a failed read. Hashes are pseudonymous identity keys, not anonymous data.
Alert deduplication is currently in memory and does not survive restart. Clear
history keeps existing scan cursors, which prevents reimport of already
consumed records, but does not yet baseline every source at its current end.
Transactional migrations and owned-process tree cleanup are covered. Complete
installed-CLI configuration/telemetry/MCP integration and the remaining UI
state matrix are still open requirements.
