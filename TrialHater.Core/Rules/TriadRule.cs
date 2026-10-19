namespace TrialHater.Core.Rules;

// TripleTriadRule sheet row ids, used directly so sheet data needs no translation table.
public enum TriadRule : byte
{
    None = 0,
    Roulette = 1,
    AllOpen = 2,
    ThreeOpen = 3,
    Same = 4,
    SuddenDeath = 5,
    Plus = 6,
    Random = 7,
    Order = 8,
    Chaos = 9,
    Reverse = 10,
    FallenAce = 11,
    Ascension = 12,
    Descension = 13,
    Swap = 14,
    Draft = 15,
}

// The rules in force for one match, one bit per sheet id.
public readonly record struct RuleSet(ushort Mask)
{
    public static readonly RuleSet Empty = new(0);

    public bool Has(TriadRule rule) => (this.Mask & (1 << (int)rule)) != 0;

    public RuleSet With(TriadRule rule) => rule == TriadRule.None ? this : new((ushort)(this.Mask | (1 << (int)rule)));

    public RuleSet Without(TriadRule rule) => new((ushort)(this.Mask & ~(1 << (int)rule)));

    public static RuleSet Of(params TriadRule[] rules) => Of((IEnumerable<TriadRule>)rules);

    public static RuleSet Of(IEnumerable<TriadRule> rules)
    {
        var set = Empty;
        foreach (var rule in rules)
            set = set.With(rule);
        return set;
    }

    public IEnumerable<TriadRule> Rules
    {
        get
        {
            for (var id = 1; id <= (int)TriadRule.Draft; id++)
                if ((this.Mask & (1 << id)) != 0)
                    yield return (TriadRule)id;
        }
    }

    // Unresolved Roulette means the real rules are not known yet; nothing should be solved on it.
    public bool HasUnresolvedRoulette => this.Has(TriadRule.Roulette);

    // Ascension and Descension both shift typed cards; the game never combines them, but if the
    // data ever did, Ascension's sign wins rather than cancelling silently.
    public int TypeStep => this.Has(TriadRule.Ascension) ? 1 : this.Has(TriadRule.Descension) ? -1 : 0;

    public override string ToString() => this.Mask == 0 ? "(none)" : string.Join(", ", this.Rules);
}
