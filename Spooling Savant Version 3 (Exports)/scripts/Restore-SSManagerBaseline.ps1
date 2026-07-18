<#
.SYNOPSIS
  Restore SS Manager from the single canonical baseline checkpoint.
#>
[CmdletBinding()]
param(
    [string]$BaselineId = '20260707_205248'
)

$ErrorActionPreference = 'Stop'
$root = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)'
$srcDir = Join-Path (Join-Path $root 'SSManager-Baseline') $BaselineId
if (-not (Test-Path -LiteralPath $srcDir)) {
    throw "SS Manager baseline not found: $srcDir"
}

$workers = Join-Path $root 'SpoolingSavantV3Exports.Workers'
$baselineRoot = Join-Path $srcDir 'SpoolingSavantV3Exports.Workers'

Get-ChildItem -LiteralPath $baselineRoot -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($baselineRoot.Length).TrimStart('\')
    if ($rel -eq 'MANIFEST.md' -or $rel -like '*\MANIFEST.md') {
        return
    }
    $target = Join-Path $workers $rel
    $targetParent = Split-Path $target -Parent
    if (-not (Test-Path -LiteralPath $targetParent)) {
        New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
    }
    Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    Write-Host "Restored $rel"
}

& (Join-Path $root 'scripts\Build-Workers.ps1') -Configuration Release
Write-Host "Restored SS Manager baseline $BaselineId and built Release. Hotload SS Manager in Revit."
