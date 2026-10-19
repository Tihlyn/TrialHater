# Mapping the game's Triple Triad state

Saucy, TriadBuddy and ATTG all read the **UI addon** (`AddonTripleTriad`). This page records what we have verified ourselves with Cartographer and the plugin's model probe.

## Session 1: 2026-10-19, Ruhtwyda of the Three Hearts (Ascension + Sudden Death)

Inputs: the plugin probe (`model_probe.jsonl`), Cartographer's addon layouts and callbacks, a memory watch on `UIState.TripleTriad` and agent 174, and a ReceiveEvent trace of agent 174. The match is kept as a replay test: `TrialHater.Tests/LiveReplayTests.cs`.

### Card slots (`AddonTripleTriad.TripleTriadCard`)

- **`NumSideR` holds the card's left value and `NumSideL` its right.** All five NPC cards matched their sheet rows (TripleTriadCardResident Top/Right/Bottom/Left) only with these two swapped. ATTG's matcher swaps them silently. The reader corrects it in `BoardReader.Slot`.
- With that correction the engine reproduces every capture of the match, including Ascension.
- **Board slots show raw values under Ascension.** A Scion still read 9-5-3-8 after a second Scion landed.
- **A face-down card has `HasCard` set and all four sides 0** (type 0, rarity 1). The NPC's played card appears face-up in its hand slot the frame before it lands. Its leftover card is revealed at the end.
- `CardOwner` is 1 for the player and 2 for the NPC.

### Board AtkValues (count 26): a command stream from the agent

Each Refresh sets a command code in `[23]` with arguments `[24]` and `[25]`. The addon acknowledges each command with callback `[12]`.

| [23] | [24] | [25] | Meaning |
|---|---|---|---|
| 12, 16, 9, 11 | | (16: 31) | match start sequence (deal, rule announce, first player) |
| 7 | 0 player / 1 NPC | -1 | turn starts |
| 1 | 0 | -1 | **player may act** (follows `7, 0`) |
| 25 | side | hand slot | card picked |
| 23 | cell | hand slot | card placed |
| 27 | cell | hand slot | placement resolved |
| 3 | varies (48, 67, -123...) | 0 | after captures (meaning open) |
| 14 | 11 | 121622 | rule effect banner (Ascension here) |
| 17 | 2 | cell | typed card value update, one per cell |
| 10 | 1 | 0 | board full; callback `[-2]` follows |

Other values: `[1]` player name, `[4]` NPC name, `[8]`/`[9]` rule names (the previous match left "All Open" in `[8]` until refreshed), `[14..22]` constant ids.

The **ATTG claim "`[23] == 1` on the player's turn" holds**, but because 1 is the "player acts" command, not a turn flag. `CanAct` = command 1 and `TurnState` 1 or 2. `TurnState` was 1 during the NPC's turn as well, so it is not enough on its own.

**Session 2 (Malevolent Weasel: Three Open, Plus, Chaos)** answered the open question. Under Chaos the "player may act" command is **6** (not 1), with `[25]` = **the forced hand slot**, and `TurnState` is 2. All five plays used exactly that slot. The art tint that Saucy and ATTG read for Chaos (MultiplyRed < 100) stayed 255 on every card for the whole match: on this client the command is the only working signal. Three Open shows three NPC cards; their slots carry owner 2 while in hand, and hidden slots carry owner 0. The left/right correction matched all five of this NPC's cards. The match replays exactly (`LiveReplayTests.MalevolentWeasel_PlusChaos_2026_10_19`).

Still open: Order (presumably command 6 with the leftmost slot), Swap, Random, Reverse, Fallen Ace, Same.

### Placement

The player's drag-drop reaches agent 174 as `ReceiveEvent [Int 14, UInt slot << 16 | cell]` (seen as callback `[14, x]`). **The plugin's `BoardActuator` sent the same values on the first move, and the game accepted it**: the card landed 80 ms later.

### Session protocol (sessions 1–4, used by `SessionRunner`)

| Window | Control | Event | Callback the game sends |
|---|---|---|---|
| `SelectIconString` (NPC menu) | entry whose icon (AtkValues `6 + 3k`) is **71101** (Malevolent Weasel) or **71102** (hall overseer) | — | `[k]`, close = true |
| `Talk` | collision node | `MouseClick` | none; `[0]` is the line, `[1]` the speaker (sometimes empty) |
| `TripleTriadRequest` | node 41 Challenge / node 42 Quit | `ButtonClick` 1 / 2 | `[0]` / `[1]` |
| `TripleTriadSelDeck` | node 3 confirm; list node 2 selects a row | `ButtonClick` 2 / `ListItemClick` 1 | `[5]` |
| `TripleTriadResult` | node 20 rematch / node 21 quit | `ButtonClick` 1 / 2 | `[0]` (the game then sends `[-2]` itself) / `[1]` |

A rematch goes Result → Request → deck → board without the NPC menu or Talk. Quitting adds one Talk line. `SelDeck` values: `[0]` deck count, then per deck `(valid, name, 5 small-icon ids = 88000 + card id)` in stride 7 from `[2]`. `Result` values: `[3]` the prompt ("Seek a rematch?", then "Rematch request submitted..."), `[9..11]` the reward card (item id, name).

### Match windows (first capture)

| Step | Addon | Node / event | Callback |
|---|---|---|---|
| "Triple Triad Challenge" | `SelectIconString` | first entry | `[0]` |
| NPC lines | `Talk` | click ×4 | (none: click events) |
| Challenge | `TripleTriadRequest` | ButtonClick 1 on node 41 | `[0]` |
| Deck chosen | `TripleTriadSelDeck` | ButtonClick 2 on node 3 | `[5]` (meaning of 5 still to read from its values) |
| Leave result | `TripleTriadResult` | ButtonClick 2 on node 21 | `[1]` (ATTG: `[0]` = rematch, `[1]` = quit) |

### Game-side objects

| Object | Finding |
|---|---|
| `UIState.TripleTriad` (+0x13D58, 0x1830 bytes) | **Did not change during the match** (0 bytes in Cartographer's watch and in the plugin probe). Not the live match model; probably tournament/ranking data. |
| `AgentTripleTriad` (id 174) | Changes every frame. In the first 0x400 bytes, `+0x19C`/`+0x1A0` held a `(slot, cell)` pair per move: (0,3), (1,4), (4,8), (4,0), each matching a placement logged around it, and -1 when idle. `+0x188`/`+0x190` were small counters; `+0x1A8` changed most frames (a timer?). Card data was not seen in the first 0x400 bytes; the agent is larger. |

Next: watch the agent beyond 0x400, and look for the NPC's hand (card ids) before it is revealed.
