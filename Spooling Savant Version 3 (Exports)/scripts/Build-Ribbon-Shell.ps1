param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
& $msbuild "$root\SpoolingSavantV3Exports\SpoolingSavantV3Exports.csproj" /t:Rebuild /p:Configuration=$Configuration /p:DeployRevitAddin=true /p:DeploySpoolingSavantV3ExportsShellToProgramData=true /p:BuildingRibbonShell=true /v:m
powershell.exe -NoLogo -NonInteractive -ExecutionPolicy Bypass -File "$root\SpoolingSavantV3Exports\Properties\DeployStableAddin.ps1" -TargetDir "$root\SpoolingSavantV3Exports\bin\$Configuration" -RevitAddinsRoot "C:\ProgramData\Autodesk\Revit\Addins" -ProjectDir "$root\SpoolingSavantV3Exports" -AssemblyBaseName "SpoolingSavantV3Exports"
Write-Host "Ribbon shell deployed. Restart Revit to pick up ribbon changes."
