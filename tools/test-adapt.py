from pathlib import Path
for p in Path('tests').rglob('*.cs'):
 s=p.read_text(encoding='utf-8')
 s=s.replace('"five_hour"','"codex:primary"').replace('"seven_day"','"codex:secondary"').replace('5-hour','Current window').replace('7-day','Secondary window')
 if p.name=='PollingStateMachineTests.cs':
  a=s.index('    public void Authentication_problems_');b=s.index('    [',a)
  segment=s[a:b].replace('status.Snapshot.ShouldBe(snapshot);','status.Snapshot.ShouldBeNull();')
  s=s[:a]+segment+s[b:]
 p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp.Core/Domain/WindowNameHumanizer.cs');s=p.read_text(encoding='utf-8').replace('_ => key.Replace', '_ => Capitalize(key.Replace').replace('.Replace("_", " ", StringComparison.Ordinal),','.Replace("_", " ", StringComparison.Ordinal)),');s=s.replace('\n}', '\n    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];\n}');p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp/ViewModels/SettingsViewModel.cs');s=p.read_text(encoding='utf-8').replace('    [ObservableProperty]\n    private string _pricingPathText', '    [ObservableProperty]\n    private string _codexExecutableText = string.Empty;\n\n    [ObservableProperty]\n    private string _codexHomeText = string.Empty;\n\n    [ObservableProperty]\n    private string _pricingPathText');s=s.replace('            PricingPathText = settings.PricingFilePath', '            CodexExecutableText = settings.CodexExecutablePath ?? string.Empty;\n            CodexHomeText = settings.CodexHomePath ?? string.Empty;\n            PricingPathText = settings.PricingFilePath');s=s.replace('    partial void OnMaskEmailChanged', '''    [RelayCommand]
    private void ApplyCodexPaths()
    {
        Persist(s => s with { CodexExecutablePath = string.IsNullOrWhiteSpace(CodexExecutableText) ? null : CodexExecutableText.Trim(), CodexHomePath = string.IsNullOrWhiteSpace(CodexHomeText) ? null : CodexHomeText.Trim() });
        _poller.TryRequestRefresh(out _);
    }

    partial void OnMaskEmailChanged''');s=s.replace('the endpoint rate-limits aggressively.','the app limits background requests.');p.write_text(s,encoding='utf-8')
p=Path('src/CodexTrayApp/Views/SettingsWindow.xaml');s=p.read_text(encoding='utf-8').replace('extra usage','credit balance').replace('Extra usage','Credit balance').replace('codename','additional');a=s.index('            <!-- Footer')
s=s[:a]+'''            <Border Style="{StaticResource SettingsSection}">
                <StackPanel>
                    <TextBlock Style="{StaticResource SettingsHeading}" Text="Codex connection" />
                    <TextBlock Style="{StaticResource CaptionText}" Text="Executable path (empty uses automatic discovery)" TextWrapping="Wrap" />
                    <TextBox Text="{Binding CodexExecutableText, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Codex executable path" Margin="0,4,0,8" />
                    <TextBlock Style="{StaticResource CaptionText}" Text="Codex home (empty follows CODEX_HOME). Local scanning changes on restart." TextWrapping="Wrap" />
                    <TextBox Text="{Binding CodexHomeText, UpdateSourceTrigger=PropertyChanged}" AutomationProperties.Name="Codex home directory" Margin="0,4,0,8" />
                    <Button Style="{StaticResource FlyoutButton}" Content="Apply connection" Command="{Binding ApplyCodexPathsCommand}" HorizontalAlignment="Left" />
                </StackPanel>
            </Border>

'''+s[a:]
# Use existing styles names, inspect below before compile.
p.write_text(s,encoding='utf-8')
