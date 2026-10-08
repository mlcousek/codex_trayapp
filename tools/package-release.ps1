param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+([-.][0-9A-Za-z.-]+)?$')]
    [string]$Version,
    [string]$OutputDirectory = 'artifacts/release',
    [string]$ReleaseRepository = 'mlcousek/codex_trayapp'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$outputRoot = Join-Path $repositoryRoot $OutputDirectory
$resolvedOutput = [System.IO.Path]::GetFullPath($outputRoot)
if (-not $resolvedOutput.StartsWith($repositoryRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must remain inside the repository: $resolvedOutput"
}
if (Test-Path -LiteralPath $outputRoot) {
    Remove-Item -LiteralPath $outputRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot | Out-Null

$archives = @()
foreach ($runtime in @('win-x64', 'win-arm64')) {
    $publishDirectory = Join-Path $outputRoot "publish-$runtime"
    $packageDirectory = Join-Path $outputRoot "package-$runtime"
    $archivePath = Join-Path $outputRoot "codex-usage-tray-v$Version-$runtime.zip"

    dotnet publish (Join-Path $repositoryRoot 'src/CodexTrayApp/CodexTrayApp.csproj') `
        -c Release -r $runtime -p:Version=$Version -p:ReleaseRepository=$ReleaseRepository `
        -o $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $runtime (exit $LASTEXITCODE)." }

    $requiredFiles = @('CodexTrayApp.exe', 'pricing.json')
    foreach ($name in $requiredFiles) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $name) -PathType Leaf)) {
            throw "Published $runtime output is missing $name."
        }
    }
    $pricing = Get-Content -LiteralPath (Join-Path $publishDirectory 'pricing.json') -Raw | ConvertFrom-Json
    if ($pricing.effectiveDate -ne '2026-10-08' -or $pricing.models.Count -lt 1) {
        throw "Published $runtime output has invalid or empty pricing data."
    }

    New-Item -ItemType Directory -Path $packageDirectory | Out-Null
    foreach ($name in @('CodexTrayApp.exe', 'pricing.json')) {
        Copy-Item -LiteralPath (Join-Path $publishDirectory $name) -Destination $packageDirectory
    }
    foreach ($name in @('README.md', 'LICENSE')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination $packageDirectory
    }

    $manifest = foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File | Sort-Object Name) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($file.Name)"
    }
    Set-Content -LiteralPath (Join-Path $packageDirectory 'SHA256SUMS.txt') -Value $manifest -Encoding ascii
    Compress-Archive -Path (Join-Path $packageDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
    $archives += Get-Item -LiteralPath $archivePath

    $zip = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
    try { $entries = @($zip.Entries | ForEach-Object FullName) }
    finally { $zip.Dispose() }
    foreach ($name in @('CodexTrayApp.exe', 'pricing.json', 'README.md', 'LICENSE', 'SHA256SUMS.txt')) {
        if ($entries -notcontains $name) { throw "$runtime archive is missing $name." }
    }
    if ($entries | Where-Object { $_ -match '\.(dll|pdb|xml)$' }) {
        throw "$runtime archive contains separate managed dependency or symbol files."
    }
    Remove-Item -LiteralPath $publishDirectory, $packageDirectory -Recurse -Force
}

$archiveManifest = foreach ($archive in $archives | Sort-Object Name) {
    $hash = (Get-FileHash -LiteralPath $archive.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($archive.Name)"
}
Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Value $archiveManifest -Encoding ascii
Write-Host "Release packages verified in $outputRoot"
