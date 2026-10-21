using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;

namespace TrialHater.Windows;

// The card-shaped window: translucent backdrop, gold frame, a title row clear of the frame's
// top-right flourish, a scrolling body inside the frame and a one-line footer. The frame is drawn
// on the body's draw list after the body so it sits over scrolled content.
internal static class Chrome
{
    // Frame sprite pixels to screen pixels.
    public static float FrameScale => Theme.FrameScale * ImGuiHelpers.GlobalScale;

    // Inside the frame's waist (12 source pixels) and below its top run (7), plus breathing room.
    public static Vector2 Padding => new(12f * FrameScale + Theme.Px(10f), 7f * FrameScale + Theme.Px(8f));

    // The top-right flourish reaches 37 source pixels in from the right edge on the title row.
    public static float TitleReserve => Math.Max(0f, 37f * FrameScale - Padding.X);

    public static float FooterHeight => ImGui.GetTextLineHeight() * Theme.LabelScale + Theme.Px(Theme.SmallGap);

    // The frame padding is the window's own; tooltips, popups and combos opened while the window
    // draws get ordinary padding. Pair with PopInner at the end of Draw.
    public static void PushInner() => ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Theme.Px(new Vector2(8f, 6f)));

    public static void PopInner() => ImGui.PopStyleVar();

    public static void Backdrop(Backdrop backdrop, float opacity)
    {
        var draw = ImGui.GetWindowDrawList();
        var min = ImGui.GetWindowPos();
        var max = min + ImGui.GetWindowSize();
        // Inset to the frame's waist so no backdrop shows outside its curved sides.
        var inset = new Vector2(5f * FrameScale);
        var (top, bottom) = Theme.BackdropColors(backdrop, opacity);
        draw.AddRectFilledMultiColor(min + inset, max - inset, Theme.Color(top), Theme.Color(top), Theme.Color(bottom), Theme.Color(bottom));
        // A faint sheen under the title, as on the game's card faces.
        var sheenBottom = new Vector2(max.X - inset.X, min.Y + inset.Y + Theme.Px(90f));
        draw.AddRectFilledMultiColor(min + inset, sheenBottom, Theme.Color(new Vector4(1f, 0.9f, 0.7f, 0.06f)), Theme.Color(new Vector4(1f, 0.9f, 0.7f, 0.06f)), 0, 0);
    }

    // Title in gold with a muted subtitle (the version) and icon buttons on the right.
    // Returns the index of the clicked button, or -1.
    public static int Header(string title, string subtitle, params (FontAwesomeIcon Icon, string Tooltip)[] buttons)
    {
        var origin = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X - TitleReserve;
        var buttonSize = ImGui.GetFrameHeight();
        var titleSize = ImGui.CalcTextSize(title) * Theme.TitleScale;
        var rowHeight = Math.Max(titleSize.Y, buttonSize);
        var draw = ImGui.GetWindowDrawList();
        var titlePos = origin + new Vector2(0f, (rowHeight - titleSize.Y) / 2f);
        Widgets.OutlinedText(draw, titlePos, title, Theme.GoldBright, Theme.TitleScale);
        var subtitleSize = ImGui.CalcTextSize(subtitle) * Theme.LabelScale;
        draw.AddText(ImGui.GetFont(), ImGui.GetFontSize() * Theme.LabelScale,
            titlePos + new Vector2(titleSize.X + Theme.Px(6f), titleSize.Y - subtitleSize.Y - Theme.Px(2f)), Theme.Color(Theme.Muted), subtitle);

        var clicked = -1;
        var x = origin.X + width - buttons.Length * buttonSize - (buttons.Length - 1) * Theme.Px(Theme.SmallGap);
        for (var i = 0; i < buttons.Length; i++)
        {
            ImGui.SetCursorScreenPos(new Vector2(x + i * (buttonSize + Theme.Px(Theme.SmallGap)), origin.Y + (rowHeight - buttonSize) / 2f));
            if (ImGuiComponents.IconButton($"##chrome{i}", buttons[i].Icon, new Vector2(buttonSize)))
                clicked = i;
            if (ImGui.IsItemHovered())
                Widgets.Tooltip(buttons[i].Tooltip);
        }

        ImGui.SetCursorScreenPos(origin);
        ImGui.Dummy(new Vector2(width, rowHeight));
        Widgets.Ornament();
        return clicked;
    }

    public static bool BeginBody(string id)
        => ImGui.BeginChild(id, new Vector2(0f, -FooterHeight - Theme.Px(Theme.SmallGap)), false, ImGuiWindowFlags.NoBackground);

    public static void EndBody(bool visible, Vector2 windowMin, Vector2 windowMax)
    {
        if (visible)
            Frame(ImGui.GetWindowDrawList(), windowMin, windowMax);
        ImGui.EndChild();
        if (!visible)
            Frame(ImGui.GetWindowDrawList(), windowMin, windowMax);
    }

    // Right-aligned so it stays clear of the frame's bottom-left flourish.
    public static void Footer(string text)
    {
        var size = ImGui.CalcTextSize(text) * Theme.LabelScale;
        var pos = ImGui.GetCursorScreenPos() + new Vector2(ImGui.GetContentRegionAvail().X - size.X, Theme.Px(Theme.SmallGap));
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), ImGui.GetFontSize() * Theme.LabelScale, pos, Theme.Color(Theme.Faint), text);
    }

    private static void Frame(ImDrawListPtr draw, Vector2 min, Vector2 max)
    {
        draw.PushClipRect(min, max, false);
        CardArt.WindowFrame(draw, min, max, FrameScale);
        draw.PopClipRect();
    }
}
