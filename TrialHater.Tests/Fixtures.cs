using TrialHater.Core.Cards;
using TrialHater.Core.Engine;
using TrialHater.Core.Rules;

namespace TrialHater.Tests;

internal static class Fixtures
{
    // "5A35" = up 5, right A, down 3, left 5; an optional "/Primal" style suffix sets the type.
    public static CardStats C(string text)
    {
        var parts = text.Split('/');
        var sides = parts[0].Select(ch => ch is 'A' or 'a' ? (byte)10 : (byte)(ch - '0')).ToArray();
        var type = parts.Length > 1 ? Enum.Parse<CardType>(parts[1]) : CardType.None;
        return new CardStats(sides[0], sides[1], sides[2], sides[3], type);
    }

    public static BoardCard B(string text) => new(C(text), Side.Blue);

    public static BoardCard R(string text) => new(C(text), Side.Red);

    // A board in reading order (null = empty); missing trailing cells are empty.
    public static BoardCard?[] Board(params BoardCard?[] cells)
    {
        var board = new BoardCard?[9];
        Array.Copy(cells, board, Math.Min(cells.Length, 9));
        return board;
    }

    public static BoardCard?[] EmptyBoard() => new BoardCard?[9];

    public static CardStats?[] Hand(params string?[] cards)
    {
        var hand = new CardStats?[5];
        for (var i = 0; i < cards.Length; i++)
            hand[i] = cards[i] is { } c ? C(c) : null;
        return hand;
    }

    public static Position Pos(RuleSet rules, BoardCard?[] board, CardStats?[] blue, CardStats?[] red, Side toMove = Side.Blue)
        => new(rules, board, blue, red, toMove);

    // Owner of every cell after playing `slot` into `cell` from `position`; '.' = empty.
    public static string OwnersAfter(Position position, int slot, int cell)
    {
        var engine = new Engine(position);
        engine.Play(slot, cell);
        return Owners(engine);
    }

    public static string Owners(Engine engine)
    {
        var chars = new char[9];
        for (var c = 0; c < 9; c++)
            chars[c] = engine.CellInstance(c) == Engine.Empty ? '.' : engine.IsBlue(c) ? 'B' : 'R';
        return new string(chars);
    }

    public static string DataPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "research", "data")))
            dir = dir.Parent;
        return dir is null
            ? throw new FileNotFoundException("research/data not found above the test output folder")
            : Path.Combine(dir.FullName, "research", "data", file);
    }

    private static CardCatalog? catalog;
    private static NpcCatalog? npcs;

    public static CardCatalog Catalog => catalog ??= CardCatalog.FromCsv(DataPath("cards.csv"));

    public static NpcCatalog Npcs => npcs ??= NpcCatalog.FromCsv(DataPath("npc-decks.csv"));
}
