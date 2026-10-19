# Cross-review of prior research, independent checks, and recommended approach

Date: 19 October 2026. Inputs reviewed: [REPORT.md](../research/REPORT.md) (source audit and architecture), [ffxiv_triple_triad_cnn_plan.md](../research/ffxiv_triple_triad_cnn_plan.md) (neural policy plan), the pinned checkouts under `research/sources/`, and the data exports under `research/data/`. New evidence produced here: [validation/SolverBench](../research/validation/SolverBench/) and its output [solver-bench-output.json](../research/validation/solver-bench-output.json).

## Conclusion

Build a new plugin around an **exact game-tree solver** with a **safe-then-exploit** move policy. Do not fork the existing solver lineage, and do not train a neural network.

Exact search takes under a second for the hardest move of a match, and NPC hidden hands (at most 10) are small enough to enumerate. In benchmarks on real card data, the existing lineage's solver loses 32–50% of drawn-or-better positions to a competent opponent. Exact minimax never loses but is passive. Keeping the minimax value and then exploiting an opponent model wins 95–98.5% against the modelled opponent with zero losses. It also matches the existing solver's wins with almost no losses when the model is wrong. The deciding factor for win rate against NPCs is therefore **opponent modelling inside the set of minimax-safe moves**, not search approximations or learned policies. Take the client-interaction layer (FFXIVClientStructs reads, the addon callback, Lumina sheets) from the existing projects as reference. Keep the REPORT's architecture: pure core library, immutable snapshots, freshness guard, and an explicit session state machine.

## 1. How the two prior documents compare

| Question | REPORT.md | CNN plan | Verdict after checks |
|---|---|---|---|
| Scope | NPC automation; PvP explicitly out | NPC **and PvP** | NPC only. PvP automation conflicts with Dalamud's competitive-advantage rule, and TriadBuddy disables itself there deliberately |
| Move selection | Search: iterative deepening, exact endgame, belief sampling, ISMCTS candidate | CNN policy distilled from a search teacher | Exact search for the **whole match**; the teacher is fast enough to be the product |
| Cost assumption | Opening search expensive (9!·5!·5! ≈ 5.2e9 sequences); exact "much more attractive late" | Implicitly expensive (needs a learned approximation) | **Wrong in practice.** Alpha-beta + transposition table solves the opening in ~0.3 s (§3) |
| Hidden information | Constrained belief over NPC fixed + variable pools; warns about strategy fusion | Information-set sampling; warns about leakage and strategy fusion | Both correct in principle. For NPCs the belief has **≤10 hands** (median 6), so exact enumeration replaces sampling |
| Existing code | Adopt ATTG if AGPL is acceptable, else TriadBuddy/Saucy | Lists repos as rules references | Reuse the **interaction knowledge**, replace the **solver** (§2) |
| Validation | Strong: fixtures, freshness, journal, metrics | Strong ML metrics: top-action agreement, regret, held-out splits | Merge: the CNN plan's regret/agreement metrics become solver regression tests |

The REPORT is the stronger document. Nearly all of its factual claims checked out (§2). Its main gap is that it inherited the lineage's assumption that full search is too costly, so it never questioned the rollout design. The CNN plan is methodologically sound ML, but it solves a problem this game does not have. A learned policy can only approximate its teacher, and here the teacher already runs in real time. It would also add a native inference runtime to a Dalamud plugin for no gain. Parts worth keeping: per-action score labels (as solver test fixtures), leakage discipline, and held-out evaluation. The one place a learned model could help is **predicting NPC moves** from logged games, and that needs a small classifier, not a policy CNN.

## 2. Claims re-verified against source

| Claim (REPORT) | Check | Result |
|---|---|---|
| Sudden Death capped at 3 restarts; official guide says 5 | `TriadFlowRules.cs:105` `MaxRestarts = 3`; Saucy `TriadGameModifierRules.cs:86`; origin is TriadBuddy `TriadGameModifier.cs:252` with a `// TODO: don't follow this more than once when simulating in solver?` comment. Official guide: *"until five more consecutive drawn matches have been played"* | **Confirmed.** It is a search shortcut inherited from TriadBuddy, not a measured rule. Low practical impact: the live controller just reads each new round |
| Result score ranks a lower win chance higher | `TriadSolver.cs:27-41`: banded score `win/10`, `draw`, `win+10` | **Confirmed** (probe: 40/35/25 beats 45/50/5) |
| Opponent layers aggregate rather than minimise | ATTG `TriadAgents.cs:183-198`, Saucy `TriadGameAgents.cs:327-345` | **Confirmed, and more significant than stated.** The opponent level sums its children, so every opponent move counts equally: the search assumes a uniformly random opponent. Below 5 placed cards, values come from random-vs-random rollouts (`MaxStatesToExplore = 10_000` gives an exhaustive threshold of 5 placed cards) |
| Same lineage across projects | Identical structure and constants in TriadBuddy → Saucy → ATTG; ATTG `THIRD-PARTY-NOTICES.md` | **Confirmed.** The three solvers are one data point, not three |
| ATTG solves off-thread but does not re-validate before placing | `AutoCommon.TriadMatch.cs:248-255` checks only `frame.CanAct`; `Task.Run` at 288 on a shared `memory.Solver` | **Confirmed.** Also a shared-solver race risk |
| Saucy solves synchronously | `TriadSession.Move.cs:92` | **Confirmed** |
| Move submission path | Saucy `TriadBoardAutomation.cs:70` and ATTG `TriadMatchOps.cs:87` both `Callback.Fire(addon, true, 14, cell \| slot << 16)` | **Confirmed, identical.** This is the one callback a new plugin must reproduce |
| Board data available | `AddonTripleTriad`: 5+5 hand slots, 9 board slots; sides, rarity, type, owner, `TurnState` (Waiting/Normal/Masked) | **Confirmed.** No card ID; identity is inferred from sides+type+rarity against NPC pools |

## 3. New evidence: [SolverBench](../research/validation/SolverBench/)

I wrote a small independent engine ([ExactEngine.cs](../research/validation/SolverBench/ExactEngine.cs)): bitboard ownership, Same/Plus/Combo, negamax with alpha-beta and a Zobrist transposition table, valued W/D/L. It is linked against the **unmodified** ATTG logic, the same way RuleProbe is. The run uses all 475 real cards; "strong" decks are one 5★ + one 4★ + three 3★ drawn at random. Machine: 16 logical cores, .NET 10; the exact engine is single-threaded.

**Differential test (correctness).** 20,000 random-move games per rule set (Plain, Same, Plus, Same+Plus), 720,000 placements in total: **0 ownership mismatches, 0 outcome mismatches** against the upstream simulation. The timing and regret numbers below therefore measure the same rules.

**Exact solve cost.** 60 random strong deck pairs per rule set, single thread, cold table:

| Rules | Opening best move, median / p90 / max | All 45 opening moves scored, median | Second player's first move, median / max | Perfect-play result for first player (L/D/W) |
|---|---|---|---|---|
| Plain | 323 / 637 / 890 ms | 1.58 s | 45 / 126 ms | 1 / 58 / 1 |
| Same | 383 / 702 / 969 ms | 1.87 s | 62 / 153 ms | 1 / 58 / 1 |
| Plus | 326 / 616 / 867 ms | 2.20 s | 58 / 125 ms | 0 / 60 / 0 |
| Same+Plus | 359 / 625 / 967 ms | 2.45 s | 68 / 149 ms | 0 / 60 / 0 |

The opening is the hardest decision of the match, and it takes under a second on one core. Every later move is faster. Root moves are independent, so multithreading divides this further. Under the 90-second turn timer the opening needs no approximation, and from the second decision on, every move can be solved exactly with time to spare. **Perfect play is a draw in 236 of 240 deals.**

**Upstream live agent quality, full information** (its most favourable case: both hands known). 100 deals per rule set; ATTG's `TriadLiveAgent` plays Blue. Each ATTG decision made against a perfect Red was scored exactly. The same deal is then replayed with exact minimax as Blue.

| | Plain | Same+Plus |
|---|---|---|
| ATTG decisions that lowered the game-theoretic value | 31 / 450 (6.9%) | 52 / 450 (11.6%) |
| …of which threw away a won position / a drawn position | 2 / 29 | 2 / 50 |
| ATTG move time, median / max (16 threads) | 1.4 / 115 ms | 2.4 / 92 ms |
| **vs perfect Red:** ATTG W/D/L | 1 / 67 / **32** | 0 / 50 / **50** |
| **vs perfect Red:** exact minimax W/D/L | 3 / 94 / 3 | 2 / 98 / 0 |
| **vs greedy Red:** ATTG W/D/L | 77 / 13 / **10** | 78 / 12 / **10** |
| **vs greedy Red:** exact minimax W/D/L | 59 / 41 / 0 | 79 / 21 / 0 |

(Exact's 3 losses vs perfect Red are deals that are lost from the start under perfect play.)

Two conclusions follow:
1. The upstream agent assumes a random opponent, so a competent opponent punishes it: it loses a third to half of positions that were at least drawn.
2. Pure minimax never loses, but it is too passive against a weak opponent: 59% wins against greedy versus ATTG's 77% under plain rules. Neither is right on its own, which motivates the lexicographic policy below.

**Safe-then-exploit policy** ([SafeExploit.cs](../research/validation/SolverBench/SafeExploit.cs), output [solver-bench-exploit.json](../research/validation/solver-bench-exploit.json)). Among the moves that keep the exact minimax value, Blue picks the one that maximises the expected result against an opponent **model** (greedy, uniform tie-break). This is the expectimax over Blue's safe moves and the model's replies, with memoisation. The opening move uses plain minimax. The test plays 200 deals per rule set against two opponents:
- **greedy**, where the model is correct;
- **noisy** (each move 50% uniform-random, 50% perfect), where the model is deliberately wrong.

| Blue policy (W / D / L of 200) | Plain vs greedy | Plain vs noisy | Same+Plus vs greedy | Same+Plus vs noisy |
|---|---|---|---|---|
| ATTG live agent | 149 / 32 / **19** | 158 / 34 / **8** | 169 / 14 / **17** | 161 / 19 / **20** |
| Exact minimax | 131 / 68 / 1 | 132 / 67 / 1 | 161 / 39 / 0 | 156 / 44 / 0 |
| **Safe-then-exploit** | **190 / 10 / 0** | 157 / 42 / 1 | **197 / 3 / 0** | 160 / 40 / 0 |

Move time: median 1.7–1.8 ms, p90 177–250 ms, max 1.4–2.1 s, single thread. With exploitation also applied to the opening move, the worst case rises to 6–9 s single-threaded. That still fits the 90 s timer and parallelises across root moves.

What this shows:
- **Safety is free.** Safe-then-exploit loses at most the deals that are lost under perfect play (1 of 800 here).
- **Exploitation pays when the model is right:** 95–98.5% wins against greedy, versus 75–85% for ATTG and 65–80% for minimax.
- **With a wrong model, the policy matches ATTG's win count with almost no losses** (1 vs 28 across the noisy runs).
- **Win rate therefore depends on modelling real NPC behaviour accurately.** That is where measurement effort belongs, not in deeper or learned search.

The ATTG and minimax rows come from a fresh run, and the first 100 deals per rule set are the same deck pairs as the regret experiment. They differ from the earlier table because greedy tie-breaks draw a different random sequence. Treat a few-game difference as noise.

## 4. Facts from the data that shape the design

- **NPC hidden hands are tiny.** All 134 named NPCs: fixed/variable splits range from 1+5 to 5+0; possible hands min 1, **median 6, max 10**. Three/All Open and every revealed card shrink this further. Enumerate the hands; no sampling needed.
- **Rule frequency across NPCs** (sheet IDs): Plus 46, Same 31, Swap 24, Three Open 21, Chaos 21, Roulette 15, Fallen Ace 14, Ascension 10, All Open 9, Descension 7, Order 6, Reverse 6, Sudden Death 3, before regional rules. Plus/Same/Combo and Chaos must be first-class from day one.
- **Perfect play is usually a draw** with competitive decks (§3). Against a perfect opponent you rarely win, so throughput depends on exploiting NPC errors while never giving up the minimax value.
- **Community signal:** a TriadBuddy user reported in Sept 2026 that its "optimized" decks won less than their hand-built ones ([Discord](https://discord.com/channels/581875019861328007/581875020632948762/1544336203724226710)). This is anecdotal, but it matches the rollout/scoring weaknesses above.

## 5. Recommended approach

### Solver (the differentiator)
1. **Exact negamax + alpha-beta + TT** over the full rule set. The key covers board, owners, hands, side to move, type counters (Ascension/Descension) and forced slot. Reverse and Fallen Ace go into the comparison function. Chaos becomes an expectimax chance node over the forced card. Order fixes the card index. Swap and Random are observation problems, not search problems.
2. **Hidden NPC hand:** enumerate every hand consistent with the pool and observations (≤10). Solve each exactly. Pick the move that is minimax-safe under all hypotheses (or under the most probable ones), then break ties by expected win under the NPC model. For NPCs the info set collapses quickly, so strategy-fusion error is small. Measure it anyway against an information-set solver on the same tiny trees.
3. **Objective:** lexicographic. First keep the guaranteed (minimax) result; then maximise P(win) against the fitted NPC model; then card margin. This replaces the banded score. It is measured in §3 under "Safe-then-exploit policy".
4. **NPC model:** start from greedy/random/minimax mixtures. Fit weights from logged in-game NPC moves (the adviser phase produces these logs). This is the only learning component worth building.
5. **Deck optimiser:** heuristic prefilter, then exact evaluation of finalists across all NPC hands × both starting players, run on worker threads. This gives a real guaranteed value per deck instead of a rollout estimate.
6. Cross-check transitions against the ATTG engine in CI (as SolverBench does) **and** against recorded client transitions. The upstream engines are not independent of each other.

### Plugin structure (REPORT §"Proposed architecture" holds; concrete choices)
- **Core library** (`net10.0`, no Dalamud references): rules, solver, belief, deck optimiser, fixtures. MIT-licensed own code.
- **Plugin** (Dalamud.NET.Sdk 15): observation adapter over `AddonTripleTriad` and the prep/result/deck-select addons; Lumina sheets for cards, NPC pools and rules; action adapter for `Callback.Fire(…,14, cell | slot<<16)` plus menu callbacks; an `AddonLifecycle`-driven session state machine with per-state deadlines; a freshness guard comparing snapshot fingerprint and turn generation before every placement.
- **Reuse policy:** read TriadBuddy (MIT) and Saucy (BSD-3) for readers, the NPC identification heuristics and hidden-card handling, with notices. Use ATTG (AGPL) as behavioural reference only.

### Phases
| # | Deliverable | Exit criterion |
|---|---|---|
| 1 | Core engine + exact solver + fixtures for every rule | Zero mismatches vs ATTG engine on random games per rule; hand-written fixtures pass; solve p99 within budget |
| 2 | Read-only overlay adviser (no automation) | Snapshots match the screen for N real matches; NPC moves logged |
| 3 | Single-NPC auto-play loop | Freshness guard never fires a stale move; emergency stop; bounded retries |
| 4 | Farming: rematch, reward journal, card registration | Ownership and reward totals reconcile |
| 5 | Deck optimiser + fitted NPC model | Beats seed decks on held-out NPC simulations, then in-game |
| 6 | Collection planner + travel IPC (vnavmesh/Lifestream) | Recovers from travel failure, locked NPCs and zone changes |

### Out of scope
PvP and tournament automation (Dalamud competitive-advantage rule; no fixed opponent pool), and the CNN policy. Automating play is botting under Square Enix's terms and cannot go through the official Dalamud repository. Plan for a custom repository and accept the account risk knowingly.

## Sources
- Official rules: https://na.finalfantasyxiv.com/lodestone/playguide/contentsguide/goldsaucer/tripletriad/
- Dalamud restrictions: https://dalamud.dev/plugin-publishing/restrictions/
- ATTG: https://github.com/XeldarAlz/FFXIV-AutoTripleTriadGrind · Saucy: https://github.com/PunishXIV/Saucy
- Dalamud.NET.Sdk 15.0.0 (29 Apr 2026): https://www.nuget.org/packages/Dalamud.NET.Sdk

