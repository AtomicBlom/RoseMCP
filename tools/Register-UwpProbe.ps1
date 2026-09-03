<#
.SYNOPSIS
    Builds and registers the classic-UWP probe app, and prints its AUMID.

.DESCRIPTION
    The entry point for dogfooding the live-app tools by hand: this leaves a registered, launchable
    packaged app whose AUMID can be handed straight to rose_debug_launch_uwp. The integration tests do
    the same thing and then unregister it again, which is right for a test and useless for a person
    who wants to click around in it.

    The staging is the part that is not obvious. A classic-UWP CoreCLR debug build produces two
    executables -- the managed app assembly, and a native CoreCLR apphost under Core\ -- and only the
    layout described by the .build.appxrecipe wires them together correctly: the native apphost becomes
    the package executable, the managed assembly moves under entrypoint\, and CoreCLR's own
    System.Runtime.dll is placed beside them rather than the desktop-framework one that also sits in
    the build folder. Register the root manifest instead and Windows hosts the managed exe under the
    desktop .NET Framework CLR, which cannot load CoreCLR's System.Private.CoreLib: the app dies with
    a BadImageFormatException at host init, before a line of app code runs. These old-style projects
    have no Deploy target, so MSBuild emits the recipe and stages nothing -- hence this.

.EXAMPLE
    ./tools/Register-UwpProbe.ps1
    ./tools/Register-UwpProbe.ps1 -Unregister
#>
[CmdletBinding()]
param(
    [switch] $Unregister,
    [ValidateSet('x64', 'arm64')]
    [string] $Platform = 'x64'
)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path "$PSScriptRoot/.."
$packageName = 'RoseMcp.ProbeApp.UwpClassic'
$appDirectory = Join-Path $repo 'tests/apps/uwp-classic'

if ($Unregister)
{
    Get-AppxPackage $packageName | Remove-AppxPackage -ErrorAction SilentlyContinue
    "unregistered $packageName"
    return
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { throw 'vswhere.exe not found; Visual Studio is required.' }

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' |
    Where-Object { $_ -like '*MSBuild.exe' } | Select-Object -First 1
if (-not $msbuild) { throw 'No MSBuild found.' }

# MSBuild alone is not enough; the classic-UWP C# targets have to be installed too.
$windowsXaml = Join-Path (Split-Path $msbuild -Parent) '..\..\..\MSBuild\Microsoft\WindowsXaml'
if (-not (Test-Path $windowsXaml)) { throw 'The classic-UWP MSBuild targets are not installed.' }

$csproj = Join-Path $appDirectory 'Rose.ProbeApp.UwpClassic.csproj'
foreach ($target in 'Restore', 'Build')
{
    & $msbuild $csproj "-t:$target" '-p:Configuration=Debug' "-p:Platform=$Platform" '-v:minimal' '-nologo'
    if ($LASTEXITCODE -ne 0) { throw "UWP $target failed (exit $LASTEXITCODE)" }
}

$recipePath = Join-Path $appDirectory "bin/$Platform/Debug/Rose.ProbeApp.UwpClassic.build.appxrecipe"
if (-not (Test-Path $recipePath)) { throw "No appxrecipe at $recipePath; the build did not complete." }

$ns = @{ msb = 'http://schemas.microsoft.com/developer/msbuild/2003' }
$recipe = [xml](Get-Content $recipePath -Raw)
$layoutNode = Select-Xml -Xml $recipe -Namespace $ns -XPath '//msb:LayoutDir' | Select-Object -First 1
if (-not $layoutNode) { throw 'The appxrecipe declares no LayoutDir.' }
$layout = [uri]::UnescapeDataString($layoutNode.Node.InnerText)

if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force -Path $layout | Out-Null

# Both the manifest and every packaged file carry an Include (the source on disk, MSBuild-escaped)
# and a PackagePath (where it lands in the layout).
$manager = New-Object System.Xml.XmlNamespaceManager($recipe.NameTable)
$manager.AddNamespace('msb', $ns.msb)

$entries = Select-Xml -Xml $recipe -Namespace $ns -XPath '//msb:AppXManifest | //msb:AppxPackagedFile'
foreach ($entry in $entries)
{
    $source = [uri]::UnescapeDataString($entry.Node.GetAttribute('Include'))
    $packagePath = [uri]::UnescapeDataString($entry.Node.SelectSingleNode('msb:PackagePath', $manager).InnerText)
    $destination = Join-Path $layout $packagePath
    New-Item -ItemType Directory -Force -Path (Split-Path $destination -Parent) | Out-Null
    Copy-Item $source $destination -Force
}

Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml') -ErrorAction Stop
$package = Get-AppxPackage $packageName
if (-not $package) { throw 'Registration reported success but the package is not present.' }

"registered $($package.PackageFullName)"
"AUMID: $($package.PackageFamilyName)!App"
