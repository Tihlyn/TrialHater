# TrialHater

A private Dalamud plugin that plays FFXIV Triple Triad against NPCs. It reads the match from the game and enumerates every hand the NPC could be holding (at most 10). It solves each hand exactly and plays the move that keeps the best guaranteed result. Among the moves that do, it picks the one most likely to win against the NPC's play.

The design and the measurements behind it are in [docs/RESEARCH.md](docs/RESEARCH.md). The short version:
- An exact search of the opening takes about 0.35 s on one core.
- The solver used by TriadBuddy, Saucy and ATTG loses 32–50% of drawn-or-better positions to a competent opponent.
- Safe-then-exploit wins 95–98.5% against the modelled opponent and loses none.

> Private tool. It automates gameplay, which Square Enix's terms forbid, and it will never be submitted to the official repository.

## Layout

```
TrialHater.Core/     pure game logic, no Dalamud: cards, rules, engine, exact solver, NPC hand belief, move policy
TrialHater/          the plugin: sheet data, board reader, match tracker, analysis worker, actuator, auto play, windows
TrialHater.Tests/    xunit tests for Core (rules, solver vs naive search, belief, policy)
docs/                RESEARCH (decision record), RULES (engine spec + confidence), MODEL_MAPPING (reverse engineering)
research/            the original research bundle, benchmarks (validation/SolverBench) and the engine differential (validation/EngineDiff)
```

## Build and run

```powershell
dotnet build TrialHater.sln -c Debug
dotnet test TrialHater.Tests
```

In game: `/xlsettings` → Experimental → add `TrialHater\bin\Debug\net10.0-windows\TrialHater.dll` as a Dev Plugin Location. Then enable it under `/xlplugins` → Dev Tools, and run `/xlreload TrialHater` after each rebuild.

| Command | Effect |
|---|---|
| `/thater` | toggle the window |
| `/thater config` | settings |
| `/thater auto [on\|off]` | toggle auto play (placing the recommended card on our turn) |

Target the NPC before challenging it. The target identifies the NPC's deck, and without that its hidden cards cannot be read.

## Status

| Piece | State |
|---|---|
| Rules engine, exact solver, belief, policy | Done. Brute-force-checked (Chaos included), differential-tested against ATTG's engine, live matches replay exactly |
| Board reader, tracker, adviser window | Working in game (Ascension, Sudden Death, Three/All Open, Plus, Same, Chaos) |
| Auto play of our moves | Working in game. 2026-10-19: 7 consecutive matches fully auto-played, 33 placements, all accepted, 6 wins / 1 loss (Chaos luck; every move optimal in hindsight) |
| Journals | `matches.csv` (results), `npc_moves.jsonl` (NPC moves for the opponent model) |
| Farming session (talk, challenge, deck, play, rematch, quit) | Working in game. 2026-10-19: 10-match session vs hall overseer (Chaos + Fallen Ace), 6W 4D 0L, every window action sent once. Two fixes from that run: the NPC-menu icon varies by NPC (71101/71102), and the session now quits through the last result window instead of ending before its buttons go live |
| Card registration, deck choice, travel between NPCs | Not built |
| Not yet seen in game | Order, Reverse, Descension, Swap, Random, Roulette |
