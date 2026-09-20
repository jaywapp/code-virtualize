param(
    [ValidateSet('Install', 'Uninstall')][string]$Action = 'Install',
    [Parameter(Mandatory = $true)][string]$Workspace,
    [Parameter(Mandatory = $true)][string]$McpDll,
    [Parameter(Mandatory = $true)][string]$CliDll,
    [string]$SettingsPath,
    [string]$McpPath,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$workspacePath = [IO.Path]::GetFullPath($Workspace)
$settingsFile = if ($SettingsPath) { [IO.Path]::GetFullPath($SettingsPath) } else { Join-Path $workspacePath '.claude\settings.json' }
$mcpFile = if ($McpPath) { [IO.Path]::GetFullPath($McpPath) } else { Join-Path $workspacePath '.mcp.json' }
$stateFile = Join-Path $workspacePath '.code-virtualize\claude-integration-backup.json'
$hookPath = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'hook.ps1'))

function Read-JsonObject([string]$Path) {
    if (-not [IO.File]::Exists($Path)) { return [pscustomobject]@{} }
    $raw = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false))
    if ([string]::IsNullOrWhiteSpace($raw)) { return [pscustomobject]@{} }
    return $raw | ConvertFrom-Json
}

function Set-Property($Object, [string]$Name, $Value) {
    $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $Value -Force
}

function Command([string]$Event) {
    $escapedHook = $hookPath.Replace("'", "''")
    $escapedWorkspace = $workspacePath.Replace("'", "''")
    $escapedCli = ([IO.Path]::GetFullPath($CliDll)).Replace("'", "''")
    return "powershell.exe -NoProfile -File '$escapedHook' -Event $Event -Workspace '$escapedWorkspace' -CliDll '$escapedCli'"
}

if ($Action -eq 'Install') {
    $settings = Read-JsonObject $settingsFile
    $mcp = Read-JsonObject $mcpFile
    if (-not [IO.File]::Exists($stateFile)) {
        $backup = [ordered]@{
            settingsExisted = [IO.File]::Exists($settingsFile)
            settings = if ([IO.File]::Exists($settingsFile)) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsFile)) } else { $null }
            mcpExisted = [IO.File]::Exists($mcpFile)
            mcp = if ([IO.File]::Exists($mcpFile)) { [Convert]::ToBase64String([IO.File]::ReadAllBytes($mcpFile)) } else { $null }
        }
    }

    if (-not $mcp.mcpServers) { Set-Property $mcp 'mcpServers' ([pscustomobject]@{}) }
    Set-Property $mcp.mcpServers 'code-virtualize' ([ordered]@{
        type = 'stdio'; command = 'dotnet'; args = @([IO.Path]::GetFullPath($McpDll), '--workspace', $workspacePath)
    })
    if (-not $settings.hooks) { Set-Property $settings 'hooks' ([pscustomobject]@{}) }
    foreach ($event in @('SessionStart', 'PostToolUse', 'SessionEnd')) {
        $entry = [ordered]@{ matcher = ''; hooks = @([ordered]@{ type = 'command'; command = (Command $event); timeout = 30 }) }
        $existing = @($settings.hooks.$event | Where-Object { $_.hooks.command -notlike '*integrations*claude*hook.ps1*' })
        Set-Property $settings.hooks $event @($existing + $entry)
    }

    $plan = [ordered]@{ action = 'install'; dryRun = [bool]$DryRun; settingsPath = $settingsFile; mcpPath = $mcpFile }
    if (-not $DryRun) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($settingsFile)) | Out-Null
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($mcpFile)) | Out-Null
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($stateFile)) | Out-Null
        if ($backup) { [IO.File]::WriteAllText($stateFile, ($backup | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false)) }
        [IO.File]::WriteAllText($settingsFile, ($settings | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($mcpFile, ($mcp | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    }
    $plan | ConvertTo-Json -Compress
    exit 0
}

if (-not [IO.File]::Exists($stateFile)) { throw 'Code-Virtualize integration backup is missing; uninstall cannot safely restore settings.' }
$saved = Read-JsonObject $stateFile
$plan = [ordered]@{ action = 'uninstall'; dryRun = [bool]$DryRun; settingsPath = $settingsFile; mcpPath = $mcpFile }
if (-not $DryRun) {
    if ($saved.settingsExisted) { [IO.File]::WriteAllBytes($settingsFile, [Convert]::FromBase64String($saved.settings)) }
    elseif ([IO.File]::Exists($settingsFile)) { Remove-Item -LiteralPath $settingsFile -Force }
    if ($saved.mcpExisted) { [IO.File]::WriteAllBytes($mcpFile, [Convert]::FromBase64String($saved.mcp)) }
    elseif ([IO.File]::Exists($mcpFile)) { Remove-Item -LiteralPath $mcpFile -Force }
    Remove-Item -LiteralPath $stateFile -Force
}
$plan | ConvertTo-Json -Compress
