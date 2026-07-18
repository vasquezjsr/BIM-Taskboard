# Best-effort restore from Cursor "Undo Create Diff" local history (Jul 4 ~2:28 PM snapshots).
# No git repo exists for this project; these are the only file-level backups available.
$ErrorActionPreference = 'Stop'
$root = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)'
$history = 'C:\Users\vasqu\AppData\Roaming\Cursor\User\History'

$restores = @(
    @{ Src = Join-Path $history '13047136\siP3.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\CreateSpoolSheetsHandler.AutoDimensionRules.cs' }
    @{ Src = Join-Path $history 'a020a21\JA7g.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\AssemblyMemberChangeCoordinator.cs' }
    @{ Src = Join-Path $history '52b5fe7\W66n.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\AssemblyMemberSyncService.cs' }
    @{ Src = Join-Path $history '17e98e60\dJVA.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\RefreshSheetsHandler.cs' }
    @{ Src = Join-Path $history '64e3dd1b\rj8A.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Views\SpoolingManagerPane.xaml.cs' }
    @{ Src = Join-Path $history '-6f829712\wGDc.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\AssemblyTemporaryVisibilityHandler.cs' }
    @{ Src = Join-Path $history '74915818\oKrx.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Models\SpoolingManagerSettings.cs' }
    @{ Src = Join-Path $history '-180a4830\Yujn.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Views\AssemblySettingsWindow.xaml.cs' }
    @{ Src = Join-Path $history '236598ee\o29D.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\FabricationPartClassification.cs' }
    @{ Src = Join-Path $history '-5f105a57\Biwe.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\RenameSheetsHandler.cs' }
    @{ Src = Join-Path $history '-48e2a11c\OaHU.xaml'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\Themes\SsSavantDialogResources.xaml' }
    @{ Src = Join-Path $history '66a11551\yMnw.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\RevitRequestBridge.cs' }
    @{ Src = Join-Path $history '5a5bb909\IrCK.cs'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Services\ApplyAssemblyPackageHandler.cs' }
    @{ Src = Join-Path $history '2ff0ce8e\hBSS.xaml'; Dst = Join-Path $root 'SpoolingSavantV3Exports.Workers\SpoolingManager\Views\AssemblySettingsWindow.xaml' }
)

foreach ($item in $restores) {
    if (-not (Test-Path -LiteralPath $item.Src)) {
        Write-Warning "Missing snapshot: $($item.Src)"
        continue
    }
    Copy-Item -LiteralPath $item.Src -Destination $item.Dst -Force
    Write-Host "Restored $($item.Dst)"
}

Write-Host 'Restore complete.'
