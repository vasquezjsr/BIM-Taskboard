<#
.SYNOPSIS
  Refresh the single SS Manager baseline checkpoint (full SpoolingManager + shared Workers deps).

  There is only ONE baseline. Calling this overwrites that snapshot with the current workspace state.
#>
[CmdletBinding()]
param(
    [string]$BaselineId = '20260707_205248',
    [string]$PromptSummary = '',
    [string]$ResponseSummary = ''
)

$ErrorActionPreference = 'Stop'
$root = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)'
$workers = Join-Path $root 'SpoolingSavantV3Exports.Workers'
$dest = Join-Path (Join-Path $root 'SSManager-Baseline') $BaselineId

if (Test-Path -LiteralPath $dest) {
    Remove-Item -LiteralPath $dest -Recurse -Force
}

New-Item -ItemType Directory -Path $dest -Force | Out-Null

$copied = @()
$script:copied = $copied

function Copy-Tree([string]$SourceRoot, [string]$RelativeRoot) {
    if (-not (Test-Path -LiteralPath $SourceRoot)) {
        return
    }
    Get-ChildItem -LiteralPath $SourceRoot -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($SourceRoot.Length).TrimStart('\')
        $targetDir = Join-Path $dest (Join-Path $RelativeRoot (Split-Path $rel -Parent))
        New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        $target = Join-Path $dest (Join-Path $RelativeRoot $rel)
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        $script:copied += (Join-Path $RelativeRoot $rel) -replace '\\', '/'
    }
}

Copy-Tree -SourceRoot (Join-Path $workers 'SpoolingManager') -RelativeRoot 'SpoolingSavantV3Exports.Workers/SpoolingManager'

$sharedFiles = @(
    'FabricationPartClassification.cs',
    'FabricationConnectorEnds.cs',
    'FabricationSavantParameterSync.cs'
)
foreach ($name in $sharedFiles) {
    $src = Join-Path $workers $name
    if (-not (Test-Path -LiteralPath $src)) {
        continue
    }
    $targetDir = Join-Path $dest 'SpoolingSavantV3Exports.Workers'
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Copy-Item -LiteralPath $src -Destination (Join-Path $targetDir $name) -Force
    $script:copied += "SpoolingSavantV3Exports.Workers/$name"
}

$iso = Get-Date -Format 'yyyy-MM-dd HH:mm:ss K'
$manifest = @(
    "# SS Manager baseline - $BaselineId",
    '',
    '**This is the only restore point for SS Manager.** Do not restore to anything before this baseline.',
    '',
    "- **Last refreshed:** $iso",
    "- **User prompt:** $(if ($PromptSummary) { $PromptSummary } else { '(not recorded)' })",
    "- **Assistant action:** $(if ($ResponseSummary) { $ResponseSummary } else { '(not recorded)' })",
    '',
    '## Contents',
    '',
    '- Full `SpoolingSavantV3Exports.Workers/SpoolingManager/` tree (handlers, views, models, commands, auto-dim partials)',
    '- Shared Workers: `FabricationPartClassification.cs`, `FabricationConnectorEnds.cs`, `FabricationSavantParameterSync.cs`',
    '',
    "## Files ($($script:copied.Count))",
    ''
)
foreach ($path in ($script:copied | Sort-Object)) {
    $manifest += "- $path"
}
$manifest += ''
$manifest += '## Restore'
$manifest += ''
$manifest += '```powershell'
$manifest += "& '$root\scripts\Restore-SSManagerBaseline.ps1'"
$manifest += '```'
[System.IO.File]::WriteAllText((Join-Path $dest 'MANIFEST.md'), ($manifest -join "`r`n"), [System.Text.UTF8Encoding]::new($false))

$indexPath = Join-Path (Join-Path $root 'SSManager-Baseline') 'README.md'
$index = @(
    '# SS Manager baseline',
    '',
    'Single canonical checkpoint for Spooling Savant V3 (Exports) SS Manager. **Never restore before `20260707_205248`.**',
    '',
    '| Baseline | Last refreshed | Notes |',
    '|----------|----------------|-------|',
    "| ``$BaselineId`` | $iso | $(if ($ResponseSummary) { ($ResponseSummary -replace '\|', '/') } else { 'User-confirmed good state' }) |",
    '',
    '## Refresh (overwrite baseline with current code)',
    '',
    '```powershell',
    "& '$root\scripts\Backup-SSManagerBaseline.ps1' ``",
    "  -PromptSummary '...' ``",
    "  -ResponseSummary '...'",
    '```',
    '',
    '## Restore',
    '',
    '```powershell',
    "& '$root\scripts\Restore-SSManagerBaseline.ps1'",
    '```'
)
[System.IO.File]::WriteAllText($indexPath, ($index -join "`r`n"), [System.Text.UTF8Encoding]::new($false))

Write-Host "SS Manager baseline refreshed: $dest ($($script:copied.Count) files)"
Write-Output $dest
