# TrialHater

A Dalamud plugin that plays FFXIV Triple Triad against NPCs. It reads the match from the game, enumerates every hand the NPC could be holding, solves each one exactly and plays the move that keeps the best guaranteed result; among those, it picks the one most likely to win against how NPCs actually play. Around that: farming sessions (until N matches or until every card the NPC drops is in), a deck builder that writes the best deck into a saved slot, and the Open Tournament (draft advice, automatic draft, queueing).

> Unofficial tool. It automates gameplay, which Square Enix's terms forbid, and it will never be submitted to the official repository.

## Install

`/xlsettings` → Experimental → Custom Plugin Repositories, add

```
https://raw.githubusercontent.com/Tihlyn/TrialHater/main/repo.json
```

then install TrialHater from `/xlplugins`. Releases are listed in [CHANGELOG.md](CHANGELOG.md).

## What it does

**Auto play.** On our turn the plugin places the recommended card itself (`/thater auto`, or the toggle at the top of the window). An action only counts once the card is seen on the board, a decision is never sent for a position that has changed since, and anything it cannot read or verify stops it with a reason instead of a guess. While auto play runs unattended it keeps the game from flagging the player as idle (a brief Control press once the idle counters pass 30 s).

**Farming** (Farm tab). Target an NPC, stand next to it, and either:
- *Start farming*: N matches, or until stopped (0);
- *Farm until every card drops*: until every card the NPC can drop is collected or in the bags. The tab lists the targeted NPC's drops and which are still missing; the session will not start with full bags and stops if they fill up.

The session talks to the NPC, challenges, picks the deck, plays, rematches and quits through the result window. With "Sessions pick the best saved deck", it ranks your saved decks against the NPC and picks the best one in the deck window. With "Sessions build the best deck first", it searches the best deck from your whole collection and writes it into the one slot Settings allow before the first challenge, if it beats your saved decks clearly.

The deck is chosen for the rules actually in force, read from the challenge window before every match (the NPC's own rules plus the region's, which change over time). If they are not the rules the deck was chosen for, the saved decks are ranked again, or, when the session builds decks, it declines that challenge (a rematch only offers saved decks), builds and writes the deck for the new rules, and challenges again.

**Deck builder** (Decks tab). The best decks from the cards you own against the targeted NPC, how your saved decks compare, and a button to write one into the allowed slot through the Gold Saucer deck editor. Searches are cached per character and reused until a newly obtained card would change them.

**Open Tournament** (Tournament tab).
- *Draft advisor*: while the draft window is up, ranks the 27 possible decks under the tournament's rules against the decks players drafted before, and shows the option to take at each pick. With "Draft the advised deck" and auto play on, it drafts them itself, confirming only when the confirmation shows exactly the advised deck.
- *Queue*: with the Open Tournament Official targeted, signs up, commences when the duty pops, plays, closes the results and rewards, targets the Official again and signs up for the next one, N tournaments or until stopped. Sign-up is open for the first 30 minutes after each start (every two hours, odd UTC hours); outside it the queue waits and says when it opens. English client only.
- *Schedule*: the next 10 tournaments and their rules. *Our drafts*: each tournament we played, our deck and where we placed.

**Match log** tab: every turn and event of the current match, as the tracker read it.

The main window is drawn as a Triple Triad card. Always in view at the top: auto play, whatever is running with its stop button, and the opponent with the match's rules. Below: the match as the game lays it out, with the move to play glowing green and what it guarantees.

## How it decides

The design and every measurement are in [docs/RESEARCH.md](docs/RESEARCH.md); the rules engine and how each rule point was verified in [docs/RULES.md](docs/RULES.md).

- **Exact search.** An NPC's hand is 5 cards from its deck data, at most 10 possible hands, so the plugin solves every one exactly (the opening in about 0.35 s on one core without Chaos). The solver used by TriadBuddy, Saucy and ATTG loses 32–50% of drawn-or-better positions to a competent opponent.
- **Safe first.** A move that wins against every hand the NPC could hold is played first; failing that, one that never loses. Otherwise the move with the best result against the hidden cards.
- **Then exploit.** NPCs do not look ahead: fitted to 1454 logged NPC moves they lean towards immediate captures, noisily. Among the safe moves the plugin takes the one that does best against that model. Under Chaos it may also take a move within 0.1 of the best expected value if it does better against the model.
- **Chaos and Order.** Under Chaos the NPC's card is a random draw from its hand and the plugin plays the expected value of every draw. Under Order the NPC plays left to right, so its visible cards' turns are known.
- **Solve cache.** Openings already solved are remembered across matches (`solve_cache.json`), which farming repeats constantly.

In simulation on recorded matchups (`tools/Arena`, fresh NPC deals, the fitted NPC model as opponent): 96.6 / 3.0 / 0.4 % wins / draws / losses on 16 matchups without Chaos, 54 / 31 / 15 % on 11 Chaos matchups. Against real NPCs these hold within noise except Kaizan (more draws) and Tsuzura under Ascension (more losses); see RESEARCH §8–13.

## Files

Kept in `%APPDATA%\XIVLauncher\pluginConfigs\TrialHater\`:

| File | Holds |
|---|---|
| `matches.csv` | one line per match: NPC, rules, result |
| `npc_moves.jsonl` | every NPC move with the position, for fitting the NPC model |
| `model_probe.jsonl` | the game's own match model at every turn (Settings → Diagnostics, off by default) |
| `solve_cache.json` | solved openings, shared by all characters |
| `tournament_decks.csv` | decks seen in Open Tournament results, for the draft advisor |
| `owned_cards.txt` | the card ids you own, written by a deck search, for `tools/DeckBench` |
| `characters/<content id>/` | per character: the deck cache and the Open Tournament record |

## Commands and settings

| Command | Effect |
|---|---|
| `/thater` | toggle the window |
| `/thater config` | settings |
| `/thater auto [on\|off]` | toggle auto play |

Settings (all apply immediately): window (open at load, backdrop and opacity); move choice (exploit inside the safe moves, model noise, exploit time budget, exploit from N placed cards, delay before placing); anti-AFK; farming (pick the best saved deck, build the best deck first, the one deck slot the plugin may overwrite, "None" by choice); Open Tournament (draft the advised deck); diagnostics (match model recording, the deck cache and clearing it).

## Development

```
TrialHater.Core/     pure game logic, no Dalamud: cards, rules, engine, exact solver, NPC hand belief, move policy,
                     deck search and cache, session and tournament planners
TrialHater/          the plugin: sheet data, board reader, match tracker, analysis worker, actuator, auto play,
                     farming session, deck writer, tournament queue and draft, windows
TrialHater.Tests/    xunit tests for Core
tools/               Arena (policy simulation on recorded matchups), DeckBench (offline deck search),
                     ProbeReplay (replays recorded matches through the engine), release.ps1
research/            the original research bundle; validation/: SolverBench, EngineDiff (differential test against
                     ATTG's engine), ModelFit (NPC move models)
docs/                RESEARCH (decision record), RULES (engine spec), MODEL_MAPPING (game windows and memory)
```

```powershell
dotnet build TrialHater.sln -c Debug
dotnet test TrialHater.Tests
dotnet run --project tools/ProbeReplay -c Release    # after a play session: every recorded placement must replay exactly
```

For a dev build: `/xlsettings` → Experimental → add `TrialHater\bin\Debug\net10.0-windows\TrialHater.dll` as a Dev Plugin Location, enable it under `/xlplugins` → Dev Tools. It reloads on every rebuild, mid-match included. Engine changes must keep `research/validation/EngineDiff` at 0 mismatches.

### Versions and releases

The version lives in `Directory.Build.props` (`<Version>`); the window shows it next to the name and changes go under `## Unreleased` in [CHANGELOG.md](CHANGELOG.md).

| Workflow | Trigger | Does |
|---|---|---|
| CI | push to `main`, pull request, manual | builds the plugin in Release, runs the tests, uploads `latest.zip` |
| Release | tag `v*` | checks the tag against `<Version>`, builds, tests, publishes a GitHub release with `latest.zip`, updates `repo.json` on `main` |
| Testing release | tag `testing-a.b.c.d` | publishes a pre-release and the testing channel of `repo.json`, keeping the stable entry |

Cut a release from a clean `main` that matches `origin/main`:

```powershell
./tools/release.ps1 -Bump patch        # or minor / major, or -Version 1.0.0; -DryRun to preview
```

It runs the tests, dates the Unreleased notes, bumps `<Version>`, commits, tags `vX.Y.Z` and pushes both; CI does the rest. A testing build: `git tag testing-1.1.0.1; git push origin testing-1.1.0.1` (four parts, newer than the stable version).

## Status

| Piece | State |
|---|---|
| Rules engine, solver, belief, policy | Done. Brute-force-checked, differential-tested against ATTG's engine; 611 recorded matches replay with 0 mismatches |
| Board reading, auto play | Working in game under every rule but Random |
| Farming sessions | Working in game, including 100-match runs. New in 0.2.8: rules read from the challenge window (checked at Tsuzura, Momodi and Ruhtwyda); a mid-session deck rebuild not seen in game yet |
| Deck builder, deck writing | Working in game; writes only the slot chosen in Settings |
| Open Tournament draft, matches, queue | Working in game, retargeting the Official between tournaments included; waiting for the sign-up window is new in 0.2.8 |
| Anti-AFK | Working in game |
| Open questions | Tsuzura under Ascension loses more than the NPC model predicts (RESEARCH §13); Random and a combo deciding Fallen Ace not seen in game yet (RULES) |
| Not built | Registering won cards from the bags; travelling between NPCs |
