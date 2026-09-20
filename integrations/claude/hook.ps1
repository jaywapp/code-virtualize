param(
    [ValidateSet('SessionStart', 'PostToolUse', 'SessionEnd')][string]$Event,
    [Parameter(Mandatory = $true)][string]$Workspace,
    [Parameter(Mandatory = $true)][string]$CliDll
)

$ErrorActionPreference = 'Stop'
try {
    $raw = [Console]::In.ReadToEnd()
    $payload = if ([string]::IsNullOrWhiteSpace($raw)) { [pscustomobject]@{} } else { $raw | ConvertFrom-Json }
    $session = if ($payload.session_id) { [string]$payload.session_id } else { 'claude-session' }
    $arguments = @($CliDll, 'cv-update', '--workspace', [IO.Path]::GetFullPath($Workspace), '--session', $session, '--format', 'json')
    if ($Event -eq 'SessionStart') { $arguments += '--start-session' }
    elseif ($Event -eq 'SessionEnd') { $arguments += '--end-session' }
    else {
        $path = if ($payload.tool_input.file_path) { [string]$payload.tool_input.file_path } elseif ($payload.tool_input.path) { [string]$payload.tool_input.path } else { $null }
        if (-not $path) { '{"continue":true}'; exit 0 }
        $root = [IO.Path]::GetFullPath($Workspace).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
        $fullPath = [IO.Path]::GetFullPath($path)
        $prefix = $root + [IO.Path]::DirectorySeparatorChar
        if (-not $fullPath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { '{"continue":true}'; exit 0 }
        $relative = $fullPath.Substring($prefix.Length)
        $arguments += @('--changed', $relative)
    }
    $result = & dotnet @arguments 2>&1
    $exitCode = $LASTEXITCODE
    $result = $null
    if ($exitCode -notin @(0, 3)) { '{"continue":true,"systemMessage":"Code-Virtualize lifecycle update failed; continue with existing repository search/read tools."}'; exit 0 }
    '{"continue":true}'
}
catch {
    [Console]::Error.WriteLine('Code-Virtualize lifecycle hook failed; use existing repository search/read tools.')
    '{"continue":true,"systemMessage":"Code-Virtualize lifecycle hook failed; continue with existing repository search/read tools."}'
    exit 0
}
