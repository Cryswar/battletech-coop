# BattleTech Co-op

A mod that lets 2+ players play the BattleTech (2018) single-player campaign together over
Steam. The host owns the campaign save. Each player commands their own lance in missions.
Between missions, each player can refit their own mechs and pilots.

Status: **pre-alpha.** Co-op skirmish with shared control works in automated tests. Campaign
co-op is not done yet. See [docs/DESIGN.md](docs/DESIGN.md) for the
architecture and milestones.

Target game: Steam build 20387569, BattleTech 1.9.1 (686R). All players need to own the
game and run the exact same mod list.

## Layout

| Path | What |
|---|---|
| `src/BattletechCoop/` | The ModTek/HBS mod (C#, .NET 4.7.2, Harmony) |
| `src/BattletechCoop/Net/Core/` | Unity-free network core: transport interface, TCP transport, protocol, session |
| `tests/NetTests/` | Headless tests for the network core (.NET 8 console, no packages) |
| `docs/DESIGN.md` | Architecture, decisions, milestones |
| `tools/` | Read-only analysis scripts (metadata dump, IL call graph) |
| `ca/` | Checkout of [coop-anything](https://github.com/cranebeaker/coop-anything); research notes in `ca/games/battletech/NOTES.md` (own git repo, ignored here) |
| `local/` | Private, ignored: copied game assemblies, dumps, logs |

## Building

1. Copy `GameDir.props.example` to `GameDir.props` and set `BattleTechDir` to your install.
   (The build only reads game DLLs from it; it never writes there.)
2. `dotnet build src/BattletechCoop -c Release`
3. Optional: set `TestInstallDir` in `GameDir.props` to a **copy** of the game to have the
   build deploy `Mods/BattletechCoop/` there.

## Release zip

`powershell -ExecutionPolicy Bypass -File tools\make-release.ps1` builds
`dist\BattletechCoop-<version>-ModTek-<version>.zip`: the official ModTek release (hash-checked,
unmodified, LGPL-2.1 with source link) plus this mod's Release build and
`BattletechCoop-README.txt` with install, test and uninstall steps. Every player installs the same
zip into their game folder, so their mod fingerprints match.

## Testing

- Network core (no game needed): `dotnet run --project tests/NetTests`. It runs host and guests
  over real localhost TCP sockets.
- Two game copies on one PC: `powershell -ExecutionPolicy Bypass -File tools\two-copy-test.ps1`.
  It starts a host in `TestInstall` and a guest in `TestInstall2` over localhost TCP, waits for
  each side to receive the other's test message, prints both logs and closes the games.
  ModTek allows one game process per install folder, so the second folder is required. It can
  junction `BattleTech_Data` to the first copy.
- Combat lockstep: `tools\two-copy-test.ps1 -Combat [-TargetSeq 60] [-TimeScale 3]`. The host
  auto-launches a fixed 4v4 skirmish with the AI playing both sides, and the guest replays the
  host's invocation stream. It passes when the guest has replayed `-TargetSeq` items (or the
  mission ended) with no desync. Use `-TargetSeq 100000` for a whole match.
  `-InjectDesyncAt <seq>` corrupts guest state on purpose; then PASS means the desync detector
  caught it. `-Shared` makes the Player 1 AI alternate between guest and host each activation,
  so half the orders are guest requests (shared control). It also requires accepted requests and
  none rejected.
- In game, **Ctrl+Shift+C** opens the co-op window: host or join over TCP, host via Steam,
  invite Steam friends, send test messages (including a 2 MB one), leave.

## Rules this project follows

From coop-anything's `AGENTS.md`: never modify the real install or saves (test on a copy),
no game files or decompiled source in git, test with bots, and record what has actually
been verified.
