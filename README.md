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

The main window is drawn as a Triple Triad card (version next to the name). At the top, always in view: auto play, whatever is running with its stop button, and the opponent with the match's rules. Below: the draft advisor while drafting, the match (both hands, the board, scores, and the move to play glowing green with what it guarantees), then tabs for farming, the deck builder, Open Tournaments and the match log. Settings choose whether the window opens when the plugin loads and the backdrop (Crimson or Midnight, and its opacity).

Target the NPC before challenging it. The target identifies the NPC's deck, and without that its hidden cards cannot be read.

## Versions and releases

The version lives in `Directory.Build.props` (`<Version>`); the window shows it next to the name and the changes go under `## Unreleased` in [CHANGELOG.md](CHANGELOG.md).

| Workflow | Trigger | Does |
|---|---|---|
| CI | push to `main`, pull request, manual | builds the plugin in Release, runs the tests, uploads `latest.zip` |
| Release | tag `v*` | checks the tag against `<Version>`, builds, tests, publishes a GitHub release with `latest.zip`, updates `repo.json` on `main` |
| Testing release | tag `testing-a.b.c.d` | publishes a pre-release and the testing channel of `repo.json`, keeping the stable entry |

Cut a release from a clean `main` that matches `origin/main`:

```powershell
./tools/release.ps1 -Bump minor        # or patch / major, or -Version 1.0.0; -DryRun to preview
```

It runs the tests, dates the Unreleased notes, bumps `<Version>`, commits, tags `vX.Y.Z` and pushes both; CI does the rest. A testing build: `git tag testing-1.1.0.1; git push origin testing-1.1.0.1` (four parts, newer than the stable version).

## Status

| Piece | State |
|---|---|
| Rules engine, exact solver, belief, policy | Done. Brute-force-checked (Chaos included), differential-tested against ATTG's engine, live matches replay exactly |
| Board reader, tracker, adviser window | Working in game (Ascension, Sudden Death, Three/All Open, Plus, Same, Chaos) |
| Auto play of our moves | Working in game. 2026-10-19: 7 consecutive matches fully auto-played, 33 placements, all accepted, 6 wins / 1 loss (Chaos luck; every move optimal in hindsight) |
| Journals | `matches.csv` (results), `npc_moves.jsonl` (NPC moves for the opponent model) |
| Farming session (talk, challenge, deck, play, rematch, quit) | Working in game. 2026-10-19: 10-match session vs hall overseer (Chaos + Fallen Ace), 6W 4D 0L, every window action sent once. Two fixes from that run: the NPC-menu icon varies by NPC (71101/71102), and the session now quits through the last result window instead of ending before its buttons go live |
| Open Tournament (Draft) | Matches auto-played after the fix for drafted decks; 2026-10-19: 2nd of 8. Draft picks and tournament windows are manual. The main window lists the next 10 tournaments and their rules |
| Deck builder | Main window, "Deck builder": best decks from the owned cards against the targeted NPC, and how the saved decks compare (`TrialHater.Core/Deck`, offline: `tools/DeckBench`). Choosing a saved deck in the session waits on mapping the deck list's row selection |
| Anti-AFK | Control press when the game's idle counters pass 30 s during unattended auto play (ported from MahjongHater) |
| Deck writing | Working in game (2026-10-20): "Write #1 to Deck N" builds a deck through the Gold Saucer editor in about 5 s, checking the editor shows the target deck first and game memory after. Only the slot chosen in Settings is ever written. With "Sessions build the best deck first", a session builds the best deck against its NPC before talking to it, when it is new and beats the saved decks by 0.10 or more, then plays it |
| Draft advisor | Main window, "Draft advisor", while the tournament draft window is up. The draft is three picks (two of pairs, one of single cards), so 27 decks are possible. The advisor ranks them under the current tournament's rules, against drafted decks seen in past tournaments (research/data/draft-decks.csv plus every results window since, in tournament_decks.csv), and shows one option per pick. With auto play on and "Draft the advised deck" set, the plugin presses those options and confirms, but only when the confirmation shows exactly the advised deck. It also presses OK on each round's report window |
| Open Tournament queue | Main window, "Open Tournament queue": with the Official targeted, signs up, commences when the duty pops, and closes the results and rewards, N tournaments in a row. The draft and matches run on auto play (English client texts) |
| Card registration, travel between NPCs | Not built |
| Not yet seen in game | Random; a combo deciding Fallen Ace (docs/RULES.md) |
