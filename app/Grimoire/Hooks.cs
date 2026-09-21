using System.Runtime.InteropServices;
using static Spellbook.Native;

namespace Spellbook;

/// <summary>
/// Low-level mouse + keyboard hooks on their own thread (so UI stalls never delay system input).
///  - Ctrl/Alt + right-button-down opens the menu (the click is swallowed).
///  - Registered key chords are swallowed and dispatched; the following Alt release is "masked"
///    with a Ctrl tap so the target app does not drop into menu-bar mode (the AutoHotkey trick).
/// </summary>
internal sealed class InputHooks : IDisposable
{
    public event Action<IntPtr, Point>? MenuTriggered;
    public event Action<Action>? HotkeyTriggered;

    public volatile bool UseCtrl = true;
    public volatile bool UseAlt = true;
    public volatile bool Enabled = true;

    Dictionary<(uint mods, uint vk), Action> _hotkeys = new();

    Thread? _thread;
    IntPtr _mouseHook, _kbHook;
    LowLevelMouseProc? _mouseProc;   // kept alive for the GC
    LowLevelProc? _kbProc;
    uint _threadId;
    bool _swallowUp;                 // swallow the right-button-up matching a swallowed down
    uint _swallowKeyUp;              // swallow the key-up matching a swallowed hotkey down
    volatile bool _maskAltUp;        // next Alt release gets a Ctrl tap in front of it

    public void SetHotkeys(IEnumerable<(uint mods, Keys key, Action action)> list)
    {
        var d = new Dictionary<(uint, uint), Action>();
        foreach (var (mods, key, action) in list) d[(mods, (uint)key)] = action;
        _hotkeys = d;   // reference swap: hook thread reads the new table atomically
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = Brand.Name + "Hooks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    void Loop()
    {
        _mouseProc = MouseCallback;
        _kbProc = KeyCallback;
        _threadId = GetCurrentThreadId();
        var mod = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
        _kbHook = SetWindowsHookExK(WH_KEYBOARD_LL, _kbProc, mod, 0);
        Log.Write($"hooks installed mouse=0x{_mouseHook:X} keyboard=0x{_kbHook:X}");
        Application.Run();
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_kbHook != IntPtr.Zero) UnhookWindowsHookEx(_kbHook);
    }

    static uint CurrentMods()
    {
        uint m = 0;
        if (KeyDown(VK_CONTROL)) m |= MOD_CONTROL;
        if (KeyDown(VK_MENU)) m |= MOD_ALT;
        if (KeyDown(VK_SHIFT)) m |= MOD_SHIFT;
        if (KeyDown(VK_LWIN) || KeyDown(VK_RWIN)) m |= MOD_WIN;
        return m;
    }

    IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.dwExtraInfo == Magic)
            {
                // our own injected event: let it through, but note when it already released Alt
                if (k.vkCode is VK_MENU or VK_LMENU or VK_RMENU && ((int)wParam is WM_KEYUP or WM_SYSKEYUP)) _maskAltUp = false;
                return CallNextHookEx(_kbHook, nCode, wParam, lParam);
            }
            int msg = (int)wParam;
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;

            if (up && _swallowKeyUp != 0 && k.vkCode == _swallowKeyUp)
            {
                _swallowKeyUp = 0;
                return (IntPtr)1;
            }

            if (up && _maskAltUp && k.vkCode is VK_MENU or VK_LMENU or VK_RMENU)
            {
                // Re-issue as: Ctrl down, Ctrl up, Alt up  (all stamped) so no app enters menu mode.
                _maskAltUp = false;
                Input.MaskedAltRelease((ushort)k.vkCode);
                return (IntPtr)1;
            }

            if (down && Enabled)
            {
                var vk = k.vkCode;
                if (vk is not (VK_CONTROL or VK_MENU or VK_SHIFT or VK_LWIN or VK_RWIN or VK_LMENU or VK_RMENU or VK_LCONTROL or VK_RCONTROL or 0xA0 or 0xA1))
                {
                    var mods = CurrentMods();
                    if (mods != 0 && _hotkeys.TryGetValue((mods, vk), out var action))
                    {
                        _swallowKeyUp = vk;
                        if ((mods & MOD_ALT) != 0) _maskAltUp = true;
                        HotkeyTriggered?.Invoke(action);
                        return (IntPtr)1;
                    }
                }
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Enabled)
        {
            int msg = (int)wParam;
            if (msg == WM_RBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if (info.dwExtraInfo != Magic)
                {
                    bool ctrl = KeyDown(VK_CONTROL), alt = KeyDown(VK_MENU);
                    bool shift = KeyDown(VK_SHIFT), win = KeyDown(VK_LWIN) || KeyDown(VK_RWIN);
                    bool hit = !shift && !win && !(ctrl && alt) && ((ctrl && UseCtrl) || (alt && UseAlt));
                    if (hit)
                    {
                        _swallowUp = true;
                        if (alt) _maskAltUp = true;
                        var fg = GetForegroundWindow();
                        var pt = new Point(info.pt.X, info.pt.Y);
                        MenuTriggered?.Invoke(fg, pt);
                        return (IntPtr)1;
                    }
                }
            }
            else if (msg == WM_RBUTTONUP && _swallowUp)
            {
                _swallowUp = false;
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }
}
