# Rules implemented by the engine

Source of truth: `TrialHater.Core/Engine/Engine.cs`. This page records what the engine does and how sure we are of each point.

## Capture order for one placement

1. **Type shift (Ascension / Descension).** Every typed card is worth ±1 per card of its type already on the board, excluding the card being placed. The shift applies before any comparison. Values are clamped to 1..A.
2. **Same / Plus** on the placed card. Every occupied neighbour takes part, friendly ones included. The enemy cards among the matches flip and seed the combo.
3. **Plain captures** from the placed card, with Reverse and Fallen Ace applied.
4. **Combo.** Each card flipped in step 2, and every card flipped by the combo after it, captures weaker enemy neighbours by plain comparison. Reverse, Fallen Ace and a new Same/Plus do not apply.
5. The placed card's type count increases, which shifts later placements.

Scoring: owned board cards plus any card still in hand. 6+ of 10 wins and 5 draws.

## Card selection

| Rule | Engine treatment |
|---|---|
| Order (ours) | Leftmost remaining card only |
| Order (NPC) | Chance node over its remaining cards, because its deck order is not visible |
| Chaos | Chance node for both sides; our drawn card is read from the screen (the forced slot) when it is our turn |

## Confidence

| Point | Status |
|---|---|
| Plain, Same, Plus, Combo, Reverse, Fallen Ace, Ascension, Descension and all their combinations | Matches the TriadBuddy lineage (ATTG engine) on 2.16 M random placements across 48 rule sets (`research/validation/EngineDiff`) |
| Combo ignores Reverse and Fallen Ace | Lineage spec, written down by TriadBuddy's author (`TriadGameSimulation.cs:246`). **Verify in game**: no combo has occurred under Reverse or Fallen Ace in any recorded match yet (2026-10-20: the only Same + Reverse match had no Same trigger) |
| Type shift excludes the placed card during its own captures | **Verified in game** 2026-10-20, once: Yayake (Chaos, Descension) placed 9596/Primal at cell 4 and captured cell 5, which it does only if its own card is not counted. The same plain-capture model agrees with all 18 typed placements under Ascension/Descension recorded so far |
| Order | **Verified in game** 2026-10-20 (Aurifort, 5 matches): our turn is command 6 with the forced slot in [25], as under Chaos; the slots came 0, 1, 2, 3, 4 |
| Reverse, Descension on a real board | **Verified in game** 2026-10-20 (Kaizan, Tsuzura, Yayake: ProbeReplay) |
| Swap | **Verified in game** 2026-10-20: one card each way, a second after the deal and before the first turn, announced by commands 25 and 24 (`[24]=0, [25]` = our slot, then `[24]=1, [25]` = theirs). The card we give shows face up in the NPC's hand. Play itself is unchanged |
| Plain captures and Ascension on a real board | **Verified in game** 2026-10-19: the engine reproduces all 9 placements of a recorded Ascension match (`LiveReplayTests`) |
| Board slots show raw values, not type-shifted ones | **Verified in game** (Ascension) |
| The addon's left/right fields are swapped (`NumSideR` = left) | **Verified in game**; corrected in `BoardReader` |
| Sudden Death limit | Official guide: five further draws. The engine never searches across rounds, and the tracker counts rounds from the screen |
| Hidden card = sides 0 | **Verified in game** |
| Our turn = AtkValues[23] command 1, or 6 under Chaos with the forced slot in [25] | **Verified in game** (docs/MODEL_MAPPING.md) |
| Plus and Chaos on a real board | **Verified in game** 2026-10-19 (`LiveReplayTests`) |
| Every recorded match | `tools/ProbeReplay` replays the plugin's probe file through the engine. 2026-10-19: 15 matches, 135 placements (Ascension, Sudden Death, Three/All Open, Plus, Same, Chaos, Fallen Ace), 0 mismatches. Whether a Fallen Ace capture (1 takes A) actually occurred in those matches was not checked. 2026-10-20: 56 matches, 504 placements (adds Draft, Order, Swap, Reverse, Descension, Roulette), 0 mismatches |
| Roulette | Seen 2026-10-20 (Yayake, Tsuzura): the board's rule text names the rolled rule, which is parsed like any other. Rolled: Swap, Ascension, Descension, Three Open, Same. Random has not come up |
| Chaos dims undrawn cards (MultiplyRed < 100) | **False on this client**: the tint stayed 255. Not used |

## Search cost under Chaos

Chaos puts a chance node at every turn, so alpha-beta cannot skip much of the tree. Star1 pruning (bounded expectimax) cut the opening by more than half, and splitting the root across workers brought it to ~13 s on 16 threads for 3 hands. From the second decision on it takes under 60 ms. `PolicyOptions.SolveBudget` (40 s) caps the exact half and falls back to immediate captures, saying so in the decision reason.
