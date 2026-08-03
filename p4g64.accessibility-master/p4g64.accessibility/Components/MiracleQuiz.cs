using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// The MIRACLE QUIZ minigame reader (2026-07-31). The TV-Listings quiz show's
/// questions and choices are UNVOICED baked message windows (the story banter
/// around them is voiced — untouched). All 186 questions + their 4 choices are
/// baked offline from quiz.arc/msg_quiz.bmd into tvlistings_catalog.json
/// ("quiz_questions", built by database/tools/build_quiz_questions.py), so the
/// reader is a pure table lookup — zero capture latency against the show timer
/// (the timer has its own audio cue; no timer feature needed).
///
/// RE map (5-snapshot live hunt, 2026-07-31):
///  - Task <b>ch_quiz</b> (named-task registry) exists while the show runs;
///    its WORK struct carries everything:
///    +0x0A u16 = current question record, ZERO-BASED (11 = QUIZ_012)
///    +0x0C u16 = on-screen question number ("Q2")
///    +0x12 u16 = answer cursor 0..3 in SCREEN LETTER order A,B,C,D
///    +0x56 u16[4] = the per-showing SHUFFLE: letter k shows BMD choice
///                   perm[k]. The game randomizes letter positions each time.
///  - ⚠ SPOILER RULE: BMD choice[0] is ALWAYS the correct answer — never
///    announce choices in BMD order; always speak through the permutation.
///  - A valid permutation of {0,1,2,3} doubles as the "question is staged"
///    gate (announce trigger); during intro/transitions it isn't one.
///  - F re-reads the current question + choices.
///
/// LANGUAGE SUPPORT (2026-07-31, user ask): the primary text source is the
/// game's OWN loaded msg_quiz BMD — a raw unrelocated MSG1 file image pointed
/// to by <b>work+0x3B8</b> (+0x3C0 system, +0x3C8 banter; live-verified) — so
/// non-English installs hear their localized questions. Layout: dialog table
/// at +0x20 ({u32 kind, u32 offset} pairs, offsets relative to +0x20), dialog
/// idx = rec*2 (QUIZ_NNN) / rec*2+1 (ANSWER_NNN, choices = its 0x0A-split
/// lines in BMD order). Function opcodes ((b&0xF0)==0xF0, skip 2+((b&0xF)-1)*2
/// bytes) are stripped BEFORE AtlusEncoding decode (their args would misdecode
/// as glyphs). The baked English catalog is the FALLBACK only.
/// </summary>
internal sealed unsafe class MiracleQuiz
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] TaskName = System.Text.Encoding.ASCII.GetBytes("ch_quiz");

    private const int VK_F = 0x46;

    private readonly List<(string q, string[] c)> _questions = new();

    private bool _active;
    private int _spokenRec = -1;     // record already announced (re-armed on change)
    private int _lastCursor;
    private bool _fWas;

    // The choice texts (BMD order) of the announced question — the cursor
    // announcer must use exactly what was spoken (live or fallback source).
    private string[] _curChoices = Array.Empty<string>();

    // Cached copy of the game's loaded msg_quiz MSG1 image (localized).
    private nint _bmdCached;
    private byte[]? _img;

    internal MiracleQuiz()
    {
        LoadQuestions();
        new Thread(Poll) { IsBackground = true, Name = "MiracleQuiz" }.Start();
        Log($"[Quiz] Miracle Quiz reader ready ({_questions.Count} questions; ch_quiz task)");
    }

    private void LoadQuestions()
    {
        try
        {
            string path = DataPath("tvlistings_catalog.json");
            if (path == null || !File.Exists(path)) { Log("[Quiz] no catalog json"); return; }
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("quiz_questions", out var arr)) return;
            foreach (var e in arr.EnumerateArray())
            {
                string q = e.GetProperty("q").GetString() ?? "";
                var c = new List<string>();
                foreach (var ch in e.GetProperty("c").EnumerateArray())
                    c.Add(ch.GetString() ?? "");
                _questions.Add((q, c.ToArray()));
            }
        }
        catch (Exception ex) { Log($"[Quiz] catalog load failed: {ex.Message}"); }
    }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(100);
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
            _spokenRec = -1;
            _lastCursor = 0;
            _fWas = IsKeyDown(VK_F);          // ghost-key guard
            if (!active) { _bmdCached = 0; _img = null; _curChoices = Array.Empty<string>(); }
#if DEBUG
            Log($"[QuizDiag] task {(active ? "appeared" : "gone")}");
#endif
            return;
        }
        if (!active || !GameHasFocus() || SettingsMenu.IsOpen) return;

        var st = stackalloc byte[0x60];
        if (!TryReadRaw(work, st, 0x60)) return;
        int rec = *(ushort*)(st + 0x0A);
        int qn = *(ushort*)(st + 0x0C);
        int cursor = *(ushort*)(st + 0x12);
        Span<int> perm = stackalloc int[4];
        int permMask = 0;
        for (int k = 0; k < 4; k++)
        {
            perm[k] = *(ushort*)(st + 0x56 + k * 2);
            if (perm[k] < 4) permMask |= 1 << perm[k];
        }
        bool staged = permMask == 0xF && rec >= 0 && rec < 400;

        if (!staged) return;   // keep _spokenRec latched — a perm flicker must not re-announce

        if (rec != _spokenRec)
        {
            // Live localized text first; the baked English catalog is the fallback.
            bool live = TryLiveTexts(work, rec, out string q, out string[] c);
            if (!live)
            {
                if (rec >= _questions.Count) return;
                (q, c) = _questions[rec];
            }
            if (c.Length != 4) return;
            _spokenRec = rec;
            _lastCursor = cursor;
            _curChoices = c;
            string line = $"Question {qn}. {q}";
            for (int k = 0; k < 4; k++)
                line += $" {(char)('A' + k)}: {c[perm[k]]}.";
#if DEBUG
            Log($"[QuizDiag] rec={rec} qn={qn} perm={perm[0]}{perm[1]}{perm[2]}{perm[3]} src={(live ? "live" : "catalog")}");
#endif
            Speech.Say(line, true);
            return;
        }

        if (cursor != _lastCursor)
        {
            _lastCursor = cursor;
            if (cursor >= 0 && cursor < 4 && _curChoices.Length == 4)
                Speech.Say($"{(char)('A' + cursor)}: {_curChoices[perm[cursor]]}.", true);
            return;
        }

        bool f = IsKeyDown(VK_F);
        if (f && !_fWas) _spokenRec = -1;      // re-arm → next tick re-reads
        _fWas = f;
    }

    // ── Live localized text (the game's own loaded msg_quiz MSG1 image) ──

    private bool TryLiveTexts(nint work, int rec, out string q, out string[] choices)
    {
        q = "";
        choices = Array.Empty<string>();
        try
        {
            nint bmd;
            if (!TryReadRaw(work + 0x3B8, &bmd, 8) || bmd == 0) return false;
            var hdr = stackalloc byte[0x20];
            if (!TryReadRaw(bmd, hdr, 0x20)) return false;
            if (*(uint*)(hdr + 8) != 0x3147534D) return false;          // "MSG1"
            int size = *(int*)(hdr + 4);
            int count = *(int*)(hdr + 0x18);
            if (size < 0x40 || size > 0x100000 || rec * 2 + 1 >= count) return false;

            if (bmd != _bmdCached || _img == null || _img.Length != size)
            {
                var buf = new byte[size];
                fixed (byte* pb = buf)
                    if (!TryReadRaw(bmd, pb, size)) return false;
                _img = buf;
                _bmdCached = bmd;
            }

            var qLines = DialogLines(rec * 2, "QUIZ_");
            var aLines = DialogLines(rec * 2 + 1, "ANSWER_");
            if (qLines == null || qLines.Count == 0 || aLines == null || aLines.Count < 4) return false;
            q = string.Join(" ", qLines);
            choices = new[] { aLines[0], aLines[1], aLines[2], aLines[3] };
            return q.Length > 0 && choices.All(s => s.Length > 0);
        }
        catch { return false; }
    }

    /// <summary>Decoded, opcode-stripped lines of dialog <paramref name="idx"/>
    /// (0x0A-split), or null if the entry doesn't parse / isn't <paramref name="prefix"/>.</summary>
    private List<string>? DialogLines(int idx, string prefix)
    {
        var img = _img!;
        const int BASE = 0x20;
        int entry = BASE + idx * 8;
        if (entry + 8 > img.Length) return null;
        int pos = BASE + BitConverter.ToInt32(img, entry + 4);
        if (pos < BASE || pos + 28 > img.Length) return null;

        // name[24], ASCII, must match the expected record kind
        int nameLen = 0;
        while (nameLen < 24 && img[pos + nameLen] != 0) nameLen++;
        string name = System.Text.Encoding.ASCII.GetString(img, pos, nameLen);
        if (!name.StartsWith(prefix)) return null;

        int pages = BitConverter.ToUInt16(img, pos + 24);
        if (pages < 1 || pages > 8 || pos + 28 + pages * 4 > img.Length) return null;

        var lines = new List<string>();
        var cur = new List<byte>();
        void Flush()
        {
            if (cur.Count == 0) return;
            string s = Native.Text.AtlusEncoding.P4.GetString(cur.ToArray()).Replace("\0", "").Trim();
            if (s.Length > 0) lines.Add(s);
            cur.Clear();
        }
        for (int p = 0; p < pages; p++)
        {
            int t = BASE + BitConverter.ToInt32(img, pos + 28 + p * 4);
            if (t < BASE || t >= img.Length) return null;
            int i = t, guard = 0;
            while (i < img.Length && guard++ < 4000)
            {
                byte b = img[i];
                if (b == 0) break;
                if ((b & 0xF0) == 0xF0) { i += 2 + ((b & 0x0F) - 1) * 2; continue; }   // opcode + args
                if (b == 0x0A) { Flush(); i++; continue; }
                if (b < 0x80) { cur.Add(b); i++; }
                else
                {
                    if (i + 1 >= img.Length) break;
                    cur.Add(b); cur.Add(img[i + 1]); i += 2;
                }
            }
            Flush();
        }
        return lines;
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
                // Exact-name rule: terminator is NON-PRINTABLE, not always NUL —
                // and "ch_quiz" must not match "ch_quiz_answer" (probe[7] < 0x20).
                if (match && probe[TaskName.Length] < 0x20)
                    return *(nint*)(probe + 0x48);
                node = *(nint*)(probe + 0x50);
            }
        }
        return 0;
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
