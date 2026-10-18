# Builds dist\BattletechCoop-<version>-ModTek-<modtek>.zip: the official ModTek release, unmodified,
# plus this mod's Release build and install notes. Everyone who plays together installs the same zip,
# so their Mods folders produce the same fingerprint.
#
#   powershell -ExecutionPolicy Bypass -File tools\make-release.ps1
param(
    [string]$ModTekZip = "$PSScriptRoot\..\local\downloads\modtek-4.5.1\ModTek.zip",
    [string]$ModTekVersion = '4.5.1',
    [string]$ModTekSha256 = '53c038c1142c1552dcf7687bbee2b00d795b4691ff50a8c10901913cfa2339a9'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Resolve-Path "$PSScriptRoot\.."

$actual = (Get-FileHash $ModTekZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $ModTekSha256) { throw "ModTek.zip hash mismatch: $actual" }

dotnet build "$root\src\BattletechCoop" -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'build failed' }
$bin = "$root\src\BattletechCoop\bin\Release"
$version = (Get-Content "$bin\mod.json" -Raw | ConvertFrom-Json).Version

$stage = "$root\local\release-stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null
[System.IO.Compression.ZipFile]::ExtractToDirectory($ModTekZip, $stage)

$modDir = New-Item -ItemType Directory "$stage\Mods\BattletechCoop"
Copy-Item "$bin\BattletechCoop.dll", "$bin\mod.json" $modDir

$notes = @"
BattleTech Co-op $version (with ModTek $ModTekVersion)
=====================================================

PRE-ALPHA. Co-op works in SKIRMISH only, with shared control: every player can command every
mech in the player lance. Campaign contracts are not supported yet (they start single-player).

Install (every player, same zip)
--------------------------------
1. In Steam: right-click BATTLETECH > Manage > Browse local files. This opens the folder
   that contains BattleTech.exe.
2. Extract this zip's contents into that folder, so winhttp.dll sits next to BattleTech.exe
   and the Mods folder sits next to it too.
3. Your Mods folder must contain only ModTek and BattletechCoop (plus the .modtek folder that
   ModTek creates). Any other mod makes your mod list differ, and joining is refused.
4. Start the game from Steam. The version text in the bottom-left of the main menu should end
   with "/W MODTEK". The first start takes longer than usual.

Connection test
---------------
- Everyone starts the game and waits in the main menu until the co-op window (Ctrl+Shift+C)
  says "Steam ready". This can take about 20 seconds after the game starts.
- Host: click "Host via Steam", then "Invite Steam friends".
- Friend: accept the invite in Steam. The co-op window should list both players.
  If the invite does nothing, try either of these:
    a) In the Steam friends list, right-click the host and choose "Join Game".
    b) The host clicks "Copy ID" next to the lobby number and sends it over chat. The friend
       pastes it into "Lobby ID" and clicks "Join Steam lobby".
- Both: click "Send test message" and "Send 2 MB test". The other side's window shows the
  message at the bottom.
- Check that the "Mods:" line at the bottom of the window is identical for everyone.
- Afterwards, send the host the file Mods\BattletechCoop\BattletechCoop.log from each PC.

Co-op skirmish
--------------
1. Connect as above. The friend stays in the main menu.
2. Host: open Skirmish, pick your lance and the opposing lance and a map, and launch as usual.
   The friend's game loads the same battle by itself.
3. Both: click "Begin mission".
4. Either player can select and command any mech in the player lance. Wait for an order to
   finish playing out before giving the next one. If both of you act at the same moment, the
   order that reaches the host first wins, and the other player sees "Order not accepted" and
   can give it again.
5. The top-right corner shows sync status. If a red "CO-OP DESYNC" banner appears, the two games
   no longer match: finish or quit the battle and send both BattletechCoop.log files and both
   Mods\.modtek\battletech_log.txt files.

Co-op Campaign / Career missions (new, first test)
--------------------------------------------------
1. Host: load your campaign (or career) save as usual. Connect, friend joins and waits in the
   main menu.
2. Host: pick a contract and launch it as usual. After you press "Begin mission", your game
   reloads the mission once (about 10 seconds) and shows the briefing again: press "Begin
   mission" a second time. This is expected: both games then start from exactly the same state.
3. Friend: the mission loads by itself; press "Begin mission".
4. Play as in skirmish. The friend's game never saves while connected (it is running your
   campaign, which must not overwrite their own saves). Your game saves as normal.

Known limits: no saving or loading during co-op combat; if someone disconnects mid-battle, the
battle cannot be resumed together.

Uninstall
---------
Delete winhttp.dll, doorstop_config.ini, libdoorstop.dylib, libdoorstop.so, run.sh, the
doorstop folder and the Mods folder from the game folder. Then, in Steam, right-click
BATTLETECH > Properties > Installed Files > Verify integrity of game files.

Licenses
--------
ModTek $ModTekVersion is included unmodified under the LGPL-2.1 (see Mods\ModTek\LICENSE).
Source: https://github.com/BattletechModders/ModTek/tree/v$ModTekVersion
Official download: https://github.com/BattletechModders/ModTek/releases/tag/v$ModTekVersion
(ModTek.zip SHA-256 $ModTekSha256)
"@
Set-Content "$stage\BattletechCoop-README.txt" $notes -Encoding UTF8

New-Item -ItemType Directory "$root\dist" -Force | Out-Null
$zip = "$root\dist\BattletechCoop-$version-ModTek-$ModTekVersion.zip"
if (Test-Path $zip) { Remove-Item $zip }
# Windows PowerShell's ZipFile.CreateFromDirectory writes '\' separators, which other unzip tools
# extract as flat file names. Write entries with '/' instead.
$archive = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
try {
    foreach ($item in Get-ChildItem $stage -Recurse) {
        $entry = $item.FullName.Substring($stage.Length + 1).Replace('\', '/')
        if (-not $item.PSIsContainer) {
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.FullName, $entry, 'Optimal')
        }
        elseif (-not (Get-ChildItem $item.FullName -Force)) {
            [void]$archive.CreateEntry("$entry/") # keep empty folders, e.g. ModTek's AssembliesOverride
        }
    }
}
finally { $archive.Dispose() }
Remove-Item $stage -Recurse -Force

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "Created $zip"
Write-Host "Size    $((Get-Item $zip).Length) bytes"
Write-Host "SHA-256 $hash"
