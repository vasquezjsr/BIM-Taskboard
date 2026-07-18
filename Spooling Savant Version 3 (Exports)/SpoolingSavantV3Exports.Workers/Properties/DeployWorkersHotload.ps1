# Unblock OpenXml / ClosedXML satellites after robocopy (avoids HRESULT 0x80131515 on LoadFrom).
param(
    [Parameter(Mandatory = $true)]
    [string] $SourceDir,
    [Parameter(Mandatory = $true)]
    [string] $AddinsRoot
)

$SourceDir = [System.IO.Path]::GetFullPath($SourceDir.TrimEnd('\', '/'))
$RevitAddinsRoot = [System.IO.Path]::GetFullPath($AddinsRoot.TrimEnd('\', '/'))

if (-not (Test-Path -LiteralPath $SourceDir)) {
    Write-Warning "DeployWorkersHotload: source directory not found: $SourceDir"
    exit 0
}

foreach ($year in @('2024', '2025', '2026', '2027')) {
    $dest = Join-Path $RevitAddinsRoot "$year\Spooling-Savant-V3-Exports\Hotload"
    $null = New-Item -ItemType Directory -Force -Path $dest
    & robocopy.exe $SourceDir $dest '*.dll' '*.pdb' /IS /IT /R:0 /W:0 /NP 2>&1 | Out-Null
    Get-ChildItem -LiteralPath $dest -Filter '*.dll' -ErrorAction SilentlyContinue | ForEach-Object {
        try { Unblock-File -LiteralPath $_.FullName -ErrorAction SilentlyContinue } catch {}
    }
}

exit 0
