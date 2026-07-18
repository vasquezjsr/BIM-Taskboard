<#
Builds a focused "core dimension-placement code" dump for external AI review (Claude), per its
specific request: TryPlaceSpoolLinearDimensionSleeveStyle, the stack-index logic, and the
reference-selection logic — plus a couple of example Dimension Reference Discovery report files
so the report format is self-explanatory.
#>

$root = "C:\Addins"
$svc = "$root\Spooling Savant V3 (Exports)\SpoolingSavantV3Exports.Workers\SpoolingManager\Services"
$reportsDir = "C:\ProgramData\Autodesk\Revit\Addins\2024\Spooling-Savant-V3-Exports\SpoolingManager\TestingReports"
$out = "$root\Spooling Savant V3 (Exports)\AutoDim-Checkpoints\AutoDim-CoreForClaude.md"

function Get-LineRange {
    param([string]$Path, [int]$Start, [int]$End)
    $all = Get-Content -Path $Path
    return ($all[($Start - 1)..($End - 1)] -join "`r`n")
}

$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("# Auto Dimension - core placement code + example discovery reports")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Generated $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("This is a focused subset of the full Auto Dim source, answering a direct request for:")
[void]$sb.AppendLine("1. The stack-index / orchestrator logic (per-view rule runner, 3 independent stack counters).")
[void]$sb.AppendLine("2. The reference-selection / scoring logic (how a candidate Reference on a fitting/pipe is scored and chosen).")
[void]$sb.AppendLine("3. The full anchor-resolution -> chain-building -> offset -> TryPlaceSpoolLinearDimensionSleeveStyle placement pipeline (this is one contiguous block in the source file).")
[void]$sb.AppendLine("4. Three real example Dimension Reference Discovery report files, one of each witness-pair kind (E-Disallowed rejection, F-E, C-F), with the report format explained.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("For the complete, unabridged dump of every auto-dim file (including tagging/view-crop adjacent code), see AutoDim-FullContext.md in this same folder.")
[void]$sb.AppendLine("")

# 1. Orchestrator / stack-index logic - full file
$orchestrator = Join-Path $svc "CreateSpoolSheetsHandler.AutoDimensionRules.cs"
[void]$sb.AppendLine("## 1. Orchestrator + stack-index logic: CreateSpoolSheetsHandler.AutoDimensionRules.cs (full file)")
[void]$sb.AppendLine('```csharp')
[void]$sb.AppendLine((Get-Content $orchestrator -Raw))
[void]$sb.AppendLine('```')
[void]$sb.AppendLine("")

$eligibility = Join-Path $svc "CreateSpoolSheetsHandler.SpoolDimensionAnchorEligibility.cs"
[void]$sb.AppendLine("## 2. Centralized anchor eligibility (full file)")
[void]$sb.AppendLine('```csharp')
[void]$sb.AppendLine((Get-Content $eligibility -Raw))
[void]$sb.AppendLine('```')
[void]$sb.AppendLine("")

$resolvers = Join-Path $svc "CreateSpoolSheetsHandler.SpoolDimensionAnchorResolvers.cs"
[void]$sb.AppendLine("## 3. Anchor resolver dispatch table (full file)")
[void]$sb.AppendLine('```csharp')
[void]$sb.AppendLine((Get-Content $resolvers -Raw))
[void]$sb.AppendLine('```')
[void]$sb.AppendLine("")

$fixtures = Join-Path $svc "AutoDimensionRegressionFixtures.cs"
[void]$sb.AppendLine("## 4. Regression fixture catalog (full file)")
[void]$sb.AppendLine('```csharp')
[void]$sb.AppendLine((Get-Content $fixtures -Raw))
[void]$sb.AppendLine('```')
[void]$sb.AppendLine("")

$mainFile = Join-Path $svc "CreateSpoolSheetsHandler.cs"

# 5. Reference-selection / scoring logic
[void]$sb.AppendLine("## 5. Reference-selection / scoring logic (CreateSpoolSheetsHandler.cs, lines 1031-1579)")
[void]$sb.AppendLine('```csharp')
[void]$sb.AppendLine((Get-LineRange -Path $mainFile -Start 1031 -End 1579))
[void]$sb.AppendLine('```')
[void]$sb.AppendLine("")

# 6. Anchor resolution through placement (line range shifts after dead-code removal; see resolvers file for GetFabricationFittingDimensionAnchor)
[void]$sb.AppendLine("## 6. Chain building, stack offset, TryPlaceSpoolLinearDimensionSleeveStyle (CreateSpoolSheetsHandler.cs, lines 9200-11800 approx)")
[void]$sb.AppendLine('```csharp')
$mainLineCount = (Get-Content $mainFile).Count
$chainStart = 9200
$chainEnd = [Math]::Min(11800, $mainLineCount)
[void]$sb.AppendLine((Get-LineRange -Path $mainFile -Start $chainStart -End $chainEnd))
[void]$sb.AppendLine('```')
[void]$sb.AppendLine("")

# 7. Example discovery reports
[void]$sb.AppendLine("## 7. Example Dimension Reference Discovery report format")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Each report is produced by a Step-1 diagnostic tool that lets the user pick an existing Dimension in")
[void]$sb.AppendLine("the model and dumps: the dimension's line/direction, each witness Reference's stable representation")
[void]$sb.AppendLine("and the fabrication part it points at, then a pattern classification using single-letter witness")
[void]$sb.AppendLine("kinds (C = fitting center, F = flange face, E = pipe open end, Disallowed = weld/gasket/valve -- never")
[void]$sb.AppendLine("a valid anchor). Witness pair is the two letters in dimension order; Inferred pattern and Lesson")
[void]$sb.AppendLine("status show whether the rules engine already has a named pattern/lesson for that pair.")
[void]$sb.AppendLine("")

$exampleFiles = @(
    "DimensionReferenceDiscovery_20260706_050556.txt",
    "DimensionReferenceDiscovery_20260706_050700.txt",
    "DimensionReferenceDiscovery_20260706_051100.txt"
)
foreach ($name in $exampleFiles) {
    $path = Join-Path $reportsDir $name
    if (-not (Test-Path $path)) { continue }
    [void]$sb.AppendLine("### Example: $name")
    [void]$sb.AppendLine('```text')
    [void]$sb.AppendLine((Get-Content $path -Raw))
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine("")
}

Set-Content -Path $out -Value $sb.ToString() -Encoding UTF8
Write-Output "Wrote $out"
Write-Output ("Total size: {0:N0} bytes" -f (Get-Item $out).Length)
