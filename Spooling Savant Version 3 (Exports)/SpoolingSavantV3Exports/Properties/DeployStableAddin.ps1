# Best-effort deploy of SpoolingSavantV3Exports.dll, PDB, Icons, Parameters, and per-year SpoolingSavantV3Exports.addin to ProgramData.
param(
    [Parameter(Mandatory = $true)]
    [string] $TargetDir,
    [Parameter(Mandatory = $true)]
    [string] $RevitAddinsRoot,
    [Parameter(Mandatory = $true)]
    [string] $ProjectDir,
    [Parameter(Mandatory = $true)]
    [string] $AssemblyBaseName
)

$TargetDir = [System.IO.Path]::GetFullPath($TargetDir.TrimEnd('\', '/'))
$RevitAddinsRoot = [System.IO.Path]::GetFullPath($RevitAddinsRoot.TrimEnd('\', '/'))
$ProjectDir = [System.IO.Path]::GetFullPath($ProjectDir.TrimEnd('\', '/'))

foreach ($year in @('2024', '2025', '2026', '2027')) {
    $destTools = Join-Path $RevitAddinsRoot "$year\Spooling-Savant-V3-Exports"
    $destIcons = Join-Path $destTools 'Icons'
    $destParams = Join-Path $destTools 'Parameters'
    $destHotload = Join-Path $destTools 'Hotload'

    $null = New-Item -ItemType Directory -Force -Path $destTools, $destIcons, $destParams, $destHotload

    foreach ($name in @("$AssemblyBaseName.dll", "$AssemblyBaseName.pdb")) {
        $src = Join-Path $TargetDir $name
        if (Test-Path -LiteralPath $src) {
            Copy-Item -LiteralPath $src -Destination $destTools -Force -ErrorAction SilentlyContinue
        }
    }

    $iconsSrc = Join-Path $TargetDir 'Icons'
    if (Test-Path -LiteralPath $iconsSrc) {
        & robocopy.exe $iconsSrc $destIcons /E /IS /IT /R:0 /W:0 /NP 2>&1 | Out-Null
    }

    $paramsSrc = Join-Path $ProjectDir "..\SpoolingSavantV3Exports.Workers\Parameters"
    if (Test-Path -LiteralPath $paramsSrc) {
        & robocopy.exe $paramsSrc $destParams /E /IS /IT /R:0 /W:0 /NP 2>&1 | Out-Null
    }

    $addin = Join-Path $ProjectDir "Addins\$year\SpoolingSavantV3Exports.addin"
    if (Test-Path -LiteralPath $addin) {
        # Revit 2027+ ignores all-users manifests under ProgramData; deploy per-user to %AppData% instead.
        if ([int]$year -ge 2027) {
            $userAddins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$year"
            $null = New-Item -ItemType Directory -Force -Path $userAddins
            Copy-Item -LiteralPath $addin -Destination (Join-Path $userAddins "SpoolingSavantV3Exports.addin") -Force -ErrorAction SilentlyContinue
        }
        else {
            Copy-Item -LiteralPath $addin -Destination (Join-Path $RevitAddinsRoot "$year\SpoolingSavantV3Exports.addin") -Force -ErrorAction SilentlyContinue
        }
    }
}

exit 0
