<#
    build.ps1 - build PixelSenseToTouchLib.dll + PixelSenseToTouch.exe without Visual Studio.

    The .sln/.csproj still build normally in Visual Studio; this exists so the table itself can
    rebuild the app. The table has no VS and no MSBuild newer than 4.0, whose C# 5 compiler cannot
    read this code (it uses string interpolation and null-conditional operators). So we drive the
    Roslyn compiler from the .NET SDK directly and hand it the reference set the .csproj files
    declare - which is all MSBuild would have done for two projects this simple.

    Targets x86 deliberately: the Surface 1.0 runtime is 32-bit and is loaded in-process.

    Output goes to build-out\ rather than bin\, so a local build never silently replaces the
    binaries that are known to work on the table. Copy it over deliberately, or pass -Stage.

        .\build.ps1                       # build into build-out\
        .\build.ps1 -Stage                # also copy into ..\..\dist\pixelsense2touch\
        .\build.ps1 -Configuration Debug  # DEBUG build (adds the tray's Debug menu)
#>
[CmdletBinding()]
param(
    [switch]$Stage,
    # Not named -Debug: that is a PowerShell common parameter and collides with CmdletBinding.
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = "Stop"

$root = $PSScriptRoot
$lib  = Join-Path $root "PixelSenseToTouchLib"
$tray = Join-Path $root "PixelSenseToTouchTray"
$out  = Join-Path $root "build-out"

# --- Roslyn, from the .NET SDK ---
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) {
    # A per-user SDK (dotnet-install.ps1 without -AddToPath) is off PATH but perfectly usable.
    foreach ($c in "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe", "$env:ProgramFiles\dotnet\dotnet.exe") {
        if (Test-Path $c) { $dotnet = $c; break }
    }
}
if (-not $dotnet) { throw "dotnet was not found. Install the .NET SDK (https://dot.net/v1/dotnet-install.ps1)." }
$csc = Get-ChildItem (Join-Path (Split-Path $dotnet) "sdk\*\Roslyn\bincore\csc.dll") -ErrorAction SilentlyContinue |
       Sort-Object FullName | Select-Object -Last 1
if (-not $csc) { throw "Roslyn (csc.dll) was not found under the .NET SDK." }

# --- reference set ---
# The table has no .NET Framework developer pack, so reference the installed framework assemblies
# themselves. Their reference metadata is what the compiler reads, so this is equivalent for our
# purposes - and it is exactly what the v4 csc does implicitly.
$fx = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319"
if (-not (Test-Path (Join-Path $fx "mscorlib.dll"))) { throw ".NET Framework 4.x was not found at $fx" }

$fxRefs = 'mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll',
          'System.Data.dll','System.Xml.dll' | ForEach-Object { Join-Path $fx $_ }

$surface = Join-Path $root "lib"
$tcd     = Join-Path $root "packages\TCD.System.TouchInjection.1.0.0\lib\net45\TCD.System.TouchInjection.dll"
$extRefs = @(
    (Join-Path $surface "Microsoft.Surface.Core.dll"),
    (Join-Path $surface "Microsoft.Surface.Common.dll"),
    $tcd
)
foreach ($r in $fxRefs + $extRefs) { if (-not (Test-Path $r)) { throw "Missing reference: $r" } }

$defines  = if ($Configuration -eq 'Debug') { "DEBUG;TRACE" } else { "TRACE" }
$optimize = if ($Configuration -eq 'Debug') { "-optimize-" } else { "-optimize+" }

New-Item -ItemType Directory -Force -Path $out | Out-Null

function Invoke-Csc([string[]]$cscArgs) {
    & $dotnet $csc.FullName @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "compile failed ($LASTEXITCODE)" }
}

# --- 1. the library ---
$libDll = Join-Path $out "PixelSenseToTouchLib.dll"
$libSrc = @(
    (Join-Path $lib "ITouchSink.cs"),
    (Join-Path $lib "InjectTouchSink.cs"),
    (Join-Path $lib "HidDigitizerSink.cs"),
    (Join-Path $lib "HydraIdleHint.cs"),
    (Join-Path $lib "PixelSenseToTouch.cs"),
    (Join-Path $lib "Properties\AssemblyInfo.cs")
)
foreach ($s in $libSrc) { if (-not (Test-Path $s)) { throw "Missing source: $s" } }

Write-Host "Compiling PixelSenseToTouchLib..." -ForegroundColor Cyan
$a = @('-nologo','-nostdlib','-noconfig','-target:library','-platform:x86',"-define:$defines",$optimize,
       '-debug-',"-out:$libDll")
$a += ($fxRefs + $extRefs | ForEach-Object { "-r:$_" })
$a += $libSrc
Invoke-Csc $a

# --- 2. the tray executable ---
$exe = Join-Path $out "PixelSenseToTouch.exe"
$traySrc = @(
    (Join-Path $tray "PixelSenseToTouchTray.cs"),
    (Join-Path $tray "Properties\AssemblyInfo.cs")
)
foreach ($s in $traySrc) { if (-not (Test-Path $s)) { throw "Missing source: $s" } }

# The tray falls back to an icon embedded in its own assembly when the loose file is absent, so it
# has to be embedded here exactly as the .csproj does.
$icon = Join-Path $tray "trayicon.ico"
Write-Host "Compiling PixelSenseToTouch.exe..." -ForegroundColor Cyan
$a = @('-nologo','-nostdlib','-noconfig','-target:winexe','-platform:x86',"-define:$defines",$optimize,
       '-debug-',"-out:$exe")
$a += ($fxRefs + $extRefs | ForEach-Object { "-r:$_" })
$a += "-r:$libDll"
if (Test-Path $icon) { $a += "-win32icon:$icon"; $a += "-resource:$icon,PixelSenseToTouch.trayicon.ico" }
$a += $traySrc
Invoke-Csc $a

# --- 3. everything the output needs beside it ---
Copy-Item $extRefs $out -Force
Copy-Item (Join-Path $surface "Microsoft.Surface.dll") $out -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $surface "Microsoft.Surface.Tools.dll") $out -Force -ErrorAction SilentlyContinue
if (Test-Path $icon) { Copy-Item $icon $out -Force }
$appConfig = Join-Path $tray "App.config"
if (Test-Path $appConfig) { Copy-Item $appConfig (Join-Path $out "PixelSenseToTouch.exe.config") -Force }

Write-Host ""
Write-Host "Built into $out" -ForegroundColor Green
Get-ChildItem $out -File | Select-Object Name, Length | Format-Table -AutoSize

if ($Stage) {
    $dist = Resolve-Path (Join-Path $root "..\..\dist\pixelsense2touch")
    Write-Host "Staging into $dist ..." -ForegroundColor Cyan
    Copy-Item (Join-Path $out "PixelSenseToTouch.exe") $dist -Force
    Copy-Item (Join-Path $out "PixelSenseToTouchLib.dll") $dist -Force
    Write-Host "Staged. Injection is the default sink; PIXELSENSETOUCH_SINK=hid opts into the HydraTouch digitizer." -ForegroundColor Green
}
