using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components;

/// <summary>
/// The TV "Weather News" weekly-forecast panel (2026-07-30, user feature). The seven
/// day cards are BAKED SPRITES (no text hook), but the mod already carries the whole
/// year's weather (weather_schedule.json) and the in-game date — so the panel is
/// "today+1 .. today+7" from our own data, no icon reading needed.
///
/// Detection = the NAMED-TASK REGISTRY (the silent-screen anchor): task
/// <b>weather_spr</b> exists exactly while the panel is up (live diff 2026-07-30,
/// open vs closed at the living-room TV; `check_livingroom_tv` also appears there but
/// is TV-spot-specific — the panel opens from 3 places, weather_spr is the panel
/// itself). While open, <b>J / L</b> browse the seven cards ("Saturday, February 4.
/// Cloudy."); the game doesn't use those keys here. The dialogue reader speaks the
/// screen's own "This is the weekly forecast…" line as usual; our open prompt queues
/// after it (non-interrupting).
/// </summary>
internal sealed unsafe class WeatherNews
{
    private static readonly nint[] TaskHeads =
    {
        unchecked((nint)0x1462486F8L),
        unchecked((nint)0x1462486A8L),
        unchecked((nint)0x146248768L),
    };
    private static readonly byte[] TaskName = System.Text.Encoding.ASCII.GetBytes("weather_spr");

    private const int VK_J = 0x4A, VK_L = 0x4C;

    private bool _open;
    private int _idx;            // 1..7 = the browsed card (today+idx); 0 = none yet
    private bool _jWas, _lWas;   // edge flags, synced on open (ghost-key guard pattern)

    internal WeatherNews()
    {
        new Thread(Poll) { IsBackground = true, Name = "WeatherNews" }.Start();
        Log("[Weather] Weather News panel reader ready (weather_spr task; J/L browse)");
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
        bool open = TaskAlive();
        if (open != _open)
        {
            _open = open;
            if (open)
            {
                _idx = 0;
                // Ghost-key guard: only presses that BEGIN while the panel is open count.
                _jWas = IsKeyDown(VK_J);
                _lWas = IsKeyDown(VK_L);
                // Queued AFTER the screen's own "This is the weekly forecast…" dialogue line.
                Speech.Say("Seven day forecast. J and L to browse.", interrupt: false);
            }
            return;
        }
        if (!open || !GameHasFocus() || SettingsMenu.IsOpen) return;

        bool j = IsKeyDown(VK_J);
        if (j && !_jWas) Step(-1);
        _jWas = j;

        bool l = IsKeyDown(VK_L);
        if (l && !_lWas) Step(+1);
        _lWas = l;
    }

    private void Step(int dir)
    {
        // Cards = tomorrow (today+1) .. today+7. First press of either key starts at
        // tomorrow; ends clamp (re-reading the edge card is the "you're at the end" cue).
        _idx = _idx == 0 ? 1 : Math.Clamp(_idx + dir, 1, 7);
        string line = FieldTracker.ForecastLine(_idx);
        if (line.Length == 0) { Speech.Say("Forecast unavailable.", true); return; }
        Speech.Say(line, true);
    }

    private static bool TaskAlive()
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
                // Exact-name rule: the terminator is a NON-PRINTABLE byte, not always NUL
                // (the shuffle-anchor lesson — an ==0 check silently missed real tasks).
                if (match && probe[TaskName.Length] < 0x20) return true;
                node = *(nint*)(probe + 0x50);
            }
        }
        return false;
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
