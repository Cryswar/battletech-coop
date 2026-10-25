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

#### M3 shared control (owner decision 2026-10-16)

Every player can command every player-team unit. Per-lance ownership is an optional setting for
later, implemented as a host-side filter on the same request path.

- **Guest orders become requests.** An invocation the guest creates (HUD, or the guest's AI in
  the shared test) is not run locally. It is serialized and sent to the host with the guest's
  last applied stream seq. Only one request can be outstanding at a time; a second one is
  dropped with a notice.
- **The host serializes everything.** Requests are queued and handled when the host is idle,
  under the same conditions the guest uses for replay. A request is accepted only if the guest
  had seen the latest state (its seq equals the host's), the combat is running, and the active
  turn actor is the player team. The host then gives it a fresh `InvocationID` (`G<player>.<n>`,
  so it can never collide with the host's own IDs) and publishes it locally. From there it is an
  ordinary host invocation: it executes, gets streamed, and the guest replays it. The guest gets
  an accept or reject result. A reject (usually "the state changed first") shows a notice; the
  player re-issues the order.
- **Conflicts** (both players acting at once) resolve as first-come: whichever order runs on the
  host first wins, and the other side's order is rejected as stale.
- **AI completion in the shared test.** The test makes the Player 1 AI think on the guest on
  even activations and on the host on odd ones. A guest-side AI never completes locally: when
  its `think` sets `isComplete`, the guest resets it and sends a completion request, and the
  host applies it and streams `AIComplete` as usual. Completion therefore only ever arrives
  through the stream, which keeps the activation gating intact.

#### M2 campaign contracts (Campaign and Career modes)

Findings (build 20387569):

- SimGame contracts launch through the same `GameInstance.LaunchContract`, inside
  `SimGameState.StartContract` (Accept, loading curtain, launch). Just before that,
  `SimGameState.OnContractReady` saves with `SIM_GAME_CONTRACT_ACCEPTED`.
- A sent `Contract` would be a poor basis on the guest. Its `ContractOverride` (objectives,
  lances) is not serialized ("we populate this later" in MP, which sends the ID separately), and
  `ContractOverride.GenerateUnits` picks the enemy units while the contract loads.
- `new GameInstanceSave(game, reason)` captures both the `SimGameSave` and the live
  `CombatGameState`. `GameInstance.Load(save)` rebuilds the SimGame headless, then calls
  `CreateCombatFromSave` when the save has combat data (`IsSkirmish` saves go straight to
  `CreateCombatFromSave`). `LazySingletonBehavior<LoadTransitioning>.Instance.Load(save)` is how
  the main menu resumes a save. Real saves use `Serializer` with target `SaveGame` (2) and gzip.
- Saves live in Steam Cloud (`userdata/<id>/637090/remote/C0/SGS1/*.sav`).

Design: **send a combat save, not a contract.** Once a campaign mission has begun on the host and
the host is idle, the host builds a `GameInstanceSave` in memory (never written to disk),
serializes and gzips it, and sends it with the current stream `seq` and activation count. The
guest loads it through `LoadTransitioning`, so the campaign state, contract, spawned units and
mission scripts come back the way the game itself restores them, with nothing regenerated. It
then replays stream items with a higher `seq`. Story missions and Flashpoints take the same path,
and so would rejoining a battle later.

- **Saving is blocked on the guest** whenever it is in a co-op session: the guest is running
  the host's campaign, and an autosave would put it into the guest's own Steam Cloud slots.
  `-coop-no-saves` blocks saving in test copies, which share the owner's Steam Cloud saves.
- Between missions the guest stays in the host's campaign as it was at mission start, unsaved.
  That is the first, read-only form of the shared ship view; each new mission brings a fresh save.
- Skirmish keeps the contract path. (`-coop-save-transfer`, which sent skirmishes through the
  save path for testing, was retired in 0.0.13: see "Mission start".)

Implementation notes and results (2026-10-17):

- **When the save is taken:** only where `CombatGameState.CanSave(false)` holds (local player
  team's turn, quiet stack, no unit half-activated, not a tutorial, mission not over). A save
  taken right after "Begin mission", mid round start, made the guest resume at a different phase.
  In autoplay, Player 1 is an `AITeam`, which sits on the stack, so the test uses the same
  conditions minus the stack check.
- **Loading in memory:** `LoadTransitioning.Load` takes a slot and reads it from disk;
  `LoadTransitioning.FileReadComplete(true, save)` is where that read hands over the
  `GameInstanceSave`, so the transferred save goes in there. Bytes are
  `SaveBlock<GameInstanceSave>(SaveGame).ToBlockBytes`, i.e. exactly a save file, about 257 KB.
- **Resuming:** a loaded combat shows its briefing again, and its state already reads "begun"
  long before the map is up. The guest resumes when "Begin mission" is pressed
  (`Briefing.BeginPlaying`); activations counted while loading are ignored and the count is set
  to the host's at save time.
- **The host reloads the same save.** A loaded combat differs from the live one it was saved
  from: components come back in a different order, so a critical hit landed on a different
  component on the guest (seq 207 with a full-stat comparison). The host therefore sends the
  save and immediately loads it too; until its own "Begin mission", it drops invocations and its
  AI doesn't think. Both sides then run identical post-load state. The cost is one ~8 s reload,
  and the host sees the briefing a second time.
- **The state check now covers every statistic:** a digest of the unit, pilot and component stat
  collections, rendered culture-invariant, with components labelled `def@location` and sorted.
  Lines are matched by unit and section rather than position. `-coop-deep-state` lists the
  stats in full so a diff names the exact one.
- Save-transfer full mission with full-stat comparison: **PASS** (176 items, 38 guest orders,
  24 guest AI completions, 0 desyncs).

**2026-10-17, first human campaign test (0.0.7, two PCs over Steam): works.** A campaign mission
reached the guest as a combat save (656 KB) after the host's "Begin mission" and reload; 77 items,
17 of them guest orders (moves, attacks, jump, melee, DFA), 0 desyncs. The busy-unit skip from
0.0.6 fired about a dozen times on the guest with no false alarm. Both saw the results screen and
salvage, and the guest's post-mission save (`SIM_GAME_COMPLETED_CONTRACT`) was blocked.
Reported issues:

- **Re-hosting failed after a lobby ended** until the game was restarted. A plain
  host/leave/host cycle works (`-coop-test-rehost`, 3 rounds). The fix in 0.0.8 covers the likely
  cause: a session closing mid-battle left a `CombatSync` bound to the dead session, which skipped
  the next launch ("already in co-op combat") and stopped the guest's menu reports. A closed
  session now clears all combat-sync state.
- **Ghost campaign:** after the host quit, the guest kept playing its local copy of the host's
  campaign. In 0.0.8, a guest whose session ends while it is in the host's battle or campaign
  gets a notice and returns to the main menu after 6 s (`-HostQuitTest`: PASS).
- The guest sees nothing of the campaign management screen until a mission starts: ship view
  and requests in 0.0.9 (see "Campaign layer").
- The guest misses the opening conversation, because the save is only possible once
  `CanSave` holds, i.e. after it. Next.

Not yet tested: a real Campaign or Career mission (story scripts, Flashpoints, a non-skirmish
`SimGameState` on both sides), loading while the guest is in the SimGame rather than the main
menu, Ironman campaigns, and the mission-end and AAR flow back to the ship on the guest.

#### Mission start (0.0.13): both players watch the opening

Problem (human test 0.0.7): the guest missed the mission's opening. The combat save was taken at
the first point where `CombatGameState.CanSave` holds, which is after the opening: the camera
intro and dropship landing (`TurnDirector.CheckCameraIntroAndDropshipComplete`), then
`ShowMissionStartDialogue`; its `DialogComplete` lets `CheckGameBegin` start round 1.

Findings (build 20387569):

- `GameInstance.CreateCombatFromSave` has a second path: when the save is flagged for a
  combat restart (`GameInstanceSave.flaggedForRestart`, normally set when the save's combat
  version is out of date) it calls `Contract.ResetStateForRestart`, `CombatHydrate` and
  `LaunchContract`: the campaign state comes from the save and the saved contract starts fresh,
  intro and opening dialogue included. `LoadTransitioning.CheckAndHandleCombatRestart` shows a
  "mission save out of date" popup first; its RESTART button only calls `ProcessLoadedFile`.
- `ResetStateForRestart` reloads the mission definition (`ContractOverride`) from the game data,
  and the launch rolls its tagged lances and units again (`LanceOverride.RequestLance`,
  `UnitSpawnPointOverride.RequestUnit` re-select whenever the definition is tagged). First run:
  host 2 enemy 'Mechs, guest 3, other loadouts.
- A load started from the briefing (`ProcessLoadedFile` while the briefing is up) never gets to
  `CreateCombatFromSave`, even after the loading curtain has gone.
- Conversations are `InterruptDialogSequence`s: Continue hides the dialog box, whose callback is
  `AfterDialogHide` -> `PlayNextMessage` -> `Play(next line)` (it returns a
  `SimGameConversationManager.ConversationState`, not a bool). Side chatter is
  `AsyncDialogSequence` on timers (`CombatHUDDialogItem`), skippable with Space/Esc.

Design:

- **The save is taken at the host's briefing** (`Briefing.SetStartButtonActive(true)`, once the
  loading curtain is gone), before anything has happened, and the guest loads it flagged for a
  restart (popup skipped). Both sides then launch the same mission: the host from its live
  campaign, the guest from the saved one. The host no longer reloads (it sees the briefing once);
  the old reason for its reload, a resumed battle differing from the live one, does not apply to
  two fresh launches.
- **Lance pins.** With the save the host sends what its launch picked for every non-player
  team (selected lance def, unit def and type, pilot def per spawn point); a postfix on the
  guest's `ResetStateForRestart` writes them into the reloaded definition as explicit IDs, so
  its launch builds the same units.
- **Shared "Begin mission".** `Briefing.BeginPlaying` is held until every player's briefing is
  loaded; whoever presses it starts the mission for everyone (the guest's press goes through the
  host). Intro, dropship and side chatter then run on matching timers.
- **Conversation lines advance together.** Either player's Continue (or skip) goes through the
  host, which advances its own copy and tells the guests to advance the same line; a guest that
  is a line behind applies the steps in order once it shows that line.

Tests: `two-copy-test.ps1 -MissionStart` (host `-coop-test-shipview -coop-test-mission
-coop-autoplay`): the host loads its newest career save, sends the ship, then launches the first
ordinary contract the way the lance screen's Accept does (`SimGameState.OnContractReady`; the
test also fills the contract's own lance, which the lance screen normally does). In test mode
the guest presses Begin and the two sides press Continue on alternate lines. It requires both
sides to finish the opening dialogue, identical dialogue lines in the same order, steps from
both sides, then `-TargetSeq` stream items with no desync.

Results (2026-10-25, career save 'Avalanche's Marauders', contract 'Aggressive Intrusion'):

- First runs: the host's own reload from the briefing stalled in `ProcessLoadedFile` (twice,
  also after waiting for the curtain), hence no host reload. Then the guest's restart re-rolled
  the enemies (host 6 actors, guest 7), hence the lance pins. One build broke every patch:
  `AfterDialogHide`'s `__result` was declared `bool`, HarmonyX refused it, and `PatchAll` stops at
  the first failure.
- With pins: **PASS**, both 8 actors (10 picks pinned: a vehicle lance of Galleon and Strikers),
  the same 3 opening lines on both ("We've got them, Commander..." to "...Oliveira out."), the
  guest's Continue used once, both "opening dialogue complete" 0.05 s apart, then 54 stream items
  with no desync. Long run: **PASS**, 212 stream items, no desync. `-Combat -Shared` (skirmish,
  now also with the shared Begin): **PASS**, 60 items.
- Unattended test runs at night need the display on: both copies hung at "Input initialized"
  until the test process asked Windows to keep the display on (`SetThreadExecutionState` with
  `ES_DISPLAY_REQUIRED`, for the run only).
- Not tested yet: story missions and Flashpoints (scripted openings with more conversations),
  a human pressing Continue on the guest, and Space/Esc skips of side chatter.

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
- The host is the only writer. The guest's ship is a throwaway copy of the host's campaign;
  changes the guest makes there are local except the requests below.
- **Ship view (0.0.9).** The host sends its campaign as an in-memory save (same bytes as a save
  file, ~390 KB) when it arrives on the ship, when a guest joins, and when a guest presses
  "Refresh the host's ship"; at most every 10 s and only where `SimGameState.CanSave` holds. The
  guest loads it through `LoadTransitioning.FileReadComplete`, as with mission saves. The new
  `SimGameState` exists before the save is applied to it (default company, funds 0), so the guest
  counts the ship as showing only after the game's own `_OnAttachUXComplete` for the received
  company. Saving stays blocked on the guest.
- **Requests (0.0.9).** The guest's game applies a change at once (so the player sees it), and
  the same change goes to the host. The host rebuilds it through the game's own functions with
  its own objects and replies applied/refused, shown as a notice on both screens:
  - *Refits and repairs:* captured at `SimGameState.InitializeMechLabEntry`, which MechLab
    confirm, repair and repair-all all go through. Sent as their work orders (install/remove,
    armor, structure, component repair), not objects: a part taken from storage gets a new ID on
    the guest, and the host's game takes the same part type from its own storage. The host
    re-creates each order with `CreateComponentInstallWorkOrder` / `CreateMechArmorModifyWorkOrder`
    / `CreateMechRepairWorkOrder` / `CreateComponentRepairWorkOrder`, then does what
    `MechBayPanel.OnMechLabComplete` does without the UI (queue, `InitializeMechLabEntry`,
    `UpdateMechLabWorkQueue`). So costs and refit time are the host game's own. Refused when the
    'Mech already has a refit in progress on the host, or when funds are short.
  - *'Mech names:* `MechBayPanel.OnMechLabComplete` (renames happen there even without orders).
  - *Pilot training:* `SGBarracksMWDetailPanel.OnPilotConfirmAccepted` sends the target skill
    levels and the XP spent; the host repeats `SGBarracksAdvancementPanel.SetTempPilotSkill` a
    level at a time (abilities from `AbilityTree`), spends the XP, and calls `UpgradePilot`.
    Refused when the host's pilot lacks the XP or already has a higher level.
  - *Pilot names:* after `SGBarracksMWCustomizationPopup.Save`: first/last name, callsign,
    gender, voice. Portraits are not sent yet.
- **Updates (0.0.10).** The host's own changes of the same kinds (refits and repairs, 'Mech
  and pilot names, pilot training) are captured by the same patches and sent to the guests as
  updates; each guest applies them to its copy the same way, so they need no full reload. An
  update that arrives while a ship save is still loading waits until that ship is showing; a
  newly received ship save drops waiting updates, since it was built after them. A guest's
  request that the host applies is forwarded to any other guests as an update. While a change
  from the other side is applied, the capture patches stay quiet, so nothing echoes back.
- **Open screens are redrawn (0.0.10)** after a change from the other side: the Mech Bay with
  `MechBayPanel.RefreshData` and the same bay slot selected again, the Barracks with
  `SGBarracksWidget.Reset` on the pilot being shown. Only screens that are `Visible`; it waits
  while the MechLab is open or the shown pilot has unconfirmed training
  (`SGBarracksWidget.IsCurPilotModified`; 0.0.11).
- **Refresh (0.0.10):** the button is disabled while a ship is loading; notices say "Asking the
  host...", "Loading the host's ship...", "Ship updated from the host", or after 30 s that the
  host sends its ship only once it is on the ship screen. The host answers a refresh at once
  instead of waiting out the 10 s throttle. A full reload still takes 15-20 s, nearly all of it
  the game's own loading.
- **The rest of the ship (0.0.12)**, same pattern, captured where the screens call the game:
  - *Hiring Hall:* `StarSystem.HirePilot`, the pilot found by its def ID among the system's
    pilots for hire.
  - *Store:* `Shop.Purchase` and `Shop.SellInventoryItem(ShopDefItem)` (sent only when they
    succeed), the store found by type (system, faction, black market) in the current system.
  - *Mech Bay:* `ReadyMech`, `UnreadyMech`, `ScrapActiveMech`, `ScrapInactiveMech`, and
    `CancelWorkOrder` (the order found by 'Mech ID and order kind, since work-order IDs differ).
  - *Barracks:* `DismissPilot`.
  - *Customize Company:* `SGRoomController_CptQuarters.OnHeraldryClosed`; the heraldry is sent
    as JSON and applied as there (header name, crest, 'Mech colors).
  - Several of these call each other (`ReadyMech` uses up the stored 'Mech through
    `ScrapInactiveMech`; cancelling a readying order calls `UnreadyMech`), so only the outermost
    call is sent. `ReadyMech` and `ScrapInactiveMech` rewrite their `id` argument before
    returning, so arguments are taken in the prefix.
- **Same IDs everywhere (0.0.12).** New pilots and 'Mechs get IDs from a counter
  (`GenerateSimGameUID` = `"SGRef_" + ++UIDCount`), and each copy counting on its own would give
  the same thing different IDs, so a later change to it would miss. Each change now carries the
  counter its side started from, and the other side applies it from that counter (then restores
  its own). A guest counts in its own range, 1e9 x player index above the host, so IDs made on
  different copies never collide.
- Open Store and Hiring Hall screens are redrawn too (the buy or sell list of the current store;
  the list of pilots for hire), and the company header after a heraldry change.
- Control is shared, as in combat: either player may refit any 'Mech or train any pilot. Owners
  (a company tag per 'Mech, e.g. `coop.owner=<steamid>`) can come later as an option.
- Not covered yet: pilot portraits, refits of a 'Mech whose host refit is still in progress,
  and time passing, travel or contracts started on the guest (those belong to the host; the
  guest's copy goes stale until the next full ship: mission end, joining, or "Refresh the host's
  ship").
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
| M5 | Ship level B: guest refit + pilot requests | Guest refit request appears as a work order on host; invalid requests rejected (0.0.9: automated test passes) |
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

**2026-10-16, M3 shared-control tests** (`-Combat -Shared`: the Player 1 AI decides on the guest on
even activations and on the host on odd ones):

- Full mission: **PASS**. 165 items to mission over. **37 guest orders** were accepted and run by
  the host (20 moves, 12 attacks, 3 melee, 2 reserves), plus 23 guest AI completions and 66 host
  orders. 0 requests rejected, 0 desyncs.
- Regression, host-only full mission after the change: **PASS** (174 items, 0 desyncs).
- Release build 0.0.3, 60-item shared run: **PASS** (15 guest orders, 10 completions, 0 rejected),
  fingerprint `CCC137FFC7EDA24B (25 files)`.
- Found and fixed: `InvocationID`'s setter is private on the base class, so the host now writes
  `<InvocationID>k__BackingField`. Until that fix every guest order was rejected cleanly, with a
  notice on the guest, so the reject path is exercised too.

**2026-10-16/17, first human test (0.0.3, two PCs over Steam): guest stuck before "Begin
mission".** The host launched from the real skirmish screen. Its lance units are built from
`MechDef`/`PilotDef` objects, giving a 326,768-byte contract against 1,428 bytes in the tests,
which used string IDs. The guest's game log showed the spawn crashing in
`MechDef.GetChassisLocationDef`. Deserialized defs lack their chassis, component and ability links.

- Reproduced locally by making auto-skirmish load real defs through the DataManager, like the
  screen does: same crash, test FAIL.
- Fix (`Sync/LanceLoader.cs`, guest only, before launching): rebuild every unit from its
  `UnitJson`/`PilotJson` (`SpawnableUnit.FromJSON`), set the DataManager, `Refresh`, gather the
  missing dependencies into an `InjectedDependencyLoadRequest`, and after loading also refresh
  the chassis, whose movement caps were missing and crashed `Mech.InitStats`. This mirrors
  `Contract.RequestDependencies`. Forcing the game's own multiplayer branch of
  `BeginRequestResources` stalled the load instead.
- After the fix, with object-based lances: full shared mission **PASS** (178 items, 35 guest
  orders, 22 completions, 0 rejected, 0 desyncs), and the 0.0.4 release build 60-item shared
  run **PASS**.
- The test script now also fails on a contract-setup crash in either side's game log. Both
  sides log 4 identical harmless `Team.OnVisibilityIncreased` errors: an audio and VFX cue whose
  spotting unit is not in the AI-substituted player team, which happens in autoplay only.

**2026-10-17, second human test (0.0.4, two PCs over Steam): played well, one desync.** Both
players gave orders through the HUD (4 guest orders: moves and an attack; 13 host orders), with
no visible lag. The players saw nothing wrong, but the guest reported `DESYNC at seq 4`: player
mech `461141c2….0` had moved and activated on the host (`act=1 evp=4`, new position) but was
untouched on the guest (`act=0 evp=0`, spawn position). The state matched before seq 3, so seq
3, the host's first own move (`P1TDG.0`), executed on the guest with no effect. This was during
the free-move phase before contact (all four player moves in activation 1). No further desyncs
were reported, but the two games disagreed about that mech from then on.

Ruled out from the code and logs: the duplicate-ID check (`InvocationHandler.previousInvocations`;
no "Blocked invocation" in the guest log, although both PCs do generate `P1TDG.<n>` IDs, since
the prefix comes from the local team); HUD ID matching (`SelectionState.PublishInvocation` only
listens synchronously, so the guest's HUD logs "No Orders assigned from invocation!" for every
order it sends to the host, which is harmless); and an undo path (unreferenced).
`AbstractActorMovementInvocation.Invoke` always publishes an `ActorMovementSequence`.

Leading suspect, not confirmed: in non-interleaved mode, `StackManager.AddNormalStackSequence`
calls `StopCancelableSequences`, which silently cancels running cancelable sequences, so a
guest-local sequence added at the wrong moment could cancel a replayed move. 0.0.5 adds
diagnostics to settle it: every cancelled `OrderSequence` is logged with its unit and the call
stack, every stream line names its unit, and the full state of both sides is dumped at the first
desync. There is also a large red on-screen banner, because the corner text went unnoticed.

**2026-10-17, third human test (0.0.5): the desync was reproduced on purpose and is a false
alarm.** Both players gave orders to different mechs at the same time in the free-move phase.
The guest reported `DESYNC at seq 3` for `ec9d2280….0`, which the host had just ordered to move
(seq 2, 2 s earlier). On the host the mech was mid-walk; on the guest it had not started walking
yet. The guest's reserve order (seq 3) ran on the host before the walk ended, because free-move
orders don't wait for other units' moves. **Every later check matched** (the guest's desync count
stayed at 1 for the rest of the match, as in the first session), and no order sequence was
cancelled. So the games did not diverge: the checker compared a unit in the middle of a sequence
that runs on each machine's own clock.

Fix (0.0.6): `StateText` renders a unit that still has an `OrderSequence` running (normal or
parallel stack, children of multi-sequences included) as `<GUID> busy`. The comparison skips
units busy on either side, and they get compared at the next check where both have them at rest.
The desync self-test still catches injected corruption at the exact seq. The full shared mission
passes (239 items, 37 guest orders). Checks that skip busy units are logged, so the next human
session confirms that walking units are detected.

Not covered by these tests: a human using the HUD on either machine, in particular how the HUD
reacts when the other player's order moves a unit, and whether the guest's HUD waits correctly
between sending an order and seeing it replayed. That needs the 0.0.3 human test.

What M1 does not cover yet: human orders from the host's HUD (only AI-issued invocations so
far, although the HUD's `ReserveActorInvocation` and `MechMovementInvocation` take the same
path), guest orders (dropped until M3), Steam transport latency during combat, maps other than
the first, and combat saves/loads.

### Ship view and requests (0.0.9, 2026-10-17)

- `two-copy-test.ps1 -ShipView`: the host loads its newest campaign save read-only
  (`-coop-test-shipview`, with `-coop-no-saves`) and the guest must show the same company,
  funds, day and system. **PASS** ('Avalanche's Marauders', funds 647740, day 171, Alloway;
  391 KB, built in ~0.6 s, guest load ~5-12 s). The first run failed: the guest reported the
  placeholder `SimGameState` (funds 0) 0.1 s into the load, hence the UX-attached signal.
- `two-copy-test.ps1 -ShipRequests`: the guest (`-coop-test-requests`) renames a 'Mech, refits
  it (CT armor -5, one weapon removed), trains a pilot one Gunnery level and renames them,
  through the same functions the screens call. **PASS**: 4 requests applied, 0 refused; the refit
  is 2 tasks / 500 C-Bills on both sides (host funds 647740 -> 647240), and the pilot's skills,
  XP and abilities match exactly. An earlier run hit a null in the game's pop-up code because the
  guest's screens were still attaching, which the UX-attached signal also fixed.
- Not yet tested by hand: the real MechLab and barracks screens on the guest, refits finishing
  on the host as days pass, and parts from storage.
- **2026-10-17, first human ship test (0.0.9, two PCs over Steam): works.** The guest followed
  the host into the campaign; pilot rename, pilot training (2500 XP), 'Mech rename and a
  Vindicator refit (9000 C-Bills) were all applied on the host, none refused. Reported: the
  host's open tab showed a change only after switching tabs; the guest did not see the host's
  own changes until a refresh; refresh took 10-30 s with no message (a second click while
  loading started a second load). All three addressed in 0.0.10 (updates, redraw, refresh).
- 0.0.10 `-ShipRequests` now runs both directions: the host (`-coop-test-requests`) also renames,
  refits, trains and renames its own 'Mech and pilot 3 s after sending its ship, so its updates
  arrive while the guest is still loading and must wait. **PASS**: 4 requests applied on the host
  and 4 updates on the guest, refits (2 tasks / 500 C-Bills) and pilots identical both ways, no
  errors. The first run caught the redraw touching a Mech Bay panel that was in the scene but
  never opened; it now redraws only `Visible` screens. The redraw itself with a screen open is
  only tested by hand.
- **2026-10-18, human test of 0.0.10:** 'Mech renames redrew at once for the other player, but
  Barracks changes showed only after leaving and re-entering. Cause: the "unconfirmed training"
  check compared the detail panel's `tempPilot` with `curPilot`, but `SetPilot` leaves
  `tempPilot` null until something is edited, so it always waited, and while the Barracks was
  open it also held up every other redraw. 0.0.11 uses the game's own `IsCurPilotModified`. The
  host side of `-ShipRequests` now opens the Barracks first (as `MemorialWallShortcut` does,
  with the personnel menu: queue menu 2, `ForceShipRoomChangeOfRoom(4)`) and the test requires
  a Barracks redraw on the host: **PASS** (redrawn for each of the guest's pilot changes).
- 0.0.12 `-ShipRequests` adds, on top: the host stores a 'Mech, hires a pilot and renames them,
  buys and sells a weapon; the guest hires a different pilot, buys and sells, dismisses the
  pilot the host hired (found by the host's ID), readies the 'Mech the host stored and cancels
  that order (the new 'Mech's ID must match for the cancel to find it), and renames the company.
  Both sides log a digest (company, funds, pilots, bays, work orders, storage counts, pilots for
  hire) after every applied change; the host's last must equal the guest's final one.
  **PASS**: 9 requests and 9 updates, none refused; the readied 'Mech is `SGRef_1000006363` on
  both; digests equal. The previous run caught `ReadyMech` rewriting its argument: the host got
  the storage stat ID, readied nothing, and refused the cancel. Store and Hiring Hall redraws
  are only tested by hand.

## Open questions

- Can a mid-mission save be loaded identically on another PC? This is needed for reconnecting.
- ModTek 4 (HarmonyX with a Harmony 1 shim) or HBS's built-in loader (Harmony 1.2.0.1)?
  We currently build against the game's Harmony 1.2 API, which works under both.
- Flashpoints and DLC story missions are untested.
