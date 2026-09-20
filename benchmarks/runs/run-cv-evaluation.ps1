param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
$resultPath = Join-Path $RepositoryRoot 'benchmarks\results\cv-result.json'
$manifestPath = Join-Path $RepositoryRoot 'benchmarks\results\cv-run-manifest.json'
$schemaPath = Join-Path $RepositoryRoot 'benchmarks\result.schema.json'
$runnerProject = Join-Path $RepositoryRoot 'benchmarks\runs\CvEvaluationRunner.csproj'

& dotnet restore (Join-Path $RepositoryRoot 'CodeVirtualize.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Solution locked restore failed.' }
& dotnet build (Join-Path $RepositoryRoot 'CodeVirtualize.sln') -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Solution Release build failed.' }
& dotnet restore $runnerProject --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Benchmark runner locked restore failed.' }
& dotnet build $runnerProject -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Benchmark runner Release build failed.' }
& dotnet run --project $runnerProject -c Release --no-build -- --repository $RepositoryRoot --result $resultPath --manifest $manifestPath
if ($LASTEXITCODE -ne 0) { throw 'Benchmark execution failed.' }
& dotnet run --project $runnerProject -c Release --no-build -- --result $resultPath --manifest $manifestPath --verify-only
if ($LASTEXITCODE -ne 0) { throw 'Benchmark arithmetic verification failed.' }

$pwsh = Get-Command pwsh -ErrorAction SilentlyContinue
if ($null -eq $pwsh) { throw 'pwsh is required for result.schema validation.' }
$validatorPath = Join-Path ([IO.Path]::GetTempPath()) ('cv-schema-' + [guid]::NewGuid().ToString('N') + '.ps1')
$validator = @"
param([string]`$JsonPath, [string]`$SchemaPath)
`$ErrorActionPreference = 'Stop'
`$json = [IO.File]::ReadAllText(`$JsonPath)
if (-not (`$json | Test-Json -SchemaFile `$SchemaPath)) { throw 'result.schema validation failed.' }
"@
try {
    [IO.File]::WriteAllText($validatorPath, $validator, (New-Object Text.UTF8Encoding($false)))
    & $pwsh.Source -NoProfile -File $validatorPath -JsonPath $resultPath -SchemaPath $schemaPath
    if ($LASTEXITCODE -ne 0) { throw 'Benchmark result.schema validation failed.' }
}
finally {
    if (Test-Path -LiteralPath $validatorPath) { Remove-Item -LiteralPath $validatorPath -Force }
}

Write-Output 'TASK-016 benchmark, arithmetic, and schema verification passed.'