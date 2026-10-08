from pathlib import Path
import shutil
src=Path('C:/Git/Projects/claude_trayapp')
dst=Path('C:/Git/Projects/codex_trayapp')
skip={'bin','obj','Assets','Credentials','ClaudeCode','Fixtures'}
excluded={'OAuthUsageProvider.cs','UsageResponseParser.cs','AccountInfoFileSource.cs','CredentialFileSourceTests.cs','CompositeCredentialSourceTests.cs','ClaudeCodeVersionDetectorTests.cs','ClaudeCliRefreshNudgeTests.cs','ClaudeCliLocatorTests.cs','AppPathsTests.cs','AccountInfoFileSourceTests.cs','OAuthUsageProviderTests.cs','UsageResponseParserTests.cs','JsonlLineParserTests.cs','JsonlScannerTests.cs','AnalyticsCalculatorTests.cs','FakeCredentialSource.cs','FakeProcessRunner.cs','UsageSnapshotTests.cs','WindowNameHumanizerTests.cs','ChartDataLoaderTests.cs'}
for folder in ['src','tests']:
 for file in (src/folder).rglob('*'):
  if not file.is_file() or any(p in skip for p in file.relative_to(src).parts) or file.name in excluded: continue
  rel=str(file.relative_to(src)).replace('ClaudeTrayApp','CodexTrayApp')
  target=dst/rel; target.parent.mkdir(parents=True,exist_ok=True)
  content=file.read_text(encoding='utf-8-sig')
  for a,b in [('ClaudeTrayApp','CodexTrayApp'),('ClaudeUsageTray','CodexUsageTray'),('Claude Usage Tray','Codex Usage Tray'),('Claude Code','Codex CLI'),('Claude usage','Codex usage'),('"Claude"','"Codex"'),('claude_trayapp','codex_trayapp'),('Anthropic','OpenAI'),('net9.0','net10.0')]:content=content.replace(a,b)
  target.write_text(content,encoding='utf-8')
for name in ['.editorconfig','.gitignore','.gitattributes','Directory.Build.props','Directory.Packages.props','LICENSE']:
 content=(src/name).read_text(encoding='utf-8-sig')
 for a,b in [('ClaudeTrayApp','CodexTrayApp'),('ClaudeUsageTray','CodexUsageTray'),('Claude Usage Tray','Codex Usage Tray'),('Claude usage','Codex usage'),('Anthropic','OpenAI'),('claude_trayapp','codex_trayapp'),('0.1.6','0.1.0'),('9.0.19','10.0.0')]:content=content.replace(a,b)
 (dst/name).write_text(content,encoding='utf-8')
(dst/'global.json').write_text('{"sdk":{"version":"10.0.300","rollForward":"latestFeature","allowPrerelease":false}}\n')
p=dst/'src/CodexTrayApp/CodexTrayApp.csproj';s=p.read_text();s=s.replace('<ApplicationIcon>Assets\\app.ico</ApplicationIcon>','').replace('<Resource Include="Assets\\app.ico" />','');p.write_text(s)
p=dst/'src/CodexTrayApp/App.xaml.cs';s=p.read_text();s=s.replace('using CodexTrayApp.Core.ClaudeCode;','using CodexTrayApp.Core.Codex;').replace('using CodexTrayApp.Core.Credentials;','')
a=s.index('        services.AddSingleton<ICredentialSource>');b=s.index('        services.AddSingleton<ISnapshotCache>',a)
s=s[:a]+'''        services.AddSingleton(sp => new CodexUsageProvider(paths, sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<ILogger<CodexUsageProvider>>()));
        services.AddSingleton<IUsageProvider>(sp => capturing ? new DemoUsageProvider() : sp.GetRequiredService<CodexUsageProvider>());
'''+s[b:]
s=s.replace('services.AddSingleton<IAccountInfoSource>(_ => new AccountInfoFileSource(paths.ClaudeConfigFile));','services.AddSingleton<IAccountInfoSource>(sp => sp.GetRequiredService<CodexUsageProvider>());').replace('OAuthUsageProvider','CodexUsageProvider').replace('paths.ClaudeProjectsDirectory','paths.CodexHome')
s=s.replace('provider.FetchAsync(CancellationToken.None, includeShapeValues: true)','provider.FetchAsync(CancellationToken.None)')
p.write_text(s)
# Blue palette and compact typography.
for theme,colors in [('Dark',{'SurfaceColor':'#101820','SurfaceElevatedColor':'#18232F','SurfaceTranslucentColor':'#F0101820','HairlineColor':'#334354','TextPrimaryColor':'#F3F7FC','TextSecondaryColor':'#ADBCCD','AccentColor':'#69A9FF','OkColor':'#69A9FF','SuccessColor':'#64C7A5','WarnColor':'#F5C46B','DangerColor':'#FF8F91','Chart1Color':'#69A9FF','Chart2Color':'#A698FF','Chart3Color':'#64C7A5','Chart4Color':'#F5C46B','Chart5Color':'#ADBCCD'}),('Light',{'SurfaceColor':'#F4F7FB','SurfaceElevatedColor':'#FFFFFF','SurfaceTranslucentColor':'#F0F4F7FB','HairlineColor':'#D4DDE8','TextPrimaryColor':'#162334','TextSecondaryColor':'#536579','AccentColor':'#175CD3','OkColor':'#175CD3','SuccessColor':'#166B52','WarnColor':'#82530B','DangerColor':'#B42332','Chart1Color':'#175CD3','Chart2Color':'#6952B8','Chart3Color':'#166B52','Chart4Color':'#82530B','Chart5Color':'#536579'})]:
 p=dst/f'src/CodexTrayApp/Themes/Palette.{theme}.xaml'
 p.write_text('<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">\n'+''.join(f'    <Color x:Key="{k}">{v}</Color>\n' for k,v in colors.items())+'</ResourceDictionary>\n')
p=dst/'src/CodexTrayApp/Themes/Theme.xaml';s=p.read_text().replace('>11<','>12<').replace('>12.5<','>14<').replace('>15<','>18<').replace('>30<','>28<');p.write_text(s)
p=dst/'src/CodexTrayApp/Views/FlyoutWindow.xaml';s=p.read_text().replace('Width="352"','Width="380"').replace('Text="Extra usage"','Text="Credits"').replace('This block','This window');p.write_text(s)
p=dst/'src/CodexTrayApp/Views/FlyoutWindow.xaml.cs';s=p.read_text().replace('352','380');p.write_text(s)
p=dst/'src/CodexTrayApp/Themes/Controls.xaml';s=p.read_text().replace('Value="116"','Value="104"').replace('Value="120"','Value="104"');p.write_text(s)
# Pricing starts empty until sourced from verified official prices.
(dst/'src/CodexTrayApp/pricing.json').write_text('{"effectiveDate":"2026-10-08","currency":"USD","models":{}}\n')
print('Native foundation adapted; provider-specific source and tests excluded.')
