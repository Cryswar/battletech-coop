# BattleTech Co-op

A mod that lets 2+ players play the BattleTech (2018) single-player campaign together over
Steam. The host owns the campaign save. Each player commands their own lance in missions.
Between missions, each player can refit their own mechs and pilots.

Status: **pre-alpha, nothing playable.** See [docs/DESIGN.md](docs/DESIGN.md) for the
architecture and milestones.

Target game: Steam build 20387569, BattleTech 1.9.1 (686R). All players need to own the
game and run the exact same mod list.

## Layout

| Path | What |
|---|---|
| `src/BattletechCoop/` | The ModTek/HBS mod (C#, .NET 4.7.2, Harmony) |
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

## Rules this project follows

From coop-anything's `AGENTS.md`: never modify the real install or saves (test on a copy),
no game files or decompiled source in git, test with bots, and record what has actually
been verified.
