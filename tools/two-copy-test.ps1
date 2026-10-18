# Starts two game copies on this PC (host + guest over localhost TCP) and checks them, then closes the games.
# Exit code 0 = pass. Requires Steam running and two test installs (ModTek allows one process per folder).
#
#   Network test (default): each side must receive the other's test message.
#     powershell -ExecutionPolicy Bypass -File tools\two-copy-test.ps1
#   Combat test: the host auto-launches a fixed skirmish with the AI playing both sides; the guest
#   replays the host's stream. Passes when the guest has applied -TargetSeq stream items (or the
#   mission ended) with zero desyncs.
#     powershell -ExecutionPolicy Bypass -File tools\two-copy-test.ps1 -Combat [-TargetSeq 60] [-TimeScale 3]
param(
    [string]$HostDir = 'D:\Modding\Battletech\TestInstall',
    [string]$GuestDir = 'D:\Modding\Battletech\TestInstall2',
    [int]$Port = 47637,
    [switch]$Combat,
    # Shared control test: the Player 1 AI alternates between guest and host each activation, so
    # half of Player 1's orders are guest requests. Also requires accepted and zero rejected requests.
    [switch]$Shared,
    # Send the skirmish to the guest as a combat save, the way campaign missions travel (M2).
    [switch]$SaveTransfer,
    # Compare every unit, pilot and component statistic, not just a digest (slower, bigger).
    [switch]$DeepState,
    # After -TargetSeq items, kill the host's game; PASS needs the guest to return to its main menu.
    [switch]$HostQuitTest,
    # Ship view: the host loads its newest campaign save (read-only); PASS when the guest shows the
    # same company, funds, day and system the host sent.
    [switch]$ShipView,
    [switch]$ShipRequests,
    [int]$TargetSeq = 60,
    [double]$TimeScale = 3,
    [string]$Map = '',
    # Detector self-test: corrupt guest state before this stream item; then PASS means a DESYNC was reported.
    [int]$InjectDesyncAt = 0,
    [int]$TimeoutSeconds = 0
)

$ErrorActionPreference = 'Stop'
if ($TimeoutSeconds -le 0) { $TimeoutSeconds = if ($Combat) { 1200 } else { 300 } }
$window = @('-screen-fullscreen', '0', '-screen-width', '1024', '-screen-height', '600')

function Get-CoopLog([string]$dir) { Join-Path $dir 'Mods\BattletechCoop\BattletechCoop.log' }

function Read-Log([string]$log) { if (Test-Path $log) { Get-Content $log -ErrorAction SilentlyContinue } else { @() } }

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
# -coop-no-saves on both: the test copies share the owner's Steam Cloud saves.
$hostArgs = @('-coop-host-tcp', "$Port", '-coop-name', 'HostBot', '-coop-no-saves')
$guestArgs = @('-coop-join-tcp', "127.0.0.1:$Port", '-coop-name', 'GuestBot', '-coop-no-saves')
if ($Combat) {
    $hostArgs += @($(if ($Shared) { '-coop-autoplay-shared' } else { '-coop-autoplay' }), '-coop-autoskirmish')
    if ($Map) { $hostArgs += $Map }
    if ($SaveTransfer) { $hostArgs += '-coop-save-transfer' }
    if ($DeepState) { $hostArgs += '-coop-deep-state'; $guestArgs += '-coop-deep-state' }
    $ts = $TimeScale.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    $hostArgs += @('-coop-timescale', $ts)
    $guestArgs += @('-coop-timescale', $ts)
    if ($InjectDesyncAt -gt 0) { $guestArgs += @('-coop-inject-desync', "$InjectDesyncAt") }
} elseif ($ShipView -or $ShipRequests) {
    $hostArgs += '-coop-test-shipview'
    if ($ShipRequests) { $guestArgs += '-coop-test-requests'; $hostArgs += '-coop-test-requests' }
} else {
    $hostArgs += '-coop-test-message'
    $guestArgs += '-coop-test-message'
}

$hostProc = $null; $guestProc = $null
$passed = $false
$reason = ''
try {
    $hostProc = Start-Process "$HostDir\BattleTech.exe" -WorkingDirectory $HostDir -PassThru -ArgumentList ($hostArgs + $window)
    Write-Host "host pid $($hostProc.Id)"
    if (-not (Wait-ForLog $hostLog 'Hosting via' $hostProc $deadline)) { throw 'host did not start hosting' }
    $guestProc = Start-Process "$GuestDir\BattleTech.exe" -WorkingDirectory $GuestDir -PassThru -ArgumentList ($guestArgs + $window)
    Write-Host "guest pid $($guestProc.Id)"

    if ($ShipRequests) {
        $done = Wait-ForLog $guestLog 'SHIP test: guest pilot now' $guestProc $deadline
        $end = (Get-Date).AddSeconds(20)
        while ((Get-Date) -lt $end -and (@(Read-Log $guestLog | Where-Object { $_ -match 'SHIP guest: host (applied|refused)' }).Count -lt 4 -or
                @(Read-Log $guestLog | Where-Object { $_ -match "SHIP guest: (applied|could not apply) the host's change" }).Count -lt 4)) { Start-Sleep -Milliseconds 500 }
        $h = Read-Log $hostLog; $g = Read-Log $guestLog
        $replies = @($g | Where-Object { $_ -match 'SHIP guest: host (applied|refused)' })
        $refused = @($replies | Where-Object { $_ -match 'refused' })
        $errors = @($h + $g | Where-Object { $_ -cmatch '\[ERROR\]' })
        $askRefit = @($g | Where-Object { $_ -match "asked the host to refit '.*' \((.*)\)$" }) | Select-Object -Last 1
        $hostRefit = @($h | Where-Object { $_ -match "queued refit '.*' \((.*)\), funds" }) | Select-Object -Last 1
        $a = if ($askRefit -match "refit '(.*)' \((.*)\)$") { "$($Matches[1]) $($Matches[2])" } else { '' }
        $b = if ($hostRefit -match "queued refit '(.*)' \((.*)\), funds") { "$($Matches[1]) $($Matches[2])" } else { '' }
        $gp = @($g | Where-Object { $_ -match 'SHIP test: guest pilot now (.*)$' }) | Select-Object -Last 1
        $hp = @($h | Where-Object { $_ -match 'SHIP host: pilot now (.*)$' }) | Select-Object -Last 1
        $gps = if ($gp -match 'guest pilot now (.*)$') { $Matches[1] } else { '' }
        $hps = if ($hp -match 'host: pilot now (.*)$') { $Matches[1] } else { '' }
        # Host -> guest: the host's own changes, applied on the guest as updates.
        $updates = @($g | Where-Object { $_ -match "SHIP guest: (applied|could not apply) the host's change" })
        $failedUpdates = @($updates | Where-Object { $_ -match 'could not apply' })
        $toldRefit = @($h | Where-Object { $_ -match "told the guests: refit '.*' \((.*)\)$" }) | Select-Object -Last 1
        $gotRefit = @($g | Where-Object { $_ -match "SHIP guest: queued refit '.*' \((.*)\), funds" }) | Select-Object -Last 1
        $c = if ($toldRefit -match "refit '(.*)' \((.*)\)$") { "$($Matches[1]) $($Matches[2])" } else { '' }
        $e = if ($gotRefit -match "queued refit '(.*)' \((.*)\), funds") { "$($Matches[1]) $($Matches[2])" } else { '' }
        $hp2 = @($h | Where-Object { $_ -match 'SHIP test: host pilot now (.*)$' }) | Select-Object -Last 1
        $gp2 = @($g | Where-Object { $_ -match 'SHIP guest: pilot now (.*)$' }) | Select-Object -Last 1
        $hps2 = if ($hp2 -match 'host pilot now (.*)$') { $Matches[1] } else { '' }
        $gps2 = if ($gp2 -match 'guest: pilot now (.*)$') { $Matches[1] } else { '' }
        $passed = $done -and $replies.Count -ge 3 -and $refused.Count -eq 0 -and $errors.Count -eq 0 -and $a -and ($a -eq $b) -and $gps -and ($gps -eq $hps) -and
            $updates.Count -ge 3 -and $failedUpdates.Count -eq 0 -and $c -and ($c -eq $e) -and $hps2 -and ($hps2 -eq $gps2)
        $reason = "guest->host: replies $($replies.Count) (refused $($refused.Count)); refit guest [$a] host [$b]; pilot guest [$gps] host [$hps]. " +
            "host->guest: updates $($updates.Count) (failed $($failedUpdates.Count)); refit host [$c] guest [$e]; pilot host [$hps2] guest [$gps2]. errors $($errors.Count)"
        if ($failedUpdates.Count -gt 0) { $reason += "; first failed update: $($failedUpdates[0])" }
        if ($refused.Count -gt 0) { $reason += "; first refusal: $($refused[0])" }
        if ($errors.Count -gt 0) { $reason += "; first error: $($errors[0])" }
    }
    elseif ($ShipView) {
        $shown = Wait-ForLog $guestLog 'SHIP guest: now showing' $guestProc $deadline
        $sentLine = @(Read-Log $hostLog | Where-Object { $_ -match 'SHIP host: sent (.*) \(\d+ bytes' }) | Select-Object -Last 1
        $shownLine = @(Read-Log $guestLog | Where-Object { $_ -match 'SHIP guest: now showing (.*)$' }) | Select-Object -Last 1
        $sent = if ($sentLine -match 'SHIP host: sent (.*) \(\d+ bytes') { $Matches[1] } else { '' }
        $seen = if ($shownLine -match 'now showing (.*)$') { $Matches[1] } else { '' }
        $passed = $shown -and $sent -and ($sent -eq $seen)
        $reason = "host sent [$sent], guest shows [$seen]"
    }
    elseif (-not $Combat) {
        $guestGot = Wait-ForLog $guestLog 'Test message from player 0' $guestProc $deadline
        $hostGot = Wait-ForLog $hostLog 'Test message from player 1' $hostProc $deadline
        $passed = $guestGot -and $hostGot
    }
    else {
        $lastReport = Get-Date
        while ($true) {
            if ((Get-Date) -gt $deadline) { $reason = "timed out after $TimeoutSeconds s"; break }
            if ($hostProc.HasExited) { $reason = "host exited (code $($hostProc.ExitCode))"; break }
            if ($guestProc.HasExited) { $reason = "guest exited (code $($guestProc.ExitCode))"; break }
            $h = Read-Log $hostLog; $g = Read-Log $guestLog
            # Case-sensitive: status lines say "desyncs 0", only real reports say "DESYNC".
            $bad = @($h + $g | Where-Object { $_ -cmatch 'DESYNC|\[ERROR\]' })
            if ($InjectDesyncAt -gt 0) {
                $caught = @($g | Where-Object { $_ -cmatch 'SYNC DESYNC at seq' })
                if ($caught.Count -gt 0) { $passed = $true; $reason = "injected desync detected: $($caught[0])"; break }
                $bad = @($bad | Where-Object { $_ -notmatch 'DESYNC' })
            }
            if ($bad.Count -gt 0) { $reason = "error or desync: $($bad[0])"; break }
            $applied = 0
            foreach ($line in $g) { if ($line -match 'SYNC guest: seq (\d+) ') { $applied = [int]$Matches[1] } }
            $guestOver = @($g | Where-Object { $_ -match 'SYNC mission over' }).Count -gt 0
            if ($applied -ge $TargetSeq -or $guestOver) {
                if ($InjectDesyncAt -gt 0) { $reason = "injected desync at seq $InjectDesyncAt was NOT detected ($applied items applied)"; break }
                $passed = $true
                $reason = if ($guestOver) { "mission over on guest after $applied stream items" } else { "guest applied $applied stream items" }
                break
            }
            if (((Get-Date) - $lastReport).TotalSeconds -ge 30) {
                $sent = 0
                foreach ($line in $h) { if ($line -match 'SYNC host: seq (\d+) ') { $sent = [int]$Matches[1] } }
                Write-Host "$(Get-Date -Format HH:mm:ss) host sent $sent, guest applied $applied"
                $lastReport = Get-Date
            }
            Start-Sleep -Seconds 1
        }
        if ($passed -and $HostQuitTest) {
            Write-Host "killing the host's game to check the guest's return to the main menu"
            Stop-Process -Id $hostProc.Id -Force
            $back = Wait-ForLog $guestLog 'Returning to the main menu' $guestProc (Get-Date).AddSeconds(90)
            Start-Sleep -Seconds 10
            if (-not $back) { $passed = $false; $reason += "; guest did not return to the main menu after the host quit" }
            else { $reason += "; guest returned to the main menu after the host quit" }
        }
        # Let the logs settle, then re-check for late desyncs.
        Start-Sleep -Seconds 2
        $late = @((Read-Log $hostLog) + (Read-Log $guestLog) | Where-Object { $_ -cmatch 'DESYNC|\[ERROR\]' })
        if ($InjectDesyncAt -gt 0) { $late = @($late | Where-Object { $_ -notmatch 'DESYNC' }) }
        # The game's own log: a crash while setting up the contract (e.g. spawning a lance) leaves
        # that side stuck before "Begin mission" without any error in our log.
        foreach ($pair in @(@('host', $HostDir), @('guest', $GuestDir))) {
            $gameLog = Join-Path $pair[1] 'Mods\.modtek\battletech_log.txt'
            if (-not (Test-Path $gameLog)) { continue }
            $setup = @(Select-String -Path $gameLog -Pattern 'Delegate (InitializeContract|OnInitializeContract)' | ForEach-Object { $_.Line })
            $critical = @(Select-String -Path $gameLog -Pattern 'CRITICAL ERROR' -CaseSensitive).Count
            if ($critical -gt 0) { Write-Host "$($pair[0]) game log: $critical CRITICAL ERROR entries" }
            if ($setup.Count -gt 0) { $passed = $false; $reason += "; $($pair[0]) crashed setting up the contract: $($setup[0])" }
        }
        if ($passed -and $late.Count -gt 0) { $passed = $false; $reason = "error or desync: $($late[0])" }
        if ($Shared) {
            $h = Read-Log $hostLog
            $acceptedOrders = @($h | Where-Object { $_ -cmatch 'accepted guest \d+ request \d+ (?!AIComplete)' }).Count
            $acceptedComplete = @($h | Where-Object { $_ -cmatch 'accepted guest \d+ request \d+ AIComplete' }).Count
            $rejected = @($h | Where-Object { $_ -cmatch 'rejected guest' })
            Write-Host "guest requests accepted: $acceptedOrders orders, $acceptedComplete AI completions; rejected: $($rejected.Count)"
            if ($passed -and $acceptedOrders -lt 5) { $passed = $false; $reason += "; too few guest orders ($acceptedOrders)" }
            if ($passed -and $rejected.Count -gt 0) { $passed = $false; $reason += "; rejected: $($rejected[0])" }
        }
    }
}
finally {
    foreach ($p in $guestProc, $hostProc) {
        if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force }
    }
    # Wait until they are really gone, so a following run doesn't find them still exiting.
    foreach ($p in $guestProc, $hostProc) {
        if ($p) { [void]$p.WaitForExit(30000) }
    }
}

foreach ($pair in @(@('HOST', $hostLog), @('GUEST', $guestLog))) {
    Write-Host "===== $($pair[0]) log ====="
    $lines = @(Read-Log $pair[1] | Where-Object { $_ -notmatch 'GenericInvoke' })
    if ($Combat -and $lines.Count -gt 60) {
        $lines[0..19] + @("... ($($lines.Count - 60) lines omitted) ...") + $lines[($lines.Count - 40)..($lines.Count - 1)]
    } else { $lines }
}
if ($reason) { Write-Host "REASON: $reason" }
if ($passed) { Write-Host 'RESULT: PASS'; exit 0 } else { Write-Host 'RESULT: FAIL'; exit 1 }
