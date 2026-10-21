using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using TrialHater.Core.Cards;

namespace TrialHater.Windows;

// The Triple Triad card template (assets/Triple Triad Card Template.png, embedded): a sprite
// atlas of the gold card frame, the card back, the red and blue medallions and the type icons.
// The texture loads asynchronously; until it is in, callers draw a plain fallback.
internal static class CardArt
{
    public const string ResourceName = "TrialHater.CardTemplate.png";

    private static readonly Vector2 AtlasSize = new(380f, 492f);
    private static ITextureProvider? provider;

    public static readonly Sprite RedMedallion = new(156, 4, 59, 59);
    public static readonly Sprite BlueMedallion = new(156, 72, 59, 59);
    public static readonly Sprite Star = new(255, 295, 12, 13);
    public static readonly Sprite CardBack = new(14, 365, 100, 126);
    public static readonly Sprite CardFrame = new(118, 365, 100, 126);

    // Type icons, top right of a card. The atlas has them in the order star, blue, red, gold, green.
    public static readonly Sprite ScionIcon = new(270, 294, 20, 20);
    public static readonly Sprite PrimalIcon = new(292, 295, 20, 17);
    public static readonly Sprite GarleanIcon = new(315, 294, 18, 20);
    public static readonly Sprite BeastmanIcon = new(336, 295, 19, 18);

    public static void Initialize(ITextureProvider textureProvider) => provider = textureProvider;

    public static Sprite? TypeIcon(CardType type) => type switch
    {
        CardType.Primal => PrimalIcon,
        CardType.Scion => ScionIcon,
        CardType.Beastman => BeastmanIcon,
        CardType.Garlean => GarleanIcon,
        _ => null,
    };

    public static bool TryGet(out ImTextureID handle)
    {
        handle = default;
        if (provider is null)
            return false;
        if (!provider.GetFromManifestResource(typeof(CardArt).Assembly, ResourceName).TryGetWrap(out var wrap, out _) || wrap.Width == 0)
            return false;
        handle = wrap.Handle;
        return true;
    }

    public static bool Draw(ImDrawListPtr draw, Sprite sprite, Vector2 min, Vector2 max, float alpha = 1f)
        => DrawPart(draw, sprite, Vector2.Zero, sprite.Size, min, max, alpha);

    // A sub-rectangle of a sprite (offset and size in source pixels) stretched over min..max.
    public static bool DrawPart(ImDrawListPtr draw, Sprite sprite, Vector2 offset, Vector2 size, Vector2 min, Vector2 max, float alpha = 1f)
    {
        if (!TryGet(out var texture))
            return false;
        var origin = sprite.Origin + offset;
        draw.AddImage(texture, min, max, origin / AtlasSize, (origin + size) / AtlasSize, Theme.Color(new Vector4(1f, 1f, 1f, alpha)));
        return true;
    }

    // The gold card frame stretched around a window. Only straight runs of the frame are
    // stretched: the waist of the sides (rows 54..72), the top run left of the notch and the
    // bottom run right of the flourish; the ornaments keep their shape at `scale`.
    public static void WindowFrame(ImDrawListPtr draw, Vector2 min, Vector2 max, float scale)
    {
        if (!TryGet(out _))
        {
            draw.AddRect(min + new Vector2(2f), max - new Vector2(2f), Theme.Color(Theme.Gold), Theme.Px(8f), ImDrawFlags.None, Theme.Px(4f));
            return;
        }

        const float top = 54f, bottom = 54f, height = 126f;
        var s = scale;
        var midTop = min.Y + top * s;
        var midBottom = max.Y - bottom * s;

        // Top band: corner | run | notch and flourish.
        Slice(draw, 0, 0, 12, top, min.X, min.Y, min.X + 12 * s, midTop);
        Slice(draw, 12, 0, 19, top, min.X + 12 * s, min.Y, max.X - 69 * s, midTop);
        Slice(draw, 31, 0, 69, top, max.X - 69 * s, min.Y, max.X, midTop);

        // Sides.
        if (midBottom > midTop)
        {
            Slice(draw, 0, top, 12, height - top - bottom, min.X, midTop, min.X + 12 * s, midBottom);
            Slice(draw, 88, top, 12, height - top - bottom, max.X - 12 * s, midTop, max.X, midBottom);
        }

        // Bottom band: flourish and notch | run | corner.
        var y = height - bottom;
        Slice(draw, 0, y, 72, bottom, min.X, midBottom, min.X + 72 * s, max.Y);
        Slice(draw, 72, y, 16, bottom, min.X + 72 * s, midBottom, max.X - 12 * s, max.Y);
        Slice(draw, 88, y, 12, bottom, max.X - 12 * s, midBottom, max.X, max.Y);
    }

    private static void Slice(ImDrawListPtr draw, float sx, float sy, float sw, float sh, float x0, float y0, float x1, float y1)
    {
        if (x1 > x0 && y1 > y0)
            DrawPart(draw, CardFrame, new Vector2(sx, sy), new Vector2(sw, sh), new Vector2(x0, y0), new Vector2(x1, y1));
    }

    public readonly record struct Sprite(float X, float Y, float W, float H)
    {
        public Vector2 Origin => new(this.X, this.Y);

        public Vector2 Size => new(this.W, this.H);

        public float Aspect => this.W / this.H;
    }
}
