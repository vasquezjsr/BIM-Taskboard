# Aggregate captured dimension examples into a view-agnostic placement policy.
# Top / Front / Left / other detail elevations share one rule set (AllDetailViews).
param(
    [string]$ExamplesFolder = "",
    [string]$OutputPath = ""
)

function Resolve-ExamplesFolder {
    param([string]$Preferred)
    if (-not [string]::IsNullOrWhiteSpace($Preferred) -and (Test-Path $Preferred)) {
        return $Preferred
    }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Spooling-Savant-V3-Exports\SpoolingManager\DimensionExamples"),
        (Join-Path $env:ProgramData "Autodesk\Revit\Addins\2024\Spooling-Savant-V3-Exports\SpoolingManager\DimensionExamples"),
        (Join-Path $env:ProgramData "Autodesk\Revit\Addins\2024\Spooling-Savant-V3-Exports\SpoolingManager\DimensionExamples")
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { return $c }
    }
    return $null
}

function Resolve-PolicyOutputPath {
    param([string]$Preferred)
    if (-not [string]::IsNullOrWhiteSpace($Preferred)) {
        return $Preferred
    }
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Spooling-Savant-V3-Exports\SpoolingManager\DimensionPolicy.json"),
        (Join-Path $env:ProgramData "Autodesk\Revit\Addins\2024\Spooling-Savant-V3-Exports\SpoolingManager\DimensionPolicy.json")
    )
    foreach ($c in $candidates) {
        $dir = Split-Path $c -Parent
        if (Test-Path $dir) { return $c }
    }
    return $candidates[0]
}

function Get-MeasurementOrientation {
    param($Dim)
    $pull = [string]$Dim.pullDirection
    if ($pull -eq "Up" -or $pull -eq "Down") { return "Horizontal" }
    if ($pull -eq "Left" -or $pull -eq "Right") { return "Vertical" }
    $role = [string]$Dim.inferredRole
    if ($role -eq "VerticalDrop" -or $role -eq "BranchHeight") { return "Vertical" }
    return "Horizontal"
}

function Get-OffsetSignForOrientation {
    param($Dim, [string]$Orientation)
    if ($Orientation -eq "Horizontal") {
        if ($null -ne $Dim.offsetSignUp) { return [int]$Dim.offsetSignUp }
        if ($Dim.pullDirection -eq "Down") { return -1 }
        return 1
    }
    if ($null -ne $Dim.offsetSignRight) { return [int]$Dim.offsetSignRight }
    if ($Dim.pullDirection -eq "Left") { return -1 }
    return 1
}

$ExamplesFolder = Resolve-ExamplesFolder $ExamplesFolder
if ([string]::IsNullOrWhiteSpace($ExamplesFolder) -or -not (Test-Path $ExamplesFolder)) {
    Write-Error "No DimensionExamples folder found. Capture examples first."
    exit 1
}

$files = Get-ChildItem -Path $ExamplesFolder -Filter "example_*.json"
if ($files.Count -eq 0) {
    Write-Error "No example_*.json files in $ExamplesFolder"
    exit 1
}

$bucket = @{}
$allowedRoles = @{}
$totalDims = 0
foreach ($f in $files) {
    try {
        $j = Get-Content $f.FullName -Raw | ConvertFrom-Json
    }
    catch {
        Write-Warning "Skipping $($f.Name): $_"
        continue
    }
    foreach ($d in $j.dimensions) {
        $role = [string]$d.inferredRole
        if ([string]::IsNullOrWhiteSpace($role) -or $role -eq "Unknown" -or $role -eq "Other") { continue }
        $allowedRoles[$role] = $true
        $orientation = Get-MeasurementOrientation $d
        $sign = Get-OffsetSignForOrientation $d $orientation
        $key = "$role|$orientation"
        if (-not $bucket.ContainsKey($key)) {
            $bucket[$key] = @{ Plus = 0; Minus = 0 }
        }
        if ($sign -lt 0) { $bucket[$key].Minus++ } else { $bucket[$key].Plus++ }
        $totalDims++
    }
}

$rules = @()
foreach ($key in ($bucket.Keys | Sort-Object)) {
    $parts = $key.Split("|")
    $role = $parts[0]
    $orientation = $parts[1]
    $plus = $bucket[$key].Plus
    $minus = $bucket[$key].Minus
    $count = $plus + $minus
    $offsetSign = if ($plus -ge $minus) { 1 } else { -1 }
    $rules += [ordered]@{
        inferredRole = $role
        measurementOrientation = $orientation
        offsetSign = $offsetSign
        sampleCount = $count
        lockOffsetSign = $true
    }
    Write-Host ("  {0,-14} {1,-10} => sign {2} ({3} samples, +{4}/-{5})" -f $role, $orientation, $offsetSign, $count, $plus, $minus)
}

# Captured pick-ups are tagged RunOverall but runtime E-C intents map to RunPickUp — mirror horizontal rules.
foreach ($rule in @($rules | Where-Object { $_.inferredRole -eq "RunOverall" -and $_.measurementOrientation -eq "Horizontal" })) {
    $rules += [ordered]@{
        inferredRole = "RunPickUp"
        measurementOrientation = "Horizontal"
        offsetSign = $rule.offsetSign
        sampleCount = $rule.sampleCount
        lockOffsetSign = $true
    }
    Write-Host ("  {0,-14} {1,-10} => sign {2} ({3} samples, mirrored from RunOverall)" -f "RunPickUp", "Horizontal", $rule.offsetSign, $rule.sampleCount)
}

$roleList = @($allowedRoles.Keys | Sort-Object)
if ($roleList -notcontains "RunPickUp") { $roleList += "RunPickUp" }

$suppressIntentLabels = @(
    "branch-off-run",
    "perpendicular run",
    "fitting-pipe-fitting",
    "C-E pipe span",
    "E-E dominant run",
    "E-E run",
    "F-F segment",
    "F-F overall",
    "C-C",
    "mid-run c-f",
    "mid-run f-e",
    "mid-run c-e overall",
    "takeoff c-f from host center",
    "olet vertical takeoff",
    "branch vertical takeoff"
)

$policy = [ordered]@{
    schemaVersion = 1
    trainedAt = (Get-Date).ToString("o")
    scope = "AllDetailViews"
    note = "Rules trained from captured examples. View geometry class is ignored at runtime so Top examples apply to Front/Left/Right/Back detail views."
    exampleFileCount = $files.Count
    dimensionSampleCount = $totalDims
    allowedRoles = $roleList
    suppressIntentLabels = $suppressIntentLabels
    rules = $rules
}

$json = $policy | ConvertTo-Json -Depth 6
$OutputPath = Resolve-PolicyOutputPath $OutputPath
$outDir = Split-Path $OutputPath -Parent
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
Set-Content -Path $OutputPath -Value $json -Encoding UTF8
Write-Host ""
Write-Host "Wrote policy: $OutputPath"
Write-Host "Example files: $($files.Count)  Dimension samples: $totalDims  Rules: $($rules.Count)"

# Mirror beside module settings when ProgramData folder exists.
$modulePath = Join-Path $env:ProgramData "Autodesk\Revit\Addins\2024\Spooling-Savant-V3-Exports\SpoolingManager\DimensionPolicy.json"
if ($OutputPath -ne $modulePath) {
    $moduleDir = Split-Path $modulePath -Parent
    if (Test-Path $moduleDir) {
        Copy-Item -Path $OutputPath -Destination $modulePath -Force
        Write-Host "Copied policy to: $modulePath"
    }
}
