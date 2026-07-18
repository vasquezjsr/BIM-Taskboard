$path = 'C:\Users\vasqu\.cursor\projects\c-Addins\agent-transcripts\e4d044ad-c023-494a-bbf3-7b5dc0f8abbb\e4d044ad-c023-494a-bbf3-7b5dc0f8abbb.jsonl'
$adPattern = '(?i)auto.?dim|dimension|witness|CHW-|olet|flange|Spool2D|Send Dims|cursor-inbox|TryApplySpool|LinearDimension|detail line|topology|intent|E-C|E-CL|F-E|C-C|C-F|weldolet|AutoDimPlacement|AutoDimDiagnostics|almost perfect|revert|hotload|CreateSpoolSheetsHandler|DimensionLearning|FabricationRefDiag|branch takeoff|tagging|ApplyUniversalRun|lesson|Spool2D|placement log|nothing changed|wrong way|stacked|clutter'
$offPattern = '(?i)^just to clarify.*dark mode|white background|hang up|completed dialog|froze|crash.*2027|speed up.*sheet|SpoolGenReport|cutting off.*view|crop|pane automatically|tab needs|theme.*pane|smaller\. Still waiting'

$rawEntries = @()
$lineNum = 0
Get-Content $path -Encoding UTF8 | ForEach-Object {
    $lineNum++
    try { $obj = $_ | ConvertFrom-Json } catch { return }
    if ($obj.role -notin @('user', 'assistant')) { return }
    $raw = ($obj.message.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }) -join ''
    if ([string]::IsNullOrWhiteSpace($raw)) { return }
    $ts = if ($raw -match '<timestamp>([^<]+)</timestamp>') { $Matches[1] } else { '' }
    if ($ts -and $ts -notmatch 'Jul [345]|July [345]') { return }
    $text = $raw -replace '<timestamp>[^<]*</timestamp>\s*', '' -replace '<user_query>\s*', '' -replace '</user_query>', ''
    $text = $text -replace '\[Image\]', '[Image attached]'
    $text = $text -replace '(?s)<image_files>.*?</image_files>\s*', ''
    $text = $text -replace '<[^>]+>', ''
    $text = $text.Trim()
    if ($text.Length -lt 1) { return }
    $rawEntries += [PSCustomObject]@{ Line = $lineNum; Role = $obj.role; Ts = $ts; Text = $text }
}

# Merge consecutive assistant messages into single replies per user turn
$turns = @()
$i = 0
while ($i -lt $rawEntries.Count) {
    $e = $rawEntries[$i]
    if ($e.Role -eq 'user') {
        $user = $e
        $asstParts = @()
        $i++
        while ($i -lt $rawEntries.Count -and $rawEntries[$i].Role -eq 'assistant') {
            $asstParts += $rawEntries[$i].Text
            $i++
        }
        $asstText = ($asstParts | Where-Object { $_ -and $_.Trim().Length -gt 0 }) -join "`n`n"
        $turns += [PSCustomObject]@{
            UserLine = $user.Line
            Ts       = $user.Ts
            User     = $user.Text
            Assistant = $asstText
        }
    }
    else { $i++ }
}

function Test-AdTurn($userText, $asstText) {
    $combined = "$userText $asstText"
    if ($combined -match $adPattern) { return $true }
    if ($userText -match $offPattern) { return $false }
    return $false
}

$adTurns = $turns | Where-Object { Test-AdTurn $_.User $_.Assistant }

Write-Host "Raw entries: $($rawEntries.Count); turns: $($turns.Count); auto-dim turns: $($adTurns.Count)"

$out = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)\AutoDimensions-Chat-Export-Jul3.md'
$lines = @(
    '# Auto Dimensions Chat Export (July 3-5, 2026)',
    '',
    "Full conversation turns about auto dimensions: **$($adTurns.Count) user prompts** with merged assistant replies.",
    'Source: Cursor agent transcript `e4d044ad-c023-494a-bbf3-7b5dc0f8abbb.jsonl`. Images referenced but not embedded.',
    '',
    '---',
    ''
)

$n = 0
foreach ($t in $adTurns) {
    $n++
    $tsPart = if ($t.Ts) { " ($($t.Ts))" } else { '' }
    $lines += "## Turn $n$tsPart"
    $lines += ''
    $lines += '### You'
    $lines += ''
    $lines += $t.User
    $lines += ''
    $lines += '### Assistant'
    $lines += ''
    $asst = $t.Assistant
    if ($asst.Length -gt 15000) {
        $asst = $asst.Substring(0, 15000) + "`n`n[... assistant reply truncated at 15000 chars - transcript user line $($t.UserLine) ...]"
    }
    if ([string]::IsNullOrWhiteSpace($asst)) {
        $asst = '_(no text reply captured in transcript)_'
    }
    $lines += $asst
    $lines += ''
    $lines += '---'
    $lines += ''
}

[System.IO.File]::WriteAllText($out, ($lines -join "`r`n"), [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $out"

# Also write compact index
$idx = 'C:\Apps\BIM-Taskboard\Spooling Savant Version 3 (Exports)\AutoDimensions-Chat-Index-Jul3.md'
$idxLines = @('# Auto Dimensions Chat Index (July 3-5, 2026)', '', '| # | When | Your prompt (first line) |', '|---|------|---------------------------|')
$num = 0
foreach ($t in $adTurns) {
    $num++
    $firstLine = ($t.User -split "`n" | Where-Object { $_.Trim().Length -gt 0 } | Select-Object -First 1)
    if ($firstLine.Length -gt 120) { $firstLine = $firstLine.Substring(0, 117) + '...' }
    $firstLine = $firstLine -replace '\|', '/'
    $idxLines += "| $num | $($t.Ts) | $firstLine |"
}
[System.IO.File]::WriteAllText($idx, ($idxLines -join "`r`n"), [System.Text.UTF8Encoding]::new($false))
Write-Host "Wrote $idx"
