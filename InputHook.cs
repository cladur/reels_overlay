using System.Runtime.InteropServices;
using static ReelsOverlay.Native;

namespace ReelsOverlay;

// Low-level keyboard and mouse hooks: the hotkeys, modifier+wheel (next/previous reel) and
// modifier+middle-click (like).
//
// Why hooks and not RegisterHotKey: games read the keyboard with raw input and often pass
// RIDEV_NOHOTKEYS, which stops Windows from delivering any app's registered hotkeys while
// the game has focus. Low-level hooks run earlier in the input path and still see the keys.
//
// Windows calls these hooks for every key and mouse event from every program, the game
// included, and holds that event until the hook returns. So they live on a thread of their
// own that does nothing else: they read the modifier keys, hand any action to the UI thread
// without waiting, and return.
sealed class InputHook : IDisposable
{
    // One hotkey. Fire must not block: it is called on the hook thread.
    public sealed record Binding(uint Mods, int Vk, Action Fire);

    readonly Action<int> onWheel; // +1 = next, -1 = previous
    readonly Action onMiddle;
    readonly Thread thread;
    readonly ManualResetEventSlim started = new();
    HookProc? keyProc, mouseProc; // kept in fields so the GC cannot collect the delegates
    IntPtr keyHook, mouseHook;
    uint threadId;
    long lastWheel;
    bool swallowMiddleUp;
    readonly HashSet<int> swallowedDown = new(); // matched keys still held: drop repeats and the release

    // Written by the UI thread, read by the hook thread.
    public volatile Binding[] Bindings = [];
    public volatile bool KeysEnabled = true;
    public volatile int[] MouseModifierVks = []; // empty = mouse controls off
    public volatile bool MouseEnabled = true;

    public bool Installed => keyHook != IntPtr.Zero && mouseHook != IntPtr.Zero;

    public InputHook(Action<int> onWheel, Action onMiddle)
    {
        this.onWheel = onWheel;
        this.onMiddle = onMiddle;
        thread = new Thread(Run) { IsBackground = true, Name = "reels_overlay input hook", Priority = ThreadPriority.Highest };
        thread.Start();
        started.Wait();
    }

    void Run()
    {
        threadId = GetCurrentThreadId();
        keyProc = KeyProc;
        mouseProc = MouseProc;
        IntPtr module = GetModuleHandle(null);
        keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyProc, module, 0);
        mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
        started.Set();
        // Low-level hooks are called through this thread's message loop.
        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
        if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
        if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
    }

    IntPtr KeyProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            int vk = Marshal.ReadInt32(lParam); // KBDLLHOOKSTRUCT.vkCode
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if (swallowedDown.Contains(vk))
            {
                // A matched key: its auto-repeats and its release never reach anyone.
                if (up) swallowedDown.Remove(vk);
                return 1;
            }
            if (down && KeysEnabled && Match(vk, HeldModifiers()) is Binding b)
            {
                swallowedDown.Add(vk);
                b.Fire();
                return 1;
            }
        }
        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // Exact modifiers first; a binding without modifiers also matches with only Shift held,
    // so bare keys keep working mid-sprint.
    Binding? Match(int vk, uint mods)
    {
        var bindings = Bindings;
        foreach (var b in bindings)
            if (b.Vk == vk && b.Mods == mods) return b;
        if (mods == MOD_SHIFT)
            foreach (var b in bindings)
                if (b.Vk == vk && b.Mods == 0) return b;
        return null;
    }

    static uint HeldModifiers()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        uint mods = 0;
        if (Down(VK_CONTROL)) mods |= MOD_CONTROL;
        if (Down(VK_MENU)) mods |= MOD_ALT;
        if (Down(VK_SHIFT)) mods |= MOD_SHIFT;
        if (Down(VK_LWIN) || Down(VK_RWIN)) mods |= MOD_WIN;
        return mods;
    }

    IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            int msg = (int)wParam;
            if (msg == WM_MOUSEWHEEL && MouseChordHeld())
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
            if (msg == WM_MBUTTONDOWN && MouseChordHeld())
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

    bool MouseChordHeld()
    {
        var vks = MouseModifierVks;
        if (!MouseEnabled || vks.Length == 0) return false;
        foreach (int vk in vks)
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
