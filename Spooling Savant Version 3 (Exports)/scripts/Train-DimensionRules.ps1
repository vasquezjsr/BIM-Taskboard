param(
    [string]$SessionsFolder = "$env:LOCALAPPDATA\Spooling-Savant-V3-Exports\DimensionLearning\sessions",
    [string]$OutputPath = "$env:LOCALAPPDATA\Spooling-Savant-V3-Exports\DimensionLearning\DimensionRules.json"
)

$ErrorActionPreference = 'Stop'

function Normalize-Role([string]$role) {
    if ([string]::IsNullOrWhiteSpace($role) -or $role -eq 'Unknown') { return 'Other' }
    return $role.Trim()
}

function Build-SituationKey($session) {
    return "{0}|{1}|{2}" -f $session.topology.topologyHash, $session.viewGeometry.geometryClass, $session.viewGeometry.viewType
}

function Build-RuleKey($dim) {
    $orientation = if ($dim.isHorizontalMeasurement) { 'Horizontal' } else { 'Vertical' }
    return "{0}|{1}|{2}" -f $dim.witnessPair, (Normalize-Role $dim.inferredRole), $orientation
}

function Mode-Int($values, [int]$fallback) {
    if (-not $values -or $values.Count -eq 0) { return $fallback }
    return ($values | Group-Object | Sort-Object Count -Descending | Select-Object -First 1).Name
}

function Mode-String($values, [string]$fallback) {
    if (-not $values -or $values.Count -eq 0) { return $fallback }
    $best = $values | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Group-Object | Sort-Object Count -Descending | Select-Object -First 1
    if ($best) { return [string]$best.Name }
    return $fallback
}

function Resolve-StackGroup([string]$pullDirection, [string]$orientation) {
    if ($pullDirection -in @('Left','Right')) { return 0 }
    if ($orientation -eq 'Vertical') { return 30 }
    return 0
}

$files = Get-ChildItem -Path $SessionsFolder -Filter '*.json' -File | Where-Object { $_.Name -ne 'index.jsonl' }
if (-not $files -or $files.Count -eq 0) {
    Write-Error "No session JSON files found in $SessionsFolder"
}

$sessions = @()
foreach ($file in $files) {
    try {
        $doc = Get-Content $file.FullName -Raw | ConvertFrom-Json
        if ($doc.dimensions -and $doc.dimensions.Count -gt 0 -and $doc.topology) {
            $sessions += [PSCustomObject]@{ File = $file.FullName; Doc = $doc }
        }
    } catch {}
}

if ($sessions.Count -eq 0) {
    Write-Error 'No usable learning sessions with dimensions were found.'
}

$situations = @{}
$globalAcc = @{}
$sampleCount = 0

foreach ($entry in $sessions) {
    $session = $entry.Doc
    $situationKey = Build-SituationKey $session
    if (-not $situations.ContainsKey($situationKey)) {
        $situations[$situationKey] = @{
            situationId = $situationKey
            topologyHash = $session.topology.topologyHash
            geometryClass = $session.viewGeometry.geometryClass
            viewType = $session.viewGeometry.viewType
            topology = $session.topology
            accumulators = @{}
        }
    }
    foreach ($dim in $session.dimensions) {
        if (-not $dim.witnessPair) { continue }
        $sampleCount++
        $ruleKey = Build-RuleKey $dim
        foreach ($target in @($situations[$situationKey].accumulators, $globalAcc)) {
            if (-not $target.ContainsKey($ruleKey)) {
                $orientation = if ($dim.isHorizontalMeasurement) { 'Horizontal' } else { 'Vertical' }
                $target[$ruleKey] = @{
                    witnessPair = $dim.witnessPair
                    inferredRole = Normalize-Role $dim.inferredRole
                    measurementOrientation = $orientation
                    refAElementRole = $dim.refA.elementRole
                    refBElementRole = $dim.refB.elementRole
                    offsetSigns = @()
                    pullDirections = @()
                    stackOrders = @()
                    sampleCount = 0
                }
            }
            $bucket = $target[$ruleKey]
            $bucket.sampleCount++
            $bucket.offsetSigns += ($(if ($dim.offsetSign -ge 0) { 1 } else { -1 }))
            if ($dim.pullDirection) { $bucket.pullDirections += $dim.pullDirection }
            if ($dim.stackOrder -ge 0) { $bucket.stackOrders += [int]$dim.stackOrder }
        }
    }
}

function Convert-Bucket($bucket) {
    $pull = Mode-String $bucket.pullDirections 'Up'
    return [ordered]@{
        witnessPair = $bucket.witnessPair
        inferredRole = $bucket.inferredRole
        measurementOrientation = $bucket.measurementOrientation
        pullDirection = $pull
        offsetSign = [int](Mode-Int $bucket.offsetSigns 1)
        lockOffsetSign = $false
        stackOrder = [int](Mode-Int $bucket.stackOrders 0)
        stackGroup = (Resolve-StackGroup $pull $bucket.measurementOrientation)
        sampleCount = $bucket.sampleCount
        refAElementRole = $bucket.refAElementRole
        refBElementRole = $bucket.refBElementRole
    }
}

$situationRecords = @()
foreach ($kv in ($situations.GetEnumerator() | Sort-Object Name)) {
    $rules = @()
    foreach ($ruleKv in ($kv.Value.accumulators.GetEnumerator() | Sort-Object Name)) {
        $rules += Convert-Bucket $ruleKv.Value
    }
    $rules = $rules | Sort-Object stackGroup, stackOrder, witnessPair
    $situationRecords += [ordered]@{
        situationId = $kv.Value.situationId
        topologyHash = $kv.Value.topologyHash
        geometryClass = $kv.Value.geometryClass
        viewType = $kv.Value.viewType
        topology = [ordered]@{
            pipeRunSegmentCount = [int]$kv.Value.topology.pipeRunSegmentCount
            oletCount = [int]$kv.Value.topology.oletCount
            flangeCount = [int]$kv.Value.topology.flangeCount
            branchStubCount = [int]$kv.Value.topology.branchStubCount
            fittingCount = [int]$kv.Value.topology.fittingCount
            hasVerticalDrop = [bool]$kv.Value.topology.hasVerticalDrop
            dominantRunLengthBucket = [string]$kv.Value.topology.dominantRunLengthBucket
        }
        rules = $rules
    }
}

$globalRules = @()
foreach ($ruleKv in ($globalAcc.GetEnumerator() | Sort-Object Name)) {
    $globalRules += Convert-Bucket $ruleKv.Value
}

$document = [ordered]@{
    schemaVersion = 1
    trainedAt = (Get-Date).ToString('o')
    sessionFileCount = $files.Count
    sampleDimensionCount = $sampleCount
    outputScope = 'Match by topologyHash + geometryClass at runtime. Learned rules override offsetSign only; collectors keep witness geometry and stack slots.'
    situations = $situationRecords
    globalRules = $globalRules
}

$dir = Split-Path -Parent $OutputPath
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
($document | ConvertTo-Json -Depth 8) | Set-Content -Path $OutputPath -Encoding UTF8

Write-Host "Trained $($situationRecords.Count) situation(s) from $sampleCount learned dimension(s) across $($files.Count) session file(s)."
Write-Host "Saved: $OutputPath"
Write-Host ''
Write-Host 'Dimension Rules'
Write-Host "Trained: $($document.trainedAt)"
Write-Host "Sessions: $($document.sessionFileCount)  Samples: $($document.sampleDimensionCount)"
Write-Host "Situations: $($situationRecords.Count)"
Write-Host ''

foreach ($situation in $situationRecords) {
    Write-Host "=== $($situation.situationId) ==="
    Write-Host ("  pipes={0} olets={1} flanges={2} fittings={3} verticalDrop={4} run={5}" -f `
        $situation.topology.pipeRunSegmentCount, `
        $situation.topology.oletCount, `
        $situation.topology.flangeCount, `
        $situation.topology.fittingCount, `
        ($(if ($situation.topology.hasVerticalDrop) { 'yes' } else { 'no' })), `
        $situation.topology.dominantRunLengthBucket)
    $i = 0
    foreach ($rule in $situation.rules) {
        $i++
        Write-Host ("  {0}. {1}  {2}  {3}  pull {4}  offsetSign {5}  stack {6}:{7}  n={8}  roles {9}-{10}" -f `
            $i, $rule.witnessPair, $rule.inferredRole, $rule.measurementOrientation, $rule.pullDirection, `
            $rule.offsetSign, $rule.stackGroup, $rule.stackOrder, $rule.sampleCount, `
            $rule.refAElementRole, $rule.refBElementRole)
    }
    Write-Host ''
}

if ($globalRules.Count -gt 0) {
    Write-Host '=== Global fallbacks ==='
    foreach ($rule in $globalRules) {
        Write-Host ("- {0}  {1}  {2}  pull {3}  offsetSign {4}  stack {5}:{6}  n={7}" -f `
            $rule.witnessPair, $rule.inferredRole, $rule.measurementOrientation, $rule.pullDirection, `
            $rule.offsetSign, $rule.stackGroup, $rule.stackOrder, $rule.sampleCount)
    }
}
