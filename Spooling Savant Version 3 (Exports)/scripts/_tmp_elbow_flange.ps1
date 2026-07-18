$p = 'C:\Users\vasqu\OneDrive\Documents\Spooling Savant Test Reports\TP004 - PCF - Steel.pcf'
$c = [IO.File]::ReadAllText($p)
Write-Output ("len={0} mtime={1}" -f (Get-Item -LiteralPath $p).Length, (Get-Item -LiteralPath $p).LastWriteTime)
foreach ($t in @('PIPE','ELBOW','TEE','FLANGE','OLET','CAP','WELD','MISC-COMPONENT')) {
  $n = [regex]::Matches($c, "(?m)^$([regex]::Escape($t))\b").Count
  Write-Output ("{0} : {1}" -f $t, $n)
}
Write-Output '==== FLANGES ===='
$blocks = [regex]::Split($c, '(?m)(?=^(PIPE|ELBOW|TEE|FLANGE|OLET|CAP|WELD)\b)')
foreach ($b in $blocks) {
  if ($b -match '^FLANGE\b') {
    ($b -split "`r?`n" | Select-Object -First 12) | ForEach-Object { $_ }
    Write-Output '---'
  }
}
# Find consecutive elbows same spool (possible back-to-back)
Write-Output '==== ELBOW blocks with nearby coords hint ===='
$elbows = @()
foreach ($b in $blocks) {
  if ($b -notmatch '^ELBOW\b') { continue }
  $spool = if ($b -match 'SPOOL-ID\s+(\S+)') { $Matches[1] } else { '?' }
  $id = if ($b -match 'COMPONENT-IDENTIFIER\s+(\d+)') { $Matches[1] } else { '?' }
  $eps = [regex]::Matches($b, 'END-POINT\s+([-\d.]+)\s+([-\d.]+)\s+([-\d.]+)')
  if ($eps.Count -ge 2) {
    $elbows += [pscustomobject]@{ Id=$id; Spool=$spool; X1=[double]$eps[0].Groups[1].Value; Y1=[double]$eps[0].Groups[2].Value; Z1=[double]$eps[0].Groups[3].Value; X2=[double]$eps[1].Groups[1].Value; Y2=[double]$eps[1].Groups[2].Value; Z2=[double]$eps[1].Groups[3].Value }
  }
}
for ($i=0; $i -lt $elbows.Count; $i++) {
  for ($j=$i+1; $j -lt $elbows.Count; $j++) {
    $a=$elbows[$i]; $b=$elbows[$j]
    $ptsA = @(@($a.X1,$a.Y1,$a.Z1), @($a.X2,$a.Y2,$a.Z2))
    $ptsB = @(@($b.X1,$b.Y1,$b.Z1), @($b.X2,$b.Y2,$b.Z2))
    foreach ($pa in $ptsA) {
      foreach ($pb in $ptsB) {
        $dx=$pa[0]-$pb[0]; $dy=$pa[1]-$pb[1]; $dz=$pa[2]-$pb[2]
        $d=[math]::Sqrt($dx*$dx+$dy*$dy+$dz*$dz)
        if ($d -lt 0.2) {
          Write-Output ("ELBOW {0}({1}) near ELBOW {2}({3}) d={4:F4}" -f $a.Id,$a.Spool,$b.Id,$b.Spool,$d)
          Write-Output ("  A: ({0:F3},{1:F3},{2:F3})-({3:F3},{4:F3},{5:F3})" -f $a.X1,$a.Y1,$a.Z1,$a.X2,$a.Y2,$a.Z2)
          Write-Output ("  B: ({0:F3},{1:F3},{2:F3})-({3:F3},{4:F3},{5:F3})" -f $b.X1,$b.Y1,$b.Z1,$b.X2,$b.Y2,$b.Z2)
        }
      }
    }
  }
}
