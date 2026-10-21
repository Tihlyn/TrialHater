# Changelog

Notable changes are recorded here using the Keep a Changelog format and semantic versioning.
`tools/release.ps1` dates the Unreleased section when it cuts a version.

## Unreleased

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
- Releases: CI builds and tests every push to main; a `v*` tag publishes a GitHub release and
  updates `repo.json`; a `testing-*` tag publishes a testing build (`tools/release.ps1`).

### Initial feature set (before versioned releases)

- Exact solver, NPC hand belief and safe-then-exploit move choice; live board reading.
- Auto play, farming sessions, anti-AFK.
- Deck builder, saved deck choice per NPC, deck writing into an allowed slot.
- Open Tournament: draft advisor, automatic draft, queue (sign up, commence, finish), schedule.
