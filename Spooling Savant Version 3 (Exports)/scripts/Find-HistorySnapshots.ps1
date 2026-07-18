$hist = 'C:\Users\vasqu\AppData\Roaming\Cursor\User\History'
Get-ChildItem $hist -Recurse -Filter 'entries.json' | ForEach-Object {
    $j = Get-Content $_.FullName -Raw | ConvertFrom-Json
    foreach ($e in $j.entries) {
        $ts = [DateTimeOffset]::FromUnixTimeMilliseconds($e.timestamp).LocalDateTime
        if ($j.resource -match 'CreateSpoolSheetsHandler|AutoDimension|DimensionPrinciples|TopologyRun|Spool2D|LessonDriven') {
            [PSCustomObject]@{
                Time = $ts
                Resource = [uri]::UnescapeDataString($j.resource -replace 'file:///', '')
                File = Join-Path $_.DirectoryName $e.id
                Size = if (Test-Path (Join-Path $_.DirectoryName $e.id)) { (Get-Item (Join-Path $_.DirectoryName $e.id)).Length } else { 0 }
            }
        }
    }
} | Sort-Object Time | Format-Table -AutoSize
