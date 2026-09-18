<#
.SYNOPSIS
    Publishes RoseMCP, either over the running instance or into release zips.

.DESCRIPTION
    Two jobs that share all their plumbing:

      promote  Hand the running instance a new build, and nothing else. It runs no tests: whoever
               is deploying is often not whoever changed the code, and a suite that tests none of
               their work still costs them the wait -- so the gate is `dotnet test` before a commit
               and CI on every push, both of which run where somebody can act on a failure. The
               whole tree is published into a stage first, with the running instance untouched;
               only once that succeeds are the tray and any stdio server running from the install
               stopped -- a running exe cannot be overwritten -- the stage copied over, and the
               tray restarted. So the outage is a copy rather than a build, a failed build leaves
               the old instance serving, and it still costs an /mcp reconnect and a solution reload.

      package  Build the release artifacts. Windows gets **one** zip carrying every architecture,
               because choosing between them is a step people get wrong -- an x64 zip on an ARM64
               laptop installs and runs, emulated, with no native debug host, and nothing says so.
               install.ps1 ships inside it, reads the machine, and lays down only what that machine
               can execute, so the download is the only thing that grew: the install is the size it
               always was. The live-app debug hosts are shared rather than duplicated, since the
               ARM64 set (arm64, x64, x86) is a superset of the x64 set (x64, x86).
               Linux gets a tar.gz per architecture with the broker and the worker only -- the tray
               is WinUI and the debug host is ICorDebug, so neither has a Linux build to ship. tar
               rather than zip because a zip records no Unix permission bits, and an apphost without
               +x is "permission denied" on unpack; for the same reason a Linux artifact has to be
               rolled on Linux, and packaging one here warns rather than shipping something broken.

    Paths use forward slashes throughout; PowerShell accepts them on Windows.

    promote installs to -Destination, or $env:ROSEMCP_DEPLOY_ROOT, or
    %LOCALAPPDATA%/BinaryVibrance/RoseMCP -- the same vendor/product folder the logs already use,
    so an install and its logs sit under one root instead of two unrelated ones.
    Nothing here assumes a particular drive; where a given machine keeps its install is that
    machine's business, not the repository's.

.EXAMPLE
    ./tools/deploy.ps1
    Promote the current architecture over the running instance.

.EXAMPLE
    ./tools/deploy.ps1 -Mode package
    Build every artifact under artifacts/: one rosemcp-win.zip carrying x64 and ARM64, and
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

    # Install root for promote. Falls back to $env:ROSEMCP_DEPLOY_ROOT, then
    # %LOCALAPPDATA%/BinaryVibrance/RoseMCP.
    [string] $Destination,

    [int] $Port = 5077,

    # Working directory for the tray, which is where a tool call with no path looks for a solution.
    [string] $WorkspaceRoot,

    [switch] $NoRestart,

    # Fail rather than warn when the native XAML provider cannot be built. Always on for
    # package, because that run cuts a release and a release that quietly ships without it is
    # indistinguishable from a product bug. Available for promote so a local deploy can insist.
    [switch] $RequireXamlProvider
)

$ErrorActionPreference = 'Stop'

# Stopping an install, waiting for its workers and starting its tray are shared with install.ps1,
# which does the same things to the same tree from a release archive rather than from a build.
. "$PSScriptRoot/RoseMcp.Deploy.ps1"

$onWindows = Test-OnWindows

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
    $Destination = if ($configured) { $configured } else { Join-Path $localAppData 'BinaryVibrance/RoseMCP' }
}

$Destination = $Destination.Replace('\', '/')

function Invoke-Dotnet
{
    param([string[]] $Arguments, [string] $What)

    & dotnet @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "$What failed (dotnet exited $LASTEXITCODE)" }
}

function Publish-Tree
{
    <#
        One architecture's tree.

        -NoLiveApp leaves out the debug hosts, which the combined package publishes once into a shared
        folder instead: an ARM64 machine can execute ARM64, x64 and x86 and an x64 machine the last
        two, so the ARM64 set is a superset of the x64 set and a second copy would be the same bytes
        under a different parent.
    #>
    param([string] $Rid, [string] $Into, [switch] $NoLiveApp)

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

    # The inspector gets a folder of its own for the same reason, and one more: two WinUI publishes
    # into one directory overwrite each other's WindowsAppSDK payload, and `publish -o` never
    # removes what it does not write, so the loser keeps whichever files the winner did not have.
    Invoke-Dotnet @('publish', "$repo/src/RoseMcp.Inspector", '-c', 'Release', '-r', $Rid,
        '--self-contained', 'false', '-o', "$Into/inspector") "RoseMcp.Inspector ($Rid)"

    if (-not $NoLiveApp) { Publish-LiveAppHosts -Into $Into -HostRids (Get-LiveAppRuntimes -Rid $Rid) }
}

function Get-PackagedLiveAppRuntimes
{
    <#
        The union of the debug hosts every packaged architecture needs, which is what the shared
        live-app folder in a combined package holds. Deduped, because the sets overlap by design.
    #>
    param([string[]] $Rids)

    $union = [System.Collections.Generic.List[string]]::new()
    foreach ($rid in $Rids)
    {
        foreach ($hostRid in Get-LiveAppRuntimes -Rid $rid)
        {
            if (-not $union.Contains($hostRid)) { $union.Add($hostRid) }
        }
    }

    return $union.ToArray()
}

function Publish-LiveAppHosts
{
    <#
        The layout is the one LiveAppHostLauncher looks for: live-app/<rid> beside the broker, with
        each host's native XAML providers under xaml-provider/<rid> beside that host.
    #>
    param([string] $Into, [string[]] $HostRids)

    foreach ($hostRid in $HostRids)
    {
        $hostDir = "$Into/live-app/$hostRid"
        Invoke-Dotnet @('publish', "$repo/src/RoseMcp.LiveApp", '-c', 'Release', '-r', $hostRid,
            '--self-contained', 'false', '-o', $hostDir) "RoseMcp.LiveApp ($hostRid)"

        Copy-XamlProviders -Rid $hostRid -HostDir $hostDir -Required:$xamlProviderRequired
    }
}

function Split-SharedPayload
{
    <#
        Moves every file that is byte-identical across all packaged architectures into payload/shared,
        leaving each architecture's folder holding only what is actually its own.

        Nearly all of a payload is architecture-neutral IL that the two publishes emit identically;
        what genuinely differs is the apphosts, a handful of assemblies stamped with their RID, and
        the native WindowsAppSDK pieces. Shipping the identical remainder twice is most of the
        download.

        Compression does not save this. Inno's lzma2/max works from an 8 MB dictionary and the copies
        sit more than a hundred megabytes apart in the stream, so it never sees them as repeats; a zip
        is worse still, compressing every file on its own and finding no cross-file repetition at all.
        The duplication has to go before compression rather than be left for it.

        Identity is decided by SHA256 over files at the same relative path, so a file that differs in
        any way stays where it is -- two PE images built for different machines cannot collide, and
        neither can anything else that matters. A file present in only one architecture's payload is
        not shared either, because it is not in all of them.
    #>
    param([Parameter(Mandatory)][string] $Stage, [Parameter(Mandatory)][string[]] $Rids)

    $hashesByRid = @{}
    foreach ($rid in $Rids)
    {
        $root = [System.IO.Path]::GetFullPath("$Stage/payload/$rid")
        $map = @{}

        foreach ($file in Get-ChildItem $root -Recurse -File)
        {
            $relative = $file.FullName.Substring($root.Length).TrimStart('\', '/')
            $map[$relative] = (Get-FileHash $file.FullName -Algorithm SHA256).Hash
        }

        $hashesByRid[$rid] = $map
    }

    $first = $Rids[0]
    $others = $Rids | Select-Object -Skip 1
    $shared = [System.Collections.Generic.List[string]]::new()

    foreach ($relative in $hashesByRid[$first].Keys)
    {
        $hash = $hashesByRid[$first][$relative]
        $everywhere = $true

        foreach ($rid in $others)
        {
            if (-not $hashesByRid[$rid].ContainsKey($relative) -or $hashesByRid[$rid][$relative] -ne $hash)
            {
                $everywhere = $false
                break
            }
        }

        if ($everywhere) { $shared.Add($relative) }
    }

    $bytes = 0
    foreach ($relative in $shared)
    {
        $target = "$Stage/payload/shared/$relative"
        New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null

        $bytes += (Get-Item "$Stage/payload/$first/$relative").Length
        Move-Item "$Stage/payload/$first/$relative" $target -Force

        foreach ($rid in $others) { Remove-Item "$Stage/payload/$rid/$relative" -Force }
    }

    foreach ($rid in $Rids) { Remove-EmptyDirectory -Path "$Stage/payload/$rid" }

    $saved = [math]::Round(($bytes * $others.Count) / 1MB)
    Write-Host ("  deduplicated {0} file(s) into payload/shared, {1} MB not shipped again" -f $shared.Count, $saved)
}

function Remove-EmptyDirectory
{
    <#
        Depth first, because emptying a leaf is what makes its parent empty. Moving the shared files
        out leaves whole directories behind -- the satellite assembly folders especially, which are
        identical in every architecture -- and an empty directory in a payload becomes an empty
        directory in the install.
    #>
    param([Parameter(Mandatory)][string] $Path)

    foreach ($directory in Get-ChildItem $Path -Directory)
    {
        Remove-EmptyDirectory -Path $directory.FullName
    }

    if (-not (Get-ChildItem $Path -Force)) { Remove-Item $Path -Force }
}

function Get-ProviderPlatform
{
    param([string] $Rid)

    switch ($Rid)
    {
        'win-arm64' { return 'arm64' }
        'win-x86' { return 'x86' }
        default { return 'x64' }
    }
}

function Copy-XamlProviders
{
    <#
        Both native taps, for one host architecture.

        Two, because which one serves a target is decided by the XAML framework that target runs, and
        a provider built for one cannot serve the other: UWP's diagnostics are reached through
        Windows.UI.Xaml.dll on the VisualDiagConnection1 endpoint, WinUI 3's through the
        WindowsAppRuntime's FrameworkUdk on its own. Shipping only the UWP tap left every WinUI 3
        target without XAML inspection or live editing, with nothing in the install to say why.
    #>
    param([string] $Rid, [string] $HostDir, [switch] $Required)

    foreach ($project in 'RoseMcp.Xaml.Uwp.Tap', 'RoseMcp.Xaml.WinUi.Tap')
    {
        Copy-XamlProvider -Project $project -Rid $Rid -HostDir $HostDir -Required:$Required
    }
}

function Copy-XamlProvider
{
    <#
        A provider is native C++ and is injected into the target, so it matches the target's
        architecture exactly. It needs the MSVC toolset for that architecture, which a machine that
        only publishes managed code will not have -- so a missing toolset is a warning and the debugger
        ships without XAML inspection, rather than the whole deploy failing over a capability the user
        may not want. For a package it is fatal, because a release that quietly ships without it is
        indistinguishable from a product bug.
    #>
    param([string] $Project, [string] $Rid, [string] $HostDir, [switch] $Required)

    $platform = Get-ProviderPlatform -Rid $Rid
    $build = "$repo/src/$Project/build.ps1"
    $dll = "$repo/src/$Project/bin/$platform/Release/$Project.dll"

    Write-Host "  building $Project ($platform)"
    & pwsh -NoProfile -File $build -Platform $platform -Configuration Release *> $null
    $exitCode = $LASTEXITCODE

    # Clear it deliberately. A warned-and-continued failure otherwise leaves $LASTEXITCODE set, and
    # nothing after this point is a native command that would replace it -- so the script inherits the
    # failed build's code and exits non-zero after promoting perfectly well. A deploy that says it
    # failed while the install is live is worse than the missing provider it was warning about, because
    # anything automating it believes the deploy.
    $global:LASTEXITCODE = 0

    if ($exitCode -ne 0 -or -not (Test-Path $dll))
    {
        $message = "$Project could not be built for $platform; XAML inspection and live editing will " +
            "be unavailable for $Rid targets."

        if ($Required) { throw $message }

        Write-Warning "  $message"
        return
    }

    $into = "$HostDir/xaml-provider/$Rid"
    New-Item -ItemType Directory -Force -Path $into | Out-Null
    Copy-Item $dll $into -Force
    Write-Host "  $Project ($platform) -> $into"
}

function Test-PayloadFile
{
    <#
        Whether an install for $Rid would end up with a file, wherever the package keeps it: in that
        architecture's folder, or in the shared one every architecture is laid down on top of.
    #>
    param(
        [Parameter(Mandatory)][string] $Stage,
        [Parameter(Mandatory)][string] $Rid,
        [Parameter(Mandatory)][string] $Relative
    )

    return (Test-Path "$Stage/payload/$Rid/$Relative") -or (Test-Path "$Stage/payload/shared/$Relative")
}

function Assert-WindowsPackage
{
    <#
        Every win-* package must carry a debug host for each architecture its machine can execute,
        both native XAML providers for each of those, and each
        provider must actually be built for the architecture its folder claims.

        This exists because the failure it catches is silent. Copy-XamlProvider warns and returns
        when the MSVC toolset is missing -- right for a laptop that only publishes managed code -- and
        nothing downstream looked. release.yml asserts only that an archive exists, which is true of a
        zip with no xaml-provider directory in it at all, so the release went green, the checksums
        were computed over it, and rose_xaml_tree failed on every target of that architecture for a
        reason that was in a log nobody reads on a green run.

        Asserted here rather than at the upload step, which is as close to the cause as it can be put.
    #>
    param([string] $Stage, [string[]] $Rids)

    $hostRids = Get-PackagedLiveAppRuntimes -Rids $Rids
    $checked = 0

    # The installer and the half of it that install.ps1 and deploy.ps1 share. A package that carries
    # the binaries and not the script that lays them down is an archive somebody has to read the wiki
    # to use, and nothing else in the release would say it was missing.
    foreach ($script in 'install.ps1', 'RoseMcp.Deploy.ps1')
    {
        if (-not (Test-Path "$Stage/$script")) { throw "the package is missing $script" }
    }

    foreach ($Rid in $Rids)
    {
        foreach ($required in 'RoseMcp.Server.exe', 'RoseMcp.Worker.exe')
        {
            # Either place. Deduplication moves whatever is identical across architectures into
            # shared/, and which files those are is a fact about a given build rather than something
            # to assert -- what matters is that an install assembled from shared plus this
            # architecture has them.
            if (-not (Test-PayloadFile -Stage $Stage -Rid $Rid -Relative $required))
            {
                throw "$Rid payload is missing $required"
            }
        }

        # Both windows, and each built for the architecture whose payload it is in. A package missing
        # the inspector is not obviously broken from the outside: the tray comes up, and Inspect
        # reports that it cannot find the exe -- which reads like a lookup bug rather than a package
        # that never carried one.
        foreach ($window in 'tray/RoseMcp.Tray.exe', 'inspector/RoseMcp.Inspector.exe')
        {
            # These can never be shared -- two PE images built for different machines cannot be
            # byte-identical -- so finding one outside its architecture's folder means the
            # deduplication matched something it should not have.
            $exe = "$Stage/payload/$Rid/$window"
            if (-not (Test-Path $exe))
            {
                if (Test-Path "$Stage/payload/shared/$window")
                {
                    throw "$window was deduplicated into payload/shared, which cannot be right for a " +
                        'native image: the two architectures would have had to produce identical bytes.'
                }

                throw "$Rid payload is missing $exe"
            }

            $machine = Get-PeMachine $exe
            if ($machine -ne (Get-ExpectedPeMachine -Rid $Rid))
            {
                throw (("$Rid payload has the wrong {0}: it reports machine 0x{1:X4}, expected 0x{2:X4}.") -f
                    $window, $machine, (Get-ExpectedPeMachine -Rid $Rid))
            }
        }
    }

    foreach ($hostRid in $hostRids)
    {
        $hostExe = "$Stage/payload/live-app/$hostRid/RoseMcp.LiveApp.exe"
        if (-not (Test-Path $hostExe)) { throw "the package is missing $hostExe" }

        foreach ($project in 'RoseMcp.Xaml.Uwp.Tap', 'RoseMcp.Xaml.WinUi.Tap')
        {
            $dll = "$Stage/payload/live-app/$hostRid/xaml-provider/$hostRid/$project.dll"
            if (-not (Test-Path $dll))
            {
                throw "the package is missing the XAML provider at $dll. XAML inspection and live " +
                    "editing would be unavailable for $hostRid targets, and nothing else would say " +
                    "so. On a build agent this is usually a missing MSVC cross-toolset for that " +
                    "architecture."
            }

            $machine = Get-PeMachine $dll
            if ($machine -ne (Get-ExpectedPeMachine -Rid $hostRid))
            {
                # Parenthesised before -f on purpose: -f binds tighter than +, so formatting a
                # concatenation without these brackets formats only the last piece of it and leaves
                # the placeholders in the rest sitting there as literal text.
                throw (("the package has the wrong XAML provider for {0}: {1} reports machine " +
                    "0x{2:X4}, expected 0x{3:X4}. It would be injected into a target of the other " +
                    "architecture.") -f
                    $hostRid, $dll, $machine, (Get-ExpectedPeMachine -Rid $hostRid))
            }

            $checked++
        }
    }

    Write-Host ("  layout checked: {0} architecture(s) ({1}), {2} shared debug host(s) ({3}) and {4} XAML provider(s), all correctly built" -f
        $Rids.Count, ($Rids -join ', '), $hostRids.Count, ($hostRids -join ', '), $checked)
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

    # Build everything before touching the running instance. Publishing straight into the install
    # means stopping it first, so Rose is unavailable for the length of the build -- and a build that
    # fails partway leaves nothing running and a half-written install that may not start. A fresh
    # stage, because `publish -o` never removes what it does not write and a leftover from an earlier
    # promote would otherwise be copied into the install as though this build had produced it.
    $stage = "$repo/artifacts/promote/$($Runtime[0])"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Publish-Tree -Rid $Runtime[0] -Into $stage

    # Everything about stopping and restarting is about the tray and the stdio servers holding the
    # install's files open, and neither exists off Windows: there is nothing to stop, and nothing to
    # overwrite while it runs. Stop-Install knows that and returns a no-op there.
    $stopped = Stop-Install -Root $Destination

    Write-Host "  copying $stage -> $Destination"
    New-Item -ItemType Directory -Force -Path $Destination | Out-Null
    Copy-Item -Path "$stage/*" -Destination $Destination -Recurse -Force

    if (-not (Test-WindowsRid $Runtime[0])) { Write-Host '  no tray to restart on this platform' }
    elseif ($NoRestart) { Write-Host '  not restarting (-NoRestart)' }
    else { Start-Tray -Root $Destination -WorkspaceRoot $WorkspaceRoot -Port $Port }

    Write-Host "promoted $($Runtime[0]) to $Destination"
    if ($stopped.TrayWasRunning -or $stopped.ServersStopped) { Write-Host 'reconnect the MCP client with /mcp; the first call reloads the solution' }

    return
}

$artifacts = "$repo/artifacts"
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

$windowsRids = @($Runtime | Where-Object { Test-WindowsRid $_ })
$linuxRids = @($Runtime | Where-Object { -not (Test-WindowsRid $_) })

# One Windows archive carrying every architecture, because choosing one is a step people get wrong:
# an x64 zip on an ARM64 laptop installs and runs, emulated, with no native debug host, and nothing
# says so. install.ps1 reads the machine and lays down only what it can execute, so the download is
# the only thing that is bigger -- the install is the size it always was.
if ($windowsRids.Count -gt 0)
{
    $stage = "$artifacts/stage/win"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    foreach ($rid in $windowsRids)
    {
        Publish-Tree -Rid $rid -Into "$stage/payload/$rid" -NoLiveApp
    }

    # Once, shared. The debug hosts and their native taps are the expensive half of the package and
    # the sets overlap completely: an ARM64 machine can execute all three architectures and an x64
    # machine two of them, so a per-architecture copy would be the same bytes twice.
    Publish-LiveAppHosts -Into "$stage/payload" -HostRids (Get-PackagedLiveAppRuntimes -Rids $windowsRids)

    # Only with something to compare against. One architecture's payload is trivially identical to
    # itself, and hoisting all of it into shared/ would leave an architecture folder that exists but
    # holds nothing -- a layout neither installer expects and both would lay down as an empty install.
    if ($windowsRids.Count -gt 1) { Split-SharedPayload -Stage $stage -Rids $windowsRids }

    Copy-Item "$repo/installer/install.ps1" "$stage/install.ps1" -Force
    Copy-Item "$PSScriptRoot/RoseMcp.Deploy.ps1" "$stage/RoseMcp.Deploy.ps1" -Force

    $archive = "$artifacts/rosemcp-win.zip"
    if (Test-Path $archive) { Remove-Item $archive -Force }

    Assert-WindowsPackage -Stage $stage -Rids $windowsRids

    # ZipFile rather than Compress-Archive: the combined package is several hundred megabytes across
    # thousands of files, which Compress-Archive walks one pipeline object at a time.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        [System.IO.Path]::GetFullPath($stage), [System.IO.Path]::GetFullPath($archive),
        [System.IO.Compression.CompressionLevel]::Optimal, $false)

    $size = [math]::Round((Get-Item $archive).Length / 1MB)
    Write-Host "  packaged $archive (${size} MB, $($windowsRids -join ' + '))"

    # The stage is left behind on purpose: build-installer.ps1 compiles the Inno installer from this
    # same tree, so the archive and the installer carry identical bytes rather than two publishes that
    # agree by coincidence. Compiling it is not done here -- an installer is a release artifact, and
    # this script is also what a developer runs to dogfood a build.
    Write-Host "  stage kept at $stage for ./tools/build-installer.ps1"
}

foreach ($rid in $linuxRids)
{
    $stage = "$artifacts/stage/$rid"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

    Publish-Tree -Rid $rid -Into $stage

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

    $size = [math]::Round((Get-Item $archive).Length / 1MB)
    Write-Host "  packaged $archive (${size} MB)"
}

Write-Host "packaged $($Runtime -join ', ') into $artifacts"
