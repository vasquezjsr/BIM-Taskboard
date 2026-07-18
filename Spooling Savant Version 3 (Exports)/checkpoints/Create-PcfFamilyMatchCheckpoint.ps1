$ErrorActionPreference = 'Stop'
$root = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)'
$dest = Join-Path $root 'checkpoints\pcf-before-family-match'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

$files = @(
	'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\PcfImportService.cs',
	'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\PcfExportService.cs',
	'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\PcfParser.cs',
	'SpoolingSavantV3Exports.Workers\FabricationConnectorEnds.cs',
	'SpoolingSavantV3Exports.Workers\FabricationPartClassification.cs'
)

foreach ($rel in $files) {
	$src = Join-Path $root $rel
	$name = ($rel -replace '[\\/]', '_')
	Copy-Item -LiteralPath $src -Destination (Join-Path $dest $name) -Force
	Write-Output "OK $rel"
}

@'
PCF checkpoint before Family-first palette matching.
Created: 2026-07-11

This does NOT replace SS Manager baseline 20260707_205248.

Restore:
  1. Copy files from this folder back to their live paths (underscores -> path separators).
  2. Run scripts\Build-Workers.ps1 -Configuration Release
  3. Click SS Manager V3 to hotload
'@ | Set-Content -Path (Join-Path $dest 'README.txt') -Encoding UTF8

Get-ChildItem $dest | Select-Object Name, Length | Format-Table -AutoSize
