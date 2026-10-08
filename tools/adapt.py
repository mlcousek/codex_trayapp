from pathlib import Path
root=Path('.')
for folder in ['src','tests']:
 for p in (root/folder).rglob('*'):
  if not p.is_file() or p.suffix not in ['.cs','.xaml','.csproj']:continue
  s=p.read_text(encoding='utf-8-sig')
  s=s.replace('Claude','Codex').replace('claude','codex').replace('anthropic','openai').replace('D97757','69A9FF')
  p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp.Core/Domain/UsageSnapshot.cs');s=p.read_text().replace('    /// <summary>Builds a window','    public long? DurationMinutes { get; init; }\n\n    /// <summary>Builds a window',1).replace('    public static UsageSnapshot Empty','    public string? AccountKey { get; init; }\n    public string? CreditsText { get; init; }\n\n    public static UsageSnapshot Empty',1).replace(': FindWindow(preferredKey) ?? PrimaryWindow;',': FindWindow(preferredKey);')
s=s.replace('"five_hour"','"codex:primary"').replace('"seven_day"','"codex:secondary"').replace('"seven_day_opus"','"codex_other:primary"').replace('"seven_day_sonnet"','"codex_other:secondary"').replace('"seven_day_oauth_apps"','"codex_extra:primary"')
p.write_text(s)
p=Path('src/CodexTrayApp.Core/Settings/AppSettings.cs');s=p.read_text().replace('    public string? PricingFilePath { get; init; }','    public string? PricingFilePath { get; init; }\n    public string? CodexExecutablePath { get; init; }\n    public string? CodexHomePath { get; init; }');p.write_text(s)
p=Path('src/CodexTrayApp/App.xaml.cs');s=p.read_text().replace('        provider.VerboseShapeLogging = true;','');p.write_text(s)
p=Path('src/CodexTrayApp/ViewModels/FlyoutViewModel.cs');s=p.read_text();a=s.index('        if (_showExtraUsage && snapshot?.Overage');b=s.index('\n    private void RebuildAnalytics',a)
s=s[:a]+'''        HasOverage = _showExtraUsage && snapshot?.CreditsText is not null;
        OverageAmountText = snapshot?.CreditsText ?? string.Empty;
        OverageText = OverageAmountText;
        OveragePercent = 0;
    }
'''+s[b:]
s=s.replace('        UpdateHeader();\n        RefreshCharts', '        if (snapshot?.AccountEmail is { } email) { _account = new AccountInfo(email, null, null, null, snapshot.PlanTier, null); }\n        UpdateHeader();\n        RefreshCharts')
p.write_text(s)
p=Path('src/CodexTrayApp/Views/FlyoutWindow.xaml');s=p.read_text();a=s.index('                <ProgressBar Grid.Row="1"', s.index('<!-- Extra usage'));b=s.index('/>',a)+2;s=s[:a]+s[b:];p.write_text(s)
p=Path('src/CodexTrayApp.Core/Domain/WindowNameHumanizer.cs');p.write_text('''namespace CodexTrayApp.Core.Domain;
public static class WindowNameHumanizer
{
    public static string Humanize(string key) => key switch
    {
        "codex:primary" => "Current window",
        "codex:secondary" => "Secondary window",
        _ => key.Replace(":", " · ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal),
    };
}
''')
p=Path('src/CodexTrayApp.Core/Analytics/AnalyticsModels.cs');s=p.read_text().replace('long LastWriteTicks);','long LastWriteTicks, string? Context = null);');p.write_text(s)
p=Path('src/CodexTrayApp.Core/Storage/SqliteStore.cs');s=p.read_text().replace('SELECT offset, length, mtime_ticks FROM scan_state','SELECT offset, length, mtime_ticks, context FROM scan_state').replace('reader.GetInt64(2)) : null;','reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;',1).replace('INSERT INTO scan_state (path, offset, length, mtime_ticks) VALUES ($path, $offset, $length, $mtime)','INSERT INTO scan_state (path, offset, length, mtime_ticks, context) VALUES ($path, $offset, $length, $mtime, $context)').replace('mtime_ticks = excluded.mtime_ticks','mtime_ticks = excluded.mtime_ticks, context = excluded.context').replace('command.Parameters.AddWithValue("$mtime", state.LastWriteTicks);','command.Parameters.AddWithValue("$mtime", state.LastWriteTicks);\n        command.Parameters.AddWithValue("$context", (object?)state.Context ?? DBNull.Value);').replace('mtime_ticks INTEGER NOT NULL','mtime_ticks INTEGER NOT NULL,\n                context TEXT')
# Clear must retain offsets/context to avoid re-importing history.
s=s.replace('DELETE FROM usage_events; DELETE FROM scan_state;','DELETE FROM usage_events;')
p.write_text(s)
# Preserve derived daily arithmetic tests; provider format tests will be authored separately.
for name in ['LocalAnalyticsProviderTests.cs','PricingTests.cs','FlyoutViewModelTests.cs']:
 p=next(Path('tests').rglob(name));p.unlink()
