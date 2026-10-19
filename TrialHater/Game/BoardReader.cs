using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TrialHater.Game;

// Copies AddonTripleTriad into a BoardFrame. Framework thread only; the only place besides the
// actuator and the probe that dereferences the board addon.
internal static unsafe class BoardReader
{
    public const string AddonName = "TripleTriad";

    // The agent drives the board with commands: every Refresh carries a command code in
    // AtkValues[23] and two arguments in [24] and [25], acknowledged by callback [12]
    // (docs/MODEL_MAPPING.md, 2026-10-19 capture). Command 1 = the player may act.
    private const int CommandIndex = 23;
    private const int BlueNameIndex = 1;
    private const int RedNameIndex = 4;
    private const int FirstRuleIndex = 5;
    private const int LastRuleIndex = 9;
    private const int LeadingValueCount = 48;
    private const int MaxTextNodes = 600;

    // `localName`: our character's name. The board names its blue player in [1] and its red player
    // in [4]; against NPCs we are always blue, but a tournament against a player can seat us red
    // (2026-10-19). Then the frame is mirrored so that Blue is always us: hands swapped, owners
    // 1 <-> 2. Our hand keeps its slot numbers (the placement callback used the red slot index).
    public static BoardFrame? Read(AtkUnitBase* unit, long frame, string? localName)
    {
        if (unit == null || unit->RootNode == null)
            return null;
        var board = (AddonTripleTriad*)unit;
        var blue = new SlotRaw[5];
        var red = new SlotRaw[5];
        var cells = new SlotRaw[9];
        for (var i = 0; i < 5; i++)
        {
            blue[i] = Slot(board->BlueDeck[i]);
            red[i] = Slot(board->RedDeck[i]);
        }

        for (var i = 0; i < 9; i++)
            cells[i] = Slot(board->Board[i]);

        var values = new int?[Math.Min((int)unit->AtkValuesCount, LeadingValueCount)];
        var strings = new string?[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            ref var v = ref unit->AtkValues[i];
            values[i] = v.Type is AtkValueType.Int or AtkValueType.UInt or AtkValueType.Bool ? v.Int : null;
            strings[i] = v.Type is AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString ? SafeString(ref v) : null;
        }

        // Rule names first, straight from the values the board was set up with ([5..9]; the
        // 2026-10-19 capture had them in [8] and [9]), then any text on screen as a fallback.
        var texts = new List<string>();
        for (var i = FirstRuleIndex; i <= LastRuleIndex && i < strings.Length; i++)
            if (strings[i] is { Length: > 0 } rule)
                texts.Add(rule);
        texts.AddRange(VisibleTexts(unit));

        int? Value(int i) => i < values.Length ? values[i] : null;
        string? Name(int i) => i < strings.Length ? strings[i] : null;
        var seatedRed = localName is { Length: > 0 }
            && string.Equals(Name(RedNameIndex), localName, StringComparison.Ordinal)
            && !string.Equals(Name(BlueNameIndex), localName, StringComparison.Ordinal);
        if (seatedRed)
        {
            (blue, red) = (Mirror(red), Mirror(blue));
            cells = Mirror(cells);
        }

        return new BoardFrame(frame, unit->IsVisible, unit->IsReady, (byte)board->TurnState,
            Value(CommandIndex), Value(CommandIndex + 1), Value(CommandIndex + 2),
            blue, red, cells, texts, values)
        {
            OpponentName = Name(seatedRed ? BlueNameIndex : RedNameIndex),
            SeatedRed = seatedRed,
        };
    }

    private static SlotRaw[] Mirror(SlotRaw[] slots)
        => slots.Select(s => s with { Owner = s.Owner switch { 1 => 2, 2 => 1, var o => o } }).ToArray();

    private static string SafeString(ref AtkValue v)
    {
        try
        {
            return v.String.ExtractText();
        }
        catch
        {
            return string.Empty;
        }
    }

    // ClientStructs' NumSideR holds the card's LEFT value and NumSideL its RIGHT: every NPC card in
    // the 2026-10-19 capture matched its sheet row only with the two swapped (ATTG swaps them too).
    private static SlotRaw Slot(AddonTripleTriad.TripleTriadCard card)
        => new(card.HasCard, card.NumSideU, card.NumSideL, card.NumSideD, card.NumSideR,
            (byte)card.CardType, card.CardRarity, (byte)card.CardOwner, MinImageTint(card.CardDropControl));

    // Lowest MultiplyRed over the visible images of a card component (255 when none).
    private static byte MinImageTint(AtkComponentBase* component)
    {
        if (component == null)
            return 255;
        byte min = 255;
        var manager = component->UldManager;
        for (var i = 0; i < manager.NodeListCount && manager.NodeList != null; i++)
        {
            var node = manager.NodeList[i];
            if (node == null || node->Type != NodeType.Image || !node->IsVisible())
                continue;
            min = Math.Min(min, node->MultiplyRed);
        }

        return min;
    }

    // Every visible text in the addon, components included (rule names, banners).
    private static List<string> VisibleTexts(AtkUnitBase* unit)
    {
        var texts = new List<string>();
        var budget = MaxTextNodes;
        Collect(&unit->UldManager, texts, ref budget, 0);
        return texts;
    }

    private static void Collect(AtkUldManager* manager, List<string> texts, ref int budget, int depth)
    {
        if (manager == null || manager->NodeList == null || depth > 6)
            return;
        for (var i = 0; i < manager->NodeListCount && budget > 0; i++, budget--)
        {
            var node = manager->NodeList[i];
            if (node == null || !node->IsVisible())
                continue;
            if (node->Type == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ExtractText().Trim();
                if (text.Length > 0)
                    texts.Add(text);
            }
            else if ((ushort)node->Type >= 1000)
            {
                var component = ((AtkComponentNode*)node)->Component;
                if (component != null)
                    Collect(&component->UldManager, texts, ref budget, depth + 1);
            }
        }
    }
}
