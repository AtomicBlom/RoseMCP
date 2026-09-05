# Builds RoseMcp.Xaml.WinUi.Tap.dll with the MSVC toolset, without needing a Developer prompt.
#
# The sibling of RoseMcp.Xaml.Uwp.Tap's build.ps1 and deliberately the same shape, including its exit
# codes: 3 from Fail for anything the machine simply does not have, so a caller can skip rather than
# go red, and anything else is a real failure.
#
# It has one step the UWP build does not need. Microsoft.UI.Xaml projections are not in the Windows
# SDK, so they are generated with cppwinrt.exe from the WindowsAppSDK's .winmd files before the
# compile (#76). projections.csproj exists only to be restored, which is what puts those winmds on
# disk; once fetched, the whole thing is offline-repeatable.
#
# The provider is injected into the target app and must match its architecture. WinUI 3 runs natively
# on ARM64, unlike classic UWP, so the arm64 provider is load-bearing here rather than a nicety.
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Platform = 'x64',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

function Fail([string]$message)
{
    Write-Error $message
    exit 3
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) { Fail "vswhere.exe not found; a Visual Studio installation with the C++ toolset is required." }

# The C++ component id is not consistently registered across editions/hosts (it is absent on this
# ARM64 install even though the toolset is present), so discover the install and then confirm the
# toolset by its folder rather than trusting a -requires filter.
$vs = & $vswhere -latest -products * -property installationPath
if (-not $vs) { Fail "No Visual Studio installation was found." }

$msvcRoot = Join-Path $vs 'VC\Tools\MSVC'
if (-not (Test-Path $msvcRoot)) { Fail "The MSVC C++ toolset was not found under $vs (install the 'Desktop development with C++' workload)." }
$msvc = Get-ChildItem $msvcRoot -Directory | Sort-Object Name | Select-Object -Last 1

$sdkRoot = 'C:\Program Files (x86)\Windows Kits\10'
if (-not (Test-Path "$sdkRoot\Include")) { Fail "The Windows 10/11 SDK was not found at $sdkRoot." }
$sdkVer = Get-ChildItem "$sdkRoot\Include" -Directory | Where-Object { $_.Name -match '^10\.' } | Sort-Object Name | Select-Object -Last 1

# cppwinrt.exe ships with the Windows SDK even though the metadata it will be pointed at does not.
$cppwinrt = "$sdkRoot\bin\$($sdkVer.Name)\x64\cppwinrt.exe"
if (-not (Test-Path $cppwinrt)) { Fail "cppwinrt.exe was not found at $cppwinrt; the Windows SDK's C++/WinRT tooling is required to project Microsoft.UI.Xaml." }

# --- The WindowsAppSDK metadata -------------------------------------------------------------------

$projections = Join-Path $here 'projections.csproj'
if (-not (Test-Path $projections)) { Fail "projections.csproj is missing; it is what acquires the WindowsAppSDK metadata." }

Write-Host "restoring the WindowsAppSDK metadata"
& dotnet restore $projections | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "Restoring the WindowsAppSDK failed; this machine cannot build the WinUI provider (exit $LASTEXITCODE)." }

$assetsPath = Join-Path $here 'obj\project.assets.json'
if (-not (Test-Path $assetsPath)) { Fail "The restore produced no project.assets.json, so the WindowsAppSDK metadata cannot be located." }

$assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
$libraryIds = @($assets.libraries.PSObject.Properties.Name)

# Resolved from the restore rather than pinned here, so the metadata and the version
# tests/apps/winui builds against cannot drift apart.
function Resolve-Package([string]$id)
{
    $entry = $libraryIds | Where-Object { $_ -like "$id/*" } | Select-Object -First 1
    if (-not $entry) { return $null }

    $name, $version = $entry -split '/', 2
    foreach ($root in $packageRoots)
    {
        $candidate = Join-Path $root ("$($name.ToLowerInvariant())\$version")
        if (Test-Path $candidate) { return $candidate }
    }

    return $null
}

$winui = Resolve-Package 'Microsoft.WindowsAppSDK.WinUI'
$interactive = Resolve-Package 'Microsoft.WindowsAppSDK.InteractiveExperiences'
$foundation = Resolve-Package 'Microsoft.WindowsAppSDK.Foundation'
# Not referenced by us, but Microsoft.UI.Xaml.winmd names CoreWebView2 and cppwinrt will not generate
# without the type. It arrives transitively with the WindowsAppSDK.
$webview2 = Resolve-Package 'Microsoft.Web.WebView2'

foreach ($pair in @(@('Microsoft.WindowsAppSDK.WinUI', $winui), @('Microsoft.WindowsAppSDK.InteractiveExperiences', $interactive),
    @('Microsoft.WindowsAppSDK.Foundation', $foundation), @('Microsoft.Web.WebView2', $webview2)))
{
    if (-not $pair[1]) { Fail "The restored packages do not include $($pair[0]); the WindowsAppSDK layout may have changed." }
}

# InteractiveExperiences keeps its metadata under a per-SDK-version folder; take the newest.
$interactiveMetadata = Get-ChildItem (Join-Path $interactive 'metadata') -Directory | Sort-Object Name | Select-Object -Last 1
if (-not $interactiveMetadata) { Fail "No metadata folder under $interactive." }

$webview2Winmd = Join-Path $webview2 'lib\Microsoft.Web.WebView2.Core.winmd'
if (-not (Test-Path $webview2Winmd)) { Fail "No WebView2 winmd at $webview2Winmd." }

$inputs = @(
    (Join-Path $winui 'metadata')
    $interactiveMetadata.FullName
    (Join-Path $foundation 'metadata')
    $webview2Winmd
    $sdkVer.Name
)

# --- Generate the projections ---------------------------------------------------------------------

$generated = Join-Path $here 'generated'
$stampPath = Join-Path $generated '.inputs'
$stamp = ($inputs -join "`n")
$current = if (Test-Path $stampPath) { Get-Content $stampPath -Raw } else { '' }

# Regenerated only when the inputs move. It emits several hundred headers and there is no reason to
# pay for that on every build of a file that changes far more often than the SDK does.
if (($current.Trim() -ne $stamp.Trim()) -or -not (Test-Path (Join-Path $generated 'winrt\Microsoft.UI.Xaml.h')))
{
    Write-Host "generating C++/WinRT projections for Microsoft.UI.Xaml"
    New-Item -ItemType Directory -Force $generated | Out-Null

    $arguments = @()
    foreach ($input in $inputs) { $arguments += @('-in', $input) }
    $arguments += @('-out', $generated)

    & $cppwinrt @arguments
    if ($LASTEXITCODE -ne 0) { throw "cppwinrt.exe failed with exit code $LASTEXITCODE" }

    Set-Content -Path $stampPath -Value $stamp -NoNewline
}
else
{
    Write-Host "projections are up to date"
}

# --- Compile ---------------------------------------------------------------------------------------

# The generated set carries the Windows.* projections as well as the Microsoft.UI.* ones, so it goes
# on INCLUDE *instead of* the SDK's own cppwinrt folder rather than in front of it. Mixing two
# generations of the same headers is the kind of thing that compiles and then behaves oddly.
$env:INCLUDE = @(
    (Join-Path $generated '')
    "$($msvc.FullName)\include"
    "$sdkRoot\Include\$($sdkVer.Name)\ucrt"
    "$sdkRoot\Include\$($sdkVer.Name)\shared"
    "$sdkRoot\Include\$($sdkVer.Name)\um"
    "$sdkRoot\Include\$($sdkVer.Name)\winrt"
) -join ';'

# Prefer a compiler hosted on this machine's architecture; fall back to the x64 (emulated) host.
$hostArch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'Hostarm64' } else { 'Hostx64' }
$hostDir = "$hostArch\$Platform"
$cl = "$($msvc.FullName)\bin\$hostDir\cl.exe"
if (-not (Test-Path $cl))
{
    $hostDir = "Hostx64\$Platform"
    $cl = "$($msvc.FullName)\bin\$hostDir\cl.exe"
}
if (-not (Test-Path $cl)) { Fail "No cl.exe targeting $Platform was found in the toolset ($($msvc.Name))." }

$env:LIB = @(
    "$($msvc.FullName)\lib\$Platform"
    "$sdkRoot\Lib\$($sdkVer.Name)\ucrt\$Platform"
    "$sdkRoot\Lib\$($sdkVer.Name)\um\$Platform"
) -join ';'

$outDir = Join-Path $here "bin\$Platform\$Configuration"
New-Item -ItemType Directory -Force $outDir | Out-Null

Write-Host "cl: $cl"
Write-Host "toolset $($msvc.Name), SDK $($sdkVer.Name), platform $Platform, out $outDir"

Push-Location $outDir
try
{
    $configFlags = if ($Configuration -eq 'Debug') { @('/Zi', '/Od', '/MDd') } else { @('/O2', '/MD') }
    # /bigobj: the C++/WinRT XAML headers push the object past the default section limit.
    & $cl /nologo /LD /EHsc /std:c++20 /bigobj /DUNICODE /D_UNICODE /W3 @configFlags `
        "$here\RoseMcp.Xaml.WinUi.Tap.cpp" `
        /Fe:RoseMcp.Xaml.WinUi.Tap.dll `
        /link /DLL /DEF:"$here\RoseMcp.Xaml.WinUi.Tap.def" ole32.lib oleaut32.lib WindowsApp.lib
    if ($LASTEXITCODE -ne 0) { throw "cl.exe failed with exit code $LASTEXITCODE" }
}
finally
{
    Pop-Location
}

Write-Host "built $(Join-Path $outDir 'RoseMcp.Xaml.WinUi.Tap.dll')"
