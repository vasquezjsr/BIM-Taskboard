$ErrorActionPreference = 'Stop'
$root = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)'
$src = Join-Path $root 'checkpoints\pcf-before-family-match'

$map = @{
	'SpoolingSavantV3Exports.Workers_SpoolingManager_Services_PcfImportService.cs' = 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\PcfImportService.cs'
	'SpoolingSavantV3Exports.Workers_SpoolingManager_Services_PcfExportService.cs' = 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\PcfExportService.cs'
	'SpoolingSavantV3Exports.Workers_SpoolingManager_Services_PcfParser.cs' = 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\PcfParser.cs'
	'SpoolingSavantV3Exports.Workers_FabricationConnectorEnds.cs' = 'SpoolingSavantV3Exports.Workers\FabricationConnectorEnds.cs'
	'SpoolingSavantV3Exports.Workers_FabricationPartClassification.cs' = 'SpoolingSavantV3Exports.Workers\FabricationPartClassification.cs'
}

foreach ($name in $map.Keys) {
	$from = Join-Path $src $name
	$to = Join-Path $root $map[$name]
	if (-not (Test-Path -LiteralPath $from)) { throw "Missing checkpoint file: $from" }
	Copy-Item -LiteralPath $from -Destination $to -Force
	Write-Output "Restored $($map[$name])"
}

Write-Output 'Done. Run scripts\Build-Workers.ps1 -Configuration Release, then click SS Manager V3.'
