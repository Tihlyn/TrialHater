# Changelog

Notable changes are recorded here using the Keep a Changelog format and semantic versioning.
`tools/release.ps1` dates the Unreleased section when it cuts a version.

## Unreleased

## [0.2.9] - 2026-10-23

### Added

- Report an issue: the bug button next to Settings (or `/thater report`) sends a report to the
  TrialHater reports repo, where it becomes an issue. Pick what went wrong (plugin stalled,
  NoBelief: no move played, solving stalled, other) and say what happened. The report carries
  what the plugin is doing, the match on screen and the solver's decision, the plugin's lines from
  dalamud.log, recent matches and the settings. Reports are accepted from testers' characters
  only; a copy of each stays in `pluginConfigs/TrialHater/reports`.

## [0.2.8] - 2026-10-22

### Changed

- Farming picks its deck for the rules actually in force. Before every match, rematches included,
  the challenge window lists the rules (the NPC's own and the region's); the plugin reads them
  there instead of guessing from recent matches, which went wrong when a regional rule changed
  (Tsuzura, 2026-10-22: a Reverse deck played under Ascension and Sudden Death). When the deck was
  chosen for other rules, the saved decks are ranked again; when the session builds decks, it
  declines that challenge (a rematch only offers saved decks), builds and writes the deck for the
  new rules, and challenges again. At most twice per session; after that it ranks the saved decks.
  The deck builder also uses the rules the NPC's challenge window showed last.

### Fixed

- Open Tournament queue: outside the sign-up window (the first 30 minutes after a tournament
  starts, every two hours) the queue no longer talks to the Official over and over. It waits, says
  when sign-up opens, and signs up for the next tournament then.

## [0.2.7] - 2026-10-22

### Added

- Farming: "Farm until every card drops" farms the targeted NPC until every card it can drop is
  collected, then quits. A won card counts as soon as it is in the bags, before it is registered.
  The panel lists the targeted NPC's reward cards and which are still missing. The session will not
  start with full bags, and stops after the current match if they fill up.

### Fixed

- Open Tournament queue: after a tournament the queue no longer stalls waiting for the Official to
  be targeted. Leaving the duty clears the target; the Official is now targeted again when it is in
  range, and the next sign-up goes on.

## [0.2.6] - 2026-10-22

### Changed

- Order: the NPC's next card is no longer treated as a random draw. NPCs play their hand left to
  right, so with Three Open the visible cards' turns are known and only the hidden ones are a draw.
  In simulation (a recorded Three Open + Order matchup, 300 deals) wins against a perfect opponent
  went from 81% to 92%, never worse, and the slowest decision from 10 s to 0.5 s. The sure-win check
  now applies to Order too.
- Openings already solved are remembered (`solve_cache.json` in the plugin's config folder, shared
  by all characters) and reused when the same position comes back, which farming does constantly:
  the move is the same, only the wait goes. In simulation (40 Hall Overseer Chaos matches in a row)
  the slowest decision of a match averaged 14 s before and 5 s after, most openings under 1 s once
  warm; the NPC opening first gives more positions to fill.
- When no move wins whatever the NPC's hidden cards are, auto play now keeps the moves that do not
  lose against any of them, if there are some. In simulation (16 recorded matchups, 300 deals each)
  losses against a perfect opponent went from 15.4% to 13.9% (75 deals better, 3 worse); against a
  realistic NPC there was no measurable difference. It adds about 1.5 s per match.
- Chaos: auto play now plays for the NPC's likely mistakes when that costs little. Among the moves
  within 0.1 of the best expected result it picks the one that does best against the NPC model,
  where it used to take the best expected result alone. In simulation (11 recorded Chaos
  matchups, 60 deals each) losses against the fitted NPC model went from 20% to 15% and wins from
  47% to 54%; against NPCs that play differently from the model it still gained 0 to 4 points and
  was never worse; against a perfect opponent the change was within noise. The decision reason
  says "within 0.1 of the best expected value" when this applies. Other rules are unchanged.

### Fixed

- Chaos move values were slightly off (up to 0.002 per move), and differed depending on what the
  solver had searched before: rounding at the edges of the chance-node pruning let a bound pass
  for an exact value. Values are now exact. Deck ratings under Chaos are recomputed once.

## [0.2.5] - 2026-10-21

### Changed

- A finished farming session keeps its won/drawn/lost count on screen (status bar and Farming tab)
  until the next session or match starts.

### Fixed

- Auto play no longer counts a move as a sure win when it only wins if the NPC's hidden cards
  are known. When some moves win whatever the hidden cards are, it plays one of those (a Prideful
  Stag match was lost this way; on recorded matches about 1 hidden-card move in 15 changes).
- Deck writing no longer stalls when the Gold Saucer window opens on another tab: it switches to
  Card Decks itself.

## [0.2.0] - 2026-10-21

### Added

- The windows are drawn as a Triple Triad card: the game's gold card frame around a translucent
  dark red (Crimson) or dark blue (Midnight) backdrop, gold titles, aqua text, green for the move
  to play. Backdrop and its opacity are in Settings.
- The main window shows the plugin version next to its name.
- Settings: "Open the main window when the plugin loads" (on by default). The window no longer
  reopens just because it was open when the game closed.
- The match is drawn as the game lays it out: our hand, the board and the NPC's hand as cards
  (sides, type icon, card backs for hidden cards), the scores on the red and blue medallions, and
  the move to play glowing green on its hand card and board cell.
- Always in view at the top: auto play, whatever is running (farming, tournament queue, deck
  writing, deck search) with its stop button, and the opponent with the match's rules.
- Farming, deck builder, Open Tournament (queue and schedule) and the match log are tabs.
- Deck cache, per character: a deck search against an NPC (session deck building, the deck
  builder) is reused until a newly obtained card would be among the cards it tries, and each deck's
  score (saved decks included) is kept per NPC and rules. A search under exact rules takes 10-15 s;
  a reused one skips it. Kept in `characters/<content id>/deck_cache.json`, so another character on
  the account with other cards never reuses it; dropped on a game patch that changes cards or NPC
  decks. Settings show what it holds and can clear it.
- Open Tournament record, per character: our drafted deck, its expected result in the advice and
  where we placed, from the results window. Top half of the 8 = vetted, otherwise discarded. The
  Tournament tab lists it, the draft advisor shows each option's record, and among decks the
  simulation cannot tell apart (within 0.03) the advice takes the one whose cards did better.
- A spinner next to everything still running (solving, deck search, session deck building, draft
  ranking), with the time spent solving; it turns orange past the exploit budget. A solver
  exception is shown in place of "No decision yet."
- Releases: CI builds and tests every push to main; a `v*` tag publishes a GitHub release and
  updates `repo.json`; a `testing-*` tag publishes a testing build (`tools/release.ps1`).

### Initial feature set (before versioned releases)

- Exact solver, NPC hand belief and safe-then-exploit move choice; live board reading.
- Auto play, farming sessions, anti-AFK.
- Deck builder, saved deck choice per NPC, deck writing into an allowed slot.
- Open Tournament: draft advisor, automatic draft, queue (sign up, commence, finish), schedule.
