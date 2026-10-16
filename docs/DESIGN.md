# Design

Evidence for every claim about the game is in `ca/games/battletech/NOTES.md` (static analysis
of build 20387569). Items marked *unverified* have not been tested in a running game.

## Goals

1. 2+ players play the single-player campaign (story, Flashpoints, procedural contracts) together.
2. In missions, each player commands their own lance (4 units each, 8 for two players).
3. Between missions, every player sees the ship, and can refit **their own** mechs and
   manage **their own** pilots. The host does everything else (contracts, travel, finances,
   hiring, story choices).
4. Works with the normal mod ecosystem (ModTek, Bigger Drops / Mission Control). Large
   packs like BTA 3062 are a later, separate milestone.

Non-goals: separate companies per player, PvP, joining without owning the game.

## Architecture

```
            Steam P2P (SteamNetworking, app 637090)
 Host  <------------------------------------------>  Guest(s)
 ├ owns campaign save (SimGameState)                 ├ loads host's save snapshot
 ├ orders all invocations (sequencer)                ├ sends own invocations to host
 ├ runs AI, broadcasts AI invocations                ├ never runs AI
 └ applies guest refit requests                      └ sends refit requests
```

### Combat: lockstep on invocations

- Every combat action is an `InvocationMessage` (move, attack, melee, DFA, jump, vent,
  sensor lock, reserve, eject). It carries its own `RandomSeed`, and moves carry their waypoints.
- `InvocationHandler.GenericInvoke` reseeds `NetworkRandom` from the invocation and runs it.
  Single-player runs this same path.
- The mod hooks the point where a local invocation would execute. It sends the invocation to
  the host instead. The host stamps a sequence number and broadcasts it, and every machine
  (host included) executes in sequence order. The game's own `ToJSON`/`FromJSON` does the
  serialization.
- **AI:** runs only on the host. The guest's `AITeam` is suppressed. The host's AI invocations
  are broadcast like player ones. (AI reads wall-clock time and `UnityEngine.Random`, so it
  cannot run on both machines.)
- **Barriers:** the game waits on local UI in a few places: dialogue continue, unit activation
  (`TurnDirector.SendTurnActorActivateMessage`), and mission end/retreat. The host's input is
  authoritative and the guest is fed the same message. The game already has a synchronized
  variant for turn activation.
- **Desync detection:** `NetworkRandom` call history plus the game's `StatsCompare` machinery.
  The game compares a hash after every invocation. On mismatch it dumps both histories.

### Unit ownership (two lances)

- Everyone stays on the Player1 team. A per-unit owner table (unit GUID → player id) is sent
  at mission start.
- The HUD only lets you select and issue orders for units you own. Other players' units show
  as friendly, with an indicator.
- The drop size of 8 comes from Bigger Drops / Mission Control. Our lance screen assigns
  slots to players.
- Turn order: the game's phase system already picks one active unit at a time. Whoever owns
  that unit acts and the others watch. *Unverified:* behaviour in non-interleaved (out of
  combat) mode.

### Campaign layer: host authority, guest refit requests ("level B")

- The campaign state is changed by direct method calls from many UI screens. There is no
  order layer, so the campaign is **not** lockstepped.
- The host is the only writer. Guests get a save snapshot from the host on connect, after
  each change that matters, and before every mission. Guests browse it with all actions blocked
  except the whitelisted ones below.
- Mech ownership: a company tag/stat on each mech (`coop.owner=<steamid>`), set in the lance
  screen or barracks. It lives in the save, so it persists.
- **Guest refit flow:** the guest edits in the normal MechLab on its local copy. On confirm,
  the guest sends `{mechGuid, new MechDef}` instead of creating the work order. The host
  validates it (owner, parts in storage, c-bills) and creates the work order through the
  game's own MechLab path, then resends the snapshot. A failure goes back as a toast message.
- **Pilots:** same pattern for skill spending, callsign and portrait, limited to pilots the
  guest owns.
- Story events and dialogue: shown on all machines, and the host picks. A vote option can
  come later.

### Mod compatibility

- All players must run identical mod lists. A manifest hash (mod names, versions, DLL hashes)
  is compared on join, and mismatches are refused.
- Mods that only change data and mods that store extra save data work automatically, because
  guests load the host's save.
- Mods that change combat logic must use seeded randomness or they desync. The desync dump
  names the first divergent call. Compatibility shims go in `src/BattletechCoop/Compat/`.

### Transport

- Steamworks.NET is shipped in `Assembly-CSharp-firstpass.dll`. The plan is a Steam lobby
  plus `SteamNetworking` P2P under app 637090, with Steam relay, no port forwarding and
  invites through the Steam overlay.
- We do **not** depend on HBS/Paradox online services.
- A loopback transport (two clients on one PC) is used for development and bot QA.

## Milestones

| | Goal | Exit test (automated where possible) |
|---|---|---|
| M0 | Mod loads; transport; invocation logging | Two game copies exchange ping over Steam/loopback; log shows every invocation in a skirmish |
| M1 | Lockstep skirmish vs AI (host AI only) | Bot plays N full skirmishes; zero desyncs |
| M2 | Campaign contract via save transfer | Guest loads host save, both run a procedural contract to completion in sync |
| M3 | Ownership + 8-unit drops | Each client can only command its own lance; Bigger Drops co-installed |
| M4 | Barriers: dialogue, activation, mission end, retreat | One story mission completes in sync |
| M5 | Ship level B: guest refit + pilot requests | Guest refit request appears as a work order on host; invalid requests rejected |
| M6 | Bot QA suite, installer, Steam invite flow | `automated` QA level honestly earned |
| M7 | BTA 3062 compatibility pass | Desync-free skirmish under BTA |

## Verified in game

**2026-10-16, M0 smoke test** (test copy, ModTek v4.5.1, one skirmish vs AI, 3 rounds):

- The mod loads under ModTek, and the main menu shows `/W MODTEK`. Harmony 1.2 patching works
  under ModTek's shim.
- `InvocationHandler.GenericInvoke` runs for **every** MessageCenter message, not just
  invocations: 1,242 calls, of which 25 were invocations.
- All 25 invocations went through it: 14 moves, 5 attacks, 4 reserves, 2 sensor locks. That
  includes the AI's moves (`AbstractActorMovementInvocation`, created by
  `AITeam.makeNormalMoveInvocation`) and the player's (`MechMovementInvocation`, created by
  `SelectionStateMoveBase.CreateMoveOrders`).
- Invocations are created with `IsNetRouted = true` even in single-player. `GenericInvoke` only
  bypasses routing because no network game is active.
- Invocation IDs are `<machine prefix>.<counter>` (here `P1TDG.0` to `P1TDG.24`), so IDs from
  different machines don't collide. Seeds are small integers (max seen 98187).

## Open questions

- Can a mid-mission save be loaded identically on another PC? This is needed for reconnecting.
- ModTek 4 (HarmonyX with a Harmony 1 shim) or HBS's built-in loader (Harmony 1.2.0.1)?
  We currently build against the game's Harmony 1.2 API, which works under both.
- Flashpoints and DLC story missions are untested.
