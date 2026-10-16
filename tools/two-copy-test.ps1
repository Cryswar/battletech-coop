# Starts two game copies on this PC (host + guest over localhost TCP), waits until each side has
# received the other's test message, prints both logs and closes the games.
# Exit code 0 = pass. Requires Steam running and two test installs (ModTek allows one process per folder).
#
#   powershell -ExecutionPolicy Bypass -File tools\two-copy-test.ps1
param(
    [string]$HostDir = 'D:\Modding\Battletech\TestInstall',
    [string]$GuestDir = 'D:\Modding\Battletech\TestInstall2',
    [int]$Port = 47637,
    [int]$TimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$window = @('-screen-fullscreen', '0', '-screen-width', '1024', '-screen-height', '600')

function Get-CoopLog([string]$dir) { Join-Path $dir 'Mods\BattletechCoop\BattletechCoop.log' }

function Wait-ForLog([string]$log, [string]$pattern, [System.Diagnostics.Process]$proc, [datetime]$deadline) {
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and (Select-String -Path $log -Pattern $pattern -Quiet)) { return $true }
        if ($proc.HasExited) { Write-Host "process $($proc.Id) exited (code $($proc.ExitCode))"; return $false }
        Start-Sleep -Milliseconds 500
    }
    Write-Host "timed out waiting for '$pattern' in $log"
    return $false
}

if (Get-Process BattleTech -ErrorAction SilentlyContinue) { throw 'A BattleTech process is already running; close it first.' }
foreach ($dir in $HostDir, $GuestDir) {
    $log = Get-CoopLog $dir
    if (Test-Path $log) { Remove-Item $log }
}

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$hostLog = Get-CoopLog $HostDir
$guestLog = Get-CoopLog $GuestDir
$hostProc = $null; $guestProc = $null
$passed = $false
try {
    $hostProc = Start-Process "$HostDir\BattleTech.exe" -WorkingDirectory $HostDir -PassThru `
        -ArgumentList (@('-coop-host-tcp', "$Port", '-coop-name', 'HostBot', '-coop-test-message') + $window)
    Write-Host "host pid $($hostProc.Id)"
    if (Wait-ForLog $hostLog 'Hosting via' $hostProc $deadline) {
        $guestProc = Start-Process "$GuestDir\BattleTech.exe" -WorkingDirectory $GuestDir -PassThru `
            -ArgumentList (@('-coop-join-tcp', "127.0.0.1:$Port", '-coop-name', 'GuestBot', '-coop-test-message') + $window)
        Write-Host "guest pid $($guestProc.Id)"
        $guestGot = Wait-ForLog $guestLog 'Test message from player 0' $guestProc $deadline
        $hostGot = Wait-ForLog $hostLog 'Test message from player 1' $hostProc $deadline
        $passed = $guestGot -and $hostGot
    }
}
finally {
    foreach ($p in $guestProc, $hostProc) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    }
}

foreach ($pair in @(@('HOST', $hostLog), @('GUEST', $guestLog))) {
    Write-Host "===== $($pair[0]) log ====="
    if (Test-Path $pair[1]) { Get-Content $pair[1] | Where-Object { $_ -notmatch 'GenericInvoke' } } else { Write-Host '(no log)' }
}
if ($passed) { Write-Host 'RESULT: PASS'; exit 0 } else { Write-Host 'RESULT: FAIL'; exit 1 }
