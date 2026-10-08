from pathlib import Path
p=Path('src/CodexTrayApp.Core/Analytics/JsonlScanner.cs');s=p.read_text(encoding='utf-8').replace('using System.Diagnostics;','using System.Diagnostics;\nusing System.Text.Json;')
s=s.replace('Directory.EnumerateFiles(_projectsDirectory, "*.jsonl", SearchOption.AllDirectories);','new[] { "sessions", "archived_sessions" }.Select(folder => Path.Combine(_projectsDirectory, folder)).Where(Directory.Exists).SelectMany(folder => Directory.EnumerateFiles(folder, "*.jsonl", SearchOption.AllDirectories));')
s=s.replace('        var events = new List<UsageEvent>();','        var context = state?.Context is { } json && offset > 0 ? JsonSerializer.Deserialize<CodexSessionContext>(json) ?? new() : new CodexSessionContext();\n        var events = new List<UsageEvent>();')
s=s.replace('ReadCompleteLines(stream, offset, events);','ReadCompleteLines(stream, offset, events, context);').replace('new ScanState(path, consumed, info.Length, lastWriteTicks)','new ScanState(path, consumed, info.Length, lastWriteTicks, JsonSerializer.Serialize(context))')
s=s.replace('List<UsageEvent> events)\n','List<UsageEvent> events, CodexSessionContext? context = null)\n').replace('        var buffer = new byte[ChunkSize];','        context ??= new CodexSessionContext();\n        var buffer = new byte[ChunkSize];').replace('JsonlLineParser.TryParse(data.Slice(lineStart, length))','context.Parse(data.Slice(lineStart, length))')
s=s.replace('            carry = lineStart < data.Length ? data[lineStart..].ToArray() : [];','            carry = lineStart < data.Length ? data[lineStart..].ToArray() : [];\n            if (carry.Length > 8 * ChunkSize) { throw new IOException("Session record exceeds ingestion bound."); }')
p.write_text(s,encoding='utf-8')
# Legacy stateless parser is no longer used or shipped.
p=Path('src/CodexTrayApp.Core/Analytics/JsonlLineParser.cs');p.unlink()
p=Path('src/CodexTrayApp.Core/Analytics/AnalyticsCalculator.cs');s=p.read_text(encoding='utf-8');a=s.index('        var window = snapshot?.FindWindow');b=s.index('        var period = Period',a)
s=s[:a]+'''        var window = snapshot?.PrimaryWindow;
        var length = window?.DurationMinutes is { } minutes ? TimeSpan.FromMinutes(minutes) : BlockLength;
        var resetKnown = window?.ResetsAt is { } resets && resets > now && resets - now <= length;
        var end = resetKnown ? window!.ResetsAt!.Value : now;
        var start = end - length;
'''+s[b:]
a=s.index('        if (resetKnown && window!');b=s.index('\n        return new BlockAnalytics',a)
s=s[:a]+'''        if (resetKnown && snapshot?.Source == UsageSource.Live && _store is IHistoryStore history)
        {
            var samples = history.GetSeries(window!.Key, start, now).Where(p => p.ResetsAt == window.ResetsAt).OrderBy(p => p.Timestamp).ToList();
            if (samples.Count >= 3 && samples[^1].Timestamp - samples[0].Timestamp >= TimeSpan.FromMinutes(10)
                && now - samples[^1].Timestamp <= TimeSpan.FromMinutes(10)
                && samples.Zip(samples.Skip(1), (a, b) => b.Percent >= a.Percent).All(x => x))
            {
                var slope = (samples[^1].Percent - samples[0].Percent) / (samples[^1].Timestamp - samples[0].Timestamp).TotalHours;
                if (slope > 0) { percentPerHour = slope; projected = now + TimeSpan.FromHours(Math.Max(0, 100 - window.UtilizationPercent) / slope); }
            }
        }
'''+s[b:]
p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp/Charts/ChartDataLoader.cs');s=p.read_text(encoding='utf-8').replace('_history.GetSeries(WindowKeys.FiveHour, block.Start, now)','_history.GetSeries(snapshot?.PrimaryWindow?.Key ?? WindowKeys.FiveHour, block.Start, now)').replace('snapshot?.FindWindow(WindowKeys.FiveHour)?.UtilizationPercent','snapshot?.PrimaryWindow?.UtilizationPercent').replace('WindowNameHumanizer.Humanize(key), _history.GetSeries','snapshot?.FindWindow(key)?.DisplayName ?? WindowNameHumanizer.Humanize(key), _history.GetSeries');p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp.Core/Analytics/AnalyticsCalculator.cs');s=p.read_text(encoding='utf-8').replace('    public IReadOnlyList<DailyModelTotals> Daily(DateTimeOffset from, DateTimeOffset to) =>\n        _store.DailyTotals(from, to, _zone.GetUtcOffset(to));','''    public IReadOnlyList<DailyModelTotals> Daily(DateTimeOffset from, DateTimeOffset to)
    {
        var rows = new List<DailyModelTotals>();
        var first = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, _zone).DateTime);
        var last = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(to, _zone).DateTime);
        for (var day = first; day <= last; day = day.AddDays(1))
        {
            var start = LocalMidnightUtc(day.ToDateTime(TimeOnly.MinValue));
            var end = LocalMidnightUtc(day.AddDays(1).ToDateTime(TimeOnly.MinValue));
            rows.AddRange(_store.TotalsByModel(start > from ? start : from, end < to ? end : to).Select(m => new DailyModelTotals(day, m.Model, m.Tokens)));
        }
        return rows;
    }''');p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp.Core/Storage/SqliteStore.cs');s=p.read_text(encoding='utf-8');s=s.replace('    public string DatabasePath { get; }','    public string? ActiveAccountKey { get; set; }\n    private string HistoryKey(string key) => ActiveAccountKey is null ? key : ActiveAccountKey + "|" + key;\n    public string DatabasePath { get; }')
p.write_text(s,encoding='utf-8')
