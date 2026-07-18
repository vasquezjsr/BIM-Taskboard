# Installs pyRevit + Revit MCP bridge for Cursor (one-time setup).
# Re-run safe; skips steps that are already done.

$ErrorActionPreference = "Stop"

Write-Host "=== Revit MCP setup ===" -ForegroundColor Cyan

function Ensure-WingetPackage([string]$Id) {
    $installed = winget list --id $Id --accept-source-agreements --disable-interactivity 2>$null
    if ($LASTEXITCODE -ne 0) {
        winget install $Id --accept-package-agreements --accept-source-agreements --disable-interactivity
    }
}

Ensure-WingetPackage "Python.Python.3.12"
Ensure-WingetPackage "pyRevit.pyRevit.CLI"
Ensure-WingetPackage "Git.Git"

$env:Path = [System.Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [System.Environment]::GetEnvironmentVariable('Path','User')

python -m pip install --upgrade uv | Out-Null

$tools = "C:\Addins\Tools\mcp-server-for-revit-python"
if (-not (Test-Path $tools)) {
    New-Item -ItemType Directory -Path "C:\Addins\Tools" -Force | Out-Null
    git clone https://github.com/revit-mcp/revit-mcp-python.git $tools
}

Push-Location $tools
uv sync
Pop-Location

$cloneName = "mastercorex"
$cloneDest = Join-Path $env:LOCALAPPDATA "pyRevit\Clones\$cloneName"
if (-not (Test-Path (Join-Path $cloneDest "extensions\pyRevitCore.extension"))) {
    if (Test-Path $cloneDest) { Remove-Item $cloneDest -Recurse -Force }
    # "core" has no extensions folder; pyRevit tab/Routes UI never loads. Use "corex" minimum.
    pyrevit clone $cloneName corex --dest $cloneDest
}
pyrevit attach $cloneName default --installed | Out-Null

# Install the official pyRevit extension (registers in pyRevit, not a manual folder copy).
pyrevit extend mcp-server-for-revit-python | Out-Null

# Remove legacy manual copy if present (duplicate name confuses pyRevit).
$legacyExt = Join-Path $env:APPDATA "pyRevit\Extensions\revit-mcp-python.extension"
if (Test-Path $legacyExt) { Remove-Item $legacyExt -Recurse -Force }

pyrevit configs routes enable | Out-Null
pyrevit configs routes port 48884 | Out-Null
pyrevit extensions enable revit-mcp-python | Out-Null

$uv = (Get-Command uv).Source
$mcpJson = @"
{
  "mcpServers": {
    "revit": {
      "command": "$($uv -replace '\\','\\')",
      "args": [
        "run",
        "--directory",
        "$($tools -replace '\\','\\')",
        "mcp",
        "run",
        "$($tools -replace '\\','\\')\\main.py"
      ]
    }
  }
}
"@

$mcpUser = Join-Path $env:USERPROFILE ".cursor\mcp.json"
New-Item -ItemType Directory -Path (Split-Path $mcpUser) -Force | Out-Null
Set-Content -Path $mcpUser -Value $mcpJson -Encoding UTF8

Write-Host ""
Write-Host "Done. Finish in Revit (required once after setup or config change):" -ForegroundColor Green
Write-Host "  1. If pyRevit tab is missing, restart Revit completely."
Write-Host "  2. pyRevit > Reload pyRevit  (or restart Revit if Reload is unavailable)"
Write-Host "  3. Confirm Routes + extension loaded: http://localhost:48884/revit_mcp/status/"
Write-Host "     Expect JSON with status=active and your open document title."
Write-Host ""
Write-Host "Then reload Cursor so MCP server 'revit' connects." -ForegroundColor Green
Write-Host "Cursor config: $mcpUser"
Write-Host "Active pyRevit config: C:\ProgramData\pyRevit\pyRevit_config.ini"
