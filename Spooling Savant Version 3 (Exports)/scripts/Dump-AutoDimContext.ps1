<#
Dumps everything related to the Auto Dimension feature (rules, history, and full current source)
into a single markdown file so it can be handed to another AI for review.
#>

$root = "C:\Addins"
$svc = "$root\Spooling Savant V3 (Exports)\SpoolingSavantV3Exports.Workers\SpoolingManager\Services"
$out = "$root\Spooling Savant V3 (Exports)\AutoDim-Checkpoints\AutoDim-FullContext.md"

$files = @(
    "CreateSpoolSheetsHandler.AutoDimensionRules.cs",
    "CreateSpoolSheetsHandler.SpoolOletStackDimensions.cs",
    "CreateSpoolSheetsHandler.SpoolAutoDimensionReferenceRules.cs",
    "CreateSpoolSheetsHandler.SpoolDimensionPatternCatalog.cs",
    "CreateSpoolSheetsHandler.SpoolAutoDimensionOrientation.cs",
    "CreateSpoolSheetsHandler.SpoolAutoDimensionLineSynthesis.cs",
    "CreateSpoolSheetsHandler.SpoolRunDimensionIntents.cs",
    "CreateSpoolSheetsHandler.SpoolDimensionAnchorEligibility.cs",
    "CreateSpoolSheetsHandler.SpoolDimensionAnchorResolvers.cs",
    "AutoDimensionRegressionFixtures.cs",
    "CreateSpoolSheetsHandler.cs"
)

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Auto Dimension feature - full context dump")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("")

$overviewPath = "$root\Spooling Savant V3 (Exports)\AutoDim-Checkpoints\AutoDim-Overview.md"
if (Test-Path $overviewPath) {
    [void]$sb.AppendLine((Get-Content $overviewPath -Raw))
    [void]$sb.AppendLine("")
}

$rulePath = "$root\.cursor\rules\auto-dimension-checkpoint.mdc"
if (Test-Path $rulePath) {
    [void]$sb.AppendLine("## Workspace rule: auto-dimension-checkpoint.mdc")
    [void]$sb.AppendLine('```markdown')
    [void]$sb.AppendLine((Get-Content $rulePath -Raw))
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine("")
}

$indexPath = "$root\Spooling Savant V3 (Exports)\AutoDim-Checkpoints\INDEX.md"
if (Test-Path $indexPath) {
    [void]$sb.AppendLine("## Checkpoint history (INDEX.md)")
    [void]$sb.AppendLine('```markdown')
    [void]$sb.AppendLine((Get-Content $indexPath -Raw))
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine("")
}

foreach ($name in $files) {
    $path = Join-Path $svc $name
    if (-not (Test-Path $path)) {
        continue
    }
    $lineCount = (Get-Content $path).Count
    [void]$sb.AppendLine("## Source: $name ($lineCount lines)")
    [void]$sb.AppendLine('```csharp')
    [void]$sb.AppendLine((Get-Content $path -Raw))
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine("")
}

Set-Content -Path $out -Value $sb.ToString() -Encoding UTF8
Write-Output "Wrote $out"
Write-Output ("Total size: {0:N0} bytes" -f (Get-Item $out).Length)
