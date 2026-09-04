using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions;
using static p4g64.accessibility.Utils;

namespace p4g64.accessibility.Components.Battle;

/// <summary>
/// Hooks the in-battle persona-menu update <c>FUN_1400edf80(param_1, menuObj)</c> —
/// the function that runs every frame the Persona menu (list OR full panel) is open.
/// Found via a Frida backtrace from the submenu row renderer (0x1400E65B0): its caller
/// chain led here. <c>*menuObj</c> (first short) is the menu MODE; the list-draw helper
/// only runs when mode != 2, so mode 2 = the full detail panel (list hidden — which is
/// why the submenu reader went silent there).
///
/// Publishes <see cref="Battle.PersonaMenuMode"/> + a freshness tick so PersonaNav can
/// gate reliably on "menu open" / "panel open" instead of guessing BtlInfo offsets.
/// </summary>
internal sealed unsafe class PersonaMenuHook
{
    private IHook<UpdateDelegate> _hook;

    internal PersonaMenuHook(IReloadedHooks hooks)
    {
        // ASLR off (image base 0x140000000), so the address is constant — hook directly.
        nint addr = unchecked((nint)0x1400EDF80L);
        _hook = hooks.CreateHook<UpdateDelegate>(Update, addr).Activate();
        Log($"[PersonaMenuHook] hooked persona menu update @ 0x{addr:X}");
    }

    private void Update(nint param1, nint menuObj)
    {
        _hook.OriginalFunction(param1, menuObj);
        try
        {
            if (!IsReadable(menuObj, 2)) return;
            int mode = *(short*)menuObj;
            Battle.PersonaMenuObj = menuObj;
            Battle.PersonaMenuMode = mode;
            Battle.PersonaMenuTick = Environment.TickCount64;
            // mode 0 = this widget closed; drop the latched persona object so a stale
            // pointer can't later match the Analyze panel's widget.
            if (mode == 0 && menuObj == Battle.PersonaMenuObjForPersona)
                Battle.PersonaMenuObjForPersona = 0;
        }
        catch { }
    }

    private delegate void UpdateDelegate(nint param1, nint menuObj);

    [DllImport("kernel32.dll")]
    private static extern nint VirtualQuery(nint lpAddress, byte* lpBuffer, nint dwLength);

    private static bool IsReadable(nint addr, int size)
        => Utils.ProbeReadable(addr, size);   // RPM probe (2026-08-31) — was a VirtualQuery copy; see Utils.ProbeReadable
}
