/*
 * Triple Triad card palette: gold frame and accents, aqua text, green for the move to play,
 * blue for our side and red for the NPC's, on a dark red or dark blue translucent backdrop.
 * Dimensions are logical pixels; Px applies the global UI scale. Dalamud's WindowSystem scales
 * window sizes and constraints itself.
 */
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;

namespace TrialHater.Windows;

public enum Backdrop
{
    Crimson,
    Midnight,
}

internal static class Theme
{
    public static readonly Vector4 Gold = new(0.89f, 0.72f, 0.38f, 1f);
    public static readonly Vector4 GoldBright = new(1f, 0.87f, 0.55f, 1f);
    public static readonly Vector4 GoldDim = new(0.89f, 0.72f, 0.38f, 0.35f);
    public static readonly Vector4 Text = new(0.76f, 0.93f, 0.96f, 1f);
    public static readonly Vector4 Muted = new(0.76f, 0.93f, 0.96f, 0.58f);
    public static readonly Vector4 Faint = new(0.76f, 0.93f, 0.96f, 0.28f);
    public static readonly Vector4 Good = new(0.46f, 0.94f, 0.62f, 1f);
    public static readonly Vector4 Blue = new(0.50f, 0.74f, 1f, 1f);
    public static readonly Vector4 Red = new(1f, 0.50f, 0.48f, 1f);
    public static readonly Vector4 Warn = new(1f, 0.64f, 0.32f, 1f);
    public static readonly Vector4 Panel = new(0f, 0f, 0f, 0.24f);
    public static readonly Vector4 PanelEdge = new(0.89f, 0.72f, 0.38f, 0.22f);
    public static readonly Vector4 Control = new(0.89f, 0.72f, 0.38f, 0.12f);
    public static readonly Vector4 Hover = new(0.89f, 0.72f, 0.38f, 0.22f);
    public static readonly Vector4 Pressed = new(0.89f, 0.72f, 0.38f, 0.32f);
    public static readonly Vector4 GoodFill = new(0.46f, 0.94f, 0.62f, 0.16f);
    public static readonly Vector4 Shadow = new(0f, 0f, 0f, 0.85f);

    // Card faces: the game's blue and red card tints, top and bottom of the gradient.
    public static readonly Vector4 BlueFaceTop = new(0.20f, 0.38f, 0.74f, 1f);
    public static readonly Vector4 BlueFaceBottom = new(0.08f, 0.16f, 0.40f, 1f);
    public static readonly Vector4 RedFaceTop = new(0.70f, 0.18f, 0.20f, 1f);
    public static readonly Vector4 RedFaceBottom = new(0.34f, 0.06f, 0.09f, 1f);
    public static readonly Vector4 EmptyCell = new(0f, 0f, 0f, 0.30f);

    public const float FrameScale = 1.6f;          // gold frame sprite, source pixels to logical pixels
    public const float PanelRadius = 6f;
    public const float ChipRadius = 100f;
    public const float CardRadius = 4f;
    public const float Gap = 8f;
    public const float SmallGap = 4f;
    public const float PanelPadding = 10f;
    public const float ToggleWidth = 38f;
    public const float ToggleHeight = 20f;
    public const float ToggleInset = 3f;
    public const float TooltipWidth = 320f;
    public const float TitleScale = 1.35f;
    public const float HeadlineScale = 1.3f;
    public const float LabelScale = 0.85f;
    public const float GlowSpread = 2f;
    public const int GlowLayers = 3;

    public static readonly Vector2 MainSize = new(560f, 800f);
    public static readonly Vector2 MainMinimum = new(440f, 420f);
    public static readonly Vector2 ConfigSize = new(500f, 700f);
    public static readonly Vector2 ConfigMinimum = new(420f, 360f);
    public static readonly Vector2 Maximum = new(1000f, 1600f);

    public static float Px(float value) => value * ImGuiHelpers.GlobalScale;

    public static Vector2 Px(Vector2 value) => value * ImGuiHelpers.GlobalScale;

    public static uint Color(Vector4 value)
    {
        value.W *= ImGui.GetStyle().Alpha;
        return ImGui.ColorConvertFloat4ToU32(value);
    }

    public static Vector4 Alpha(Vector4 color, float alpha) => color with { W = color.W * alpha };

    public static (Vector4 Top, Vector4 Bottom) BackdropColors(Backdrop backdrop, float opacity) => backdrop switch
    {
        Backdrop.Midnight => (new(0.05f, 0.09f, 0.22f, opacity), new(0.02f, 0.03f, 0.11f, opacity)),
        _ => (new(0.24f, 0.04f, 0.07f, opacity), new(0.10f, 0.01f, 0.03f, opacity)),
    };

    // Stored between PreDraw and PostDraw so the styles also apply to ImGui.Begin. The window
    // background is ours (Chrome.Backdrop), so ImGui's is transparent.
    public struct Scope : IDisposable
    {
        private int colors;
        private int variables;

        public Scope(Backdrop backdrop)
        {
            this.colors = 0;
            this.variables = 0;
            var popup = BackdropColors(backdrop, 0.97f).Top;
            this.Push(ImGuiStyleVar.WindowPadding, Chrome.Padding);
            this.Push(ImGuiStyleVar.WindowRounding, 0f);
            this.Push(ImGuiStyleVar.WindowBorderSize, 0f);
            this.Push(ImGuiStyleVar.ChildRounding, Px(PanelRadius));
            this.Push(ImGuiStyleVar.PopupRounding, Px(PanelRadius));
            this.Push(ImGuiStyleVar.PopupBorderSize, 1f);
            this.Push(ImGuiStyleVar.FramePadding, Px(new Vector2(8f, 4f)));
            this.Push(ImGuiStyleVar.FrameRounding, Px(CardRadius));
            this.Push(ImGuiStyleVar.FrameBorderSize, 0f);
            this.Push(ImGuiStyleVar.ItemSpacing, Px(new Vector2(Gap, 6f)));
            this.Push(ImGuiStyleVar.ItemInnerSpacing, Px(new Vector2(6f, SmallGap)));
            this.Push(ImGuiStyleVar.CellPadding, Px(new Vector2(6f, 3f)));
            this.Push(ImGuiStyleVar.ScrollbarSize, Px(8f));
            this.Push(ImGuiStyleVar.ScrollbarRounding, Px(ChipRadius));
            this.Push(ImGuiStyleVar.GrabRounding, Px(CardRadius));
            this.Push(ImGuiStyleVar.GrabMinSize, Px(10f));
            this.Push(ImGuiStyleVar.TabRounding, Px(CardRadius));
            this.Push(ImGuiCol.Text, Text);
            this.Push(ImGuiCol.TextDisabled, Muted);
            this.Push(ImGuiCol.WindowBg, Vector4.Zero);
            this.Push(ImGuiCol.ChildBg, Vector4.Zero);
            this.Push(ImGuiCol.PopupBg, popup);
            this.Push(ImGuiCol.Border, GoldDim);
            this.Push(ImGuiCol.Separator, GoldDim);
            this.Push(ImGuiCol.Button, Control);
            this.Push(ImGuiCol.ButtonHovered, Hover);
            this.Push(ImGuiCol.ButtonActive, Pressed);
            this.Push(ImGuiCol.FrameBg, Panel);
            this.Push(ImGuiCol.FrameBgHovered, Control);
            this.Push(ImGuiCol.FrameBgActive, Hover);
            this.Push(ImGuiCol.CheckMark, Good);
            this.Push(ImGuiCol.SliderGrab, Gold);
            this.Push(ImGuiCol.SliderGrabActive, GoldBright);
            this.Push(ImGuiCol.Header, Control);
            this.Push(ImGuiCol.HeaderHovered, Hover);
            this.Push(ImGuiCol.HeaderActive, Pressed);
            this.Push(ImGuiCol.Tab, Control);
            this.Push(ImGuiCol.TabHovered, Hover);
            this.Push(ImGuiCol.TabActive, Pressed);
            this.Push(ImGuiCol.TableHeaderBg, Control);
            this.Push(ImGuiCol.TableBorderStrong, GoldDim);
            this.Push(ImGuiCol.TableBorderLight, Alpha(GoldDim, 0.5f));
            this.Push(ImGuiCol.TableRowBg, Vector4.Zero);
            this.Push(ImGuiCol.TableRowBgAlt, new Vector4(1f, 1f, 1f, 0.035f));
            this.Push(ImGuiCol.ScrollbarBg, Vector4.Zero);
            this.Push(ImGuiCol.ScrollbarGrab, GoldDim);
            this.Push(ImGuiCol.ScrollbarGrabHovered, Gold);
            this.Push(ImGuiCol.ScrollbarGrabActive, GoldBright);
            this.Push(ImGuiCol.ResizeGrip, Vector4.Zero);
            this.Push(ImGuiCol.ResizeGripHovered, GoldDim);
            this.Push(ImGuiCol.ResizeGripActive, Gold);
            this.Push(ImGuiCol.NavHighlight, Gold);
        }

        public void Dispose()
        {
            if (this.colors > 0)
                ImGui.PopStyleColor(this.colors);
            if (this.variables > 0)
                ImGui.PopStyleVar(this.variables);
            this.colors = 0;
            this.variables = 0;
        }

        private void Push(ImGuiCol name, Vector4 value)
        {
            ImGui.PushStyleColor(name, value);
            this.colors++;
        }

        private void Push(ImGuiStyleVar name, float value)
        {
            ImGui.PushStyleVar(name, value);
            this.variables++;
        }

        private void Push(ImGuiStyleVar name, Vector2 value)
        {
            ImGui.PushStyleVar(name, value);
            this.variables++;
        }
    }
}
