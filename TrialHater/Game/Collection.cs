using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using TrialHater.Core.Cards;

namespace TrialHater.Game;

// One of the player's saved decks (Gold Saucer deck slots), as the deck selection window lists them.
public sealed record SavedDeck(int Index, string Name, IReadOnlyList<CardInfo> Cards);

// The player's card collection and saved decks, copied out of game memory. Framework thread.
internal static unsafe class Collection
{
    public static List<CardInfo> Owned(CardCatalog cards)
    {
        var ui = UIState.Instance();
        if (ui == null)
            return [];
        return cards.All.Where(c => ui->IsTripleTriadCardUnlocked(c.Id)).OrderBy(c => c.Id).ToList();
    }

    // Complete decks only (five known cards), in slot order.
    public static List<SavedDeck> SavedDecks(CardCatalog cards)
    {
        var decks = new List<SavedDeck>();
        var module = GoldSaucerModule.Instance();
        if (module == null)
            return decks;
        for (var i = 0; i < 10; i++)
        {
            var deck = module->GetDeck(i);
            if (deck == null)
                continue;
            var list = new List<CardInfo>(5);
            foreach (var id in deck->Cards)
                if (cards.Get(id) is { } card)
                    list.Add(card);
            if (list.Count == 5)
                decks.Add(new SavedDeck(i, deck->NameString is { Length: > 0 } name ? name : $"Deck {i + 1}", list));
        }

        return decks;
    }
}
