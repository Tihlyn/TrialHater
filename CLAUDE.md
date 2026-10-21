# TrialHater — working notes for Claude

Private Dalamud plugin (API 15, .NET 10) automating Triple Triad against NPCs. Reference plugin for conventions: `C:\Users\capta\Desktop\MH\MahjongHater`. Reverse-engineering tool: Cartographer (`C:\Users\capta\Desktop\dldm_reverse`), HTTP API on `127.0.0.1:9790` while the game runs (`curl http://127.0.0.1:9790/` lists endpoints).

## Build / test
- `dotnet build TrialHater.sln -c Debug` → `TrialHater/bin/Debug/net10.0-windows/TrialHater.dll` (dev plugin; `/xlreload TrialHater`).
- `dotnet test TrialHater.Tests` (Core only, no game needed; reads `research/data/*.csv`).
- Engine changes: also run `research/validation/EngineDiff` (`dotnet run -c Release -- ../../data/cards.csv 5000`). It must stay at 0 mismatches unless the change deliberately departs from the lineage; record the departure in `docs/RULES.md`.

## Versions
- `<Version>` in `Directory.Build.props`; notes under `## Unreleased` in `CHANGELOG.md` with every user-visible change.
- Release: `tools/release.ps1 -Bump patch|minor|major` from a clean `main` (tags `vX.Y.Z`, CI publishes). Remote: github.com/Tihlyn/TrialHater. Workflows ported from MahjongHater.

## Layout
- `TrialHater.Core` — no Dalamud references, ever. `Engine/` (make/unmake rules engine), `Search/` (exact solver, exploit evaluator, opponent models), `Belief/` (NPC hand hypotheses), `Policy/` (decision), `Match/` (MatchView: the plain observation).
- `TrialHater/Windows` — `Theme` (palette, style scope), `Chrome` (card-shaped window: backdrop, gold frame, header, body, footer), `CardArt` (sprite atlas over `assets/Triple Triad Card Template.png`, embedded), `Widgets` (cards, medallions, chips, toggles, panels).
- `TrialHater/Game` — `GameData` (Lumina sheets), `BoardReader` (addon → BoardFrame copy), `MatchTracker` (frames → MatchView, history), `AnalysisService` (worker), `BoardActuator` (callback 14), `AutoPlayer` (guards), `SessionRunner` + `AddonOps` (farming windows via native ButtonClick chains), `MatchJournal` (matches.csv, npc_moves.jsonl), `ModelProbe` (per-event dumps).
- `TrialHater.Core/Deck/DeckCache` — deterministic deck search results reused across encounters (key: NPC deck data, evaluation rules, search options, the owned cards the search tries); `TrialHater/Game/CharacterData` keeps one per character (`characters/<content id>/`) with the Open Tournament record (`Core/Tournament/DraftRecord`). Bump `DeckCache.Version` when evaluation results change.
- `TrialHater.Core/Session/SessionPlanner` — pure "which window is up → one action" logic for the session.
- `TrialHater.Core/Session/IdleGuard` — anti-AFK Control press while auto play runs unattended (match, session, duty), ported from MahjongHater; native seams in `TrialHater/Game/IdleInput`.
- The dev loader hot-reloads `TrialHater.dll` whenever it is rebuilt: while the user is playing, compile-check the plugin with `-o` into the scratchpad instead.
- `tools/ProbeReplay` — replays every match in `pluginConfigs/TrialHater/model_probe.jsonl` through the engine; run after play sessions (`dotnet run --project tools/ProbeReplay -c Release`).
- `docs/RESEARCH.md` is the decision record; `docs/RULES.md` lists every rule point and its verification status.

## Rules that keep the game alive (from MahjongHater / Cartographer)
- Unsafe pointer code only in `TrialHater/Game`; copy memory into plain records and work on the copies.
- Dispatched is not accepted: an action counts only when its effect is observed (a card lands).
- Never act on a decision whose fingerprint differs from the current view; never send twice for one turn generation.
- Refuse loudly instead of guessing (Status / log line saying why).
- Search runs on workers with cancellation; the framework thread never solves.
