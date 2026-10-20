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
| `TripleTriadSelDeck` | node 3 confirm (the game's default deck); list node 2: clicking a row picks that deck and starts the match | `ButtonClick` 2 / `ListItemClick` 1 | `[5]` / `[row]` (2026-10-20: `[1]` played Deck 2; clicks on empty rows did nothing) |
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

## Session 5: 2026-10-19, Open Tournament Official (Gold Saucer): the schedule

### Menu path

| Step | Addon | Action | Callback |
|---|---|---|---|
| Greeting ("Welcome to Card Squa…", speaker "Open Tournament Offi…") | `Talk` | MouseClick | none |
| Menu | `SelectString` | `[2]` prompt (e.g. "Open tournament registration closed.\nNext tournament begins in 4 minutes."), `[5]` entry count = 4, entries from `[7]`: 0 "Sign up for an Open tournament.", 1 "View the Open tournament schedule.", 2 "View tournament mode explanations.", 3 "Cancel." | `[k]` |
| Sign up while closed | `Talk` | "I'm terribly sorry, …" then "You are more than we…", 2 clicks, back to the menu | none |
| View schedule | `TripleTriadSchedule` | read-only list; close with the window's close button (Window component node 7 → button 7, `ButtonClick` 2) | none seen |

### `TripleTriadSchedule` (`AddonTripleTriadSchedule`, agent `TripleTriadSchedule`, 26 AtkValues)

| Index | Content |
|---|---|
| `[0]` | UInt 2 (meaning unknown; constant in the one capture) |
| `[1 + 2k]` | start time of tournament k in **local client time**, `"MM/dd H:mm "` (trailing space), k = 0..9, every 2 hours, the next tournament first |
| `[2 + 2k]` | `"★Basic Ruleset\nDraft"` or `"★Advanced Ruleset\nDraft/<rule>\n<rule>"`: rule names in the client language, after the first line split on `/` and `\n` (some entries are ManagedString, same text) |
| `[21..24]` | undefined |
| `[25]` | UInt 10, entry count |

Nodes: header texts 3 "Date/Time", 4 "Rules"; list component node 5 (`ListItemRenderer` rows 4, 41001–41007, time text node 2, rules text node 3); `ListItemClick` exists but selecting a row did nothing visible.

### The schedule comes from the `TripleTriadTournament` sheet

`TripleTriadTournament` has 10 rows of 4 rule ids (`TripleTriadRule`). The odd rows are `[15]` (Draft, the basic ruleset) and the even rows are Draft plus two rules:

| Row | Rules |
|---|---|
| 1, 3, 5, 7, 9 | Draft |
| 2 | Draft, Same, Three Open |
| 4 | Draft, Ascension, Fallen Ace |
| 6 | Draft, Plus, Same |
| 8 | Draft, Reverse, Three Open |
| 10 | Draft, Plus, Swap |

The rows run in order, one per two-hour slot, starting on odd UTC hours. All 10 entries of the capture fit this formula, with the start time in UTC:

```
row = ((unix_seconds / 7200) + 3) % 10 + 1
```

For example, 2026-10-20 01:00 UTC (03:00 local, UTC+2) gives row 6, Draft/Plus/Same, and 17:00 UTC gives row 4, Draft/Ascension/Fallen Ace. This is fitted from one capture (10/20 01:00–19:00 local). Recheck it against a schedule from another day before relying on it. The column order of the sheet matches the displayed order. `TripleTriadCompetition` holds only the four named tournament names (Manderville, Spinner's Pull, Durai, Rowena Cup) ×6 and plays no part here.

## Session 6: 2026-10-19 23:00 UTC, Open Tournament (basic ruleset: Draft), 8 players, the rest filled with NPCs

Result: 2nd of 8, 4 points, +2 captures. Round 1 lost 4-6 to Prideful Stag (played by hand after the plugin stalled), round 2 won 7-3 against Wyra "Greenhands" Lyehga, round 3 won 6-4 against Flichoirel the Lordling (both auto-played). ProbeReplay: all 3 matches replay with 0 mismatches.

### Draft: `TripleTriadPickUpDeckSelect` (81 AtkValues), then `TripleTriadDeckConfirmation` (28)

| Index | Content |
|---|---|
| `[0]` | 2 |
| `[1]` | "Draft" |
| `[3 + 5k]`, k = 0..14 | card image icon, **87000 + card id** |
| `[4 + 5k]` | stars |
| `[5 + 5k]` | type (0 none, 1 Primal, 2 Scion, 3 Beastman, 4 Garlean) |
| `[6 + 5k]` | sides packed as bytes, high to low: `right, left, bottom, top` |
| `[77]` | "Time Remaining: m:ss" (2:00 to start; refreshed every second) |

The 15 offered cards come three per rarity, interleaved by position (3★ at 0/2/4, 5★ at 1/3/5, 2★ at 6/8/10, 4★ at 7/9/11, 1★ at 12–14). The player picks one per rarity. Picking cards sent no callback. The callbacks were `[1]` and `[2]` (5 s apart, button meaning not yet known), then `[-2]` to close. `DeckConfirmation` lists the 5 picks as `(name, 87000 + id, stars, type, packed sides)` in stride 5 from `[0]`, with the timer at `[25]`. Confirming sends `[0]`, then `[-2]`.

**Drafted deck order:** the hand is dealt in the order 3★, 5★, 2★, 4★, 1★ (slot 0..4), for us and for the opponent. Every card revealed from a red hand slot in the 3 matches had its slot's rarity. `HandBelief` uses this: under Draft each hidden slot is sampled from the cards of its rarity (16 samples), and any NPC sheet deck is ignored.

### Tournament windows

| Addon | Values | Callbacks |
|---|---|---|
| `TripleTriadTournamentMatchList` (74) | `[1]` countdown, `[2]` status line, `[3]` "Round N Match Lineup", `[5..8]` table names; per player stride 8 from `[9]`: name, 2 bools (the first two are `true` for the local player), content id, "Points: n", "Captures: n", state (2 waiting, 9 win, 10 lose), state text | `[-2]` (close) |
| `TripleTriadTournamentResult` (97) | `[0]` round count?, `[1]` 3, per player stride 11 from `[2]`: rank, name, points, captures, **5 deck cards as 88000 + id**, 2 bools (the first is `true` for the local player) | `[5]`, then `[2, false]` |
| `TripleTriadTournamentReward` (12) | `[1]` "1,000 MGP", `[2]` " x 150 ", `[4]` "600 MGP", `[5]` 1600 total, `[6]` 10130, `[8]` 27661 (item ids?) | `[1]`, then `[0, false]` |

Scoring: a win is worth 2 points, a draw 1, a loss 0. Captures = the sum of (cards - 5). Every player's drafted deck (results window) was one card of each rarity.

A tournament match has **no `TripleTriadResult`**: the board closes on the final position. `MatchTracker` now ends a match when a full board closes without a result window.

### Why match 1 stalled

The plugin identified Prideful Stag from the target (TripleTriad 2293864, a tournament row with its own sheet deck) and built the hand belief from that deck. Draft replaces the deck: the first card the NPC played (5563, Beastman) was not in the sheet pool, the belief became unusable, and auto play waited with "no playable decision" (not logged then; refusals are now logged once per reason). Draft now ignores NPC decks. Name lookup also cannot resolve tournament NPCs: names like "Wyra "Greenhands" Lyehga" exist twice in the sheet (world NPC + tournament row), so `NpcByName` returns nothing and the opponent is sampled.

## Session 7: 2026-10-19 23:25 UTC, Open Tournament (Draft) with another player (Fox Dragus)

Result: 5th of 8 (3 points). Round 1 vs Fox Dragus: draw 5-5 (auto). Round 2 vs Flichoirel: won 6-4 (auto). Round 3 vs Fox Dragus again: lost 4-6, played by hand after the plugin stalled. ProbeReplay: 0 mismatches.

**Seats.** The board names its blue player in AtkValues `[1]` (title in `[2]`) and its red player in `[4]` (title `[5]`). Against NPCs we are always blue. In round 3 the other player was blue and we were red. Our cards sat in the red hand slots, with owner 2 on the board. The game still announced our turn with command 1. Our placements were sent as `[14, slot << 16 | cell]` with the **red slot index** of our card and the absolute cell, the same encoding as when blue. The plugin had read the other player's face-down hand as ours ("no legal move"). `BoardReader` now mirrors the frame when `[4]` is our character's name: hands swapped, owners 1 <-> 2, opponent name from `[1]`.

**Draft slot order is per tournament.** All 8 decks again had one card per rarity, all in the same slot order, but this time it was 2-1-4-3-5 stars. The first tournament used 3-5-2-4-1. The order also matched our own hand, so `MatchTracker` reads our hand's rarities at the deal (`SlotStars`) and `HandBelief` gives each hidden red slot that rarity. Without the order it falls back to "the rarities not yet seen".

**Rematches.** Each participant drafts from its own offer (tournament 1: of the 7 other decks, one shared a single card with our 15 options, the rest none), so nothing is known before the first match against someone. A drafted deck is kept for the whole tournament, though: Fox Dragus showed all 5 cards in round 1 and played 4 of them in round 3. `TournamentDecks` (in `MatchTracker`) remembers each opponent's shown cards by board name until the next tournament slot starts, and `HandBelief` fills the hidden slots of their rarity from it. Against Fox Dragus in round 3 that would have been an exact hand. The draft window's "recommended" picks were used, and the draft callbacks were `[1]`, `[2]`, then `[-2]` as before.

## Session 8: 2026-10-20 10:27-11:14 UTC, rule hunting (Aurifort, Yellow Moon, Yayake, Kaizan, Tsuzura)

25 matches, all replayed by ProbeReplay with 0 mismatches (56 matches, 504 placements in total).

**Order** (Aurifort, Three Open + Order, 5 wins on auto): our turn is command 6 with the forced slot in `[25]`, exactly like Chaos, so the existing Chaos path played it. The slots came 0, 1, 2, 3, 4.

**Swap** (Yellow Moon, Kaizan, 2 of Yayake's and 1 of Tsuzura's Roulette rolls). After the rule announcement (command 14 with `[25]` = the rule's icon), the board runs:

| `[23]` | `[24]` | `[25]` | |
|---|---|---|---|
| 25 | 0 | our slot | |
| 25 | 1 | their slot | |
| 24 | 0 | our slot | |
| 24 | 1 | their slot | then the turn order (9, 11, 7) and the first turn |

Yellow Moon: our slot 2 <-> their slot 3. Yayake: 2 <-> 4. Tsuzura: 4 <-> 1. The card we give stays face up in their hand. Every Swap match stalled ("NoBelief: ... does not fit its fixed + variable deck"): `MatchTracker` compared our hand with the saved decks on the first frame of the deal, which is before the exchange, found no difference, and gave up. Our card then showed up in the NPC's hand as an unexplained card. The tracker now keeps the hand as dealt (while it still equals a saved deck) and resolves the swap when the hand changes by one card, with the saved decks as a fallback if the deal was missed.

**Roulette** (Yayake, Tsuzura): the rule text shows the rolled rule. Rolled: Swap x3, Ascension x2, Three Open x3, Descension, Same. No Random.

**Rule points settled:** type shift excludes the placed card (one decisive placement, Yayake 10:48:57). Combo under Reverse is still open: no combo happened in a Reverse match.

**Sessions** started with the previous match's NPC as their target (`Tracker.NpcTriadId` outlives the match). Harmless here because each NPC's menu was already open, but an Interact step would have looked for the wrong NPC. `StartSession` now prefers the current target outside a match.

**Reverse results:** Tsuzura (Chaos + Reverse) won 1 of 5 and Kaizan (Reverse, Descension) 1 of 5. ProbeReplay shows the rules were played correctly. The saved deck is built from high numbers (8744, 7A91, 7479, 7528, 5468), which lose under Reverse, and under Chaos the plugin cannot choose which card to play.

## Session 9: 2026-10-20 11:14-12:55 UTC, Kaizan x10, Tsuzura x70, Nell Half-full x20, Gyoei x20

No stall. 609 automatic placements, and 18 Swap matches (10 Kaizan, 8 Tsuzura), all resolved from the dealt hand ("swap: gave ..., received ..."). Results: Kaizan 8W 2D, Tsuzura 26W 28D 16L, Nell Half-full 20W, Gyoei 14W 4D 2L.

**Combo follows Reverse.** ProbeReplay found 19 mismatches, all in Plus + Reverse matches (Gyoei, and 4 Tsuzura Plus rolls). Same + Reverse matches replayed fine because none had a combo. In every mismatch, a card flipped by Plus then combo-captured (or failed to capture) its neighbour the way Reverse says, not the plain comparison. The engine now uses the placement comparison for combos. After the fix: 176 matches, 1584 placements, 0 mismatches. The plugin went 14-4-2 against Gyoei while mispredicting combos both ways (flips that did not happen, and flips it did not expect).

Tsuzura's Roulette never rolled Random or Order in 70 matches.

## Session 10: 2026-10-20 14:38-14:45 UTC, deck selection rows

`TripleTriadSelDeck` row k is (valid, name, 5 x 88000 + card id) in stride 7 from `[2]`; k is the saved deck slot (`GoldSaucerModule.GetDeck(k)`). Clicking a row fires `ListItemClick` on list node 2, then callback `[k]`, and the window closes: there is no separate confirm. With Deck 2 empty, clicks on rows 1, 2 and 4 sent `[1]`, `[2]`, `[4]` and nothing happened. Once Deck 2 was built, `[1]` started the match, and the hand dealt was Deck 2's cards in slot order (9A17, 6967, 6576, 4782, 5468). The session sends `[k]` itself (`SessionRunner.PickDeck`), but only after checking that row k shows exactly the chosen deck's five ids.

## Session 11: 2026-10-20 14:52-14:53 UTC, building a deck (Gold Saucer window)

Recorded with `GoldSaucerInfo`, `GSInfoCardDeck`, `GSInfoEditDeck` and `ContextMenu` on the watchlist. The player built Deck 3, switched pages, replaced and removed cards, and saved. Every edit is one callback carrying plain values: card ids, not list positions.

| Step | Window | Callback | Effect |
|---|---|---|---|
| Gold Saucer window opens on the Triple Triad deck tab | `GoldSaucerInfo` | `[0, 3, 125]` (sent by the game on open; 125 = the attached child, `GSInfoCardDeck`) | `GSInfoCardDeck` shows the decks |
| Edit deck k | `GSInfoCardDeck` | `[5, k]` (`ListItemClick` on list node 2, row k) | opens `GSInfoEditDeck` for slot k (k = 2 for Deck 3) |
| (editor setup) | `GSInfoEditDeck` | `[16, 0]` | sent by the game once on open |
| Card list page | `GSInfoEditDeck` | `[7, page]` (0-based) | `[1]` = page; list redrawn |
| Put a card in deck slot s | `GSInfoEditDeck` | `[11, s, card id]` (drag from the list; `DragDropInsert` on slot nodes 172-176) | slot filled; an occupied slot is replaced; a card already in the deck is refused silently (no refresh) |
| Slot menu | `GSInfoEditDeck` | `[13, s]` (`DragDropClick` on the slot) | opens `ContextMenu` |
| Remove the card | `ContextMenu` | `[0, 0, 0]` (first entry) | slot s emptied; `[0, -1, 0]` = menu dismissed |
| Save | `GSInfoEditDeck` | `[10]` (`ButtonClick` param 38, node 180) | `GSInfoCardDeck` row k shows the new deck at once; `[631]` true -> false |
| Close the editor | `GSInfoEditDeck` | `[17]`, then `[-2]` | |
| Close the window | `GoldSaucerInfo` | `[-1]`, then `[-2]` | `GSInfoCardDeck` closes with it |

**`GSInfoCardDeck` values** (71): `[0]` = 5 decks, then per deck (valid, name, 5 x 88000 + id) in stride 7 from `[1]`. This is the same layout as `TripleTriadSelDeck`, shifted by one.

**`GSInfoEditDeck` values** (638):
- `[1]` page, `[2]` owned card count (58), `[3]` "Total: 58".
- The list shows owned cards only, 30 per page, in stride 17 from `[14]`: id, "No. n", name, 88000 + id, 87000 + id, packed sides, the four sides as text, stars, type, two flags, the acquisition icon, the acquisition text, a flag.
- `[524]` deck name, then the 5 slots in stride 17 from `[525]`, with the same fields as the list plus an occupied flag at +13.
- `[633]` prints the deck rules: "★★★★★: 1 card per deck, ★★★★ or more: 2 cards per deck, ★★★ or less: no limit". This confirms `DeckRules`.

**Notes for automation:**
- **Rarity limits:** the editor accepted a third 4★ card mid-edit (Great Gold Whisker, then Ultima Weapon and King Elmer III); the limits are presumably checked on save. The builder only produces legal decks.
- **Page independence:** the placement callback carries the card id, and Ultima Weapon (No. 51, page 1) was placed while page 2 was showing. Placing a card should therefore not need its page. Verify this when automating.
- **Opening the window:** ClientStructs has `AgentGoldSaucer` (Show/Hide). Whether opening it lands on the deck tab, as here, is still to see.

