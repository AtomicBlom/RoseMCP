<#
.SYNOPSIS
    Publishes RoslynHost, either over the running instance or into release zips.

.DESCRIPTION
    Two jobs that share all their plumbing:

      promote  Hand the running instance a new build. Tests first, because this is the moment a
               broken change reaches the tool you are using to work. The tray has to be stopped
               before publishing -- a running exe cannot be overwritten -- so this costs an /mcp
               reconnect and a solution reload.

      package  Build the release artifacts, one zip per architecture.

    Paths use forward slashes throughout; PowerShell accepts them on Windows.

.EXAMPLE
    ./tools/deploy.ps1
    Promote the current architecture over the running instance.

.EXAMPLE
    ./tools/deploy.ps1 -Mode package
    Build roslynhost-win-x64.zip and roslynhost-win-arm64.zip under artifacts/.
#>
[CmdletBinding()]
param(
    [ValidateSet('promote', 'package')]
    [string] $Mode = 'promote',

    [ValidateSet('win-x64', 'win-arm64')]
    [string[]] $Runtime,

    [string] $Destination = 'D:/Tools/RoslynHost',

    [int] $Port = 5077,

    # Working directory for the tray, which is where a tool call with no path looks for a solution.
    [string] $WorkspaceRoot,

    [switch] $NoRestart,

    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $WorkspaceRoot) { $WorkspaceRoot = $repo }

function Get-HostRuntime
{
    if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { return 'win-arm64' }
    return 'win-x64'
}

function Invoke-Dotnet
{
    param([string[]] $Arguments, [string] $What)

    & dotnet @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$What failed (dotnet exited $LASTEXITCODE)" }
}

function Publish-Tree
{
    param([string] $Rid, [string] $Into)

    Write-Host "  publishing $Rid -> $Into"

    # Worker and broker land flat together so the broker finds the worker beside itself, which is
    # its first lookup and the one that needs no configuration.
    foreach ($project in 'RoslynMcp.Worker', 'RoslynMcp.Server')
    {
        Invoke-Dotnet @('publish', "$repo/src/$project", '-c', 'Release', '-r', $Rid,
            '--self-contained', 'false', '-o', $Into) "$project ($Rid)"
    }

    # The tray goes in a subfolder: WinUI drags in a lot, and mixing it with the server risks one
    # overwriting shared assemblies with windows-targeted variants of a different version.
    Invoke-Dotnet @('publish', "$repo/src/RoslynMcp.Tray", '-c', 'Release', '-r', $Rid,
        '--self-contained', 'false', '-o', "$Into/tray") "RoslynMcp.Tray ($Rid)"
}

function Stop-Tray
{
    $running = @(Get-Process -Name 'RoslynMcp.Tray' -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return $false }

    Write-Host "  stopping tray (pid $($running.Id -join ', '))"
    $running | Stop-Process -Force

    # Workers exit when their broker closes their stdin. Publishing while one is still up fails,
    # because it holds RoslynMcp.Worker.exe open.
    for ($i = 0; $i -lt 60; $i++)
    {
        if (-not (Get-Process -Name 'RoslynMcp.Worker' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 250
    }

    $stragglers = @(Get-Process -Name 'RoslynMcp.Worker' -ErrorAction SilentlyContinue)
    if ($stragglers.Count -gt 0) { throw "workers did not exit: $($stragglers.Id -join ', ')" }

    return $true
}

function Start-Tray
{
    $exe = "$Destination/tray/RoslynMcp.Tray.exe"
    if (-not (Test-Path $exe)) { throw "no tray at $exe" }

    $process = Start-Process -FilePath $exe -PassThru -WorkingDirectory $WorkspaceRoot `
        -ArgumentList '--port', $Port, '--worker', "$Destination/RoslynMcp.Worker.exe"

    Write-Host "  started tray pid $($process.Id) (workspace root $WorkspaceRoot)"

    for ($i = 0; $i -lt 60; $i++)
    {
        Start-Sleep -Milliseconds 250
        try
        {
            $null = Invoke-WebRequest "http://127.0.0.1:$Port/admin/workspaces" -UseBasicParsing -TimeoutSec 2
            Write-Host "  endpoint answering on http://127.0.0.1:$Port/"
            return
        }
        catch { }
    }

    throw "tray started but never answered on port $Port"
}

if (-not $Runtime)
{
    # Promoting is for this machine; packaging is for everyone else's.
    $Runtime = if ($Mode -eq 'package') { @('win-x64', 'win-arm64') } else { @(Get-HostRuntime) }
}

if ($Mode -eq 'promote')
{
    if ($Runtime.Count -ne 1) { throw 'promote takes a single runtime' }

    if (-not $SkipTests)
    {
        Write-Host 'running tests before touching the live instance'
        Invoke-Dotnet @('test', "$repo/RoslynHost.slnx", '-c', 'Release') 'tests'
    }

    $wasRunning = Stop-Tray
    Publish-Tree -Rid $Runtime[0] -Into $Destination

    if ($NoRestart) { Write-Host '  not restarting (-NoRestart)' }
    elseif ($wasRunning -or -not $NoRestart) { Start-Tray }

    Write-Host "promoted $($Runtime[0]) to $Destination"
    if ($wasRunning) { Write-Host 'reconnect the MCP client with /mcp; the first call reloads the solution' }

    return
}

$artifacts = "$repo/artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

foreach ($rid in $Runtime)
{
    $stage = "$artifacts/stage/$rid"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Publish-Tree -Rid $rid -Into $stage

    $zip = "$artifacts/roslynhost-$rid.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }

    Compress-Archive -Path "$stage/*" -DestinationPath $zip
    $size = [math]::Round((Get-Item $zip).Length / 1MB)
    Write-Host "  packaged $zip (${size} MB)"
}

Write-Host "packaged $($Runtime -join ', ') into $artifacts"
