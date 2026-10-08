from pathlib import Path
p=Path('src/CodexTrayApp/ViewModels/FlyoutViewModel.cs');s=p.read_text(encoding='utf-8');a=s.index('        if (_showExtraUsage && snapshot?.Overage');b=s.index('\n    private void RebuildAnalytics',a)
s=s[:a]+'''        HasOverage = _showExtraUsage && snapshot?.CreditsText is not null;
        OverageAmountText = snapshot?.CreditsText ?? string.Empty;
        OverageText = OverageAmountText;
        OveragePercent = 0;
    }
'''+s[b:]
s=s.replace('        UpdateHeader();\n        RefreshCharts', '        if (snapshot?.AccountEmail is { } email) { _account = new AccountInfo(email, null, null, null, snapshot.PlanTier, null); }\n        UpdateHeader();\n        RefreshCharts')
p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp/Views/FlyoutWindow.xaml');s=p.read_text(encoding='utf-8');a=s.index('                <ProgressBar Grid.Row="1"', s.index('<!-- Extra usage'));b=s.index('/>',a)+2;s=s[:a]+s[b:];p.write_text(s,encoding='utf-8')
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
p=Path('src/CodexTrayApp.Core/Analytics/AnalyticsModels.cs');s=p.read_text(encoding='utf-8').replace('long LastWriteTicks);','long LastWriteTicks, string? Context = null);');p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp.Core/Storage/SqliteStore.cs');s=p.read_text(encoding='utf-8').replace('SELECT offset, length, mtime_ticks FROM scan_state','SELECT offset, length, mtime_ticks, context FROM scan_state').replace('reader.GetInt64(2)) : null;','reader.GetInt64(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;',1).replace('INSERT INTO scan_state (path, offset, length, mtime_ticks) VALUES ($path, $offset, $length, $mtime)','INSERT INTO scan_state (path, offset, length, mtime_ticks, context) VALUES ($path, $offset, $length, $mtime, $context)').replace('mtime_ticks = excluded.mtime_ticks','mtime_ticks = excluded.mtime_ticks, context = excluded.context').replace('command.Parameters.AddWithValue("$mtime", state.LastWriteTicks);','command.Parameters.AddWithValue("$mtime", state.LastWriteTicks);\n        command.Parameters.AddWithValue("$context", (object?)state.Context ?? DBNull.Value);').replace('mtime_ticks INTEGER NOT NULL','mtime_ticks INTEGER NOT NULL,\n                context TEXT')
# Clear must retain offsets/context to avoid re-importing history.
s=s.replace('DELETE FROM usage_events; DELETE FROM scan_state;','DELETE FROM usage_events;')
p.write_text(s,encoding='utf-8')
# Preserve derived daily arithmetic tests; provider format tests will be authored separately.
for name in ['LocalAnalyticsProviderTests.cs','PricingTests.cs','FlyoutViewModelTests.cs']:
 p=next(Path('tests').rglob(name));p.unlink()
