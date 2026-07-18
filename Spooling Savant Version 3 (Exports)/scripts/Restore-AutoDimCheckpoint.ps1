<#
.SYNOPSIS
  Restore from the single SS Manager baseline. The -Checkpoint parameter is ignored (legacy compat).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Checkpoint = '20260707_205248'
)

if ($Checkpoint -and $Checkpoint -ne '20260707_205248') {
    Write-Warning "Only baseline 20260707_205248 exists. Ignoring -Checkpoint '$Checkpoint'."
}

& (Join-Path $PSScriptRoot 'Restore-SSManagerBaseline.ps1') -BaselineId '20260707_205248'
