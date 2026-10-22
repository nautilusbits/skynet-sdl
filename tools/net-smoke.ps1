#Requires -Version 7
<#
.SYNOPSIS
    Two-process deathmatch smoke test: a host and a client on 127.0.0.1 see each other, trade one
    hit, and leave.

.DESCRIPTION
    Starts skynet-sdl.exe --host=<map> --smoke on any free port (--port=0) bound to 127.0.0.1, with no
    LAN discovery (so nothing collides with a real game on 27015/27016 and no firewall prompt),
    reads the port from its "listening on port N" line, then runs skynet-sdl.exe --join=127.0.0.1:N
    --smoke. Each process exits 0 once its side passed (docs/multiplayer.md, step 1): the client
    after it was welcomed, spawned, saw the host's pose, hit the host and was hit back; the host
    after it saw the client's pose, was hit, hit back and saw the client leave. The host has a
    password (the client proves it) and BALANCED classes, and its log must say so. Exits 0 when both
    passed, 1 otherwise, printing both logs.

    -Dedicated checks the dedicated server instead: skynet-sdl.exe --dedicated --config <a temporary
    server.json> --smoke (port 0, 127.0.0.1, no discovery, no log file, DOS classes, a two-map rotation
    whose first round ends at one kill), then two clients --join --smoke=dedicated: the lower player shoots the
    other until the round ends, the server rotates to the next map and welcomes both there, both leave;
    the server exits 0 once two players joined, the map rotated and everybody left. Its log must also
    say that the native SDL3 library was never loaded and that the classes are DOS's.

    -Master runs the -Dedicated check through a master server: SkyNet.Master on 127.0.0.1 (any free
    port), the dedicated server public with "master" pointing there, and the two clients without an
    address: --find asks the master's list for the server by name and joins it through the master's
    NAT introduction. Passes when -Dedicated's checks pass and the logs show the listing, the
    finds, two introductions and the server's goodbye.

.EXAMPLE
    pwsh tools/net-smoke.ps1
    pwsh tools/net-smoke.ps1 -Dedicated
    pwsh tools/net-smoke.ps1 -Master
    pwsh tools/net-smoke.ps1 -Map MAP.601 -Exe release\skynet-sdl.exe
#>
param(
    # The arena.
    [string] $Map = 'MAP.605',
    # The game; default the Debug build.
    [string] $Exe = (Join-Path $PSScriptRoot '..\src\SkyNet.App\bin\Debug\net10.0\skynet-sdl.exe'),
    # The game's data folder; default SKYNET_DATA (else the game's own search).
    [string] $Data = $env:SKYNET_DATA,
    # Seconds each side gets before it gives up.
    [int] $Timeout = 30,
    # The dedicated server and two clients instead of a host and a client.
    [switch] $Dedicated,
    # The dedicated check's second map.
    [string] $NextMap = 'MAP.601',
    # The dedicated check through a master server (implies -Dedicated).
    [switch] $Master,
    # The master server; default the Debug build.
    [string] $MasterExe = (Join-Path $PSScriptRoot 'SkyNet.Master\bin\Debug\net10.0\SkyNet.Master.exe')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { throw "No game at $Exe - build it first (dotnet build)." }
if ($Master) {
    $Dedicated = $true
    if (-not (Test-Path $MasterExe)) { throw "No master server at $MasterExe - build it first (dotnet build)." }
}
$common = @('--offscreen', '--smoke', "--quit-after=$Timeout")
if ($Data) { $common += "--data=$Data" }

$logs = Join-Path ([System.IO.Path]::GetTempPath()) "skynet-net-smoke-$PID"
New-Item -ItemType Directory -Force $logs | Out-Null

function Start-Side([string] $name, [string[]] $arguments, [string] $program = $Exe, [switch] $Bare) {
    $out = Join-Path $logs "$name.txt"
    $all = if ($Bare) { $arguments } else { $arguments + $common }
    $p = Start-Process -FilePath (Resolve-Path $program).Path -ArgumentList $all -NoNewWindow -PassThru `
        -RedirectStandardOutput $out -RedirectStandardError (Join-Path $logs "$name.err.txt")
    return @{ Process = $p; Out = $out; Name = $name }
}

function Show-Side($side) {
    Write-Host "--- $($side.Name)"
    foreach ($f in $side.Out, ($side.Out -replace '\.txt$', '.err.txt')) {
        if (Test-Path $f) { Get-Content $f | ForEach-Object { Write-Host $_ } }
    }
}

$masterSide = $null
$masterPort = $null
if ($Master) {
    $masterSide = Start-Side 'master' @('--bind', '127.0.0.1', '--port', '0', '--quit-after', "$($Timeout + 60)") $MasterExe -Bare
    $deadline = (Get-Date).AddSeconds(30)
    while (-not $masterPort -and (Get-Date) -lt $deadline -and -not $masterSide.Process.HasExited) {
        if ((Test-Path $masterSide.Out) -and ((Get-Content $masterSide.Out -Raw) -match 'listening on port (\d+)')) { $masterPort = $Matches[1] }
        Start-Sleep -Milliseconds 100
    }

    if (-not $masterPort) {
        if (-not $masterSide.Process.HasExited) { $masterSide.Process.Kill() }
        Show-Side $masterSide
        throw 'The master server did not start listening.'
    }
}

if ($Dedicated) {
    $config = Join-Path $logs 'server.json'
    $serverName = if ($Master) { 'MASTER-SMOKE' } else { 'SMOKE TEST' }
    $public = if ($Master) { "`"public`": true, `"master`": `"127.0.0.1:$masterPort`"," } else { '' }
    @"
{
  "name": "$serverName", "port": 0, "bind": "127.0.0.1", "discovery": false, "log_file": "", "ban_file": "", $public
  "motd": "smoke test", "intermission_seconds": 1, "idle_kick_seconds": 0, "classes": "dos",
  "rotation": [
    { "map": "$Map", "goal": "kills", "goal_limit": 1, "time": 0 },
    { "map": "$NextMap", "goal": "kills", "goal_limit": 1, "time": 0 }
  ]
}
"@ | Set-Content -Encoding utf8NoBOM $config
    $common = @('--offscreen', "--quit-after=$Timeout")
    if ($Data) { $common += "--data=$Data" }
    $hostSide = Start-Side 'server' @('--dedicated', "--config=$config", '--smoke')
}
else {
    $hostSide = Start-Side 'host' @("--host=$Map", '--port=0', '--bind=127.0.0.1', '--no-discovery', '--name=HOST', '--password=smoke')
}
$port = $null
$deadline = (Get-Date).AddSeconds(30)
while (-not $port -and (Get-Date) -lt $deadline -and -not $hostSide.Process.HasExited) {
    if ((Test-Path $hostSide.Out) -and ((Get-Content $hostSide.Out -Raw) -match 'listening on port (\d+)')) { $port = $Matches[1] }
    Start-Sleep -Milliseconds 100
}

if (-not $port) {
    if (-not $hostSide.Process.HasExited) { $hostSide.Process.Kill() }
    Show-Side $hostSide
    throw 'The host did not start listening.'
}

if ($Dedicated) {
    $common = @('--offscreen', '--smoke=dedicated', "--quit-after=$Timeout")
    if ($Data) { $common += "--data=$Data" }
    $where = if ($Master) { @('--find=MASTER-SMOKE', "--master=127.0.0.1:$masterPort") } else { @("--join=127.0.0.1:$port") }
    $clients = @((Start-Side 'client1' (@($where) + '--name=ALPHA')), (Start-Side 'client2' (@($where) + '--name=BRAVO')))
}
else {
    $clients = @(Start-Side 'client' @("--join=127.0.0.1:$port", '--name=CLIENT', '--password=smoke'))
}

foreach ($side in @($clients) + $hostSide) {
    if (-not $side.Process.WaitForExit(($Timeout + 10) * 1000)) { $side.Process.Kill() }
    $side.Process.WaitForExit()
}

Show-Side $hostSide
foreach ($c in $clients) { Show-Side $c }
$ok = $hostSide.Process.ExitCode -eq 0
foreach ($c in $clients) { $ok = $ok -and $c.Process.ExitCode -eq 0 }
if ($Dedicated) {
    $serverLog = Get-Content $hostSide.Out -Raw
    if ($serverLog -notmatch 'SDL: native SDL3 library not loaded' -or $serverLog -match 'native SDL3 LOADED') {
        Write-Host 'net smoke: the dedicated server loaded SDL (or did not say)'
        $ok = $false
    }

    if ($serverLog -notmatch 'classes DOS, password off') {
        Write-Host "net smoke: the dedicated server did not take the config's DOS classes"
        $ok = $false
    }
}
elseif ((Get-Content $hostSide.Out -Raw) -notmatch 'classes BALANCED, password on') {
    Write-Host 'net smoke: the host did not say BALANCED classes and its password'
    $ok = $false
}

if ($Master) {
    # The server's goodbye reaches the master as it stops; give it a moment, then stop the master.
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Date) -lt $deadline -and -not ((Get-Content $masterSide.Out -Raw) -match 'said goodbye')) { Start-Sleep -Milliseconds 100 }
    if (-not $masterSide.Process.HasExited) { $masterSide.Process.Kill() }
    $masterSide.Process.WaitForExit()
    Show-Side $masterSide
    $masterLog = Get-Content $masterSide.Out -Raw
    $checks = @(
        @{ What = 'the master listed the server'; Ok = $masterLog -match "'MASTER-SMOKE' .* listed" },
        @{ What = 'the master introduced both clients'; Ok = ([regex]::Matches($masterLog, 'introduced 127\.0\.0\.1:\d+ to')).Count -ge 2 },
        @{ What = 'the server said goodbye'; Ok = $masterLog -match "'MASTER-SMOKE' said goodbye" },
        @{ What = 'the server was listed'; Ok = (Get-Content $hostSide.Out -Raw) -match 'master server 127\.0\.0\.1:\d+: listed as 127\.0\.0\.1:\d+' }
    )
    foreach ($c in $clients) {
        $checks += @{ What = "$($c.Name) found the server in the list"; Ok = (Get-Content $c.Out -Raw) -match "found 'MASTER-SMOKE' at 127\.0\.0\.1:\d+ in the master's list" }
        $checks += @{ What = "$($c.Name) was introduced (the server's punch came through)"; Ok = (Get-Content $c.Out -Raw) -match 'the master server introduced this client' }
    }

    foreach ($c in $checks) {
        if (-not $c.Ok) {
            Write-Host "net smoke: $($c.What): NO"
            $ok = $false
        }
    }
}

$codes = (@($hostSide) + $clients | ForEach-Object { "$($_.Name) exit $($_.Process.ExitCode)" }) -join ', '
$kind = if ($Master) { ' (master)' } elseif ($Dedicated) { ' (dedicated)' } else { '' }
Write-Host ("net smoke$kind" + ": " + ($(if ($ok) { 'PASS' } else { "FAIL ($codes)" })))
Remove-Item -Recurse -Force $logs
exit ($(if ($ok) { 0 } else { 1 }))
