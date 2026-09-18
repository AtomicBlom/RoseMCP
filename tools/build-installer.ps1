<#
.SYNOPSIS
    Compiles the Inno Setup installer from a staged package.

.DESCRIPTION
    Its own script rather than a mode of deploy.ps1, because the two are run by different people for
    different reasons. deploy.ps1 promotes a build over the running instance so the next piece of
    work can be done against it, and that is a thing somebody does several times a day; an installer
    is a release artifact, built by CI on a tag. Folding it into the dogfooding path would make every
    deploy depend on a toolchain that has nothing to do with dogfooding.

    It compiles from the staged tree deploy.ps1 leaves behind rather than publishing its own, so the
    zip and the installer carry identical bytes. Two publishes that agree are a coincidence; one
    publish laid down two ways is a guarantee.

    Nothing here signs anything. Signing happens to the finished exe, wherever a pipeline does that,
    so this needs no certificate and a developer can build and test an installer without one.

.EXAMPLE
    ./tools/deploy.ps1 -Mode package -Runtime win-x64,win-arm64
    ./tools/build-installer.ps1
    The usual pair: package, then compile the installer from what packaging staged.

.EXAMPLE
    ./tools/build-installer.ps1 -Iscc 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
    Name the compiler when it is not on PATH and not where it usually installs.
#>
[CmdletBinding()]
param(
    # The staged package to compile, as deploy.ps1 -Mode package leaves it.
    [string] $Stage,

    # Overrides the version read out of the staged payload. Only useful when testing the installer
    # itself, where the binaries are whatever was last built.
    [string] $Version,

    [string] $Iscc,

    [string] $OutputDir
)

$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/RoseMcp.Deploy.ps1"

if (-not (Test-OnWindows)) { throw 'The installer is a Windows artifact and Inno Setup is a Windows tool.' }

$repo = Split-Path $PSScriptRoot -Parent
if (-not $Stage) { $Stage = "$repo/artifacts/stage/win" }
if (-not $OutputDir) { $OutputDir = "$repo/artifacts" }

$Stage = $Stage.Replace('\', '/').TrimEnd('/')
$OutputDir = $OutputDir.Replace('\', '/').TrimEnd('/')

function Resolve-Iscc
{
    <#
        ISCC on PATH, then where the 6.x installer puts it. Inno's own installer does not add itself
        to PATH, so the usual locations are not a fallback so much as the normal case.
    #>
    param([string] $Preferred)

    if ($Preferred)
    {
        if (-not (Test-Path $Preferred)) { throw "no ISCC at $Preferred" }

        return $Preferred
    }

    $onPath = Get-Command 'iscc.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    # The per-user location first, because that is where an unelevated `winget install` puts it and
    # an unelevated install is what a developer machine gets by default. Looking only under Program
    # Files finds nothing on a machine that has Inno Setup.
    $candidates = @(
        "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe",
        "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe",
        "$env:ProgramFiles/Inno Setup 6/ISCC.exe"
    )

    foreach ($candidate in $candidates)
    {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }

    throw 'Inno Setup 6.3 or later is needed to build the installer, and no ISCC.exe was found. ' +
        'Install it with `winget install JRSoftware.InnoSetup`, or pass -Iscc.'
}

function Get-StageVersion
{
    <#
        The version out of the staged payload, so the installer cannot claim a version its binaries
        do not carry. MinVer stamps it from the git tag at build time, which makes the stage
        self-describing and saves passing a number down a second path that can disagree.
    #>
    param([Parameter(Mandatory)][string] $Stage)

    # shared/ first, which is where deduplication puts anything both architectures built identically.
    foreach ($from in 'shared', 'win-x64', 'win-arm64')
    {
        $dll = "$Stage/payload/$from/RoseMcp.Server.dll"
        if (-not (Test-Path $dll)) { continue }

        $stamped = (Get-Item $dll).VersionInfo.ProductVersion
        if ($stamped) { return ($stamped -split '\+')[0].Trim() }
    }

    throw "no RoseMcp.Server.dll under $Stage/payload to read a version from."
}

function Assert-Stage
{
    <#
        That the stage is one packaging produced, named as such. A half-built or hand-assembled tree
        compiles into an installer that lays down a broken install, and the first thing to notice
        would be somebody's machine.
    #>
    param([Parameter(Mandatory)][string] $Stage)

    if (-not (Test-Path $Stage))
    {
        throw "no staged package at $Stage. Run ./tools/deploy.ps1 -Mode package first."
    }

    # Both architectures, because one installer carries both and a Check: condition that finds no
    # files silently installs nothing for that architecture. The tray exe specifically, because a
    # native image can never be deduplicated into shared/ and so must be in its own folder.
    foreach ($rid in 'win-x64', 'win-arm64')
    {
        if (-not (Test-Path "$Stage/payload/$rid/tray/RoseMcp.Tray.exe"))
        {
            throw "the stage at $Stage has no $rid payload. The installer carries both architectures, " +
                'so package with -Runtime win-x64,win-arm64.'
        }
    }

    # An un-deduplicated stage would still install correctly, but silently at twice the size, and the
    # [Files] entry for shared/ would fail the compile anyway. Saying which step is missing beats a
    # path error from ISCC.
    if (-not (Test-Path "$Stage/payload/shared"))
    {
        throw "the stage at $Stage has no payload/shared, so it was built without the deduplication " +
            'step. Run ./tools/deploy.ps1 -Mode package with both Windows runtimes.'
    }

    foreach ($hostRid in 'win-arm64', 'win-x64', 'win-x86')
    {
        if (-not (Test-Path "$Stage/payload/live-app/$hostRid/RoseMcp.LiveApp.exe"))
        {
            throw "the stage at $Stage has no live-app debug host for $hostRid."
        }
    }

    if (-not (Test-Path "$Stage/RoseMcp.Deploy.ps1"))
    {
        throw "the stage at $Stage has no RoseMcp.Deploy.ps1. The installer runs it to stop a running " +
            'install before replacing it, and to stop one before removing it.'
    }

    Assert-StagedScript -Stage $Stage
}

function Assert-StagedScript
{
    <#
        That the scripts in the stage are the ones in tools/.

        Everything else in a stage comes out of a publish, so it is as new as the build that made it.
        These two are copied from the repository instead, which means a stage built before an edit
        carries the version from before it -- and the installer compiled from that stage embeds the
        old one. What that looks like is not a build failure but a behaviour: the uninstaller runs,
        reports success, and quietly does not stop the tray it was supposed to stop, because the copy
        it ran is the copy that could not find it. An hour goes into the installer before anyone
        suspects the payload.

        Refusing rather than silently refreshing, because a stage that is stale in these is usually a
        stage that predates other changes too, and only packaging again can say.
    #>
    param([Parameter(Mandatory)][string] $Stage)

    # Only the script the installer embeds. install.ps1 is in the stage too, for the zip, but the zip
    # has already been written by the time this runs -- so refusing over it would block an installer
    # build to report something this run cannot fix.
    $script = 'RoseMcp.Deploy.ps1'
    $stagedHash = (Get-FileHash "$Stage/$script" -Algorithm SHA256).Hash
    $currentHash = (Get-FileHash "$PSScriptRoot/$script" -Algorithm SHA256).Hash

    if ($stagedHash -ne $currentHash)
    {
        throw "$script in the stage differs from tools/$script, so the stage predates an edit to it " +
            'and the installer would embed the older one. Run ./tools/deploy.ps1 -Mode package again.'
    }
}

Assert-Stage -Stage $Stage

$compiler = Resolve-Iscc -Preferred $Iscc
if (-not $Version) { $Version = Get-StageVersion -Stage $Stage }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

Write-Host "building the installer for $Version"
Write-Host "  compiler $compiler"
Write-Host "  stage    $Stage"

# Paths go in with backslashes: ISCC passes a /D value through as literal preprocessor text, and a
# trailing forward slash inside a quoted Inno path expression escapes the quote that ends it.
$arguments = @(
    "/DStageDir=$($Stage.Replace('/', '\'))",
    "/DAppVersion=$Version",
    "/O$($OutputDir.Replace('/', '\'))",
    "$repo/installer/rosemcp.iss".Replace('/', '\')
)

& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exited $LASTEXITCODE)" }

$installer = "$OutputDir/rosemcp-setup.exe"
if (-not (Test-Path $installer)) { throw "ISCC reported success but produced no $installer" }

$size = [math]::Round((Get-Item $installer).Length / 1MB)
Write-Host "built $installer (${size} MB)"
Write-Host 'it is unsigned; sign it wherever the pipeline does that'
