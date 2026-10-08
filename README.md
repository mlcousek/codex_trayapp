# Codex Usage Tray

Unofficial Windows tray utility for Codex account quota and optional local
token analytics, built with .NET 10 and WPF. Implementation is in progress;
see the [OpenSpec progress map](docs/implementation-status.md) for verified
behavior and remaining requirements.

## Build and test

On Windows with a stable .NET 10 SDK:

```powershell
dotnet restore CodexTrayApp.sln
dotnet build CodexTrayApp.sln -c Release --no-restore
dotnet test --solution CodexTrayApp.sln -c Release --no-build --minimum-expected-tests 1
```

Account monitoring uses the verified Codex CLI 0.161.0 baseline and existing
ChatGPT sign-in. Authentication belongs to Codex. See
[data sources and compatibility](docs/data-sources.md). Analytics cover local
logs; cost estimates use a dated API pricing table and remain unknown for
models without an exact verified entry.

Source lives in `src/`, tests in `tests/`, and the plan in
`openspec/changes/introduce-codex-usage-tray/`. Python files in `tools/` are
development/migration helpers; several rewrite source and are not part of
the normal build or runtime.

Weekly update checks use the `ReleaseRepository` build property, stored in
assembly metadata. Its default matches this repository's configured remote:
`mlcousek/codex_trayapp`. Set `-p:ReleaseRepository=owner/repo` when building
for another release destination, or `-p:ReleaseRepository=` for a build with
update checks unavailable. Empty or invalid destinations send no requests;
the user can also disable checks in Settings. The Releases button opens the
configured GitHub page on request and never downloads or installs an update.

MIT licensed. Provider-independent components were adapted from Jiří
Mlčoušek's MIT-licensed `claude_trayapp`, retaining its copyright.
Not affiliated with or endorsed by OpenAI.
