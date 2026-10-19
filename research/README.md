# Triple Triad research bundle

Start with [the report](REPORT.html), or its [Markdown source](REPORT.md). Open [the searchable card catalogue](CARD_CATALOGUE.html) in a browser; it works locally without a server or external scripts.

Research date: 19 October 2026. This is a research deliverable, not an installed or game-tested plugin.

| Resource | Contents |
|---|---|
| [cards.csv](data/cards.csv) / [cards.json](data/cards.json) | 475 cards; IDs, album numbers, rarity, type, four sides, acquisition text |
| [npcs.csv](data/npcs.csv) | 134 listed NPCs; locations, rules, prerequisites, rewards |
| [npc-decks.csv](data/npc-decks.csv) | 144 nonempty sheet rows; fixed cards, variable pool, fee, game rule IDs |
| [example-decks.json](data/example-decks.json) | Five legal illustrative seeds; not benchmarked optimal decks |
| [community-decks-sample.csv](data/community-decks-sample.csv) | 100 public deck samples, with basic legality checks |
| [summary.json](data/summary.json) | Counts, versions, card side-value comparison |
| [source-manifest.json](data/source-manifest.json) | API URLs and SHA-256 snapshot hashes |
| [repository-manifest.json](data/repository-manifest.json) | Inspected repositories, exact commits, commit dates |
| [upstream-tests.trx](validation/upstream-tests.trx) | 18 passing upstream offline tests |
| [rule-probe-output.txt](validation/rule-probe-output.txt) | Reproduced restart limit and scoring counterexample |
| [artifact-checks.json](validation/artifact-checks.json) | Local links, pinned source paths, source hashes, numerical data validation |
| [browser-checks.json](validation/browser-checks.json) | Headless Edge search/filter and responsive-layout checks |
| sources/ | Seven public source checkouts; TriadBuddy's pinned shared submodule is initialized |

Raw API responses are retained under `data/`. Raw responses include fields omitted from the normalized exports, such as card descriptions and artwork URLs. Those remain the property of their respective rights holders. The catalogue uses no copied artwork. Source repository licenses and notices are retained in their checkouts.

The XIVAPI card-stat export is checked against all collected cards. The NPC deck sheet contains special/unmapped rows and is not an independent count of currently playable NPCs. Public deck samples are not the complete deck database. Collection percentages are not drop rates.

Reproduce the exports from cached data (Python standard library only):

```powershell
python research/gather_resources.py
```

Refresh public API snapshots, then regenerate the exports:

```powershell
python research/gather_resources.py --refresh
```

The refresh command overwrites the API snapshot and normalized export files; archive them first if preserving this exact research snapshot. It does not update the Git checkouts. Review report counts and version claims after refreshing.

Render the readable artifacts (the dependency is local to this bundle):

```powershell
python -m pip install --target research/.tools Markdown==3.11.1
python research/build_readable_artifacts.py
```

Reproduce the upstream validation with .NET 10:

```powershell
dotnet test research/sources/FFXIV-AutoTripleTriadGrind/AutoTripleTriadGrind.Tests/AutoTripleTriadGrind.Tests.csproj --configuration Release --logger "trx;LogFileName=upstream-tests.trx" --results-directory research/validation
dotnet run --project research/validation/RuleProbe/RuleProbe.csproj --configuration Release
```

The probe compiles referenced upstream logic without altering it. Its repeated-card fixture is intentionally synthetic. Passing upstream tests establish only their tested behavior; the report documents material gaps they do not cover. No live game compatibility or win-rate benchmark is claimed.
