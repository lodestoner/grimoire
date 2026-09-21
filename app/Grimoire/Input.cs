using System.Runtime.InteropServices;
using static Spellbook.Native;

namespace Spellbook;

/// <summary>Synthesized keyboard input (SendInput) and foreground-window control.</summary>
internal static class Input
{
    static INPUT Key(ushort vk, bool up)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wVk = vk;
        i.u.ki.wScan = vk == VK_MASK ? (ushort)0 : (ushort)MapVirtualKey(vk, 0);
        i.u.ki.dwFlags = up ? KEYEVENTF_KEYUP : 0;
        i.u.ki.dwExtraInfo = Magic;
        return i;
    }

    static INPUT Unicode(char c, bool up)
    {
        var i = new INPUT { type = INPUT_KEYBOARD };
        i.u.ki.wScan = c;
        i.u.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
        i.u.ki.dwExtraInfo = Magic;
        return i;
    }

    static void Send(params INPUT[] inputs)
    {
        if (inputs.Length == 0) return;
        var n = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        if (n != inputs.Length) Log.Write($"[x] SendInput sent {n}/{inputs.Length} (err {Marshal.GetLastWin32Error()})");
    }

    /// <summary>
    /// A reserved virtual key (0x07) no app reacts to. Tapping it while Alt is down tells Windows
    /// "another key was pressed", so the following Alt release does not activate the menu bar.
    /// (Ctrl cannot serve as the mask when it is already held, as with Ctrl+Alt chords.)
    /// </summary>
    static readonly ushort VK_MASK = ushort.TryParse(Environment.GetEnvironmentVariable(Brand.EnvPrefix + "MASK"),
        System.Globalization.NumberStyles.HexNumber, null, out var mk) ? mk : (ushort)0x07;

    /// <summary>Alt release preceded by the mask tap, so the app does not enter menu-bar mode.</summary>
    public static void MaskedAltRelease(ushort altVk) =>
        Send(Key(VK_MASK, false), Key(VK_MASK, true), Key(altVk, true));

    /// <summary>Release any held modifiers so a synthesized Ctrl+C is not read as Ctrl+Alt+C.</summary>
    public static void ReleaseModifiers()
    {
        var list = new List<INPUT>();
        bool alt = KeyDown(VK_MENU) || KeyDown(VK_LMENU) || KeyDown(VK_RMENU);
        if (alt) { list.Add(Key(VK_MASK, false)); list.Add(Key(VK_MASK, true)); }   // menu mask
        foreach (var vk in new ushort[] { VK_LMENU, VK_RMENU, VK_MENU, VK_LCONTROL, VK_RCONTROL, VK_CONTROL, 0xA0, 0xA1, VK_SHIFT, VK_LWIN, VK_RWIN })
            if (KeyDown(vk)) list.Add(Key(vk, true));
        if (list.Count > 0) { Send(list.ToArray()); Thread.Sleep(40); }
    }

    public static void Combo(ushort modifier, ushort key)
    {
        ReleaseModifiers();
        Send(Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true));
    }

    /// <summary>Diagnostics only: press a chord as if from a real keyboard (no Grimoire stamp), paced like a human.</summary>
    public static void SendChordUnstamped(params ushort[] vks)
    {
        foreach (var vk in vks) { var k = Key(vk, false); k.u.ki.dwExtraInfo = IntPtr.Zero; Send(k); Thread.Sleep(30); }
        foreach (var vk in vks.Reverse()) { var k = Key(vk, true); k.u.ki.dwExtraInfo = IntPtr.Zero; Send(k); Thread.Sleep(30); }
    }

    /// <summary>Diagnostics only: Ctrl + right-click at the current cursor position, as a real mouse would.</summary>
    public static void SendCtrlRightClickUnstamped()
    {
        var c = Key(VK_CONTROL, false); c.u.ki.dwExtraInfo = IntPtr.Zero; Send(c); Thread.Sleep(40);
        var d = new INPUT { type = 0 }; d.u.mi.dwFlags = 0x0008; Send(d); Thread.Sleep(40);   // MOUSEEVENTF_RIGHTDOWN
        var u = new INPUT { type = 0 }; u.u.mi.dwFlags = 0x0010; Send(u); Thread.Sleep(40);   // MOUSEEVENTF_RIGHTUP
        var cu = Key(VK_CONTROL, true); cu.u.ki.dwExtraInfo = IntPtr.Zero; Send(cu);
    }

    /// <summary>Diagnostics only: play a key sequence like a real keyboard. Tokens: DOWN UP LEFT RIGHT ENTER ESC TAB CTRLTAB, or wait:ms.</summary>
    public static void PlayKeysUnstamped(string sequence)
    {
        foreach (var tok in sequence.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (tok.StartsWith("wait:", StringComparison.OrdinalIgnoreCase)) { Thread.Sleep(int.Parse(tok[5..])); continue; }
            ushort vk = tok.ToUpperInvariant() switch
            {
                "DOWN" => 0x28, "UP" => 0x26, "LEFT" => 0x25, "RIGHT" => 0x27, "ENTER" => 0x0D, "ESC" => 0x1B, "TAB" => 0x09, "CTRLTAB" => 0x09,
                _ => tok.Length == 1 ? (ushort)char.ToUpperInvariant(tok[0]) : (ushort)0,
            };
            if (vk == 0) continue;
            bool ctrl = tok.Equals("CTRLTAB", StringComparison.OrdinalIgnoreCase);
            var list = new List<INPUT>();
            if (ctrl) { var c = Key(VK_CONTROL, false); c.u.ki.dwExtraInfo = IntPtr.Zero; list.Add(c); }
            var d = Key(vk, false); d.u.ki.dwExtraInfo = IntPtr.Zero; list.Add(d);
            var u = Key(vk, true); u.u.ki.dwExtraInfo = IntPtr.Zero; list.Add(u);
            if (ctrl) { var c = Key(VK_CONTROL, true); c.u.ki.dwExtraInfo = IntPtr.Zero; list.Add(c); }
            Send(list.ToArray());
            Thread.Sleep(90);
        }
    }

    public static void CtrlC() => Combo(VK_CONTROL, (ushort)'C');
    public static void CtrlV() => Combo(VK_CONTROL, (ushort)'V');

    public static void TypeText(string text)
    {
        ReleaseModifiers();
        var list = new List<INPUT>(text.Length * 2);
        foreach (var c in text)
        {
            if (c == '\r') continue;
            if (c == '\n') { list.Add(Key(VK_RETURN, false)); list.Add(Key(VK_RETURN, true)); continue; }
            list.Add(Unicode(c, false));
            list.Add(Unicode(c, true));
        }
        for (int i = 0; i < list.Count; i += 200)
            Send(list.Skip(i).Take(200).ToArray());
    }

    /// <summary>Bring the target window back to the foreground (after a menu/dialog stole it).</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        if (GetForegroundWindow() == hwnd) return;
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        uint me = GetCurrentThreadId();
        uint tgt = GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        if (fg != me) AttachThreadInput(me, fg, true);
        if (tgt != me) AttachThreadInput(me, tgt, true);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        if (fg != me) AttachThreadInput(me, fg, false);
        if (tgt != me) AttachThreadInput(me, tgt, false);
        Thread.Sleep(90);
    }
}
