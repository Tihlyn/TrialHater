using System.Numerics;
using Dalamud.Bindings.ImGui;
using TrialHater.Core.Cards;
using TrialHater.Core.Engine;

namespace TrialHater.Windows;

internal static class Widgets
{
    // Panels set their own wrap position; text outside one wraps at the window edge.
    private static int panelDepth;

    public static float ContentWidth => Math.Max(1f, ImGui.GetContentRegionAvail().X);

    public static PanelScope Panel(bool highlight = false) => new(highlight);

    // Text at a scale of the current font, drawn directly (no layout wrap), then laid out.
    public static void DisplayText(string text, float scale, Vector4 color)
    {
        var size = ImGui.CalcTextSize(text) * scale;
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * scale, ImGui.GetCursorScreenPos(), Theme.Color(color), text);
        ImGui.Dummy(size);
    }

    public static void OutlinedText(ImDrawListPtr draw, Vector2 pos, string text, Vector4 color, float scale = 1f)
    {
        var font = ImGui.GetFont();
        var size = ImGui.GetFontSize() * scale;
        var shadow = Theme.Color(Theme.Shadow);
        var o = Math.Max(1f, Theme.Px(1f));
        draw.AddText(font, size, pos + new Vector2(-o, 0f), shadow, text);
        draw.AddText(font, size, pos + new Vector2(o, 0f), shadow, text);
        draw.AddText(font, size, pos + new Vector2(0f, -o), shadow, text);
        draw.AddText(font, size, pos + new Vector2(0f, o), shadow, text);
        draw.AddText(font, size, pos, Theme.Color(color), text);
    }

    public static void CenteredText(ImDrawListPtr draw, Vector2 center, string text, Vector4 color, float scale)
        => OutlinedText(draw, center - ImGui.CalcTextSize(text) * scale / 2f, text, color, scale);

    public static void Text(string text, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, color);
        if (panelDepth == 0)
            ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        if (panelDepth == 0)
            ImGui.PopTextWrapPos();
        ImGui.PopStyleColor();
    }

    public static void Muted(string text) => Text(text, Theme.Muted);

    // Gold section title with a fading rule to the right.
    public static void Section(string title)
    {
        ImGui.Dummy(new Vector2(0f, Theme.Px(2f)));
        var pos = ImGui.GetCursorScreenPos();
        var size = ImGui.CalcTextSize(title);
        var draw = ImGui.GetWindowDrawList();
        OutlinedText(draw, pos, title, Theme.Gold);
        var y = pos.Y + size.Y / 2f;
        var x0 = pos.X + size.X + Theme.Px(Theme.Gap);
        var x1 = pos.X + ContentWidth;
        if (x1 > x0)
            draw.AddRectFilledMultiColor(new Vector2(x0, y), new Vector2(x1, y + Math.Max(1f, Theme.Px(1f))),
                Theme.Color(Theme.GoldDim), 0, 0, Theme.Color(Theme.GoldDim));
        ImGui.Dummy(size);
    }

    // A thin gold rule, brightest in the middle.
    public static void Ornament()
    {
        var pos = ImGui.GetCursorScreenPos();
        var width = ContentWidth;
        var draw = ImGui.GetWindowDrawList();
        var h = Math.Max(1f, Theme.Px(1f));
        var mid = pos.X + width / 2f;
        draw.AddRectFilledMultiColor(pos, new Vector2(mid, pos.Y + h), 0, Theme.Color(Theme.Gold), Theme.Color(Theme.Gold), 0);
        draw.AddRectFilledMultiColor(new Vector2(mid, pos.Y), new Vector2(pos.X + width, pos.Y + h), Theme.Color(Theme.Gold), 0, 0, Theme.Color(Theme.Gold));
        draw.AddCircleFilled(new Vector2(mid, pos.Y + h / 2f), Theme.Px(2.5f), Theme.Color(Theme.GoldBright));
        ImGui.Dummy(new Vector2(width, h + Theme.Px(2f)));
    }

    public static void Tooltip(string text)
    {
        ImGui.BeginTooltip();
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Theme.Px(Theme.TooltipWidth));
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
        ImGui.EndTooltip();
    }

    public static void HoverTooltip(string text)
    {
        if (ImGui.IsItemHovered())
            Tooltip(text);
    }

    // A rounded label; outlined in its colour, lightly filled.
    public static void Chip(string text, Vector4 color)
    {
        var padding = Theme.Px(new Vector2(7f, 1f));
        var size = ImGui.CalcTextSize(text) * Theme.LabelScale + padding * 2f;
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, Theme.Color(Theme.Alpha(color, 0.14f)), size.Y / 2f);
        draw.AddRect(pos, pos + size, Theme.Color(Theme.Alpha(color, 0.55f)), size.Y / 2f);
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * Theme.LabelScale, pos + padding, Theme.Color(color), text);
        ImGui.Dummy(size);
    }

    // Chips laid out left to right, wrapping at the content width.
    public static void Chips(IEnumerable<(string Text, Vector4 Color)> chips)
    {
        var right = ImGui.GetCursorScreenPos().X + ContentWidth;
        var first = true;
        foreach (var (text, color) in chips)
        {
            var width = ImGui.CalcTextSize(text).X * Theme.LabelScale + Theme.Px(14f);
            if (!first)
            {
                ImGui.SameLine(0f, Theme.Px(Theme.SmallGap));
                if (ImGui.GetCursorScreenPos().X + width > right)
                    ImGui.NewLine();
            }

            Chip(text, color);
            first = false;
        }
    }

    public static bool Button(string label, bool primary = false, float width = 0f)
    {
        if (primary)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, Theme.GoodFill);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Alpha(Theme.Good, 0.28f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.Alpha(Theme.Good, 0.4f));
            ImGui.PushStyleColor(ImGuiCol.Text, Theme.Good);
        }

        var clicked = ImGui.Button(label, new Vector2(width, 0f));
        if (primary)
            ImGui.PopStyleColor(4);
        return clicked;
    }

    public static bool DangerButton(string label)
    {
        ImGui.PushStyleColor(ImGuiCol.Button, Theme.Alpha(Theme.Red, 0.16f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Theme.Alpha(Theme.Red, 0.28f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Theme.Alpha(Theme.Red, 0.4f));
        ImGui.PushStyleColor(ImGuiCol.Text, Theme.Red);
        var clicked = ImGui.Button(label);
        ImGui.PopStyleColor(4);
        return clicked;
    }

    public static bool Toggle(string id, bool value)
    {
        var pos = ImGui.GetCursorScreenPos();
        var size = Theme.Px(new Vector2(Theme.ToggleWidth, Theme.ToggleHeight));
        var clicked = ImGui.InvisibleButton(id, size);
        var draw = ImGui.GetWindowDrawList();
        draw.AddRectFilled(pos, pos + size, Theme.Color(value ? Theme.GoodFill : Theme.Panel), size.Y / 2f);
        var edge = ImGui.IsItemHovered() ? Theme.Gold : value ? Theme.Alpha(Theme.Good, 0.6f) : Theme.GoldDim;
        draw.AddRect(pos, pos + size, Theme.Color(edge), size.Y / 2f);
        var radius = size.Y / 2f - Theme.Px(Theme.ToggleInset);
        var x = value ? size.X - size.Y / 2f : size.Y / 2f;
        draw.AddCircleFilled(pos + new Vector2(x, size.Y / 2f), radius, Theme.Color(value ? Theme.Good : Theme.Muted));
        return clicked;
    }

    // Toggle on the left, label beside it (also clickable); hovering either shows the tooltip.
    public static bool ToggleRow(string label, ref bool value, string? tooltip = null)
    {
        var top = ImGui.GetCursorScreenPos().Y;
        var clicked = Toggle($"##{label}", value);
        if (tooltip is not null)
            HoverTooltip(tooltip);
        ImGui.SameLine(0f, Theme.Px(Theme.Gap));
        ImGui.SetCursorScreenPos(new Vector2(ImGui.GetCursorScreenPos().X, top + (Theme.Px(Theme.ToggleHeight) - ImGui.GetTextLineHeight()) / 2f));
        ImGui.TextUnformatted(label);
        clicked |= ImGui.IsItemClicked();
        if (tooltip is not null)
            HoverTooltip(tooltip);
        if (clicked)
            value = !value;
        return clicked;
    }

    // Pulsing gold halo, the game's way of pointing at a card.
    public static void Glow(ImDrawListPtr draw, Vector2 min, Vector2 max, Vector4 color)
    {
        var pulse = 0.65f + 0.35f * MathF.Sin((float)ImGui.GetTime() * 4f);
        for (var layer = Theme.GlowLayers; layer > 0; layer--)
        {
            var spread = new Vector2(Theme.Px(layer * Theme.GlowSpread));
            draw.AddRect(min - spread, max + spread, Theme.Color(Theme.Alpha(color, pulse * 0.5f / layer)), Theme.Px(Theme.CardRadius + layer), ImDrawFlags.None, Theme.Px(2.5f));
        }

        draw.AddRect(min, max, Theme.Color(Theme.Alpha(color, pulse)), Theme.Px(Theme.CardRadius), ImDrawFlags.None, Theme.Px(2f));
    }

    // A card face: owner tint, gold frame, the four sides in a diamond and the type icon.
    public static void CardFace(ImDrawListPtr draw, Vector2 min, Vector2 max, CardStats card, Side owner, float alpha = 1f)
    {
        var size = max - min;
        var inset = new Vector2(size.X * 0.06f, size.Y * 0.03f);
        var (top, bottom) = owner == Side.Red ? (Theme.RedFaceTop, Theme.RedFaceBottom) : (Theme.BlueFaceTop, Theme.BlueFaceBottom);
        draw.AddRectFilledMultiColor(min + inset, max - inset, Theme.Color(Theme.Alpha(top, alpha)), Theme.Color(Theme.Alpha(top, alpha)),
            Theme.Color(Theme.Alpha(bottom, alpha)), Theme.Color(Theme.Alpha(bottom, alpha)));
        if (!CardArt.Draw(draw, CardArt.CardFrame, min, max, alpha))
            draw.AddRect(min, max, Theme.Color(Theme.Alpha(Theme.Gold, alpha)), Theme.Px(Theme.CardRadius), ImDrawFlags.None, Theme.Px(2f));

        var scale = Math.Clamp(size.X / Theme.Px(52f), 0.75f, 1.6f);
        var center = (min + max) / 2f + new Vector2(0f, size.Y * 0.03f);
        var d = size.X * 0.21f;
        var color = Theme.Alpha(new Vector4(1f, 1f, 1f, 1f), alpha);
        CenteredText(draw, center + new Vector2(0f, -d), Glyph(card.Up), color, scale);
        CenteredText(draw, center + new Vector2(-d, 0f), Glyph(card.Left), color, scale);
        CenteredText(draw, center + new Vector2(d, 0f), Glyph(card.Right), color, scale);
        CenteredText(draw, center + new Vector2(0f, d), Glyph(card.Down), color, scale);

        if (CardArt.TypeIcon(card.Type) is { } icon)
        {
            var h = size.X * 0.24f;
            var w = h * icon.Aspect;
            var pos = new Vector2(max.X - size.X * 0.12f - w, min.Y + size.Y * 0.08f);
            if (!CardArt.Draw(draw, icon, pos, pos + new Vector2(w, h), alpha))
                OutlinedText(draw, pos, card.Type.ToString()[..1], Theme.GoldBright);
        }
    }

    public static void CardBack(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        if (CardArt.Draw(draw, CardArt.CardBack, min, max))
            return;
        draw.AddRectFilled(min, max, Theme.Color(new Vector4(0.45f, 0.33f, 0.14f, 1f)), Theme.Px(Theme.CardRadius));
        draw.AddRect(min, max, Theme.Color(Theme.Gold), Theme.Px(Theme.CardRadius));
    }

    public static void EmptyCard(ImDrawListPtr draw, Vector2 min, Vector2 max, string label = "")
    {
        draw.AddRectFilled(min, max, Theme.Color(Theme.EmptyCell), Theme.Px(Theme.CardRadius));
        draw.AddRect(min, max, Theme.Color(Theme.Alpha(Theme.GoldDim, 0.6f)), Theme.Px(Theme.CardRadius));
        var size = ImGui.CalcTextSize(label) * Theme.LabelScale;
        if (label.Length > 0 && size.X < max.X - min.X - Theme.Px(4f))
        {
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * Theme.LabelScale, (min + max - size) / 2f, Theme.Color(Theme.Faint), label);
        }
    }

    // A score medallion (red or blue) with the count on it.
    public static void Medallion(ImDrawListPtr draw, Vector2 center, float radius, Side side, int count)
    {
        var min = center - new Vector2(radius);
        var max = center + new Vector2(radius);
        if (!CardArt.Draw(draw, side == Side.Red ? CardArt.RedMedallion : CardArt.BlueMedallion, min, max))
        {
            draw.AddCircleFilled(center, radius, Theme.Color(side == Side.Red ? Theme.RedFaceTop : Theme.BlueFaceTop));
            draw.AddCircle(center, radius, Theme.Color(Theme.Gold), 0, Theme.Px(2f));
        }

        CenteredText(draw, center, count.ToString(), new Vector4(1f, 1f, 1f, 1f), radius / Theme.Px(11f));
    }

    public static void Stars(int count, float height)
    {
        var pos = ImGui.GetCursorScreenPos();
        var draw = ImGui.GetWindowDrawList();
        var w = height * CardArt.Star.Aspect;
        for (var i = 0; i < count; i++)
        {
            var p = pos + new Vector2(i * (w + 1f), 0f);
            if (!CardArt.Draw(draw, CardArt.Star, p, p + new Vector2(w, height)))
                draw.AddText(p, Theme.Color(Theme.GoldBright), "*");
        }

        ImGui.Dummy(new Vector2(count * (w + 1f), height));
    }

    public static string Glyph(byte value) => value >= 10 ? "A" : value.ToString();

    // A rounded translucent panel behind a group, sized to its content.
    public readonly struct PanelScope : IDisposable
    {
        private readonly Vector2 origin;
        private readonly float width;
        private readonly bool highlight;

        public PanelScope(bool highlight)
        {
            this.origin = ImGui.GetCursorScreenPos();
            this.width = ImGui.GetContentRegionAvail().X;
            this.highlight = highlight;
            var draw = ImGui.GetWindowDrawList();
            draw.ChannelsSplit(2);
            draw.ChannelsSetCurrent(1);
            ImGui.BeginGroup();
            ImGui.SetCursorScreenPos(this.origin + new Vector2(Theme.Px(Theme.PanelPadding)));
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + this.width - Theme.Px(Theme.PanelPadding * 2f));
            panelDepth++;
        }

        public void Dispose()
        {
            panelDepth--;
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
            var bottom = ImGui.GetItemRectMax().Y + Theme.Px(Theme.PanelPadding);
            var max = new Vector2(this.origin.X + this.width, bottom);
            var draw = ImGui.GetWindowDrawList();
            draw.ChannelsSetCurrent(0);
            draw.AddRectFilled(this.origin, max, Theme.Color(this.highlight ? Theme.Alpha(Theme.Panel, 1.4f) : Theme.Panel), Theme.Px(Theme.PanelRadius));
            draw.AddRect(this.origin, max, Theme.Color(this.highlight ? Theme.Alpha(Theme.Gold, 0.5f) : Theme.PanelEdge), Theme.Px(Theme.PanelRadius));
            draw.ChannelsMerge();
            ImGui.SetCursorScreenPos(new Vector2(this.origin.X, bottom));
            ImGui.Dummy(new Vector2(this.width, 0f));
            ImGui.EndGroup();
        }
    }
}
