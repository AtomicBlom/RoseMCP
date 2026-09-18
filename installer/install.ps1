<#
.SYNOPSIS
    Installs a RoseMCP release package, or removes an install.

.DESCRIPTION
    Ships inside the release archive and installs from beside itself, so the whole procedure is
    "unzip, run install.ps1" with nothing to download and nothing to choose.

    One archive carries both architectures. Which one a machine gets is read off the machine rather
    than off the file somebody picked, because picking is the step people get wrong -- an x64 zip on
    an ARM64 laptop installs and runs, emulated, with no native debug host, and nothing says so. The
    live-app hosts are shared rather than duplicated: an ARM64 machine can execute ARM64, x64 and x86
    and an x64 machine the last two, so the ARM64 set is a superset and one copy serves both.

    It installs over a running instance the way deploy.ps1 promotes over one -- the inspector, then
    the tray, then the workers it owned, then any stdio server holding the same assemblies -- because
    a running exe cannot be overwritten and the failure otherwise arrives halfway through the copy,
    with the install already unusable.

    State is kept, not replaced: settings.json and Logs/ sit under the same root as the binaries and
    survive an install. Only -Uninstall -Purge removes them.

    Paths use forward slashes throughout; PowerShell accepts them on Windows.

.EXAMPLE
    ./install.ps1
    Install into %LOCALAPPDATA%/BinaryVibrance/RoseMCP and start the tray.

.EXAMPLE
    ./install.ps1 -Destination D:/Tools/RoseMcp -StartWithWindows
    Install somewhere specific and have Windows start the tray at sign-in.

.EXAMPLE
    ./install.ps1 -Uninstall -Purge
    Remove the install, its startup registration, and its settings and logs.
#>
[CmdletBinding()]
param(
    # Install root. Falls back to $env:ROSEMCP_DEPLOY_ROOT, then %LOCALAPPDATA%/BinaryVibrance/RoseMCP
    # -- the same vendor/product folder the logs already use, so an install and its state sit under
    # one root instead of two unrelated ones.
    [string] $Destination,

    # The extracted package to install from. Defaults to beside this script, which is where it is
    # when the archive has just been unzipped.
    [string] $Source,

    [int] $Port = 5077,

    # Working directory for the tray, which is where a tool call with no path looks for a solution.
    [string] $WorkspaceRoot,

    # Register the tray's http endpoint with Claude Code. Off by default: an installer that edits
    # somebody's agent configuration without being asked is doing more than installing.
    [switch] $Register,

    [switch] $StartWithWindows,

    # Install the files and stop. Nothing is started, which is what an unattended or imaging run wants.
    [switch] $NoStart,

    [switch] $Uninstall,

    # With -Uninstall, also remove settings.json. Logs go either way; settings do not, because a
    # reinstall that forgets what you configured is a worse default than one json file left behind.
    [switch] $Purge
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/RoseMcp.Deploy.ps1"

if (-not (Test-OnWindows)) { throw 'This installer is for Windows. On Linux, unpack the tar.gz and run RoseMcp.Server.' }

$uninstallKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\RoseMCP'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$runValue = 'RoseMCP'

if (-not $Source) { $Source = $PSScriptRoot }
if (-not $WorkspaceRoot) { $WorkspaceRoot = $HOME }

if (-not $Destination)
{
    $configured = $env:ROSEMCP_DEPLOY_ROOT
    $localAppData = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { Join-Path $HOME 'AppData/Local' }
    $Destination = if ($configured) { $configured } else { Join-Path $localAppData 'BinaryVibrance/RoseMCP' }
}

$Destination = $Destination.Replace('\', '/').TrimEnd('/')
$Source = $Source.Replace('\', '/').TrimEnd('/')

function Get-TargetRuntime
{
    <#
        The RID whose payload this machine gets.

        An x86 Windows install has no broker to run: nothing publishes win-x86 except the live-app
        debug hosts, which exist to match a *target* process and are not hosts in their own right.
        Saying so beats laying down a tree with no apphost that can start.
    #>
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture

    switch ($arch)
    {
        'Arm64' { return 'win-arm64' }
        'X64' { return 'win-x64' }
        default { throw "RoseMCP has no build for $arch. It ships for x64 and ARM64 Windows." }
    }
}

function Get-PackageVersion
{
    <#
        The version out of the payload itself, so the Add/Remove Programs entry cannot disagree with
        the binaries it points at. MinVer stamps this from the git tag at build time, which makes the
        package self-describing and saves carrying a version file that somebody has to keep in step.
    #>
    param([Parameter(Mandatory)][string] $PayloadRoot, [Parameter(Mandatory)][string] $SharedRoot)

    # Either root. Whether this particular assembly comes out identical for both architectures is a
    # property of the build, not something to depend on: it decides where deduplication leaves it.
    $dll = @("$PayloadRoot/RoseMcp.Server.dll", "$SharedRoot/RoseMcp.Server.dll") |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1

    if (-not $dll) { return '0.0.0' }

    $version = (Get-Item $dll).VersionInfo.ProductVersion
    if (-not $version) { return '0.0.0' }

    # Informational versions carry build metadata after a '+' (0.3.0+1a2b3c4). ARP shows this string
    # verbatim and winget compares it, so the semver core is the useful part.
    return ($version -split '\+')[0].Trim()
}

function Assert-Payload
{
    <#
        That the package actually carries what this machine needs, and that each native piece is built
        for the architecture its folder claims.

        The check is here as well as in packaging because this is the last point where the answer is
        still "the download is wrong" rather than "RoseMCP is broken". A missing XAML provider costs
        nothing until somebody debugs a XAML app weeks later; a provider built for the wrong
        architecture is worse, because it fails inside somebody else's process.
    #>
    param(
        [Parameter(Mandatory)][string] $PayloadRoot,
        [Parameter(Mandatory)][string] $SharedRoot,
        [Parameter(Mandatory)][string] $LiveAppRoot,
        [Parameter(Mandatory)][string] $Rid
    )

    # Either root: a package carrying more than one architecture keeps whatever is identical between
    # them in shared/, and an install is that folder plus this architecture's own.
    foreach ($required in 'RoseMcp.Server.exe', 'RoseMcp.Worker.exe', 'tray/RoseMcp.Tray.exe', 'inspector/RoseMcp.Inspector.exe')
    {
        $present = (Test-Path "$PayloadRoot/$required") -or (Test-Path "$SharedRoot/$required")
        if (-not $present) { throw "the package is missing $required for $Rid" }
    }

    $expected = Get-ExpectedPeMachine -Rid $Rid
    $machine = Get-PeMachine "$PayloadRoot/tray/RoseMcp.Tray.exe"
    if ($machine -ne $expected)
    {
        throw ("the $Rid payload reports machine 0x{0:X4}, expected 0x{1:X4} -- this package is built wrong." -f $machine, $expected)
    }

    foreach ($hostRid in Get-LiveAppRuntimes -Rid $Rid)
    {
        if (-not (Test-Path "$LiveAppRoot/$hostRid/RoseMcp.LiveApp.exe"))
        {
            throw "the package is missing the live-app debug host for $hostRid, which an $Rid machine can execute."
        }
    }
}

function Write-ArpEntry
{
    <#
        The Add/Remove Programs entry, per-user because the install is.

        It is what makes this uninstallable by the means people actually reach for, and it is how a
        package manager correlates an install with the version it knows about -- so DisplayName and
        Publisher here are the strings any future manifest has to match.
    #>
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $Version
    )

    $sizeKb = [int](((Get-ChildItem -LiteralPath $Root -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notmatch '\\Logs\\' } |
        Measure-Object Length -Sum).Sum) / 1KB)

    New-Item -Path $uninstallKey -Force | Out-Null

    $native = $Root.Replace('/', '\')
    $values = @{
        DisplayName = 'RoseMCP'
        DisplayVersion = $Version
        Publisher = 'Binary Vibrance'
        InstallLocation = $native
        DisplayIcon = "$native\tray\RoseMcp.Tray.exe"
        UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$native\install.ps1`" -Uninstall"
        URLInfoAbout = 'https://github.com/AtomicBlom/RoseMCP'
        NoModify = 1
        NoRepair = 1
        EstimatedSize = $sizeKb
    }

    foreach ($name in $values.Keys)
    {
        $kind = if ($values[$name] -is [int]) { 'DWord' } else { 'String' }
        New-ItemProperty -Path $uninstallKey -Name $name -Value $values[$name] -PropertyType $kind -Force | Out-Null
    }
}

function Set-StartupRegistration
{
    <#
        The per-user Run key, the same value the tray's own "Start with Windows" toggle writes -- so
        the installer setting it and the user unsetting it are the same switch rather than two that
        disagree.
    #>
    param([Parameter(Mandatory)][string] $Root)

    New-ItemProperty -Path $runKey -Name $runValue -Value "`"$($Root.Replace('/', '\'))\tray\RoseMcp.Tray.exe`"" `
        -PropertyType String -Force | Out-Null

    Write-Host '  registered to start with Windows'
}

function Remove-Install
{
    param([Parameter(Mandatory)][string] $Root)

    if (-not (Test-Path $Root)) { Write-Host "nothing installed at $Root"; return }

    Write-Host "removing $Root"
    Stop-Install -Root $Root | Out-Null

    Clear-InstallPayload -Root $Root

    # The Run value only goes if it points here. A second install elsewhere may own it, and silently
    # unregistering that one leaves a tray that never starts and nothing to explain why.
    $registered = (Get-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue).$runValue
    if ($registered -and $registered.Trim('"').StartsWith($Root.Replace('/', '\'), [System.StringComparison]::OrdinalIgnoreCase))
    {
        Remove-ItemProperty -Path $runKey -Name $runValue -ErrorAction SilentlyContinue
        Write-Host '  removed the startup registration'
    }

    Remove-Item -Path $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue

    # Logs go either way, and settings only on -Purge. They are not the same kind of thing: a log is
    # diagnostic output nothing reads again once the product is gone, and it is the part that reaches
    # gigabytes, while settings.json is what somebody chose and is worth having again if they
    # reinstall. This matches what the Inno installer's uninstall does, so which way RoseMCP was
    # installed does not change what removing it leaves behind.
    Remove-Item -LiteralPath "$Root/Logs" -Recurse -Force -ErrorAction SilentlyContinue

    if ($Purge)
    {
        Remove-Item -LiteralPath "$Root/settings.json" -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $Root -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host '  removed settings and logs'
    }
    else
    {
        Write-Host "  removed logs; kept settings.json under $Root (use -Purge to remove it)"
    }

    Write-Host 'uninstalled'
}

if ($Uninstall)
{
    Remove-Install -Root $Destination

    return
}

$rid = Get-TargetRuntime
$payload = "$Source/payload/$rid"
$shared = "$Source/payload/shared"
$liveApp = "$Source/payload/live-app"

if (-not (Test-Path $payload))
{
    throw "no $rid payload at $payload. Run this from the extracted release archive, or pass -Source."
}

Write-Host "installing RoseMCP ($rid) into $Destination"

Assert-Payload -PayloadRoot $payload -SharedRoot $shared -LiveAppRoot $liveApp -Rid $rid
$version = Get-PackageVersion -PayloadRoot $payload -SharedRoot $shared
Write-Host "  version $version"

$problems = Get-PrerequisiteProblem
foreach ($problem in $problems) { Write-Warning $problem }

$stopped = Stop-Install -Root $Destination

Clear-InstallPayload -Root $Destination
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# Shared first, then this architecture's own files. The two never name the same path -- a file is
# shared exactly when every architecture had it identical, and left in place otherwise -- so the
# order is for reading rather than for correctness.
if (Test-Path $shared)
{
    Write-Host '  copying shared payload'
    Copy-Item -Path "$shared/*" -Destination $Destination -Recurse -Force
}

Write-Host "  copying $rid payload"
Copy-Item -Path "$payload/*" -Destination $Destination -Recurse -Force

# Only the hosts this machine can execute. The package carries every architecture's, and copying the
# ones nothing can load is weight in the install for no reachable capability.
New-Item -ItemType Directory -Force -Path "$Destination/live-app" | Out-Null
foreach ($hostRid in Get-LiveAppRuntimes -Rid $rid)
{
    Write-Host "  copying live-app host $hostRid"
    Copy-Item -Path "$liveApp/$hostRid" -Destination "$Destination/live-app" -Recurse -Force
}

# Beside the payload, so uninstall works from the install rather than from an archive somebody has
# since deleted -- which is what the Add/Remove Programs entry points at.
Copy-Item -Path "$PSScriptRoot/install.ps1" -Destination "$Destination/install.ps1" -Force
Copy-Item -Path "$PSScriptRoot/RoseMcp.Deploy.ps1" -Destination "$Destination/RoseMcp.Deploy.ps1" -Force

Write-ArpEntry -Root $Destination -Version $version

if ($StartWithWindows) { Set-StartupRegistration -Root $Destination }

$endpoint = "http://127.0.0.1:$Port"

if ($NoStart)
{
    Write-Host '  not starting the tray (-NoStart)'
}
else
{
    # A tray that will not come up is worth saying loudly, but the files are already laid down and
    # correct: the usual cause is another install already holding the port, which is a thing to go
    # and look at rather than a reason to report that installing failed. An installer that says it
    # failed while the install is live is worse than the problem it is reporting, because anything
    # automating it believes the installer.
    try { Start-Tray -Root $Destination -WorkspaceRoot $WorkspaceRoot -Port $Port }
    catch { Write-Warning "  the tray did not start: $($_.Exception.Message). The install is complete; start it from $Destination/tray/RoseMcp.Tray.exe" }
}

if ($Register)
{
    if (Get-Command claude -ErrorAction SilentlyContinue)
    {
        & claude mcp add --transport http rose $endpoint
        if ($LASTEXITCODE -eq 0)
        {
            Write-Host '  registered with Claude Code'
        }
        else
        {
            Write-Warning "  claude mcp add exited $LASTEXITCODE; register it yourself with the command below."
            $Register = $false
        }

        # Cleared deliberately: a warned-and-continued failure otherwise leaves $LASTEXITCODE set and
        # nothing after this point replaces it, so the script exits non-zero after installing
        # perfectly well.
        $global:LASTEXITCODE = 0
    }
    else
    {
        Write-Warning '  claude is not on PATH; register it yourself with the command below.'
        $Register = $false
    }
}

Write-Host ''
Write-Host "installed RoseMCP $version to $Destination"
if (-not $Register) { Write-Host "register it with your agent:  claude mcp add --transport http rose $endpoint" }
if ($stopped.ServersStopped) { Write-Host 'reconnect the MCP client with /mcp; the first call reloads the solution' }
if ($problems.Count -gt 0) { Write-Host 'install the prerequisites above before expecting a solution to load' }
