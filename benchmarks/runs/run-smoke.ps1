param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
$seed = 20260920
$wallTimeLimitSeconds = 1200
$startedAt = [DateTimeOffset]::UtcNow
$overallStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

function Get-Sha256Text {
    param([string]$Text)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($Text)
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Get-SourceMeasure {
    param([string[]]$Paths)

    $bytes = 0
    $lines = 0
    foreach ($path in $Paths) {
        $bytes += ([System.IO.File]::ReadAllBytes($path)).Length
        $lines += ([System.IO.File]::ReadAllLines($path)).Length
    }
    return [pscustomobject]@{ bytes = $bytes; lines = $lines }
}

function Get-CommandVersion {
    param([string]$Name, [string[]]$Arguments)

    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($null -eq $command) {
        return $null
    }
    $output = & $Name @Arguments 2>&1 | Select-Object -First 1
    return [string]$output
}

function Get-ShuffledConditions {
    param([int]$RandomSeed)

    $items = [System.Collections.ArrayList]@('A', 'B', 'C', 'D')
    $random = New-Object System.Random($RandomSeed)
    for ($index = $items.Count - 1; $index -gt 0; $index--) {
        $swapIndex = $random.Next($index + 1)
        $temporary = $items[$index]
        $items[$index] = $items[$swapIndex]
        $items[$swapIndex] = $temporary
    }
    return @($items)
}

$navigationRoot = Join-Path $RepositoryRoot 'tests\fixtures\csharp\navigation'
$sharedRoot = Join-Path $RepositoryRoot 'tests\fixtures\csharp\shared'
$diffRoot = Join-Path $RepositoryRoot 'tests\fixtures\csharp\diff'
$fixturePaths = @(
    (Join-Path $navigationRoot 'Fixture.Navigation.csproj'),
    (Join-Path $navigationRoot 'Contracts.cs'),
    (Join-Path $navigationRoot 'Catalog.Partial.cs'),
    (Join-Path $navigationRoot 'CallSites.cs'),
    (Join-Path $navigationRoot 'DynamicCandidates.cs'),
    (Join-Path $sharedRoot 'LinkedHelper.cs'),
    (Join-Path $diffRoot 'vcs-base\ReviewTarget.cs'),
    (Join-Path $diffRoot 'vcs-target\ReviewTarget.cs'),
    (Join-Path $diffRoot 'session-start\ReviewTarget.cs'),
    (Join-Path $diffRoot 'session-current\ReviewTarget.cs')
)

$digestRows = foreach ($path in ($fixturePaths | Sort-Object)) {
    $relative = $path.Substring($RepositoryRoot.Length + 1).Replace('\', '/')
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    "$relative`0$hash"
}
$fixtureDigest = Get-Sha256Text (($digestRows -join "`n") + "`n")

$lspCandidates = @('csharp-ls', 'OmniSharp', 'Microsoft.CodeAnalysis.LanguageServer')
$serenaCandidates = @('serena', 'serena-mcp-server')
$availableLsp = @($lspCandidates | Where-Object { $null -ne (Get-Command $_ -ErrorAction SilentlyContinue) })
$availableSerena = @($serenaCandidates | Where-Object { $null -ne (Get-Command $_ -ErrorAction SilentlyContinue) })
$globalDotnetTools = (& dotnet tool list --global 2>$null | Out-String)
if ($globalDotnetTools -match '(?im)^\s*(csharp-ls|omnisharp)\s+') {
    $availableLsp += $Matches[1]
}

$toolAvailability = [ordered]@{
    C = [ordered]@{
        status = if ($availableLsp.Count -gt 0) { 'available' } else { 'unavailable' }
        candidatesChecked = $lspCandidates
        discovered = @($availableLsp | Select-Object -Unique)
        reason = if ($availableLsp.Count -gt 0) { $null } else { 'No callable C# LSP command or global dotnet tool was discovered in the fixed harness.' }
    }
    D = [ordered]@{
        status = if ($availableSerena.Count -gt 0) { 'available' } else { 'unavailable' }
        candidatesChecked = $serenaCandidates
        discovered = @($availableSerena | Select-Object -Unique)
        reason = if ($availableSerena.Count -gt 0) { $null } else { 'No callable Serena command or Serena MCP tool was available to this harness.' }
    }
}

$results = New-Object System.Collections.ArrayList
$rawEvents = New-Object System.Collections.ArrayList
$orders = [ordered]@{}

function Add-RawEvent {
    param(
        [string]$RunId,
        [string]$Condition,
        [string]$Event,
        [hashtable]$Payload
    )

    $eventObject = [ordered]@{
        schemaVersion = '1.0.0'
        eventId = "$RunId-$Event"
        timestamp = [DateTimeOffset]::UtcNow.ToString('o')
        runId = $RunId
        sessionIdHash = 'synthetic-fixture-session'
        condition = $Condition
        event = $Event
    }
    foreach ($key in $Payload.Keys) {
        $eventObject[$key] = $Payload[$key]
    }
    [void]$rawEvents.Add([pscustomobject]$eventObject)
}

function Invoke-PilotCell {
    param(
        [string]$TaskId,
        [string]$Condition,
        [int]$RepeatIndex,
        [string]$PairId
    )

    if ($overallStopwatch.Elapsed.TotalSeconds -ge $wallTimeLimitSeconds) {
        return [pscustomobject]@{
            runId = "$PairId-$Condition"
            taskId = $TaskId
            pairId = $PairId
            condition = $Condition
            started = $false
            status = 'not_started'
            stopReason = 'wall_time_limit'
        }
    }

    $runId = "$PairId-$Condition"
    Add-RawEvent -RunId $runId -Condition $Condition -Event 'run_started' -Payload ([ordered]@{
        taskId = $TaskId
        pairId = $PairId
        repeatIndex = $RepeatIndex
        harnessId = 'powershell-smoke-v1'
        cacheState = 'not_applicable'
    })

    $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $status = 'succeeded'
    $errorCategory = $null
    $qualityPassed = $null
    $primaryToolUsed = $false
    $toolCalls = 0
    $readCalls = 0
    $grepCalls = 0
    $otherCalls = 0
    $fullReadCalls = 0
    $partialReadCalls = 0
    $sourceBytes = 0
    $sourceLines = 0

    try {
        if ($Condition -eq 'C') {
            if ($toolAvailability.C.status -ne 'available') {
                $status = 'infra_failed'
                $errorCategory = 'primary_tool_unavailable'
            }
            else {
                throw 'C# LSP was discovered but this smoke harness has no approved client adapter.'
            }
        }
        elseif ($Condition -eq 'D') {
            if ($toolAvailability.D.status -ne 'available') {
                $status = 'infra_failed'
                $errorCategory = 'primary_tool_unavailable'
            }
            else {
                throw 'Serena was discovered but this smoke harness has no approved client adapter.'
            }
        }
        elseif ($TaskId -eq 'NAV' -and $Condition -eq 'A') {
            $searchOutput = & rg -n 'Load|partial class Catalog|BuildLabel|Worker|Run|Activator|ServiceKey|MemberKey|Compile Include' $navigationRoot $sharedRoot 2>&1 | Out-String
            $grepCalls++
            $navigationFiles = @(
                (Join-Path $navigationRoot 'Fixture.Navigation.csproj'),
                (Join-Path $navigationRoot 'Contracts.cs'),
                (Join-Path $navigationRoot 'Catalog.Partial.cs'),
                (Join-Path $navigationRoot 'CallSites.cs'),
                (Join-Path $navigationRoot 'DynamicCandidates.cs'),
                (Join-Path $sharedRoot 'LinkedHelper.cs')
            )
            $readOutput = ($navigationFiles | ForEach-Object { [System.IO.File]::ReadAllText($_) }) -join "`n"
            $readCalls += $navigationFiles.Count
            $fullReadCalls += $navigationFiles.Count
            $measure = Get-SourceMeasure $navigationFiles
            $sourceBytes += $measure.bytes
            $sourceLines += $measure.lines
            $combined = $searchOutput + $readOutput
            $qualityPassed = $combined -match 'public void Load\(string value\)' -and
                $combined -match 'public void Load\(string value, int retryCount\)' -and
                $combined -match 'private T Load<T>\(T value\)' -and
                $combined -match 'void ILoader\.Load\(string value\)' -and
                $combined -match 'partial class Catalog' -and
                $combined -match 'BuildLabel' -and
                $combined -match 'Activator\.CreateInstance' -and
                $combined -match 'Fixture\.Navigation\.IWorker' -and
                $combined -match 'MemberKey => "Run"' -and
                $combined -match 'Linked\\LinkedHelper\.cs'
        }
        elseif ($TaskId -eq 'NAV' -and $Condition -eq 'B') {
            $candidateOutput = & rg -l 'Load|partial class Catalog|BuildLabel|Worker|Run|Activator|ServiceKey|MemberKey|Compile Include' $navigationRoot $sharedRoot 2>&1 | Out-String
            $grepCalls++
            $declarationOutput = & rg -n 'public void Load|private T Load<T>|void ILoader\.Load|partial class Catalog' (Join-Path $navigationRoot 'Contracts.cs') (Join-Path $navigationRoot 'Catalog.Partial.cs') 2>&1 | Out-String
            $grepCalls++
            $dynamicOutput = & rg -n 'Activator\.CreateInstance|ServiceKey|MemberKey' (Join-Path $navigationRoot 'DynamicCandidates.cs') 2>&1 | Out-String
            $grepCalls++
            $linkOutput = & rg -n 'Compile Include|Link>|BuildLabel' (Join-Path $navigationRoot 'Fixture.Navigation.csproj') (Join-Path $sharedRoot 'LinkedHelper.cs') 2>&1 | Out-String
            $grepCalls++
            $linkedLines = [System.IO.File]::ReadAllLines((Join-Path $sharedRoot 'LinkedHelper.cs')) | Select-Object -Skip 2 -First 5
            $contractLines = [System.IO.File]::ReadAllLines((Join-Path $navigationRoot 'Contracts.cs')) | Select-Object -Skip 7 -First 18
            $readCalls += 2
            $partialReadCalls += 2
            $partialText = (($linkedLines -join "`n") + "`n" + ($contractLines -join "`n"))
            $sourceBytes += [System.Text.Encoding]::UTF8.GetByteCount($partialText)
            $sourceLines += $linkedLines.Count + $contractLines.Count
            $combined = $candidateOutput + $declarationOutput + $dynamicOutput + $linkOutput + $partialText
            $qualityPassed = $combined -match 'public void Load\(string value\)' -and
                $combined -match 'public void Load\(string value, int retryCount\)' -and
                $combined -match 'private T Load<T>\(T value\)' -and
                $combined -match 'void ILoader\.Load\(string value\)' -and
                $combined -match 'partial class Catalog' -and
                $combined -match 'BuildLabel' -and
                $combined -match 'Activator\.CreateInstance' -and
                $combined -match 'ServiceKey' -and
                $combined -match 'MemberKey' -and
                $combined -match 'Linked\\LinkedHelper\.cs'
        }
        elseif ($TaskId -eq 'DIFF' -and $Condition -eq 'A') {
            $vcsOutput = & git -c core.autocrlf=false -c core.safecrlf=false diff --no-index -- (Join-Path $diffRoot 'vcs-base\ReviewTarget.cs') (Join-Path $diffRoot 'vcs-target\ReviewTarget.cs') 2>$null | Out-String
            $otherCalls++
            $sessionOutput = & git -c core.autocrlf=false -c core.safecrlf=false diff --no-index -- (Join-Path $diffRoot 'session-start\ReviewTarget.cs') (Join-Path $diffRoot 'session-current\ReviewTarget.cs') 2>$null | Out-String
            $otherCalls++
            $diffFiles = @(
                (Join-Path $diffRoot 'vcs-base\ReviewTarget.cs'),
                (Join-Path $diffRoot 'vcs-target\ReviewTarget.cs'),
                (Join-Path $diffRoot 'session-start\ReviewTarget.cs'),
                (Join-Path $diffRoot 'session-current\ReviewTarget.cs')
            )
            $readOutput = ($diffFiles | ForEach-Object { [System.IO.File]::ReadAllText($_) }) -join "`n"
            $readCalls += $diffFiles.Count
            $fullReadCalls += $diffFiles.Count
            $measure = Get-SourceMeasure $diffFiles
            $sourceBytes += $measure.bytes
            $sourceLines += $measure.lines
            $qualityPassed = $vcsOutput -match '-\s*return "base";' -and
                $vcsOutput -match '\+\s*return "dirty-before-session";' -and
                $vcsOutput -match '\+\s*public static string AddedDuringSession' -and
                $vcsOutput -match '-\s*public static string Removed' -and
                $sessionOutput -match '\+\s*public static string AddedDuringSession' -and
                $sessionOutput -match '-\s*public static string Removed' -and
                $sessionOutput -notmatch '-\s*return "dirty-before-session";' -and
                $readOutput -match 'public static string Removed\(\)'
        }
        elseif ($TaskId -eq 'DIFF' -and $Condition -eq 'B') {
            $vcsOutput = & git -c core.autocrlf=false -c core.safecrlf=false diff --no-index --unified=1 -- (Join-Path $diffRoot 'vcs-base\ReviewTarget.cs') (Join-Path $diffRoot 'vcs-target\ReviewTarget.cs') 2>$null | Out-String
            $otherCalls++
            $sessionOutput = & git -c core.autocrlf=false -c core.safecrlf=false diff --no-index --unified=1 -- (Join-Path $diffRoot 'session-start\ReviewTarget.cs') (Join-Path $diffRoot 'session-current\ReviewTarget.cs') 2>$null | Out-String
            $otherCalls++
            $baseLines = [System.IO.File]::ReadAllLines((Join-Path $diffRoot 'session-start\ReviewTarget.cs')) | Select-Object -Skip 9 -First 4
            $readCalls++
            $partialReadCalls++
            $baseText = $baseLines -join "`n"
            $sourceBytes += [System.Text.Encoding]::UTF8.GetByteCount($baseText)
            $sourceLines += $baseLines.Count
            $qualityPassed = $vcsOutput -match '-\s*return "base";' -and
                $vcsOutput -match '\+\s*return "dirty-before-session";' -and
                $vcsOutput -match '\+\s*public static string AddedDuringSession' -and
                $vcsOutput -match '-\s*public static string Removed' -and
                $sessionOutput -match '\+\s*public static string AddedDuringSession' -and
                $sessionOutput -match '-\s*public static string Removed' -and
                $sessionOutput -notmatch '-\s*return "dirty-before-session";' -and
                $baseText -match 'public static string Removed\(\)'
        }

        if (($Condition -eq 'A' -or $Condition -eq 'B') -and -not $qualityPassed) {
            $status = 'quality_failed'
            $errorCategory = 'fixture_expectation_mismatch'
        }
    }
    catch {
        [Console]::Error.WriteLine(('Harness cell failed ({0}/{1}): {2}' -f $TaskId, $Condition, $_.Exception.Message))
        if ($status -eq 'succeeded') {
            $status = 'infra_failed'
            $errorCategory = 'harness_error'
        }
    }
    finally {
        $stopwatch.Stop()
    }

    $toolCalls = $readCalls + $grepCalls + $otherCalls
    if ($toolCalls -gt 0) {
        Add-RawEvent -RunId $runId -Condition $Condition -Event 'tool_summary' -Payload ([ordered]@{
            readCalls = $readCalls
            grepCalls = $grepCalls
            otherCalls = $otherCalls
            successfulToolCalls = $toolCalls
            failedToolCalls = 0
            fullReadCalls = $fullReadCalls
            partialReadCalls = $partialReadCalls
            sourceBytes = $sourceBytes
            sourceLines = $sourceLines
            contentTokens = $null
            tokenMeasurement = 'unavailable'
        })
    }
    if ($null -ne $qualityPassed) {
        Add-RawEvent -RunId $runId -Condition $Condition -Event 'quality_result' -Payload ([ordered]@{
            judgeId = 'fixture-expected-v1'
            passed = [bool]$qualityPassed
        })
    }
    Add-RawEvent -RunId $runId -Condition $Condition -Event 'run_finished' -Payload ([ordered]@{
        status = $status
        wallTimeMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        primaryToolUsed = $primaryToolUsed
        errorCategory = $errorCategory
    })

    return [pscustomobject]@{
        runId = $runId
        taskId = $TaskId
        pairId = $PairId
        condition = $Condition
        started = $true
        status = $status
        qualityPassed = $qualityPassed
        primaryToolUsed = $primaryToolUsed
        wallTimeMs = [Math]::Round($stopwatch.Elapsed.TotalMilliseconds, 3)
        toolCalls = $toolCalls
        readCalls = $readCalls
        grepCalls = $grepCalls
        otherCalls = $otherCalls
        fullReadCalls = $fullReadCalls
        partialReadCalls = $partialReadCalls
        sourceBytes = $sourceBytes
        sourceLines = $sourceLines
        modelTokens = $null
        cost = $null
        errorCategory = $errorCategory
    }
}

$tasks = @('NAV', 'DIFF')
for ($taskIndex = 0; $taskIndex -lt $tasks.Count; $taskIndex++) {
    $taskId = $tasks[$taskIndex]
    $pairId = ('pair-{0}-{1}' -f ($taskIndex + 1), $taskId.ToLowerInvariant())
    $order = Get-ShuffledConditions ($seed + $taskIndex)
    $orders[$pairId] = $order
    foreach ($condition in $order) {
        [void]$results.Add((Invoke-PilotCell -TaskId $taskId -Condition $condition -RepeatIndex 1 -PairId $pairId))
    }
}

$overallStopwatch.Stop()
$metadata = [ordered]@{
    schemaVersion = 'pilot-runtime-v1'
    protocolRevision = 'task-001-v1'
    experimentPhase = 'pilot'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    startedAt = $startedAt.ToString('o')
    randomizationSeed = $seed
    wallTimeLimitSeconds = $wallTimeLimitSeconds
    actualWallTimeMs = [Math]::Round($overallStopwatch.Elapsed.TotalMilliseconds, 3)
    fixtureDigestAlgorithm = 'sha256(sorted(relative-path NUL file-sha256) LF)'
    fixtureDigest = $fixtureDigest
    repository = [ordered]@{
        alias = 'code-virtualize-local-synthetic'
        commit = (& git -C $RepositoryRoot rev-parse HEAD).Trim()
        branch = (& git -C $RepositoryRoot branch --show-current).Trim()
        startDirty = $true
    }
    permissions = [ordered]@{
        source = 'repository synthetic fixtures only'
        personalOrExistingSessionLogs = 'denied'
        network = 'not_used'
        packageInstallation = 'denied'
        paidModelOrApi = 'denied'
        writes = 'benchmarks/runs and benchmarks/results only'
    }
    versions = [ordered]@{
        powershell = $PSVersionTable.PSVersion.ToString()
        ripgrep = Get-CommandVersion 'rg' @('--version')
        git = Get-CommandVersion 'git' @('--version')
        dotnet = Get-CommandVersion 'dotnet' @('--version')
    }
    toolAvailability = $toolAvailability
    conditionOrders = $orders
    runs = @($results)
    measurementAvailability = [ordered]@{
        modelTokens = 'unavailable'
        providerCost = 'unavailable'
        toolResultContentTokens = 'unavailable'
        sourceBytesAndLines = 'measured'
        wallTime = 'measured'
    }
}

$runtimePath = Join-Path $PSScriptRoot 'pilot-runtime.json'
$eventsPath = Join-Path $PSScriptRoot 'pilot-raw.jsonl'
[System.IO.File]::WriteAllText($runtimePath, ($metadata | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))
$eventLines = @($rawEvents | ForEach-Object { $_ | ConvertTo-Json -Compress -Depth 8 })
[System.IO.File]::WriteAllLines($eventsPath, $eventLines, (New-Object System.Text.UTF8Encoding($false)))

$metadata | ConvertTo-Json -Depth 12
