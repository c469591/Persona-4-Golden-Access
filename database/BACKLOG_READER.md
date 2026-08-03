# Dialogue Backlog reader — source of truth

Built + user-verified 2026-07-20. Component: `Components/BacklogReader.cs`. Reads the
in-dialogue BACKLOG (open with **X** during a conversation): a scrollable history of
lines; **Space** replays a voiced line (the game handles playback — the reader only
speaks the text + a "Voiced." tag). Reads the SELECTED line on each scroll.

RE method: the live-hunt loop (blind user focuses a line + tells the text on screen →
external RPM scan finds it → trace the record chain). Tools left in `database/frida/`:
`task_dump.py`, `backlog_probe.py`, `hunt_backlog_text.py`.

## The task + cursor

- Screen = the persistent named task **`itfBacklogDraw`** (task registry — the universal
  silent-screen anchor). Draw closure `FUN_140459cf0`, row drawer `FUN_140459780`, run
  layout builder `FUN_1681ed230`. `work = *(node+0x48)`.
- **`work+0x0C` (u32) STATE — gate on `(state & 0x4) != 0 && count > 0`.** The dialogue
  backlog's live states (0x4/0xD/0x1C/0x1D) all have bit 0x4; OTHER uses of the same
  task — a Social Link level-up (0x80000000), the closing/secondary screens (0x10,
  0x80000010) — do NOT. Gating on bit 0x4 is what stopped the reader leaking onto those.
- `work+0x1C` = animated scroll pos; **`work+0x20` = the SELECTION TARGET — read this**
  (the animated pos flickers through intermediate rows mid-scroll).
- **Open often arrives EMPTY and populates a frame later** (state set, count 0 → count N).
  So "open" requires count>0, and the reader keys re-reads on the cursor alone (folding
  count into the key made a settled line re-read every frame — the "leak/reads again").

## Entries + records

- `count = *(i32)( *(obj+0x08) + 0x0C )` where `obj = *work`.
- Entries = a linked list: head `*(cont+0x10)`, next `*(node+0x18)`, RECORD `*(node+0x20)`.
- Record: `+0x00` slot, `+0x04` **sub** (message index — goes past 100; do NOT cap at 64,
  that bug read every high-sub line EMPTY), `+0x08` mask, `+0x1A6` u16 voice id (0=unvoiced).

## Text of a record

Chain: `msgObj = *(0x1451FE688 + slot*0x40)` → `block = *(msgObj+8)` → line header =
`*(block + 0x38 + sub*0x10)`.

- **Run count is NOT a single field:** `n18 = u16 @line+0x18`, `n1a = u16 @line+0x1A`.
  - `n18 > 0` → DIALOGUE: `n18` is the run count. Each run is its OWN record; the mask
    marks earlier runs already-revealed. Read the ONE focused run = the **lowest CLEAR
    mask bit** (mask0→run0, mask0x1→run1). Its sibling records cover the other runs, so
    each scroll = one line, none joined.
  - `n18 == 0` → CHOICES: the option count is in `n1a`; one record holds all options —
    read every non-masked run, joined.
- Run pointer array at `line+0x20` stride 8. Decode = Atlus MSG (`DecodeAtlusRun`):
  a byte ≥0x80 begins a 2-byte token (function/glyph incl. the F5 voice code) — skip
  BOTH bytes (eats the function's inline param); 0x20–0x7E = literal English; **0x0A
  newline → a space** (so "because\nI" → "because I", not "becauseI"); 0x00 ends the run
  once text has started (the 0x00s inside F-function args come before the text).
- ⚠ Dead ends: `line+0x1A` alone as the count (reads 1 for a 2-run message → wrong
  sentence); `max(n18,n1a)` as count (n1a is a padded 5 on some records → over-read);
  reading all non-masked runs for dialogue (joins two lines); text-dedupe by content
  (silently dropped valid distinct lines once the mask fix made records distinct).

## Speaker name — ✅ WIRED + USER-VERIFIED 2026-07-30 ("Name: line. Voiced.")

Field-verified across 7+ characters; never misread a name. **Narration rule:** "> …" lines carry
a speaker id in the data but the game shows no name plate — the leading '>' is the narration
marker, the reader suppresses the name there. Fail-safe: any hop/sanity failure → no name.
The HOLDER path (bit15 dynamic names) is wired but unseen in the field — `[SpkDiag]` diagnostics
stay until sighted (strip at v1.6 packaging). The resolver chain (from the decompile):

The game's own lookup (`FUN_140459cf0` → per-row `FUN_140459780` → **`FUN_14045F030` = the
name resolver**), per record (`block+0x38 + sub*0x10` — the record the reader already has):
- **speaker id = `*(u16*)(record + 0x1A)` — DIALOGUE records only** (`+0x18` n18 > 0). On
  CHOICE records that offset is the n1a run count — DUAL-PURPOSE field (why the n18/n1a
  split rule works).
- `0xFFFF` → unnamed (narration).
- **bit15 SET** → runtime name-holder: `char* @ block + 0xD0 + (id & 0x7FFF)*8` (dynamic
  names — the protagonist's chosen name etc.). The old heuristic misread this path at the
  wrong offset (+0x08) → the Rise→"Kou" bug.
- **else** → the BMD's built-in speaker-name table: the game calls
  `thunk_FUN_1681ca820(block, id)` (VMProtect region — replicate from the standard MSG1
  speaker-table layout / extend `Native/Dialog.cs` instead of decompiling).
- Row-type flags @ record+0x10: bit0 = choice-style, neither bit = name+text row.
  `FUN_14045e800` = an F1xx control-code scanner (0xF121/0xF124 break, 0xF125 = name
  override marker) — check it when wiring.
To wire: extend `ResolveSpeaker` with this chain (Atlus-decode the string); acid test =
the scene that read Rise as "Kou".

## Reader behavior

Poll thread (100 ms). On a settled cursor change, speak the selected record's text +
" Voiced." if voiced. Gated on `Utils.GameHasFocus`. No other mod hotkeys involved (the
game owns X/Space/scroll). No release bundle impact (pure in-mod reader).
