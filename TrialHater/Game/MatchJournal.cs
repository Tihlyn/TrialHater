using System.Text.Json;
using TrialHater.Core.Cards;
using TrialHater.Core.Match;

namespace TrialHater.Game;

// Append-only records in the plugin config folder:
//   matches.csv      one row per finished match (win rate per NPC, per rule set)
//   npc_moves.jsonl  every NPC placement with the position it was made from, for fitting the
//                    opponent model the exploit half uses (docs/RESEARCH.md, "NPC model")
internal sealed class MatchJournal
{
    private const string MatchHeader = "utc,npc_id,npc_name,rules,round,blue_cards,outcome,auto_played";

    private readonly string folder;
    private readonly Action<Exception, string> warn;

    public MatchJournal(string folder, Action<Exception, string> warn)
    {
        this.folder = folder;
        this.warn = warn;
    }

    public void MatchEnded(MatchTracker tracker, NpcDeck? npc, int blueCards, int autoPlayed)
    {
        var outcome = blueCards > 5 ? "win" : blueCards == 5 ? "draw" : "loss";
        var name = (npc?.Name ?? tracker.Opponent ?? string.Empty).Replace(',', ' ');
        this.Append("matches.csv", MatchHeader,
            $"{DateTime.UtcNow:O},{tracker.NpcTriadId},{name},{tracker.Rules.ToString().Replace(", ", "+")},{tracker.Round},{blueCards},{outcome},{autoPlayed}");
    }

    public void RedMoved(MatchView before, CardStats card, int cell)
    {
        var line = JsonSerializer.Serialize(new
        {
            utc = DateTime.UtcNow,
            npc = before.NpcTriadId,
            rules = before.Rules.Mask,
            round = before.Round,
            board = before.Board.Select(c => c is { } b ? $"{(int)b.Owner}{b.Card}" : "."),
            blue = before.Blue.Select(s => s.ToString()),
            red = before.Red.Select(s => s.ToString()),
            redPlayed = before.RedPlayed.Select(c => c.ToString()),
            played = card.ToString(),
            cell,
        });
        this.Append("npc_moves.jsonl", null, line);
    }

    private void Append(string file, string? header, string line)
    {
        try
        {
            Directory.CreateDirectory(this.folder);
            var path = Path.Combine(this.folder, file);
            var lines = new List<string>(2);
            if (header is not null && !File.Exists(path))
                lines.Add(header);
            lines.Add(line);
            File.AppendAllLines(path, lines);
        }
        catch (Exception ex)
        {
            this.warn(ex, $"[Journal] failed to append to {file}");
        }
    }
}
