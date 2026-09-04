<#
.SYNOPSIS
    Publishes RoseMCP, either over the running instance or into release zips.

.DESCRIPTION
    Two jobs that share all their plumbing:

      promote  Hand the running instance a new build. Tests first, because this is the moment a
               broken change reaches the tool you are using to work. The tray, and any stdio
               server running from the install, have to be stopped before publishing -- a running
               exe cannot be overwritten -- so this costs an /mcp reconnect and a solution reload.

      package  Build the release artifacts, one archive per runtime. Windows gets a zip carrying the
               broker, the worker, the tray and both live-app debug hosts. Linux gets a tar.gz with
               the broker and the worker only -- the tray is WinUI and the debug host is ICorDebug,
               so neither has a Linux build to ship. tar rather than zip because a zip records no
               Unix permission bits, and an apphost without +x is "permission denied" on unpack;
               for the same reason a Linux artifact has to be rolled on Linux, and packaging one
               here warns rather than shipping something broken.

    Paths use forward slashes throughout; PowerShell accepts them on Windows.

    promote installs to -Destination, or $env:ROSEMCP_DEPLOY_ROOT, or %LOCALAPPDATA%/RoseMcp.
    Nothing here assumes a particular drive; where a given machine keeps its install is that
    machine's business, not the repository's.

.EXAMPLE
    ./tools/deploy.ps1
    Promote the current architecture over the running instance.

.EXAMPLE
    ./tools/deploy.ps1 -Mode package
    Build all four artifacts under artifacts/: rosemcp-win-{x64,arm64}.zip and
    rosemcp-linux-{x64,arm64}.tar.gz.

.EXAMPLE
    ./tools/deploy.ps1 -Mode package -Runtime linux-x64, linux-arm64
    Just the Linux tarballs. This is what the release workflow runs on its Linux leg.

.EXAMPLE
    ./tools/deploy.ps1 -Destination C:/Tools/RoseMcp
    Promote to a specific install root, overriding $env:ROSEMCP_DEPLOY_ROOT.
#>
[CmdletBinding()]
param(
    [ValidateSet('promote', 'package')]
    [string] $Mode = 'promote',

    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string[]] $Runtime,

    # Install root for promote. Falls back to $env:ROSEMCP_DEPLOY_ROOT, then %LOCALAPPDATA%/RoseMcp.
    [string] $Destination,

    [int] $Port = 5077,

    # Working directory for the tray, which is where a tool call with no path looks for a solution.
    [string] $WorkspaceRoot,

    [switch] $NoRestart,

    [switch] $SkipTests
,

    # Fail rather than warn when the native XAML provider cannot be built. Always on for
    # package, because that run cuts a release and a release that quietly ships without it is
    # indistinguishable from a product bug. Available for promote so a local deploy can insist.
    [switch] $RequireXamlProvider
)

$ErrorActionPreference = 'Stop'

# $IsWindows only exists in PowerShell 6 and up. Under Windows PowerShell 5.1 it is $null, which
# reads as false -- and every platform decision below would then take the Linux branch on a Windows
# machine, quietly packaging a tray-less tarball. $env:OS has been there since NT.
$onWindows = if ($null -ne $IsWindows) { $IsWindows } else { $env:OS -eq 'Windows_NT' }

$repo = Split-Path $PSScriptRoot -Parent

# package is the run that cuts a release, so a missing provider is fatal there whether or not
# anybody remembered the switch. promote is somebody's laptop and may legitimately not have the
# MSVC toolset at all.
$xamlProviderRequired = $RequireXamlProvider -or $Mode -eq 'package'
if (-not $WorkspaceRoot) { $WorkspaceRoot = $repo }

if (-not $Destination)
{
    $configured = $env:ROSEMCP_DEPLOY_ROOT
    $localAppData = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $HOME '.local/share' }
    $Destination = if ($configured) { $configured } else { Join-Path $localAppData 'RoseMcp' }
}

$Destination = $Destination.Replace('\', '/')

function Get-HostRuntime
{
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $os = if ($onWindows) { 'win' } else { 'linux' }
    return "$os-$arch"
}

# Linux gets the broker and the worker and nothing else. The tray is WinUI 3 and the live-app host is
# ICorDebug over dbgshim, both net10.0-windows, so there is no Linux build of either to ship -- a
# Linux client runs the stdio broker, which owns its own workers when no tray is there to relay to.
function Test-WindowsRid
{
    param([string] $Rid)

    return $Rid.StartsWith('win-')
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
    foreach ($project in 'RoseMcp.Worker', 'RoseMcp.Server')
    {
        Invoke-Dotnet @('publish', "$repo/src/$project", '-c', 'Release', '-r', $Rid,
            '--self-contained', 'false', '-o', $Into) "$project ($Rid)"
    }

    if (-not (Test-WindowsRid $Rid))
    {
        Write-Host '  (no tray, no live-app hosts: both are Windows-only)'
        return
    }

    # The tray goes in a subfolder: WinUI drags in a lot, and mixing it with the server risks one
    # overwriting shared assemblies with windows-targeted variants of a different version.
    Invoke-Dotnet @('publish', "$repo/src/RoseMcp.Tray", '-c', 'Release', '-r', $Rid,
        '--self-contained', 'false', '-o', "$Into/tray") "RoseMcp.Tray ($Rid)"

    Publish-LiveAppHosts -Into $Into
}

function Publish-LiveAppHosts
{
    <#
        The debug host is published for every architecture, not just the broker's, because it has to
        match the *target* process rather than the broker: an ARM64 machine still needs an x64 host to
        debug a classic UWP app, which runs emulated. ICorDebug offers no cross-architecture path, so
        this is not an optimisation to skip -- without the x64 host, debugging a packaged app on ARM64
        fails with nothing to fall back on.

        The layout is the one LiveAppHostLauncher looks for: live-app/<rid> beside the broker, with
        each host's native XAML provider under xaml-provider/<rid> beside that host.
    #>
    param([string] $Into)

    foreach ($hostRid in 'win-x64', 'win-arm64')
    {
        $hostDir = "$Into/live-app/$hostRid"
        Invoke-Dotnet @('publish', "$repo/src/RoseMcp.LiveApp", '-c', 'Release', '-r', $hostRid,
            '--self-contained', 'false', '-o', $hostDir) "RoseMcp.LiveApp ($hostRid)"

        Copy-XamlProvider -Rid $hostRid -HostDir $hostDir -Required:$xamlProviderRequired
    }
}

function Copy-XamlProvider
{
    <#
        The provider is native C++ and is injected into the target, so it matches the target's
        architecture exactly. It needs the MSVC toolset, which a machine that only publishes managed
        code will not have -- so a missing toolset is a warning and the debugger ships without XAML
        inspection, rather than the whole deploy failing over a capability the user may not want.
    #>
    param([string] $Rid, [string] $HostDir, [switch] $Required)

    $platform = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $build = "$repo/src/RoseMcp.Xaml.Uwp.Tap/build.ps1"
    $dll = "$repo/src/RoseMcp.Xaml.Uwp.Tap/bin/$platform/Release/RoseMcp.Xaml.Uwp.Tap.dll"

    Write-Host "  building the XAML provider ($platform)"
    & pwsh -NoProfile -File $build -Platform $platform -Configuration Release *> $null

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dll))
    {
        $message = "the XAML provider could not be built for $platform; XAML inspection and hot " +
            "reload will be unavailable for $Rid targets."

        if ($Required) { throw $message }

        Write-Warning "  $message"
        return
    }

    $into = "$HostDir/xaml-provider/$Rid"
    New-Item -ItemType Directory -Force -Path $into | Out-Null
    Copy-Item $dll $into -Force
    Write-Host "  xaml provider ($platform) -> $into"
}

function Get-PeMachine
{
    <#
        The machine type out of a PE header, because Test-Path cannot tell you what is in the file.

        Copy-XamlProvider derives its destination from $Rid while build.ps1 derives its source from
        $Platform, so the two could disagree and produce a tree that looks complete and injects the
        wrong architecture into the target -- which fails inside somebody else's app, a long way from
        the packaging step that caused it.

        The layout: at 0x3C sits the offset of the "PE\0\0" signature, and the machine word is the
        two bytes straight after it.
    #>
    param([string] $Path)

    $stream = [System.IO.File]::OpenRead($Path)
    try
    {
        $reader = [System.IO.BinaryReader]::new($stream)
        $stream.Position = 0x3C
        $stream.Position = $reader.ReadInt32()
        if ($reader.ReadUInt32() -ne 0x00004550) { return $null }  # "PE\0\0"

        return $reader.ReadUInt16()
    }
    finally
    {
        $stream.Dispose()
    }
}

function Assert-WindowsPackage
{
    <#
        Every win-* package must carry both debug hosts and both native XAML providers, and each
        provider must actually be built for the architecture its folder claims.

        This exists because the failure it catches is silent. Copy-XamlProvider warns and returns
        when the MSVC toolset is missing -- right for a laptop that only publishes managed code -- and
        nothing downstream looked. release.yml asserts only that an archive exists, which is true of a
        zip with no xaml-provider directory in it at all, so the release went green, the checksums
        were computed over it, and rose_xaml_tree failed on every target of that architecture for a
        reason that was in a log nobody reads on a green run.

        Asserted here rather than at the upload step, which is as close to the cause as it can be put.
    #>
    param([string] $Stage, [string] $Rid)

    $expected = @{ 'win-x64' = 0x8664; 'win-arm64' = 0xAA64 }

    foreach ($hostRid in 'win-x64', 'win-arm64')
    {
        $hostExe = "$Stage/live-app/$hostRid/RoseMcp.LiveApp.exe"
        if (-not (Test-Path $hostExe)) { throw "$Rid package is missing $hostExe" }

        $dll = "$Stage/live-app/$hostRid/xaml-provider/$hostRid/RoseMcp.Xaml.Uwp.Tap.dll"
        if (-not (Test-Path $dll))
        {
            throw "$Rid package is missing the XAML provider at $dll. XAML inspection and hot reload " +
                "would be unavailable for $hostRid targets, and nothing else would say so. On a build " +
                "agent this is usually the MSVC ARM64 cross-toolset not being installed."
        }

        $machine = Get-PeMachine $dll
        if ($machine -ne $expected[$hostRid])
        {
            # Parenthesised before -f on purpose: -f binds tighter than +, so formatting a
            # concatenation without these brackets formats only the last piece of it and leaves
            # the placeholders in the rest sitting there as literal text.
            throw (("$Rid package has the wrong XAML provider for {0}: {1} reports machine " +
                "0x{2:X4}, expected 0x{3:X4}. It would be injected into a target of the other " +
                "architecture.") -f
                $hostRid, $dll, $machine, $expected[$hostRid])
        }
    }

    Write-Host "  layout checked: both debug hosts and both XAML providers present and correctly built"
}

function Stop-Tray
{
    $running = @(Get-Process -Name 'RoseMcp.Tray' -ErrorAction SilentlyContinue)
    if ($running.Count -eq 0) { return $false }

    Write-Host "  stopping tray (pid $($running.Id -join ', '))"
    $running | Stop-Process -Force

    # Workers exit when their broker closes their stdin. Publishing while one is still up fails,
    # because it holds RoseMcp.Worker.exe open.
    for ($i = 0; $i -lt 60; $i++)
    {
        if (-not (Get-Process -Name 'RoseMcp.Worker' -ErrorAction SilentlyContinue)) { break }
        Start-Sleep -Milliseconds 250
    }

    $stragglers = @(Get-Process -Name 'RoseMcp.Worker' -ErrorAction SilentlyContinue)
    if ($stragglers.Count -gt 0) { throw "workers did not exit: $($stragglers.Id -join ', ')" }

    return $true
}

function Stop-Servers
{
    # Stdio servers -- one per editor session, registered from the install -- hold its shared
    # assemblies open, so publishing over them fails on the first DLL. Only the ones under this
    # destination: a server running from some other install is not in the way. Their clients start
    # a fresh one on the next call or on /mcp, and the tray they relay to is being replaced anyway.
    $root = [System.IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
    $running = @(Get-Process -Name 'RoseMcp.Server' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -eq 0) { return $false }

    Write-Host "  stopping $($running.Count) stdio server(s) running from the install (pid $($running.Id -join ', '))"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500

    return $true
}

function Start-Tray
{
    $exe = "$Destination/tray/RoseMcp.Tray.exe"
    if (-not (Test-Path $exe)) { throw "no tray at $exe" }

    $process = Start-Process -FilePath $exe -PassThru -WorkingDirectory $WorkspaceRoot `
        -ArgumentList '--port', $Port, '--worker', "$Destination/RoseMcp.Worker.exe"

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
    # Promoting is for this machine; packaging is for everyone else's. The release workflow narrows
    # this with -Runtime, because the Linux tarballs have to be built on Linux to keep their
    # executable bit -- see the packaging step below.
    $Runtime = if ($Mode -eq 'package') { @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64') } else { @(Get-HostRuntime) }
}

if ($Mode -eq 'promote')
{
    if ($Runtime.Count -ne 1) { throw 'promote takes a single runtime' }

    if (-not $SkipTests)
    {
        Write-Host 'running tests before touching the live instance'
        Invoke-Dotnet @('test', "$repo/RoseMcp.slnx", '-c', 'Release') 'tests'
    }

    # Everything about stopping and restarting is about the tray and the stdio servers holding the
    # install's files open, and neither exists off Windows: there is nothing to stop, and nothing to
    # overwrite while it runs.
    $wasRunning = if ($onWindows) { Stop-Tray } else { $false }
    $stoppedServers = if ($onWindows) { Stop-Servers } else { $false }

    Publish-Tree -Rid $Runtime[0] -Into $Destination

    if (-not (Test-WindowsRid $Runtime[0])) { Write-Host '  no tray to restart on this platform' }
    elseif ($NoRestart) { Write-Host '  not restarting (-NoRestart)' }
    elseif ($wasRunning -or -not $NoRestart) { Start-Tray }

    Write-Host "promoted $($Runtime[0]) to $Destination"
    if ($wasRunning -or $stoppedServers) { Write-Host 'reconnect the MCP client with /mcp; the first call reloads the solution' }

    return
}

$artifacts = "$repo/artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

foreach ($rid in $Runtime)
{
    $stage = "$artifacts/stage/$rid"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Publish-Tree -Rid $rid -Into $stage

    if (Test-WindowsRid $rid)
    {
        $archive = "$artifacts/rosemcp-$rid.zip"
        if (Test-Path $archive) { Remove-Item $archive -Force }

        Assert-WindowsPackage -Stage $stage -Rid $rid

        Compress-Archive -Path "$stage/*" -DestinationPath $archive
    }
    else
    {
        # tar rather than zip, because a zip carries no Unix permission bits: unpacked on Linux the
        # apphost comes out without +x and RoseMcp.Server is "permission denied" before it prints
        # anything. tar records the mode, but only the mode it is given -- Windows has no execute bit
        # to record, so a tarball rolled here is just as broken and says so rather than shipping.
        if ($onWindows)
        {
            Write-Warning "  $rid packaged on Windows: the apphost will unpack without +x. Build Linux artifacts on Linux."
        }

        $archive = "$artifacts/rosemcp-$rid.tar.gz"
        if (Test-Path $archive) { Remove-Item $archive -Force }

        # -C so the paths inside are relative to the stage rather than carrying artifacts/stage/<rid>.
        & tar -czf $archive -C $stage '.'
        if ($LASTEXITCODE -ne 0) { throw "tar failed for $rid (exited $LASTEXITCODE)" }
    }

    $size = [math]::Round((Get-Item $archive).Length / 1MB)
    Write-Host "  packaged $archive (${size} MB)"
}

Write-Host "packaged $($Runtime -join ', ') into $artifacts"
