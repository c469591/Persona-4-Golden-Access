using System.Text;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// Reads the S.Link ESTABLISH / MAX poem overlay ("Thou art I… And I am thou…
/// Thou hast established a new bond… Personas of the &lt;X&gt; Arcana…"). The poem
/// is a FIXED BMD message (MSG_CMM_RANKUP_OPEN / _MAX) whose only variable is
/// the arcana; it renders through a special cmm overlay that neither the
/// dialogue reader nor the shared UI-text fn touches, so text-capture failed.
///
/// RE (2026-07-07): the overlay is the task **cmmRankUpSequence** (created by
/// FUN_1401C26D0, update FUN_1401C14B0). Its work struct:
///   +0x1C (i32) COMMU INDEX  — into table *(0x1411A6B10), stride 100 (0x64);
///                              byte +0x10 of the row = the ARCANA number
///                              (standard tarot: Devil=15, card "XV" confirmed)
///   +0x20 (i32) RANK         — 1 = new bond (OPEN poem), 10 = MAX (genuine)
/// We poll for the task, read (rank, arcana) and speak the known poem once.
/// cmmRankUpSequence also runs for the mid-rank "Rank up!!" banner
/// (SocialLinkRankUp / cmmRankUp) — we only speak for rank 1 and rank 10, the
/// only ranks that show the poem, so there is no overlap.
/// </summary>
internal sealed unsafe class SocialLinkBond
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] SeqTask = Encoding.ASCII.GetBytes("cmmRankUpSequence");
    private static readonly byte[] BannerTask = Encoding.ASCII.GetBytes("cmmRankUp");
    private static readonly nint CommuTablePtrVA = unchecked((nint)0x1411A6B10L);

    private bool _wasUp;        // spoke the poem for the current sequence
    private bool _bannerSeen;   // the "Rank up!!" banner has appeared this sequence (MAX only)

    internal SocialLinkBond()
    {
        var t = new Thread(Poll) { IsBackground = true, Name = "SocialLinkBond" };
        t.Start();
        Log("[SLBond] ready");
    }

    internal SocialLinkBond(Reloaded.Hooks.Definitions.IReloadedHooks _) : this() { }

    private void Poll()
    {
        while (true)
        {
            Thread.Sleep(150);
            try
            {
                if (!GameHasFocus()) continue;
                Tick();
            }
            catch { }
        }
    }

    private void Tick()
    {
        nint task = FindTask(SeqTask);
        if (task == 0) { _wasUp = false; _bannerSeen = false; return; }   // sequence ended → reset
        if (!IsReadable(task + 0x48, 8)) return;
        nint work = *(nint*)(task + 0x48);
        if (work == 0 || !IsReadable(work, 0x28)) return;

        int f1C = *(int*)(work + 0x1C);
        int f20 = *(int*)(work + 0x20);   // 0 = FIRST-ESTABLISHMENT poem; 10 = MAX poem; 2..9 = mid-rank banner;
                                          // 11..14 = special sequence kinds (decompile FUN_1401C14B0: each has
                                          // its own poem sound cue 0x5DD/0x5DE/0x5FC — in P4G the only one that
                                          // ever fires is the ARCANA CHANGE overlay, Jester→Hunger).
        // Only the POEM screens speak: establishment (f20 == 0, "new bond"),
        // MAX (f20 == 10, "genuine bond… ultimate form" — live-verified
        // 2026-07-09: work+0x1C=7/Magician, work+0x20=10), and the ARCANA-CHANGE
        // poem (f20 > 10, gated below on the row actually being Adachi's
        // Jester/Hunger family so a leftover sequence kind can never misfire).
        // Mid-rank rank-ups (f20 == the new rank 2..9) run this same task for the
        // "Rank up!!" banner, no poem — stay silent (SocialLinkRankUp handles those).
        bool change = f20 == 13;   // LIVE-VERIFIED 2026-08-02: the change overlay ran f20=13
                                   // (f1C=31 Adachi, arcanaByte=25 — flips to 26 after)
        if (f20 != 0 && f20 != 10 && !change) return;
        if (_wasUp) return;
        if (change)
        {
            // SPOILER-SAFE by construction: this can only fire while the game
            // itself shows the transformation overlay (the task + kind exist
            // only then), and only for the Jester/Hunger commu row.
            int ab = ReadArcana(f1C);
            if (ab != 25 && ab != 26) { Log($"[SLBond] change-kind f20={f20} f1C={f1C} arcanaByte={ab} — not Jester/Hunger, silent"); return; }
            _wasUp = true;
            string t = BuildChange();
            Log($"[SLBond] f1C={f1C} f20={f20} arcanaByte={ab} say: {t}");
            Speech.Say(t, true);
            return;
        }

        // MAX poem timing (user 2026-07-09): this task appears BEFORE the "Rank up!!"
        // banner, so speaking on sight both stomps the rank-up screen AND double-fires.
        // Speak only after the banner (cmmRankUp) has APPEARED and then CLEARED — once.
        // ("cmmRankUp" only matches the banner: FindTask needs a null terminator, so it
        // never matches "cmmRankUpSequence".) Establish (f20 == 0) has no banner → speaks now.
        if (f20 == 10)
        {
            if (FindTask(BannerTask) != 0) { _bannerSeen = true; return; }   // banner up → wait
            if (!_bannerSeen) return;                                        // banner not shown yet → wait
        }
        _wasUp = true;

        int arcanaByte = ReadArcana(f1C);
        SocialLinkRankUp.ArcanaByByte.TryGetValue(arcanaByte, out string? arc);
        arc ??= "";

        string text = f20 == 10 ? BuildMax(arc) : BuildOpen(arc);
        Log($"[SLBond] f1C={f1C} f20={f20} arcanaByte={arcanaByte} say: {text}");
        Speech.Say(text, true);
    }

    private static string BuildOpen(string arcana)
    {
        var sb = new StringBuilder(
            "Thou art I, and I am thou. Thou hast established a new bond. " +
            "It brings thee closer to the truth. " +
            "Thou shalt be blessed when creating Personas");
        if (arcana.Length > 0) sb.Append(" of the ").Append(arcana).Append(" Arcana");
        sb.Append('.');
        return sb.ToString();
    }

    /// <summary>The ARCANA-CHANGE poem — the game's own official text
    /// (MSG_CMM_RANKUP_CHANGE in data_e/event_data/community.bin, dialog 61).
    /// The names it speaks are exactly what the overlay shows at that moment.</summary>
    private static string BuildChange()
    {
        return "Thou art I, and I am thou. Thou hast seen how bonds may change. " +
               "The bond that hath changed, it is thy first step in learning the truth. " +
               "Thou must bear thine inner power of The Jester becoming Hunger.";
    }

    private static string BuildMax(string arcana)
    {
        var sb = new StringBuilder(
            "Thou art I, and I am thou. Thou hast established a genuine bond. " +
            "These genuine bonds shall be thy eyes to see the truth. " +
            "We bestow upon thee the ability to create the ultimate form");
        if (arcana.Length > 0) sb.Append(" of the ").Append(arcana).Append(" Arcana");
        sb.Append('.');
        return sb.ToString();
    }

    private static int ReadArcana(int commuIdx)
    {
        if (commuIdx < 0 || commuIdx > 255) return -1;
        if (!IsReadable(CommuTablePtrVA, 8)) return -1;
        nint table = *(nint*)CommuTablePtrVA;
        nint row = table + commuIdx * 100 + 0x10;
        if (table == 0 || !IsReadable(row, 1)) return -1;
        return *(byte*)row;   // raw arcana byte (looked up in ArcanaByByte)
    }

    private static nint FindTask(byte[] name)
    {
        foreach (nint head in TaskHeads)
        {
            if (!IsReadable(head, 8)) continue;
            nint node = *(nint*)head;
            for (int i = 0; i < 512 && node != 0; i++)
            {
                if (!IsReadable(node, 0x58)) break;
                bool match = true;
                for (int j = 0; j < name.Length; j++)
                    if (*(byte*)(node + j) != name[j]) { match = false; break; }
                if (match && *(byte*)(node + name.Length) == 0) return node;
                node = *(nint*)(node + 0x50);
            }
        }
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
        => Utils.ProbeReadable(addr, size);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable
}
