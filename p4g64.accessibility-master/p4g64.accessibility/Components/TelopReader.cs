using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// TELOP caption reader (2026-08-01). "Telop" = the game's broadcast-style
/// subtitle captions (big outlined text, no message window) used by in-engine
/// event scenes — the Miracle Quiz intro/banter, and any other scene rendered
/// through the <b>fnt_telop</b> task. These never pass MsgWindow::DrawDialog,
/// so the Dialogue reader can't see them (user-proven via Shift+P 2026-07-31).
///
/// RE map (live snapshot diffs on the quiz intro, 2026-07-31/08-01):
///  - Task <b>fnt_telop</b> work <b>+0x10</b> → linked list (head+0x10 = first
///    node; node+0x18 = next, node+0x20 = ITEM). One item PER DISPLAYED
///    CAPTION (spawned per line, freed after).
///  - item <b>+0x08</b> = ptr to the BMD CONTAINER item; the container embeds
///    a RELOCATED in-memory MSG1 at <b>+0x18</b> (magic at +0x20; the event's
///    localized message file — E070_501 intro = 68 "MSG_NNN" dialogs).
///  - item <b>+0x24</b> (int) = DIALOG INDEX of this caption (verified 1→3
///    against the on-screen lines; +0x38 = wrapped line count, also matched).
///  - Relocated BMD walk: DialogHeaders @bmd+0x30 (stride 0x10, MessageDialog*
///    @+8, absolute); MessageDialog: name[24], PageCount @+0x18, Pages @+0x20
///    ({Text*, TextSize} per 0x10). Text = opcode-stripped Atlus bytes
///    (opcodes (b&0xF0)==0xF0, skip 2+((b&0xF)-1)*2 — args would misdecode).
///
/// Voice-mode aware like the Dialogue reader: captions are voiced scenes, so
/// when the user is in voice mode the text is RECORDED to history only.
/// </summary>
internal sealed unsafe class TelopReader
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] TaskName = System.Text.Encoding.ASCII.GetBytes("fnt_telop");

    private bool _active;
    // Recently spoken captions (item addr, dialog idx) — items are per-line
    // allocations, so a new line is always a new key.
    private readonly Queue<(nint item, int idx)> _spoken = new();

    internal TelopReader()
    {
        new Thread(Poll) { IsBackground = true, Name = "TelopReader" }.Start();
        Log("[Telop] caption reader ready (fnt_telop task)");
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(120);
            try { Tick(); } catch { /* poll must never die */ }
        }
    }

    private void Tick()
    {
        nint work = FindWork();
        bool active = work != 0;
        if (active != _active)
        {
            _active = active;
            if (!active) _spoken.Clear();
            return;
        }
        if (!active || !GameHasFocus()) return;

        // work+0x10 → list head; head+0x10 → first node
        nint list, node;
        if (!TryReadRaw(work + 0x10, &list, 8) || list == 0) return;
        if (!TryReadRaw(list + 0x10, &node, 8)) return;

        var nd = stackalloc byte[0x28];
        for (int n = 0; n < 8 && node != 0; n++)
        {
            if (!TryReadRaw(node, nd, 0x28)) break;
            nint item = *(nint*)(nd + 0x20);
            node = *(nint*)(nd + 0x18);
            if (item == 0) continue;

            var it = stackalloc byte[0x28];
            if (!TryReadRaw(item, it, 0x28)) continue;
            nint container = *(nint*)(it + 0x08);
            int idx = *(int*)(it + 0x24);
            if (container == 0 || idx < 0 || idx > 2000) continue;

            bool seen = false;
            foreach (var k in _spoken)
                if (k.item == item && k.idx == idx) { seen = true; break; }
            if (seen) continue;

            string text = DecodeDialog(container + 0x18, idx);
            _spoken.Enqueue((item, idx));
            while (_spoken.Count > 12) _spoken.Dequeue();
            if (text.Length == 0) continue;

            Log($"[Telop] [{idx}] {text}");
            if (Dialogue.ReaderEnabled) Speech.Say(text, true);
            else Speech.Record(text);   // voice mode: history only, the VA plays
        }
    }

    /// <summary>Decode dialog <paramref name="idx"/> of a RELOCATED in-memory
    /// MSG1 at <paramref name="bmd"/> (absolute pointers). Empty string on any
    /// validation failure — never throws, never AVs (guarded reads only).</summary>
    private static string DecodeDialog(nint bmd, int idx)
    {
        var hdr = stackalloc byte[0x30];
        if (!TryReadRaw(bmd, hdr, 0x30)) return "";
        if (*(uint*)(hdr + 8) != 0x3147534D) return "";        // "MSG1"
        int count = *(int*)(hdr + 0x18);
        if (count <= 0 || count > 4000 || idx >= count) return "";

        var dh = stackalloc byte[0x10];
        if (!TryReadRaw(bmd + 0x30 + idx * 0x10, dh, 0x10)) return "";
        if (*(int*)dh != 0) return "";                          // Message kind only
        nint msg = *(nint*)(dh + 8);
        if (msg == 0) return "";

        var md = stackalloc byte[0x20];
        if (!TryReadRaw(msg, md, 0x20)) return "";
        int pages = *(ushort*)(md + 0x18);
        if (pages < 1 || pages > 8) return "";

        var sb = new System.Text.StringBuilder();
        var page = stackalloc byte[0x10];
        for (int p = 0; p < pages; p++)
        {
            if (!TryReadRaw(msg + 0x20 + p * 0x10, page, 0x10)) break;
            nint tptr = *(nint*)page;
            int tsize = *(int*)(page + 8);
            if (tptr == 0 || tsize <= 0 || tsize > 4000) continue;
            var buf = new byte[tsize];
            fixed (byte* pb = buf)
                if (!TryReadRaw(tptr, pb, tsize)) continue;
            AppendDecoded(sb, buf);
        }
        return string.Join(" ", sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Opcode-strip + Atlus-decode raw message bytes into sb.</summary>
    private static void AppendDecoded(System.Text.StringBuilder sb, byte[] data)
    {
        var chunk = new List<byte>();
        void Flush()
        {
            if (chunk.Count == 0) return;
            string s = Native.Text.AtlusEncoding.P4.GetString(chunk.ToArray()).Replace("\0", "").Trim();
            if (s.Length > 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(s); }
            chunk.Clear();
        }
        int i = 0, guard = 0;
        while (i < data.Length && guard++ < 4000)
        {
            byte b = data[i];
            if (b == 0) break;
            if ((b & 0xF0) == 0xF0) { i += 2 + ((b & 0x0F) - 1) * 2; continue; }
            if (b == 0x0A) { Flush(); i++; continue; }
            if (b < 0x80) { chunk.Add(b); i++; }
            else
            {
                if (i + 1 >= data.Length) break;
                chunk.Add(b); chunk.Add(data[i + 1]); i += 2;
            }
        }
        Flush();
    }

    private static nint FindWork()
    {
        byte* probe = stackalloc byte[0x58];
        foreach (nint head in TaskHeads)
        {
            nint node;
            if (!TryReadRaw(head, &node, 8)) continue;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!TryReadRaw(node, probe, 0x58)) break;
                bool match = true;
                for (int k = 0; k < TaskName.Length; k++)
                    if (probe[k] != TaskName[k]) { match = false; break; }
                // Non-printable terminator rule; must NOT match "fnt_telop_res".
                if (match && probe[TaskName.Length] < 0x20)
                    return *(nint*)(probe + 0x48);
                node = *(nint*)(probe + 0x50);
            }
        }
        return 0;
    }
}
