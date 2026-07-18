<#
.SYNOPSIS
  Refreshes the single SS Manager baseline (auto-dim lives inside SpoolingManager).

  Per-turn timestamped history was retired — one baseline only: 20260707_205248.
#>
[CmdletBinding()]
param(
    [string]$PromptSummary = '',
    [string]$ResponseSummary = ''
)

& (Join-Path $PSScriptRoot 'Backup-SSManagerBaseline.ps1') `
    -BaselineId '20260707_205248' `
    -PromptSummary $PromptSummary `
    -ResponseSummary $ResponseSummary
