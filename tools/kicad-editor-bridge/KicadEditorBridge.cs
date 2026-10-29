// kicad-editor-bridge: lets Konnect write a schematic while KiCad's Schematic Editor has it open.
//
// KiCad 10's IPC API cannot save or revert a schematic, so this drives the editor's own native
// File menu (WM_COMMAND to the menu item IDs, no keystrokes, no focus stealing):
//
//   kicad-editor-bridge status <file.kicad_sch>   is the editor open / dirty / blocked by a dialog
//   kicad-editor-bridge save   <file.kicad_sch>   File > Save if there are unsaved changes, wait for "*" to clear
//   kicad-editor-bridge reload <file.kicad_sch>   File > Revert, answer Yes on KiCad's confirmation, wait
//
// Every command prints one JSON object on stdout and exits 0 on success, 1 on refusal/failure.
// Refusals are deliberate: a modal dialog already open, unsaved edits before a reload (that would
// discard the user's work), or an unexpected dialog. The bridge only ever clicks a dialog that
// appeared after it posted Revert itself.
//
// Build (no SDK needed, .NET Framework compiler ships with Windows):
//   C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize /out:kicad-editor-bridge.exe KicadEditorBridge.cs
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

internal static class Native
{
    public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] public static extern IntPtr GetMenu(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int GetMenuItemCount(IntPtr menu);
    [DllImport("user32.dll")] public static extern IntPtr GetSubMenu(IntPtr menu, int pos);
    [DllImport("user32.dll")] public static extern uint GetMenuItemID(IntPtr menu, int pos);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetMenuString(IntPtr menu, uint item, StringBuilder sb, int max, uint flags);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeoutMs, out IntPtr result);
    public const uint WM_COMMAND = 0x0111, BM_CLICK = 0x00F5, TDM_CLICK_BUTTON = 0x0400 + 102;
    public const uint GW_OWNER = 4, MF_BYPOSITION = 0x400, SMTO_ABORTIFHUNG = 0x2;
    public const int IDYES = 6;
}

internal sealed class Win
{
    public IntPtr Hwnd; public uint Pid; public string Title; public string Class; public string Process;
    public static string TextOf(IntPtr h) { var sb = new StringBuilder(1024); Native.GetWindowText(h, sb, 1024); return sb.ToString(); }
    public static string ClassOf(IntPtr h) { var sb = new StringBuilder(256); Native.GetClassName(h, sb, 256); return sb.ToString(); }
    public static List<Win> TopLevel()
    {
        var list = new List<Win>();
        Native.EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (!Native.IsWindowVisible(h)) return true;
            uint pid; Native.GetWindowThreadProcessId(h, out pid);
            var w = new Win { Hwnd = h, Pid = pid, Title = TextOf(h), Class = ClassOf(h) };
            try { w.Process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { w.Process = ""; }
            list.Add(w);
            return true;
        }, IntPtr.Zero);
        return list;
    }
}

internal static class Bridge
{
    static readonly Regex EditorTitle = new Regex(@"^(\*?)(.+?) (\u2014|-) Schematic Editor$");

    static string Json(Dictionary<string, object> d)
    {
        var sb = new StringBuilder("{");
        bool first = true;
        foreach (var kv in d)
        {
            if (!first) sb.Append(",");
            first = false;
            sb.Append("\"").Append(kv.Key).Append("\":");
            object v = kv.Value;
            if (v == null) sb.Append("null");
            else if (v is bool) sb.Append((bool)v ? "true" : "false");
            else if (v is int || v is long || v is uint) sb.Append(v.ToString());
            else sb.Append("\"").Append(v.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")).Append("\"");
        }
        return sb.Append("}").ToString();
    }

    static int Emit(bool ok, Dictionary<string, object> d)
    {
        d["ok"] = ok;
        Console.WriteLine(Json(d));
        return ok ? 0 : 1;
    }

    static Win FindEditor(string schematic, out string error)
    {
        error = null;
        string stem = Path.GetFileNameWithoutExtension(schematic);
        var hits = new List<Win>();
        foreach (var w in Win.TopLevel())
        {
            var m = EditorTitle.Match(w.Title);
            if (!m.Success) continue;
            if (!string.Equals(m.Groups[2].Value, stem, StringComparison.OrdinalIgnoreCase)) continue;
            string p = (w.Process ?? "").ToLowerInvariant();
            if (p != "kicad" && p != "eeschema") continue;
            hits.Add(w);
        }
        if (hits.Count == 0) { error = "no_editor"; return null; }
        if (hits.Count > 1) { error = "ambiguous_editor"; return null; }
        return hits[0];
    }

    static bool IsDirty(IntPtr hwnd) { return Win.TextOf(hwnd).StartsWith("*"); }

    // True once the editor has been enabled, with no KiCad windows beyond `baseline`, for `quietMs`.
    static bool WaitStable(Win w, HashSet<long> baseline, int quietMs, int timeoutMs)
    {
        var total = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        while (total.ElapsedMilliseconds < timeoutMs)
        {
            bool calm = Native.IsWindow(w.Hwnd) && Native.IsWindowEnabled(w.Hwnd);
            if (calm)
                foreach (var x in DialogsOf(w.Pid, w.Hwnd))
                    if (!baseline.Contains(x)) { calm = false; break; }
            if (!calm) quiet.Restart();
            else if (quiet.ElapsedMilliseconds >= quietMs) return true;
            Thread.Sleep(100);
        }
        return false;
    }

    static uint FindMenuItem(IntPtr frame, string topLabel, string itemLabel)
    {
        IntPtr bar = Native.GetMenu(frame);
        if (bar == IntPtr.Zero) return 0;
        int n = Native.GetMenuItemCount(bar);
        for (int i = 0; i < n; i++)
        {
            var sb = new StringBuilder(256);
            Native.GetMenuString(bar, (uint)i, sb, 256, Native.MF_BYPOSITION);
            if (sb.ToString().Replace("&", "") != topLabel) continue;
            IntPtr sub = Native.GetSubMenu(bar, i);
            int m = Native.GetMenuItemCount(sub);
            for (int j = 0; j < m; j++)
            {
                var s2 = new StringBuilder(256);
                Native.GetMenuString(sub, (uint)j, s2, 256, Native.MF_BYPOSITION);
                string label = s2.ToString().Split('\t')[0].Replace("&", "").Trim();
                if (label == itemLabel) return Native.GetMenuItemID(sub, j);
            }
        }
        return 0;
    }

    static HashSet<long> DialogsOf(uint pid, IntPtr frame)
    {
        var set = new HashSet<long>();
        foreach (var w in Win.TopLevel())
            if (w.Pid == pid && w.Hwnd != frame) set.Add(w.Hwnd.ToInt64());
        return set;
    }

    static bool WaitFor(Func<bool> cond, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs) { if (cond()) return true; Thread.Sleep(100); }
        return cond();
    }

    static Dictionary<string, object> Describe(Win w)
    {
        var d = new Dictionary<string, object>();
        d["editor"] = w != null;
        if (w != null)
        {
            d["hwnd"] = w.Hwnd.ToInt64();
            d["pid"] = w.Pid;
            d["process"] = w.Process;
            d["title"] = Win.TextOf(w.Hwnd);
            d["dirty"] = IsDirty(w.Hwnd);
            d["dialog_open"] = !Native.IsWindowEnabled(w.Hwnd);
        }
        return d;
    }

    static int Status(string sch)
    {
        string err; var w = FindEditor(sch, out err);
        var d = Describe(w);
        if (err != null && err != "no_editor") d["error"] = err;
        return Emit(err == null || err == "no_editor", d);
    }

    static int Save(string sch, bool force)
    {
        string err; var w = FindEditor(sch, out err);
        var d = Describe(w);
        if (w == null) { d["error"] = err; return Emit(false, d); }
        if (!Native.IsWindowEnabled(w.Hwnd)) { d["error"] = "dialog_open"; return Emit(false, d); }
        if (!force && !IsDirty(w.Hwnd)) { d["action"] = "none_needed"; return Emit(true, d); }
        uint id = FindMenuItem(w.Hwnd, "File", "Save");
        if (id == 0) { d["error"] = "save_menu_not_found"; return Emit(false, d); }
        var before = DialogsOf(w.Pid, w.Hwnd);
        Native.PostMessage(w.Hwnd, Native.WM_COMMAND, new IntPtr((int)id), IntPtr.Zero);
        bool done = WaitFor(delegate { return !IsDirty(w.Hwnd) && Native.IsWindowEnabled(w.Hwnd); }, 20000);
        foreach (var x in DialogsOf(w.Pid, w.Hwnd))
            if (!before.Contains(x)) { d["error"] = "unexpected_dialog"; d["dialog_title"] = Win.TextOf(new IntPtr(x)); return Emit(false, d); }
        d["action"] = "saved";
        d["dirty"] = IsDirty(w.Hwnd);
        if (!done) { d["error"] = "save_timeout"; return Emit(false, d); }
        if (force) Thread.Sleep(300);
        return Emit(true, d);
    }

    static bool Gone(IntPtr dlg) { return !Native.IsWindow(dlg) || !Native.IsWindowVisible(dlg); }

    // Returns the method that dismissed the dialog with Yes, or null.
    static string ClickYes(IntPtr dlg)
    {
        IntPtr res;
        // 1. A real "Yes" button child (classic message box, or a wx-drawn dialog): click it.
        IntPtr yes = IntPtr.Zero;
        Native.EnumChildWindows(dlg, delegate (IntPtr h, IntPtr l)
        {
            string t = Win.TextOf(h).Replace("&", "");
            if (Win.ClassOf(h) == "Button" && (t == "Yes" || t == "OK")) { yes = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (yes != IntPtr.Zero)
        {
            Native.PostMessage(yes, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
            if (WaitFor(delegate { return Gone(dlg); }, 1500)) return "button";
        }
        // 2. Task dialog (no button windows): TDM_CLICK_BUTTON.
        Native.SendMessageTimeout(dlg, Native.TDM_CLICK_BUTTON, new IntPtr(Native.IDYES), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 1000, out res);
        if (WaitFor(delegate { return Gone(dlg); }, 800)) return "task_dialog";
        // 3. Message box without findable buttons: WM_COMMAND IDYES.
        Native.PostMessage(dlg, Native.WM_COMMAND, new IntPtr(Native.IDYES), IntPtr.Zero);
        if (WaitFor(delegate { return Gone(dlg); }, 800)) return "wm_command";
        return null;
    }

    static int Reload(string sch)
    {
        string err; var w = FindEditor(sch, out err);
        var d = Describe(w);
        if (w == null) { d["error"] = err; return Emit(false, d); }
        if (!Native.IsWindowEnabled(w.Hwnd)) { d["error"] = "dialog_open"; return Emit(false, d); }
        if (IsDirty(w.Hwnd)) { d["error"] = "unsaved_changes_in_editor"; return Emit(false, d); }
        uint id = FindMenuItem(w.Hwnd, "File", "Revert");
        if (id == 0) { d["error"] = "revert_menu_not_found"; return Emit(false, d); }
        var before = DialogsOf(w.Pid, w.Hwnd);
        Native.PostMessage(w.Hwnd, Native.WM_COMMAND, new IntPtr((int)id), IntPtr.Zero);

        IntPtr confirm = IntPtr.Zero;
        WaitFor(delegate
        {
            foreach (var x in DialogsOf(w.Pid, w.Hwnd))
                if (!before.Contains(x)) { confirm = new IntPtr(x); return true; }
            return false;
        }, 6000);
        if (confirm == IntPtr.Zero) { d["error"] = "revert_confirmation_not_seen"; return Emit(false, d); }
        d["confirm_title"] = Win.TextOf(confirm);
        var clock = Stopwatch.StartNew();
        string method = ClickYes(confirm);
        d["confirm_method"] = method;
        d["confirm_ms"] = clock.ElapsedMilliseconds;
        if (method == null) { d["error"] = "could_not_confirm_revert"; return Emit(false, d); }

        // KiCad re-enables the frame as soon as the confirmation closes, then disables it again while
        // it reloads behind a progress window. Done = enabled, with no extra windows from KiCad,
        // continuously for a full second.
        bool settled = WaitStable(w, before, 1000, 30000);
        d["reload_ms"] = clock.ElapsedMilliseconds;
        d["action"] = "reloaded";
        d["title"] = Win.TextOf(w.Hwnd);
        d["dirty"] = IsDirty(w.Hwnd);
        if (!settled) { d["error"] = "reload_timeout"; return Emit(false, d); }
        return Emit(true, d);
    }

    static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: kicad-editor-bridge status|save|reload <file.kicad_sch> [--force]");
            return 2;
        }
        string cmd = args[0].ToLowerInvariant();
        string sch = args[1];
        bool force = Array.IndexOf(args, "--force") >= 0;
        try
        {
            switch (cmd)
            {
                case "status": return Status(sch);
                case "save": return Save(sch, force);
                case "reload": return Reload(sch);
                default: Console.Error.WriteLine("unknown command " + cmd); return 2;
            }
        }
        catch (Exception e)
        {
            var d = new Dictionary<string, object>();
            d["error"] = "exception";
            d["message"] = e.Message;
            return Emit(false, d);
        }
    }
}
