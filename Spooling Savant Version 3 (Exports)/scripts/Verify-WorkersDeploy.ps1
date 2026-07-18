$ErrorActionPreference = 'Stop'
$src = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)\SpoolingSavantV3Exports.Workers\bin\Release\SpoolingSavantV3Exports.Workers.dll'
$srcInfo = Get-Item $src
$srcVer = [Reflection.AssemblyName]::GetAssemblyName($src).Version
Write-Host "SOURCE: $($srcInfo.LastWriteTime) len=$($srcInfo.Length) ver=$srcVer"
foreach ($y in @('2024','2025','2026','2027')) {
    $p = "C:\ProgramData\Autodesk\Revit\Addins\$y\Spooling-Savant-V3-Exports\Hotload\SpoolingSavantV3Exports.Workers.dll"
    if (Test-Path $p) {
        $i = Get-Item $p
        $v = [Reflection.AssemblyName]::GetAssemblyName($p).Version
        $match = ($i.Length -eq $srcInfo.Length -and $v -eq $srcVer)
        Write-Host "HOTLOAD $y`: $($i.LastWriteTime) len=$($i.Length) ver=$v match=$match"
    } else {
        Write-Host "HOTLOAD $y`: MISSING"
    }
}
$shadowRoot = Join-Path $env:LOCALAPPDATA 'Spooling-Savant-V3-Exports\WorkerShadow'
if (Test-Path $shadowRoot) {
    Get-ChildItem $shadowRoot -Directory | Sort-Object LastWriteTime -Descending | Select-Object -First 3 | ForEach-Object {
        $dll = Join-Path $_.FullName 'SpoolingSavantV3Exports.Workers.dll'
        if (Test-Path $dll) {
            $i = Get-Item $dll
            $v = [Reflection.AssemblyName]::GetAssemblyName($dll).Version
            Write-Host "SHADOW $($_.Name): $($i.LastWriteTime) len=$($i.Length) ver=$v"
        }
    }
}
$bytes = [IO.File]::ReadAllBytes($src)
$text = [Text.Encoding]::UTF8.GetString($bytes)
Write-Host "String turn74-checkpoint: $($text.Contains('turn74-checkpoint'))"
Write-Host "String takeoff c-f from host center: $($text.Contains('takeoff c-f from host center'))"
Write-Host "String Spool2D engine intents: $($text.Contains('Spool2D engine intents'))"
