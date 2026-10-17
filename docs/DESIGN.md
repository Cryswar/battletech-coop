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

#### M1 protocol (skirmish vs AI)

Found in the game code (build 20387569):

- **Combat start.** The skirmish screen builds a `Contract`, then calls
  `GameInstance.LaunchContract`. MP sends the whole contract with the game's own serializer
  (`BattleTech.Serialization.Serializer`, target `All`(15), mask `HAS_ANY`(2), the same arguments
  as `NetworkMessageResolver`) inside a `GameContractNetworkMessage`. We do the same. A host
  prefix on `LaunchContract` sends the contract, and the guest launches it from its main menu.
- **Actor GUIDs are deterministic**: `UnitSpawnPointGameLogic.GetNextUnitGuid` returns
  `"<spawn point GUID>.<index>"`. The team GUIDs are constants: Player 1
  `bf40fd39-ccf9-47c4-94a6-061809681140`, Player 2 `757173dd-b4e1-4bb5-9bee-d78e623cc867`.
- **Invocations on the wire.** The invocations' `ToJSON` throws `NotImplementedException`.
  MP wraps the message in a `NetMessageCenterMessage` and runs it through `Serializer`, and so
  do we.
- **AI.** `AITeam.think()` returns immediately if `ThinksOnThisMachine` is false. But with that
  flag false, `TurnActorProcessActivation` leaves `currentUnit` null and marks the team complete
  at once, so the flag can't be used for a mirrored AI. Instead, the guest skips `think()` (no
  decisions are made there), and its `isComplete` is driven by the host.

Stream (host → guests, one ordered channel). Each item carries `seq` and `activation`, the
host's count of `TurnDirector._SendTurnActorActivateMessage` calls when the item was produced:

| Item | Produced on host | Applied on guest |
|---|---|---|
| `Invocation` (type, serialized bytes, state text before it ran) | prefix of every concrete `InvocationMessage.Invoke` override, i.e. at real execution time, in real execution order | published into the guest's `MessageCenter` with `WasNetRouted = true`, so `GenericInvoke` runs it |
| `AIComplete` (team GUID) | `AITeam.think` / `TurnActorProcessActivation` postfix, when `isComplete` turns true | sets that `AITeam`'s `isComplete` |

The guest applies the head item only when its combat has started, no order is active, the
`InvocationHandler` has no queued invocations, and its own activation count equals the item's.
That gating is the barrier: the guest can lag, but never runs ahead of or diverges from the host's
turn structure.

Guest-local invocations (HUD orders) are dropped in `GenericInvoke` with a notice. Routing
them to the host is M3, when there is ownership to check them against.

**Desync detection.** Before each invocation runs, both sides render a canonical state text:
round, phase and RNG state, then per actor sorted by GUID: position (cm), facing, dead/activated
flags, heat, stability, evasive pips, and armor and structure per location. The host sends its
text in the item. The guest compares it with its own and logs the first differing lines. The
seq and invocation ID pinpoint the step.

**Test mode** (`-coop-autoplay`, sent to the guest in the combat-start message):
`EncounterLayerData.CreatePlayerOneTeam` returns an `AITeam` substituting for the human. This is
what HBS's own `DebugBridge.PlayerOneIsAIControlled` does. The host's AI then plays both sides,
and everything flows through the stream. `-coop-autoskirmish` makes the host build and launch a
fixed skirmish once a guest has joined: first released multiplayer map by name, first
`IsSkirmish` encounter, fixed mech and pilot IDs.

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
- A TCP transport (two clients on one PC, or LAN) is used for development and bot QA.

Implemented (M0):

- `ITransport`: reliable, ordered messages. `TcpTransport` uses 4-byte length-prefixed frames.
  `SteamTransport` uses a friends-only Steam lobby plus `SteamNetworking` reliable P2P to the
  lobby owner, splitting messages into 512 KB chunks (reliable P2P caps a packet near 1 MB).
  Peer id 0 is reserved for "the transport itself failed".
- `CoopSession`: the host is player 0. A guest sends `Hello` (protocol version, mod version,
  mod-list fingerprint, name), and the host answers `Welcome` (player index) or `Reject` (reason).
  The host relays `PlayerJoined`/`PlayerLeft`. Pings run every second, smoothed into an RTT.
  Peers time out after 20 s silent, or 10 s without a handshake. `Game` messages carry a
  ushort channel and an opaque payload; this is what M1 will send invocations over.
- Mod-list fingerprint: SHA-256 over every `mod.json` and every DLL under `Mods/`, excluding
  `.modtek/`. A guest whose fingerprint differs from the host's is refused.
- Joining from a Steam invite: the `GameLobbyJoinRequested_t` callback, or `+connect_lobby <id>`
  when Steam launches the game from an invite.

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

**2026-10-16, M0 network test**:

- `dotnet run --project tests/NetTests`: 12/12 pass. Covers handshake and roster, RTT,
  messages both ways, a 4 MB message, rejection for mod-list/version mismatch and a full
  session, a guest leaving, the host leaving, connect failure, and handshake timeout.
- `tools/two-copy-test.ps1`: **PASS**. Two game processes (TestInstall hosting, TestInstall2
  joining over localhost TCP, both under ModTek 4.5.1) agreed on the mod fingerprint
  (`9C2FD5F8E6C41E4D`, 26 files). The handshake took ~4 ms, and each side received the other's
  test message.
- ModTek allows only one game process per install folder (`Mods/.modtek/ModTekPreloader.lock`,
  no setting to disable it). The second test folder copies the small files and junctions
  `BattleTech_Data` to the first.
- **First Steam test (0.0.1) failed:** the friend accepted the invite, but nothing happened.
  The cause was that ModTek starts mods about 19 s before the game initializes Steam, so the
  invite callback was never registered. In 0.0.2, Steam callbacks are registered on the first
  frame where `SteamManager.Initialized` is true (confirmed in a test copy's log). Rich presence
  `connect` was added for friends-list "Join Game", plus a manual lobby-ID join.
- **2026-10-16, Steam test (0.0.2): PASS** between two PCs on different networks (owner and a
  friend, real Steam installs from the release zip, same fingerprint `4F5D4A01439DBEA9`). The
  invite was accepted while the game was running, and lobby → P2P → handshake took ~5 s, mostly
  waiting for Steam relay to accept the session. Names and ping were shown on both sides.
  Test messages arrived both ways. A 2 MB message took 5–6 s each way (~350 KB/s through the
  relay), so save transfer (M2) needs compression and a progress indicator. It needs a second Steam account on another PC, or a
  friend. The in-game window (Ctrl+Shift+C) has also not been exercised by hand.

**2026-10-16, M1 lockstep tests** (`tools/two-copy-test.ps1 -Combat`: TestInstall hosts and
TestInstall2 joins over localhost TCP; auto-skirmish on `mapArena_AlpineRiver_iGlc`, mood
`ArcticDayFoggy`, 4v4 (Atlas, Centurion, Hunchback, Jenner), AI playing both sides, time scale 3–4):

- 60-item run: **PASS**. 26 moves, 10 attacks, 1 reserve, 25 AI completions, 0 desyncs.
- Full mission: **PASS**. The match ran to mission over on both sides in ~3 minutes: 197 items
  over 72 activations (64 moves, 48 attacks, 10 reserves, 2 jumps, 2 stand-ups after knockdowns,
  72 AI completions), 0 desyncs. The guest dropped no local invocations and the game queued none
  of the replayed ones.
- Detector self-test (`-InjectDesyncAt 15`): **PASS**. +7 heat was injected on one guest mech
  before seq 15, and the guest reported `DESYNC at seq 15`. The diff line names that unit with
  `heat=0` (host) and `heat=7` (guest), everything else on the line equal, and the host logged
  the guest's report.
- Problems found on the way and fixed: the `Invoke` overrides name their parameter differently
  (bind no parameters); the guest received the contract before its main menu existed (defer it,
  and auto-skirmish waits for the guest's menu-ready report); calling `LaunchContract` directly
  leaves the screens black (go through `Contract.Accept` and the loading curtain, like
  `SkirmishSettings_Beta.LaunchMap`); a contract with no mood stalls loading; and the briefing's
  "Begin mission" needs pressing (auto-pressed in autoplay).

What this does not cover yet: human orders from the host's HUD (only AI-issued invocations so
far, although the HUD's `ReserveActorInvocation` and `MechMovementInvocation` take the same
path), guest orders (dropped until M3), Steam transport latency during combat, maps other than
the first, and combat saves/loads.

## Open questions

- Can a mid-mission save be loaded identically on another PC? This is needed for reconnecting.
- ModTek 4 (HarmonyX with a Harmony 1 shim) or HBS's built-in loader (Harmony 1.2.0.1)?
  We currently build against the game's Harmony 1.2 API, which works under both.
- Flashpoints and DLC story missions are untested.
