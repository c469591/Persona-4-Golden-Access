using System.Runtime.InteropServices;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Navigation;

/// <summary>
/// <b>B</b> — turn the camera to face NORTH (world +Z, the H-cursor compass) using the game's
/// OWN per-frame camera rotate <c>FUN_1402d4c80</c> (via <c>FieldTracker.RequestCameraRotate</c>,
/// pumped on the game thread). The eased wrapper FUN_1402d6bb0 proved a dead end — its target
/// parameter does not do what the decompile suggested (log 2026-08-29: commanded 0 and 45 both
/// landed at the same yaw), so this drives the raw delta rotate in a measured closed loop:
/// probe +30 to learn the units/sign (degrees vs radians, either direction), then rotate the
/// remaining error in chunks each tick until the camera reads north. The chunked steps give a
/// natural ease; every step is the game's own rotate + commit, no memory writes.
/// Calibration (units factor) persists in mod_settings (cam_rot_factor_x1000).
/// </summary>
internal sealed class CameraNorth
{
    private const int PollMs = 40;
    private const int VK_B = 0x42;
    private const float NorthWorldDeg = 90f;    // atan2(fz,fx): north = +Z → 90°
    private const float TolDeg = 5f;
    private const float ProbeDeg = 30f;         // the units/sign probe rotation
    private const float MaxStepDeg = 22f;       // per-tick chunk → a natural fast pan
    private const int MaxSteps = 40;            // hard budget (soft-lock invariant)

    private readonly Thread _thread;
    private volatile bool _stopped;
    private bool _keyWas;

    private int _phase;            // 0 idle · 1 probe sent · 2 stepping
    private long _nextAt;
    private float _probeStartW;
    private int _steps;

    /// <summary>Multiply a desired WORLD-degree delta by this to get the value FUN_1402d4c80
    /// wants. Learned from the probe: ±1 (degrees) or ±π/180 (radians). 0 = unknown.</summary>
    private float Factor
    {
        get => ModSettings.GetInt("cam_rot_factor_x1000", 0) / 1000f;
        set => ModSettings.SetInt("cam_rot_factor_x1000", (int)MathF.Round(value * 1000f));
    }

    public CameraNorth()
    {
        _thread = new Thread(Poll) { IsBackground = true, Name = "CameraNorth" };
        _thread.Start();
        Log("[CameraNorth] ready (B = camera to north, direct-rotate servo)");
    }

    public void Stop() => _stopped = true;

    private static bool InField()
    {
        int major = FieldTracker.CurrentMajor;
        return major > 0 && major < 220;
    }

    private static float WorldYawDeg(out bool ok)
    {
        var (fx, fz) = FieldTracker.CameraForward3D();
        ok = fx != 0f || fz != 0f;
        return ok ? MathF.Atan2(fz, fx) * 180f / MathF.PI : 0f;
    }

    private static float Wrap(float d) { while (d > 180f) d -= 360f; while (d < -180f) d += 360f; return d; }

    private void Poll()
    {
        while (!_stopped)
        {
            Thread.Sleep(PollMs);
            try { Tick(); } catch (Exception e) { Log($"[CameraNorth] {e.Message}"); _phase = 0; }
        }
    }

    private void Tick()
    {
        if (!GameHasFocus()) { _keyWas = false; return; }
        bool b = (GetAsyncKeyState(VK_B) & 0x8000) != 0;
        bool edge = b && !_keyWas; _keyWas = b;
        if (SettingsMenu.CaptureKeys || CommandMenus.PlayerMenu.IsMenuOpen) return;

        if (edge && _phase == 0)
        {
            if (!InField()) { Speech.Say("Camera north only works in the field.", true); return; }
            float w = WorldYawDeg(out bool ok0);
            if (!ok0) { Speech.Say("Camera unreadable.", true); return; }
            _steps = 0;
            if (MathF.Abs(Wrap(w - NorthWorldDeg)) <= TolDeg) { Speech.Say("Camera north.", true); return; }
            // Always start with the probe: it doubles as "does this camera respond at all?"
            _probeStartW = w;
            if (!FieldTracker.RequestCameraRotate(ProbeDeg * (Factor == 0f ? 1f : Factor)))
            { Speech.Say("Camera control unavailable.", true); return; }
            _phase = 1;
            _nextAt = Environment.TickCount64 + 220;
            return;
        }

        if (_phase == 0 || Environment.TickCount64 < _nextAt) return;

        float now = WorldYawDeg(out bool ok);
        if (!ok) { _phase = 0; Log("[CameraNorth] camera unreadable mid-servo"); return; }

        if (_phase == 1)
        {
            float moved = Wrap(now - _probeStartW);
            float asked = ProbeDeg * (Factor == 0f ? 1f : Factor);
            Log($"[CameraNorth] probe: asked {asked:F3} raw, camera moved {moved:F1} deg (from {_probeStartW:F1} to {now:F1})");
            if (MathF.Abs(moved) < 2f)
            {
                _phase = 0;
                Speech.Say("The camera can't be turned here.", true);
                return;
            }
            // raw-per-world-degree = asked / moved; snap to the plausible conventions
            // (±1 = degrees, ±π/180 = radians) so measurement noise can't drift it.
            float f = asked / moved;
            float[] snaps = { 1f, -1f, MathF.PI / 180f, -MathF.PI / 180f };
            foreach (var sv in snaps) if (MathF.Abs(f - sv) < MathF.Abs(sv) * 0.25f) { f = sv; break; }
            Factor = f;
            Log($"[CameraNorth] factor = {f:F5} raw per world degree");
            _phase = 2;
            _nextAt = 0;
            return;
        }

        // _phase == 2: servo toward north in chunks.
        float err = Wrap(NorthWorldDeg - now);
        if (MathF.Abs(err) <= TolDeg)
        {
            _phase = 0;
            Log($"[CameraNorth] done at {now:F1} deg after {_steps} steps");
            Speech.Say("Camera north.", true);
            return;
        }
        if (_steps >= MaxSteps)
        {
            _phase = 0;
            Log($"[CameraNorth] gave up at {now:F1} deg (err {err:F1}) after {_steps} steps");
            Speech.Say("Camera could not reach north.", true);
            return;
        }
        float step = Math.Clamp(err, -MaxStepDeg, MaxStepDeg);
        FieldTracker.RequestCameraRotate(step * Factor);
        _steps++;
        _nextAt = Environment.TickCount64 + 70;   // ≥2 move ticks between measure points
    }

    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
}
