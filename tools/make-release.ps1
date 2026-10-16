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

PRE-ALPHA. This build only tests the connection between players. There is no co-op gameplay yet.

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
