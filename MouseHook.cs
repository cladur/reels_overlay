using System.Runtime.InteropServices;
using static ReelsOverlay.Native;

namespace ReelsOverlay;

// Low-level mouse hook for modifier+wheel (next/previous reel) and modifier+middle-click
// (like). Windows calls a low-level hook for every mouse event from every program, the game
// included, and holds that event until the hook returns. So the hook lives on its own thread
// that does nothing else: it reads the modifier keys, hands any action to the UI thread
// without waiting, and returns. On the UI thread it would stall the game's mouse whenever the
// overlay was busy talking to the page.
sealed class MouseHook : IDisposable
{
    readonly int[] modifierVks;
    readonly Action<int> onWheel; // +1 = next, -1 = previous
    readonly Action onMiddle;
    readonly Thread thread;
    readonly ManualResetEventSlim started = new();
    HookProc? proc; // kept in a field so the GC cannot collect the delegate Windows calls
    IntPtr hook;
    uint threadId;
    long lastWheel;
    bool swallowMiddleUp;

    // Cleared while the overlay is hidden, so the chord goes back to behaving normally.
    public volatile bool Enabled = true;

    public bool Installed => hook != IntPtr.Zero;

    public MouseHook(int[] modifierVks, Action<int> onWheel, Action onMiddle)
    {
        this.modifierVks = modifierVks;
        this.onWheel = onWheel;
        this.onMiddle = onMiddle;
        thread = new Thread(Run) { IsBackground = true, Name = "Reels Overlay mouse hook", Priority = ThreadPriority.Highest };
        thread.Start();
        started.Wait();
    }

    void Run()
    {
        threadId = GetCurrentThreadId();
        proc = Proc;
        hook = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
        started.Set();
        if (hook == IntPtr.Zero) return;
        // Low-level hooks are called through this thread's message loop.
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        UnhookWindowsHookEx(hook);
    }

    IntPtr Proc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_MOUSEWHEEL && Enabled && ChordHeld())
            {
                // MSLLHOOKSTRUCT: POINT pt (8 bytes), then mouseData with the delta in its high word.
                short delta = (short)(Marshal.ReadInt32(lParam, 8) >> 16);
                long now = Environment.TickCount64;
                if (delta != 0 && now - lastWheel >= 120)
                {
                    lastWheel = now;
                    onWheel(delta < 0 ? +1 : -1);
                }
                return 1; // swallowed
            }
            if (msg == WM_MBUTTONDOWN && Enabled && ChordHeld())
            {
                swallowMiddleUp = true;
                onMiddle();
                return 1;
            }
            if (msg == WM_MBUTTONUP && swallowMiddleUp)
            {
                // Its press was ours; a lone release would confuse whatever is underneath.
                swallowMiddleUp = false;
                return 1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    bool ChordHeld()
    {
        foreach (int vk in modifierVks)
        {
            bool down = (GetAsyncKeyState(vk) & 0x8000) != 0
                        || (vk == VK_LWIN && (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0);
            if (!down) return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (threadId != 0) PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        thread.Join(1000);
    }
}
