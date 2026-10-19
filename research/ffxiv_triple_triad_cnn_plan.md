# FFXIV Triple Triad — CNN Policy Model and Training Dataset Plan

## Goal

Build a model that predicts the **best legal move** from an observed FFXIV Triple Triad game state, for both:

- **NPC matches**, where the opponent may draw from a known or inferable card pool and follow characteristic behavior.
- **PvP matches**, where the opponent may use an unfamiliar deck and adopt a variety of strategies.

The recommended approach is a **shared policy CNN** trained on **synthetic, solver-labeled game positions**, with explicit treatment of hidden information. Optionally add a value head to estimate likely game outcomes.

> **Important:** A board image or 3×3 arrangement alone is not enough. Move quality also depends on the player's remaining hand, the known or visible opponent cards, active rules, the player to move, and any rule-specific state.

## 1. Why generate a corpus instead of relying on human replays?

A publicly downloadable corpus of complete, move-by-move FFXIV Triple Triad matches has not been verified. Existing projects are more useful as **rules engines, card data sources, and solvers** than as archives of recorded human play.

Even if human replays become available, imitation learning from them reproduces both good decisions and mistakes. A search-based teacher can label positions with strong or near-optimal moves. Synthetic generation also allows deliberate coverage of less common rules and board situations.

**Suggested pipeline:**

```text
FFXIV rules engine + cards/decks
               |
               v
Legal game-state generator
               |
               v
Expert search / simulation
(Negamax, alpha-beta, MCTS, sampled hidden hands)
               |
               v
Labeled corpus
(observations, legal actions, action scores)
               |
               v
Shared CNN policy model
         + optional value head
               |
               v
Evaluation and stronger self-play data
```

## 2. NPC vs. PvP: what changes?

| Aspect | NPC matches | PvP matches |
| --- | --- | --- |
| Opponent cards | May come from NPC-specific card pools | Typically unfamiliar until revealed, unless an open-information rule applies |
| Opponent decisions | May follow NPC-specific behavior | Can vary widely between human players |
| Training emphasis | Accurate NPC card pools, rules, and behavior | Diverse legal decks, strategies, and uncertainty |
| Core risk | Overfitting to one NPC | Training on information a real player cannot see |

Use **one shared network** with information masks and, when available, optional NPC-specific context. Keep evaluations separate for NPCs and PvP to confirm that improvements transfer.

## 3. Training data: three complementary stages

### Stage A — Full-information games

- Start with **All Open**, where both hands are visible.
- Use Negamax/alpha-beta search to label legal actions; solve exactly in tractable states.
- Generate varied board positions, decks, and supported rules.
- Goal: teach tactical capture, defense, positioning, and rule interactions before adding uncertainty.

### Stage B — Hidden-information PvP

- Sample **legal PvP decks**, preserving card and deck-building restrictions.
- Reveal only information available under the active match rules; model previously played cards and publicly observed information.
- Include multiple opponent policies: random, greedy, heuristic, search-guided, and self-play.
- Sample possible hidden hands consistent with the observation and score each candidate move across those possibilities.
- Goal: teach strong, robust play against unfamiliar opponents.

**Avoid information leakage:** Never include the actual hidden opponent cards in the CNN input if the player would not know them in the real match. The simulator may use those cards to resolve transitions, but the model's observation must not expose them.

### Stage C — NPC-specific games

- Generate games using **actual NPC card pools** and predefined match rules where available.
- Approximate or measure NPC-specific decision patterns.
- Provide optional metadata such as an NPC identifier or card-pool prior, but only if available at inference time.
- Goal: benefit from known opponents without weakening general PvP performance.

## 4. Inputs and outputs

### Observation (model input)

Include, as permitted by the active rules:

- **Board:** 3×3 grid with occupancy, ownership, and each placed card's four directional values.
- **Player hand:** up to five playable/remaining cards, with directional values, type, and any other relevant attributes.
- **Opponent information:** publicly visible cards, revealed played cards, unknown-card indicators, and optional beliefs about remaining cards.
- **Game state:** current player, turn number, rule flags, and any temporary state required by rules such as Ascension.
- **Opponent context:** optional NPC identity/card-pool features when known.

A practical starting representation is a **3×3×C** board tensor plus a separately encoded hand and rule vector. The model should distinguish *unknown* from *empty* or *absent* values.

### Policy output

At the start of a standard match, the action space can be represented as:

- **5 card slots × 9 cells = 45 policy logits**.
- Use a **legal-action mask** to disallow occupied cells, unavailable cards, and rule-restricted actions (e.g., forced card choice under Order or Chaos).
- Take the highest-probability legal action, or sample during self-play.

Represent a move as `(hand_slot, board_cell)` rather than simply predicting a cell.

### Optional value output

Add a **value head** estimating the expected outcome from the active player's perspective. This can support search-guided play or later self-play training even if the production agent uses only policy inference.

## 5. Produce good labels, not just one 'correct' move

Instead of saving only one solver-selected move, save a **score for every legal action**. Multiple actions may be equally strong; a model should not be penalized for choosing an equivalent winning line.

Illustrative sample structure:

```json
{
  "game_id": "synthetic_000123",
  "turn": 4,
  "mode": "pvp_hidden",
  "rules": ["Same", "Plus"],
  "observation": {
    "board": "encoded 3x3 state",
    "player_hand": "encoded remaining cards",
    "opponent_visible_cards": "visible cards only",
    "opponent_unknown_count": 2
  },
  "legal_action_mask": "5x9 boolean mask",
  "teacher_action_scores": "5x9 scores; invalid entries masked",
  "best_actions": [[1, 2]],
  "game_outcome": 1
}
```

This is an **illustrative schema**, not an existing dataset format.

Possible training targets:

1. A probability distribution that places equal mass on all equally optimal moves.
2. A softened distribution derived from the expert's scores, giving credit to near-optimal moves.
3. Search visit counts, if using MCTS.

With full information, the expert can approximate or compute a minimax-optimal choice. With hidden information, seek the action with the best expected outcome under plausible hidden hands:

\[
a^* = \arg\max_{a \in A(o)} \mathbb{E}_{h \sim P(h \mid o)}[Q(o,h,a)]
\]

where:

- \(o\) is everything the current player can observe;
- \(h\) is a possible hidden opponent hand or hidden state;
- \(A(o)\) is the set of legal actions;
- \(Q(o,h,a)\) is the estimated quality of action \(a\) under that hidden state.

**Caveat:** Naively solving each sampled hidden hand as though the player knew it can create *strategy fusion* and overly optimistic labels. For a stronger teacher, ensure the same move must be selected for the same information set, and consider information-set MCTS or rollout-based action evaluation as the project matures.

## 6. Suggested neural network

```text
3×3 board ──> Small residual CNN ────────┐
                                         ├──> Feature fusion
Hand cards ──> Shared card encoder ──────┤          |
                                         │          +--> Policy head: 5×9 logits
Rules / observation masks ──> MLP ──────┤          |
                                         │          +--> Value head: win/draw/loss
Opponent context (optional) ──> MLP ─────┘
```

Design notes:

- Share card-encoder weights across hand slots to avoid unnecessary dependence on card ordering.
- Preserve the link between each hand slot and its output policy logits.
- Mask illegal actions **before** softmax or action selection.
- A 3×3 board is tiny; benchmark a **simple MLP** against the CNN. Convolutions are not automatically superior for such a small spatial input.
- Later, try search guided by the learned policy/value outputs to produce stronger data.

## 7. Candidate repositories and sources

These repositories are **implementation references**, not verified public replay corpora:

| Repository | Why look at it | Caution |
| --- | --- | --- |
| [FFTriadBuddy](https://github.com/MgAl2O4/FFTriadBuddy) | FFXIV-specific rules, card and NPC data, simulations, and recommendations | Validate support and behavior for every target ruleset |
| [FFTriadBuddy Dalamud](https://github.com/MgAl2O4/FFTriadBuddyDalamud) | Reference for NPC-oriented integrations | Its documented solver approach is NPC-focused, not a complete PvP solution |
| [Yuto3S/tripleTriad](https://github.com/Yuto3S/tripleTriad) | Python simulation and Negamax-style AI | Check actual coverage of FFXIV-specific special rules |
| [TripleTriadSimulator](https://github.com/slaymaker1907/triple-triad-ai) | FFXIV-themed simulation/AI starting point | May require work to export states and labels |
| [TripleTriadSolver](https://github.com/Ununoctium117/TripleTriadSolver) | Rust Negamax with alpha-beta search | Reported limitations include several special rules; verify in code |
| [FFTriadBuddy cards.xml](https://github.com/MgAl2O4/FFTriadBuddy/blob/master/assets/data/cards.xml) | Card IDs, directional values, types, and rarity | Check completeness and currency against current FFXIV content |

Before reusing any code or distributing derived files, check each project's **license** and verify its rule implementation against the current game.

## 8. Recommended first experiment

1. **Implement or adapt a validated game simulator** with tests for card flips, Same, Plus, Reverse, Ascension, and every rule you plan to support.
2. **Begin with All Open** to make strong search-generated labels easier and to debug the observation/action encoding.
3. **Generate 10,000–50,000 games**, saving every decision state and each legal move's teacher score.
4. Train an **MLP baseline** and a **small residual CNN** using masked policy loss.
5. Evaluate on **held-out games, decks, rules, and opponent policies**, not on randomly split positions from the same games.
6. Introduce **hidden-information PvP** using observation-consistent opponent-card sampling and decision-making.
7. Add **NPC-specific distributions** and evaluate NPC and PvP performance separately.
8. Iteratively improve the teacher and dataset using **self-play**, search, and failure-case analysis.

### Metrics to track

- **Top-action agreement:** How often does the model choose one of the teacher's best moves?
- **Action regret:** How much teacher-estimated value is lost by the model's chosen move?
- **Win/draw/loss rate:** Against random, greedy, heuristic, search-based, and diverse self-play agents.
- **Generalization:** On unseen decks, NPCs, PvP opponents, and rule combinations.
- **Inference time:** Whether the model can make a move quickly enough for the intended interface.

## 9. Key risks and safeguards

- **Hidden-information leakage:** Keep the simulator's omniscient state distinct from the observation given to the model.
- **Incomplete rules:** The teacher and environment must reproduce FFXIV-specific mechanics accurately; otherwise the model learns incorrect move values.
- **Teacher bias:** A shallow solver or narrow set of opponent policies can produce a large but misleading corpus.
- **Distribution shift:** Mix deck strengths, game stages, rules, and opponent types, and test on unseen configurations.
- **Too much CNN complexity:** Compare against simple models before scaling up.
- **Non-unique best moves:** Retain per-action scores or equivalent optimal-action sets rather than forcing a single arbitrary label.

## Bottom line

For a policy model that plays against **both FFXIV NPCs and human players**, the strongest starting point is a **unified, observation-aware neural network trained on a synthetic corpus labeled by search**. Begin with full-information games to validate the engine and model, then add hidden-information PvP and NPC-specific training distributions. A good simulator, legal-action masking, and trustworthy labels will likely matter more initially than CNN size.
