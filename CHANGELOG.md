# Changelog

Notable changes are recorded here using the Keep a Changelog format and semantic versioning.
`tools/release.ps1` dates the Unreleased section when it cuts a version.

## Unreleased

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
