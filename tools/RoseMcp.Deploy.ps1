<#
.SYNOPSIS
    Everything deploy.ps1 and install.ps1 both have to agree about.

.DESCRIPTION
    Dot-sourced rather than imported as a module, so a caller can use these against its own
    $Destination without a module manifest in the way.

    Two scripts install RoseMCP: deploy.ps1 promotes a build from source over the running instance,
    and install.ps1 lays down a release package. They replace the same files, in the same place,
    while the same processes are holding them open -- so the part that decides what to stop and how
    long to wait belongs to neither of them. A second copy of that logic is a second set of rules
    for when a worker has really exited, and only one of them gets fixed.

    Every function takes the install root explicitly. Nothing here reads a script-scope variable,
    because the two callers name theirs differently and a function that reaches outward works in one
    of them by luck.
#>

# $IsWindows only exists in PowerShell 6 and up. Under Windows PowerShell 5.1 it is $null, which
# reads as false -- so every platform decision would take the Linux branch on a Windows machine.
# $env:OS has been there since NT.
$script:OnWindows = if ($null -ne $IsWindows) { $IsWindows } else { $env:OS -eq 'Windows_NT' }

function Test-OnWindows
{
    return $script:OnWindows
}

function Get-HostRuntime
{
    <#
        The RID of the machine this is running on.

        OSArchitecture rather than ProcessArchitecture: PowerShell itself may be running emulated,
        and what matters is what the machine can execute natively, not what the shell happens to be.
    #>
    $arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }
    $os = if ($script:OnWindows) { 'win' } else { 'linux' }

    return "$os-$arch"
}

function Test-WindowsRid
{
    param([Parameter(Mandatory)][string] $Rid)

    return $Rid.StartsWith('win-')
}

function Get-LiveAppRuntimes
{
    <#
        The live-app hosts an install for $Rid needs, which is exactly the set of architectures that
        machine can execute.

        ICorDebug offers no cross-architecture path, so the host must match the *target* process rather
        than the broker -- but which targets can exist at all is a property of the machine, and the
        relationship is asymmetric. ARM64 Windows runs ARM64 natively and emulates x64 and x86, so all
        three are reachable there and all three ship. An x64 machine runs x64 and, through WOW64, x86;
        it cannot execute an ARM64 binary under any emulation, so an ARM64 host in an x64 install is
        weight nothing can load.

        x86 is not a legacy case here. It is the default platform of the modern UWP project template,
        so it is the architecture an ordinary new UWP app is built and registered as.

        The ARM64 set is a superset of the x64 set, which is what lets one package carry the hosts for
        both without duplicating any of them.
    #>
    param([Parameter(Mandatory)][string] $Rid)

    switch ($Rid)
    {
        'win-arm64' { return @('win-arm64', 'win-x64', 'win-x86') }
        'win-x64' { return @('win-x64', 'win-x86') }
        default { return @($Rid) }
    }
}

function Get-InstalledProcess
{
    <#
        Only the processes running out of the install being replaced. A development tray on another
        port, or an editor session served by some other install, is not in the way of this publish --
        and stopping it, or waiting for it, is this script reaching outside what it was asked to
        replace. Matching by name alone did both: a deploy to one install died on "workers did not
        exit" naming a worker belonging to another one, which cannot exit because nothing asked it to.

        Both sides go through GetFullPath, and that is not belt and braces. A process reports the path
        it was launched with, so a tray started from an install root written in 8.3 short form -- or
        reached through a subst drive or a symlink -- reports that form, while the root being compared
        against has been normalised to the long one. The prefix then fails to match, nothing is found
        to stop, and the install proceeds to fail partway through the copy on a file the tray still
        holds open. Canonicalising only one side is what makes that silent rather than loud.
    #>
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][string] $Root
    )

    $full = [System.IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'

    return @(Get-Process -Name $Name -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Path -and [System.IO.Path]::GetFullPath($_.Path).StartsWith($full, [System.StringComparison]::OrdinalIgnoreCase)
        })
}

function Stop-TrackedProcess
{
    <#
        Kills processes and waits for each to actually be gone.

        Stop-Process signals a termination and returns; the process is still holding its exe and every
        DLL it mapped for a moment afterwards. Copying over the tree in that window fails on whichever
        file the loser happens to still own -- for the tray, reliably H.NotifyIcon.dll -- which reads
        like a permissions problem rather than a race.

        The drain loop that follows a tray kill used to hide this, because waiting for workers took
        long enough for the tray to finish dying. An install with no workers loaded has nothing to
        wait for and hits the window every time.
    #>
    param(
        [System.Diagnostics.Process[]] $Process,
        [int] $TimeoutSeconds = 10
    )

    if (-not $Process -or $Process.Count -eq 0) { return }

    $Process | Stop-Process -Force

    foreach ($one in $Process)
    {
        # A process that has already gone throws rather than returning, and that is the outcome asked
        # for either way.
        try { $null = $one.WaitForExit($TimeoutSeconds * 1000) } catch { }
    }
}

function Stop-Inspector
{
    <#
        Called before the tray: the inspector holds no workers and nothing waits on it, but its exe is
        in the tree about to be overwritten and a running one fails the copy. It is not restarted
        afterwards -- it is opened from the tray, and reopening a window somebody closed is the
        installer deciding what they were doing.
    #>
    param([Parameter(Mandatory)][string] $Root)

    $running = Get-InstalledProcess -Name 'RoseMcp.Inspector' -Root $Root
    if ($running.Count -eq 0) { return }

    Write-Host "  stopping inspector (pid $($running.Id -join ', '))"
    Stop-TrackedProcess -Process $running
}

function Stop-Tray
{
    <#
        Returns whether a tray was running, so a caller can restart only what it stopped.

        Workers exit when their broker closes their stdin, which takes a moment after the tray dies.
        Copying while one is still up fails, because it holds RoseMcp.Worker.exe open.
    #>
    param(
        [Parameter(Mandatory)][string] $Root,
        [int] $DrainSeconds = 15
    )

    $running = Get-InstalledProcess -Name 'RoseMcp.Tray' -Root $Root
    if ($running.Count -eq 0) { return $false }

    Write-Host "  stopping tray (pid $($running.Id -join ', '))"
    Stop-TrackedProcess -Process $running

    Wait-Worker -Root $Root -TimeoutSeconds $DrainSeconds

    return $true
}

function Wait-Worker
{
    <#
        Workers are the one thing here that cannot simply be killed: each owns an MSBuildWorkspace and
        exits on its own once its broker lets go. Waiting is what makes the difference between copying
        over a released file and failing on RoseMcp.Worker.exe.
    #>
    param(
        [Parameter(Mandatory)][string] $Root,
        [int] $TimeoutSeconds = 15
    )

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if ((Get-InstalledProcess -Name 'RoseMcp.Worker' -Root $Root).Count -eq 0) { return }
        Start-Sleep -Milliseconds 250
    }

    $stragglers = Get-InstalledProcess -Name 'RoseMcp.Worker' -Root $Root
    if ($stragglers.Count -gt 0) { throw "workers did not exit: $($stragglers.Id -join ', ')" }
}

function Stop-Server
{
    <#
        Stdio servers -- one per editor session, registered from the install -- hold its shared
        assemblies open, so copying over them fails on the first DLL. Only the ones under this root: a
        server running from some other install is not in the way. Their clients start a fresh one on
        the next call or on /mcp, and the tray they relay to is being replaced anyway.

        Returns whether any were stopped, so a caller can tell the user to reconnect.
    #>
    param([Parameter(Mandatory)][string] $Root)

    $running = Get-InstalledProcess -Name 'RoseMcp.Server' -Root $Root
    if ($running.Count -eq 0) { return $false }

    Write-Host "  stopping $($running.Count) stdio server(s) running from the install (pid $($running.Id -join ', '))"
    Stop-TrackedProcess -Process $running

    return $true
}

function Stop-Install
{
    <#
        Everything running out of an install root, in the order that lets the copy succeed: the
        inspector first because nothing waits on it, then the tray, then the workers it owned, then
        any stdio server holding the same assemblies.

        Returns what was stopped, so the caller can restart the tray only if there was one and can say
        whether MCP clients need reconnecting.
    #>
    param(
        [Parameter(Mandatory)][string] $Root,
        [int] $DrainSeconds = 15
    )

    if (-not (Test-OnWindows) -or -not (Test-Path $Root))
    {
        return [PSCustomObject]@{ TrayWasRunning = $false; ServersStopped = $false }
    }

    Stop-Inspector -Root $Root
    $trayWasRunning = Stop-Tray -Root $Root -DrainSeconds $DrainSeconds
    $serversStopped = Stop-Server -Root $Root

    # A tray that was never running may still have left workers behind -- a stdio server owns its own
    # when no tray is there to relay to, and those hold the same worker exe.
    if (-not $trayWasRunning) { Wait-Worker -Root $Root -TimeoutSeconds $DrainSeconds }

    return [PSCustomObject]@{ TrayWasRunning = $trayWasRunning; ServersStopped = $serversStopped }
}

function Start-Tray
{
    <#
        Starts the tray out of an install and waits for its endpoint to answer, so a caller that
        reports success has seen the thing serve rather than merely seen a process start.
    #>
    param(
        [Parameter(Mandatory)][string] $Root,
        [Parameter(Mandatory)][string] $WorkspaceRoot,
        [int] $Port = 5077,
        [int] $TimeoutSeconds = 15
    )

    $exe = "$Root/tray/RoseMcp.Tray.exe"
    if (-not (Test-Path $exe)) { throw "no tray at $exe" }

    $process = Start-Process -FilePath $exe -PassThru -WorkingDirectory $WorkspaceRoot `
        -ArgumentList '--port', $Port, '--worker', "$Root/RoseMcp.Worker.exe"

    Write-Host "  started tray pid $($process.Id) (workspace root $WorkspaceRoot)"

    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTime]::UtcNow -lt $deadline)
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

function Get-PrerequisiteProblem
{
    <#
        What an install needs that it cannot carry, as a list of problems -- empty when there are
        none.

        Strings rather than warnings, because the two installers surface them differently: install.ps1
        writes them to the console, and the Inno installer reads them back out of a redirected file to
        put in a dialog. Neither of them knows what a prerequisite is; this does.

        Nothing here is fatal. Somebody installing onto a machine they are about to finish setting up
        is doing a reasonable thing, and an installer that refuses is wrong more often than they are.
    #>
    $problems = @()

    # The SDK, not the runtime. The worker runs a design-time build through
    # Microsoft.CodeAnalysis.Workspaces.MSBuild, which locates MSBuild and the targets out of an SDK
    # installation -- with the runtime alone every project loads with no references and reports
    # thousands of errors about System.Object being undefined, which reads as a broken solution
    # rather than a missing tool.
    $sdks = @()
    try { $sdks = @(& dotnet --list-sdks 2>$null) } catch { }

    if ($sdks.Count -eq 0)
    {
        $problems += 'No .NET SDK was found. RoseMCP needs the SDK, not just the runtime: without it every project loads with no references and reports thousands of errors about System.Object. Install it from https://dotnet.microsoft.com/download'
    }
    elseif (-not ($sdks | Where-Object { $_ -match '^10\.' }))
    {
        $found = ($sdks | ForEach-Object { ($_ -split ' ')[0] }) -join ', '
        $problems += "No .NET 10 SDK was found. RoseMCP targets net10.0 and its worker needs a matching SDK. Found: $found"
    }

    # The tray and the inspector are unpackaged WinUI 3, so the Windows App Runtime has to be on the
    # machine already. Its own bootstrapper does show a dialog with a download link when it is
    # missing, but that dialog arrives when somebody starts the tray, and saying so at install time
    # costs nothing.
    try
    {
        $runtime = @(Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*' -ErrorAction SilentlyContinue)
        if ($runtime.Count -eq 0)
        {
            $problems += 'The Windows App Runtime was not found. The tray and inspector are unpackaged WinUI 3 and need it. Install it from https://aka.ms/windowsappsdk/stable'
        }
    }
    catch
    {
        # Get-AppxPackage is not always available to the shell running this. Not knowing is not a
        # reason to say anything.
    }

    return $problems
}

function Clear-InstallPayload
{
    <#
        Removes the binaries an install is made of, and nothing else.

        The whole root cannot simply be deleted: settings.json and Logs/ live under it. Nor can a new
        payload merely be copied over the old one, because `publish -o` never removes what it does not
        write, so a file that has left the product stays in the install forever and gets loaded by a
        version that never shipped it.

        Logs/ is the one directory that is not payload. Everything else at the root -- the satellite
        assembly folders, tray/, inspector/, live-app/ -- came from a publish and goes.
    #>
    param([Parameter(Mandatory)][string] $Root)

    if (-not (Test-Path $Root)) { return }

    Get-ChildItem -LiteralPath $Root -Directory |
        Where-Object { $_.Name -ne 'Logs' } |
        Remove-Item -Recurse -Force

    Get-ChildItem -LiteralPath $Root -File |
        Where-Object { $_.Name -ne 'settings.json' } |
        Remove-Item -Force
}

function Get-PeMachine
{
    <#
        The machine type out of a PE header, because Test-Path cannot tell you what is in the file.

        A packaging step derives a provider's destination from one variable and its source from
        another, so the two can disagree and produce a tree that looks complete and injects the wrong
        architecture into the target -- which fails inside somebody else's app, a long way from the
        step that caused it.

        The layout: at 0x3C sits the offset of the "PE\0\0" signature, and the machine word is the two
        bytes straight after it.
    #>
    param([Parameter(Mandatory)][string] $Path)

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

<#
    The PE machine word each RID must report. Shared so the packaging assertion and the installer's
    own check cannot drift into disagreeing about what x86 is.
#>
$script:PeMachineByRid = @{ 'win-x86' = 0x014C; 'win-x64' = 0x8664; 'win-arm64' = 0xAA64 }

function Get-ExpectedPeMachine
{
    param([Parameter(Mandatory)][string] $Rid)

    return $script:PeMachineByRid[$Rid]
}
