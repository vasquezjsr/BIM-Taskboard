# Summarize captured dimension training examples (geometry-based, not sheet-specific).
param(
    [string]$ExamplesFolder = ""
)

if ([string]::IsNullOrWhiteSpace($ExamplesFolder)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Spooling-Savant-V3-Exports\SpoolingManager\DimensionExamples"),
        (Join-Path $env:ProgramData "Autodesk\Revit\Addins\2024\Spooling-Savant-V3-Exports\SpoolingManager\DimensionExamples")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $ExamplesFolder = $c; break }
    }
}

if ([string]::IsNullOrWhiteSpace($ExamplesFolder) -or -not (Test-Path $ExamplesFolder)) {
    Write-Host "No DimensionExamples folder found yet. Capture at least one example from SS Manager first."
    exit 1
}

Write-Host "Dimension examples folder: $ExamplesFolder"
$files = Get-ChildItem -Path $ExamplesFolder -Filter "example_*.json" | Sort-Object LastWriteTime
Write-Host "Example files: $($files.Count)"
foreach ($f in $files) {
    Write-Host "  $($f.Name)  ($($f.LastWriteTime))"
}
$index = Join-Path $ExamplesFolder "index.jsonl"
if (Test-Path $index) {
    Write-Host ""
    Write-Host "Index (last 5 entries):"
    Get-Content $index -Tail 5 | ForEach-Object { Write-Host "  $_" }
}
