using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TrialHater.Game;

// Native UI dispatch for the match windows, after MahjongHater's EmjOperator: activate a control
// through the event chain the game itself registered on it, so the addon's own handler runs (with
// its cleanup) exactly as for a mouse click. A bare FireCallback reaches only the agent, and its
// third argument closes the addon. Framework thread only.
internal static unsafe class AddonOps
{
    private const int MaxChainDepth = 32;

    // Ready, visible and not blocking input (animations, modals).
    public static bool CanReceiveInput(AtkUnitBase* addon, out string why)
    {
        why = addon == null ? "closed"
            : !addon->IsVisible ? "hidden"
            : !addon->IsReady ? "not ready"
            : addon->ShouldIgnoreInputs() ? "ignoring input (animation or modal)"
            : string.Empty;
        return why.Length == 0;
    }

    // ButtonClick on the node with this id, through its addon-bound chain.
    public static DispatchResult ActivateButton(AtkUnitBase* addon, uint nodeId)
    {
        if (!CanReceiveInput(addon, out var why))
            return DispatchResult.Refused(why);
        var node = addon->GetNodeById(nodeId);
        if (node == null)
            return DispatchResult.Refused($"node {nodeId} not found");
        if (!IsChainVisible(node, out why))
            return DispatchResult.Refused($"node {nodeId}: {why}");
        var evt = FindChainEvent(node, AtkEventType.ButtonClick, (nint)addon, 0, out var holder);
        if (evt == null)
            return DispatchResult.Refused($"node {nodeId} has no visible addon-bound ButtonClick");
        return new DispatchResult(true, $"node {nodeId}: {Fire(addon, (AtkResNode*)holder, evt)}");
    }

    // MouseClick anywhere the addon registered one (Talk advances on its collision node).
    public static DispatchResult Click(AtkUnitBase* addon)
    {
        if (!CanReceiveInput(addon, out var why))
            return DispatchResult.Refused(why);
        var evt = FindChainEvent(addon->RootNode, AtkEventType.MouseClick, (nint)addon, 0, out var holder);
        if (evt == null)
            return DispatchResult.Refused("no visible addon-bound MouseClick");
        return new DispatchResult(true, Fire(addon, (AtkResNode*)holder, evt));
    }

    // The callback route, for windows whose selection the game itself sends as a callback (the NPC
    // menu: [index], close = true, as captured 2026-10-19).
    public static DispatchResult Callback(AtkUnitBase* addon, bool close, params int[] values)
    {
        if (!CanReceiveInput(addon, out var why))
            return DispatchResult.Refused(why);
        var vals = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            vals[i].Type = AtkValueType.Int;
            vals[i].Int = values[i];
        }

        addon->FireCallback((uint)values.Length, vals, close);
        return new DispatchResult(true, $"callback [{string.Join(", ", values)}] close={close}");
    }

    // Callback padded with Undefined values to `length`, the shape the game itself sends. The Gold
    // Saucer deck editor reads its slot/deck argument only from a callback of that length: with
    // three values [11, 3, id] went to slot 0 and [5, 4] opened Deck 1 (2026-10-20, session 13).
    public static DispatchResult CallbackPadded(AtkUnitBase* addon, bool close, int length, params int[] values)
    {
        if (!CanReceiveInput(addon, out var why))
            return DispatchResult.Refused(why);
        var count = Math.Max(length, values.Length);
        var vals = stackalloc AtkValue[count];
        for (var i = 0; i < count; i++)
        {
            vals[i] = default;
            if (i < values.Length)
            {
                vals[i].Type = AtkValueType.Int;
                vals[i].Int = values[i];
            }
            else
            {
                vals[i].Type = AtkValueType.Undefined;
            }
        }

        addon->FireCallback((uint)count, vals, close);
        return new DispatchResult(true, $"callback [{string.Join(", ", values)}{string.Concat(Enumerable.Repeat(", -", count - values.Length))}] close={close}");
    }

    // An Int/UInt AtkValue, or 0 (absent, other type).
    public static int IntValue(AtkUnitBase* addon, int index)
    {
        if (addon == null || index < 0 || index >= addon->AtkValuesCount)
            return 0;
        ref var v = ref addon->AtkValues[index];
        return v.Type is AtkValueType.Int or AtkValueType.UInt ? v.Int : 0;
    }

    // A string AtkValue, or "" (absent, other type).
    public static string StringValue(AtkUnitBase* addon, int index)
    {
        if (addon == null || index < 0 || index >= addon->AtkValuesCount)
            return string.Empty;
        ref var v = ref addon->AtkValues[index];
        if (v.Type is not (AtkValueType.String or AtkValueType.ConstString or AtkValueType.ManagedString))
            return string.Empty;
        try
        {
            return v.String.ExtractText();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool IsChainVisible(AtkResNode* node, out string why)
    {
        why = string.Empty;
        var depth = 0;
        for (var n = node; n != null; n = n->ParentNode)
        {
            if (++depth > MaxChainDepth)
            {
                why = "ancestor chain too deep";
                return false;
            }

            if (!n->IsVisible())
            {
                why = $"node {n->NodeId} hidden";
                return false;
            }
        }

        return true;
    }

    private static string Fire(AtkUnitBase* addon, AtkResNode* node, AtkEvent* evt)
    {
        // Read everything before ReceiveEvent: the handler may rebuild the tree.
        var type = evt->State.EventType;
        var param = (int)evt->Param;
        var listener = evt->Listener != null ? evt->Listener : (AtkEventListener*)addon;
        var data = default(AtkEventData);
        data.MouseData.PosX = (short)(node->ScreenX + (node->Width / 2f));
        data.MouseData.PosY = (short)(node->ScreenY + (node->Height / 2f));

        // ReceiveEvent marks its argument handled: dispatch a copy, never the registration.
        var copy = *evt;
        copy.NextEvent = null;
        copy.State = new AtkEventState { EventType = type };
        listener->ReceiveEvent(type, param, &copy, &data);
        return $"{type} param={param}";
    }

    // First visible, addon-bound chain event of this type in the node's subtree.
    private static AtkEvent* FindChainEvent(AtkResNode* node, AtkEventType type, nint listener, int depth, out nint holder)
    {
        holder = 0;
        if (node == null || depth > 8 || !node->IsVisible())
            return null;

        var guard = 0;
        for (var e = node->AtkEventManager.Event; e != null && guard++ < 16; e = e->NextEvent)
        {
            if (e->State.EventType == type && (nint)e->Listener == listener)
            {
                holder = (nint)node;
                return e;
            }
        }

        if ((ushort)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null)
                return null;
            var list = component->UldManager.NodeList;
            for (var i = 0; i < component->UldManager.NodeListCount && list != null; i++)
            {
                var found = FindChainEvent(list[i], type, listener, depth + 1, out holder);
                if (found != null)
                    return found;
            }

            return null;
        }

        for (var child = node->ChildNode; child != null; child = child->PrevSiblingNode)
        {
            var found = FindChainEvent(child, type, listener, depth + 1, out holder);
            if (found != null)
                return found;
        }

        return null;
    }
}
