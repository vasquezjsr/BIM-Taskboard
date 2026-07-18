# Turn 74 auto-dimension checkpoint (Jul 4 ~12:34 AM — "Ok that issue is fixed").
# siP3.cs alone is NOT Turn 74 (it is Jul 4 ~2:28 PM and still includes Turns 75–82).
# This script documents the true revert: strip Turns 75+ from AutoDimensionRules while keeping Turns 70–74:
#   - 3/8" / 1/2" horizontal first-line offsets (CreateSpoolSheetsHandler.cs)
#   - firstOffset + slot * DIM_STYLE_DIM_LINE_SNAP_DIST stacking (Turn 73)
#   - 2D pre-fit viewport before auto-dim (AssemblyLine.cs)
#   - Simple DeduplicateDimensionIntents (no branch-off-run / host-center C-F dedup)
#   - CollectBranchDirection only when !hasOletsOnRun
#   - No AdjustOletTakeoffOffsetsForHorizontalStackOverlap in orchestrator
#   - No takeoff c-f from host center / GetDominantRunPartAlongViewAxis
#
# After manual code is at Turn 74, build and hotload:
$ErrorActionPreference = 'Stop'
$root = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)'
& (Join-Path $root 'scripts\Build-Workers.ps1') -Configuration Release
Write-Host 'Turn 74 checkpoint built. Hotload SS Manager in Revit and regenerate sheets.'
Write-Host 'Confirm AutoDimPlacement.log contains: ENGINE turn74-checkpoint'
