# Builds RoseXamlTap.dll with the MSVC toolset, without needing a Developer prompt. The provider is
# injected into the target app and must match its architecture -- x64 for a classic UWP app running
# emulated on ARM64, arm64 for a native ARM64 WinUI app -- so the platform is a parameter.
#
# It finds Visual Studio through vswhere (the same discovery the tests use for the UWP toolchain), so
# there is no hard-coded install path. Exits non-zero, with a clear reason, when the C++ toolset or
# the Windows SDK is not present, so callers can skip rather than fail.
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

$env:INCLUDE = @(
    "$($msvc.FullName)\include"
    "$sdkRoot\Include\$($sdkVer.Name)\ucrt"
    "$sdkRoot\Include\$($sdkVer.Name)\shared"
    "$sdkRoot\Include\$($sdkVer.Name)\um"
    "$sdkRoot\Include\$($sdkVer.Name)\winrt"
    "$sdkRoot\Include\$($sdkVer.Name)\cppwinrt"  # C++/WinRT projections for the select-mode overlay
) -join ';'

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
        "$here\RoseXamlTap.cpp" `
        /Fe:RoseXamlTap.dll `
        /link /DLL /DEF:"$here\RoseXamlTap.def" ole32.lib oleaut32.lib WindowsApp.lib
    if ($LASTEXITCODE -ne 0) { throw "cl.exe failed with exit code $LASTEXITCODE" }
}
finally
{
    Pop-Location
}

Write-Host "built $(Join-Path $outDir 'RoseXamlTap.dll')"
