#Requires -Version 7
<#
.SYNOPSIS
    Fixed-view screenshots of the game for look regressions: the same views, the same frame, every run.

.DESCRIPTION
    Runs skynet-sdl.exe once per view with --offscreen --freeze --frames 30 --screenshot (no simulation
    ticks once the map is in, a fixed animation clock, no FPS readout or debug overlay; the hidden
    window's frame is 1280 x 960), converts each BMP to a PNG in -Out and
    writes hashes.txt there (SHA-256 of each PNG, one "name hash" line per view). Two runs of the
    same build on the same machine give the same hashes; with -Compare it checks them against an
    earlier hashes.txt and exits 1 on any difference.

    The frame also depends on the settings file's brightness, render scale and HUD art (the
    command line does not override those), so compare runs made with the same settings.
    Keep the shots out of git: the repo ignores lookshots/ at its root.

.EXAMPLE
    pwsh tools/lookshots.ps1 -Out lookshots/original
    pwsh tools/lookshots.ps1 -Out lookshots/after -Compare lookshots/original/hashes.txt
    pwsh tools/lookshots.ps1 -Out lookshots/enhanced -Look enhanced
    pwsh tools/lookshots.ps1 -Out lookshots/single -Threads single -Compare lookshots/original/hashes.txt
#>
param(
    # The folder the PNGs and hashes.txt go into (made when missing).
    [Parameter(Mandatory)] [string] $Out,
    # The DISPLAY screen's LOOK for these runs.
    [ValidateSet('original', 'enhanced')] [string] $Look = 'original',
    [ValidateSet('single', 'split')] [string] $Threads = 'split',
    # An earlier run's hashes.txt to compare with.
    [string] $Compare,
    # The game; default the Debug build.
    [string] $Exe = (Join-Path $PSScriptRoot '..\src\SkyNet.App\bin\Debug\net10.0\skynet-sdl.exe'),
    # The game's data folder; default SKYNET_DATA (else the game's own search).
    [string] $Data = $env:SKYNET_DATA
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# name, then the arguments after the common ones.
$views = [ordered]@{
    'map210-start'   = @('--map', '210')
    'map210-valley'  = @('--map', '210', '--pos', '59200,1700,-53900', '--yaw', '86', '--pitch', '-10')
    'map250-dusk'    = @('--map', '250')
    'map211-indoor'  = @('--map', '211')
    'map220-jeep'    = @('--map', '220')
}

$exePath = (Resolve-Path $Exe).Path
New-Item -ItemType Directory -Force $Out | Out-Null
$outDir = (Resolve-Path $Out).Path
# Every setting a shot depends on is given here: the player's settings file (beside the exe) must
# not change a shot.
$common = @('--offscreen', '--freeze', '--no-briefing', '--frames', '30', '--detail', 'high',
    '--hires-hud', '--look', $Look, '--no-mods', '--threads', $Threads)
if ($Look -eq 'enhanced') { $common += @('--shadows', 'high', '--aa', 'fxaa') }
if ($Data) { $common += @('--data', $Data) }

$lines = @()
foreach ($name in $views.Keys) {
    $bmp = Join-Path $outDir "$name.bmp"
    $png = Join-Path $outDir "$name.png"
    Remove-Item -ErrorAction Ignore $bmp, $png
    $arguments = $common + $views[$name] + @('--screenshot', $bmp)
    $process = Start-Process -FilePath $exePath -ArgumentList $arguments -Wait -PassThru -NoNewWindow
    if ($process.ExitCode -ne 0 -or -not (Test-Path $bmp)) {
        throw "$name`: skynet-sdl.exe exited $($process.ExitCode) without $bmp (see the game's log)"
    }

    $image = [System.Drawing.Image]::FromFile($bmp)
    try { $image.Save($png, [System.Drawing.Imaging.ImageFormat]::Png) } finally { $image.Dispose() }
    Remove-Item $bmp
    $hash = (Get-FileHash -Algorithm SHA256 $png).Hash.ToLowerInvariant()
    $lines += "$name $hash"
    Write-Host "$name $hash"
}

$hashes = Join-Path $outDir 'hashes.txt'
Set-Content -Path $hashes -Value $lines
Write-Host "Wrote $($lines.Count) shots and $hashes"

if ($Compare) {
    $before = @{}
    foreach ($line in Get-Content $Compare) {
        $name, $hash = $line -split ' ', 2
        $before[$name] = $hash
    }

    $changed = 0
    foreach ($line in $lines) {
        $name, $hash = $line -split ' ', 2
        if ($before[$name] -eq $hash) {
            Write-Host "same    $name"
        }
        else {
            Write-Host "CHANGED $name"
            $changed++
        }
    }

    if ($changed -gt 0) {
        Write-Host "$changed of $($lines.Count) shots differ from $Compare"
        exit 1
    }

    Write-Host "All $($lines.Count) shots match $Compare"
}
