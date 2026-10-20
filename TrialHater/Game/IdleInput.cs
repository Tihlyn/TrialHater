using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.UI;
using TrialHater.Core.Session;

namespace TrialHater.Game;

// The native side of IdleGuard (ported from MahjongHater): the game's input timers, this
// process's game window, and the Control key messages.
public static class IdleInput
{
    public static bool ControlHeld() => (GetAsyncKeyState(0x11) & 0x8000) != 0;

    public static unsafe IdleGuard.Timers? ReadGameTimers()
    {
        var ui = UIModule.Instance();
        if (ui == null)
            return null;
        var timers = ui->GetInputTimerModule();
        return timers == null ? null : new IdleGuard.Timers(timers->AfkTimer, timers->ContentInputTimer, timers->InputTimer);
    }

    public static nint FindGameWindow()
    {
        nint window = 0;
        while ((window = FindWindowExW(0, window, "FFXIVGAME", null)) != 0)
        {
            GetWindowThreadProcessId(window, out var processId);
            if (processId == Environment.ProcessId)
                return window;
        }
        return 0;
    }

    public static bool SendControl(nint window, bool down)
    {
        // Revalidate delayed key-up ownership; a destroyed window needs no release.
        GetWindowThreadProcessId(window, out var processId);
        if (processId != Environment.ProcessId)
            return !down;
        const uint keyDown = 0x100, keyUp = 0x101;
        const int leftControl = 0xA2;
        // Same Control messages as the upstream plugin. Cross-thread sends are bounded;
        // same-thread sends invoke the window procedure directly, without a wait loop.
        const uint abortIfHung = 0x02, errorOnExit = 0x20;
        if (SendMessageTimeoutW(window, down ? keyDown : keyUp, leftControl, 0,
                abortIfHung | errorOnExit, 50, out _) != 0)
            return true;
        if (!down)
            PostMessageW(window, keyUp, leftControl, 0);
        // Enqueueing is not completion. Keep automation blocked until a later
        // synchronous release succeeds; the queued up also covers plugin teardown.
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nint FindWindowExW(nint parent, nint after, string className, string? title);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint key, nint data,
        uint flags, uint timeoutMs, out nuint result);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint window, uint message, nint key, nint data);
}
