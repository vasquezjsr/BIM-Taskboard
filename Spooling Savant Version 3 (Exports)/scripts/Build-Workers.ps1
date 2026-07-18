param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe | Select-Object -First 1
& $msbuild "$root\SpoolingSavantV3Exports.Workers\SpoolingSavantV3Exports.Workers.csproj" /t:Rebuild /p:Configuration=$Configuration /p:DeployRevitAddin=true /v:m
if ($LASTEXITCODE -ne 0) {
    throw "MSBuild failed with exit code $LASTEXITCODE"
}
powershell.exe -NoLogo -NonInteractive -ExecutionPolicy Bypass -File "$root\SpoolingSavantV3Exports.Workers\Properties\DeployWorkersHotload.ps1" -SourceDir "$root\SpoolingSavantV3Exports.Workers\bin\$Configuration" -AddinsRoot "C:\ProgramData\Autodesk\Revit\Addins"
Write-Host "Workers built. Click SS Manager V3 in Revit again to hotload (no restart)."
