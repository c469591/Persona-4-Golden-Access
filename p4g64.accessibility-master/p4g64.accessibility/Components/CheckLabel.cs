using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// CHECK-prompt LABEL reader (2026-09-02, the world-helper session). The yellow-bar
/// name the game draws beside "CHECK!!" ("Futon", "Sofa", "Living Room") flows
/// through the shared UI-text fn FUN_140450C60 — TextSpy-proven in the field
/// (memory/world_helper_zones.md). That is the REAL selected-object source the old
/// removed geometry-guess naming lacked (FieldTracker.cs note 2026-07-09): we speak
/// the game's OWN word, never a guess.
///
/// How it works: this hook latches the last plausible label drawn on a 2.5D field
/// map (majors 1..19). FieldTracker's CHECK rise announcement calls
/// <see cref="TakeForRise"/> — a fresh latch upgrades "Check" to "Check: Sofa";
/// stale/no latch keeps the plain honest "Check". While the prompt STAYS up and the
/// game draws a DIFFERENT label (sliding along adjacent objects), the hook speaks
/// the new one itself. <see cref="OnPromptGone"/> (flag fall) resets.
///
/// Filters (the field draws almost nothing through 450C60, but): full-string draws
/// only (p6 == 0), length 2..32, not in battle/dungeon, not while a menu or
/// dialogue is up (the S.Link poem and choice overlays also draw here).
/// </summary>
internal sealed unsafe class CheckLabel
{
    private static readonly nint SetUiTextVA = unchecked((nint)0x140450C60);

    private IHook<SetTextDelegate>? _hook;
    private delegate nint SetTextDelegate(
        nint param_1, byte param_2, byte param_3, uint param_4, byte param_5, nint param_6);

    private static readonly object Lock = new();
    private static string? _label;          // last plausible field label drawn
    private static long _labelTick;         // when it was drawn
    private static string? _spoken;         // label already announced for this prompt
    private static bool _promptAnnounced;   // FieldTracker's rise announcement happened
    private static volatile string? _areaBanner;  // the map's own name bar, learned on entry — never a label
    private static string? _prevDraw;       // previous drawn label (stability gate for the change announcer)
    private static long _lastHookSayMs;     // rate limit for hook-side announcements

    /// <summary>A rise-latch older than this is stale (the label draws the same
    /// frame the prompt appears; the poll sees the rise within ~500ms).</summary>
    private const int FreshMs = 800;

    internal CheckLabel(IReloadedHooks hooks)
    {
        _hook = hooks.CreateHook<SetTextDelegate>(OnSetText, SetUiTextVA).Activate();
        Log("[CheckLabel] CHECK-label hook active on FUN_140450C60 (\"Check: <name>\")");
    }

    private nint OnSetText(nint p1, byte p2, byte p3, uint p4, byte p5, nint p6)
    {
        nint ret = _hook!.OriginalFunction(p1, p2, p3, p4, p5, p6);
        try { if (p6 == 0) Capture(p1); } catch { /* never let a hook throw */ }
        return ret;
    }

    private static void Capture(nint strPtr)
    {
        int major = FieldTracker.CurrentMajor;
        // Town always; a dungeon LOBBY only once its map is authored in the zones file
        // (2.5D like town; the file is the gate — 3D floors never match).
        if (!Navigation.OverworldZones.IsZoneMap(major, FieldTracker.CurrentMinor)) return;
        if (!FieldTracker.InFieldLive) return;                       // title/menus: CurrentMajor is stale
        if (SettingsMenu.IsOpen || CommandMenus.PlayerMenu.IsMenuOpen) return;
        if (Environment.TickCount64 - Dialogue.LastDialogTick < 300) return;

        string s = ReadCStringRpm(strPtr, 64).Trim();
        if (s.Length < 2 || s.Length > 32) return;                   // "Z" key icon = 1 char; poems are long

        // THE LOCATION BANNER (caught live at Nanako, 2026-09-02): the map's own
        // name ("Living Room") draws through the SAME bar. It draws right after a
        // map change — LEARN it there (language-independent, no name table) — and
        // it REDRAWS when the prompt targets an UNLABELED object, so an exact
        // match is never a label. A banner miss just means plain "Check".
        if (Environment.TickCount64 - FieldTracker.LastAreaChangeMs < 2500)
        {
            lock (Lock) { _areaBanner = s; _label = null; }
            return;
        }
        if (s == _areaBanner) return;

        lock (Lock)
        {
            _label = s;
            _labelTick = Environment.TickCount64;

            // Label CHANGED while the prompt is still up (walked from one object to
            // the next without the flag dropping) → speak the new target ourselves.
            // ⚠ STABILITY GATE (2026-09-02, the door/sofa corner): where two
            // interactables overlap, the game FLICKERS its target every frame and
            // the bar redraws "Sofa"/"Living Room" alternately — announcing each
            // change machine-gunned a dozen a second (log-proven 16:39:37). Only a
            // label drawn TWICE IN A ROW counts as a real re-target, rate-limited;
            // a flickering pair never settles and stays silent (the per-rise
            // announcement still names one of them — honest enough at a corner).
            bool stable = s == _prevDraw;
            _prevDraw = s;
            if (stable && _promptAnnounced && FieldTracker.CheckPromptActive && s != _spoken
                && Environment.TickCount64 - _lastHookSayMs >= 1200)
            {
                _spoken = s;
                _lastHookSayMs = Environment.TickCount64;
                Speech.Say($"Check: {s}", interrupt: false);   // queues after a zone name (user request)
            }
        }
    }

    /// <summary>FieldTracker, on the CHECK flag RISE: the label to append, or null
    /// for the plain honest "Check" (no fresh draw latched).</summary>
    internal static string? TakeForRise()
    {
        lock (Lock)
        {
            _promptAnnounced = true;
            bool fresh = _label != null && Environment.TickCount64 - _labelTick <= FreshMs;
            _spoken = fresh ? _label : null;
            return _spoken;
        }
    }

    /// <summary>FieldTracker, on the CHECK flag FALL: prompt gone, reset. The latched
    /// label is KEPT — the flag flickers 0/1 sliding between two adjacent objects,
    /// and clearing here made the quick re-rise announce a bare "Check" mid-spam.</summary>
    internal static void OnPromptGone()
    {
        lock (Lock) { _promptAnnounced = false; _spoken = null; }
    }
}
