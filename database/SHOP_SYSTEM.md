# SHOP SYSTEM — source of truth

**SECTION CLOSED 2026-06-12 with user sign-off ("this is PERFECT!!!!").**
The shop reader (`Components/ShopMenu.cs` + `DaidaraCharSelect.cs`) is fully
state-machine driven and works in Daidara and Shiroku (designed shop-generic).
This doc records the real structures — and the dead ends, because this system
burned months on misread fields. **Read this before touching ANY shop code.**

## The state machine (pShop+0x04) — THE foundation

`ShopUpdate` (hooked, SigScan `40 55 53 41 54 41 55 41 57 48 8B EC 48 83 EC
70 48 8B D9` = 0x1402827F0) receives `pShop`. The u16 at **pShop+0x04** is the
CURRENT STATE (set by `FUN_14027e4d0(pShop, newState)`, previous state at
+0x06). Complete map (live-verified through real purchases):

| State | Screen |
|---|---|
| 0x07 | top menu |
| 0x08 | character select (Daidara equip flow) |
| 0x09 | transition into buy flow (Shiroku goes 0x09→0x0A, NO charselect) |
| 0x0A | **item list** (list has focus) |
| 0x0B | **description window open** |
| 0x0C | **amount/quantity window** |
| 0x0D | purchase confirm (game dialog reads itself) |
| 0x0E | purchase complete |
| 0x0F | post-buy prompt (equip?, seen after gear purchase) |
| 0x12 | Sell screen (DONE 2026-06-18 — see "Sell screen" section below) |
| 0x1C | Talk |
| 0x1E→0x20→0x21→0x22 | exit teardown — **struct freed after; guard all reads** |

## The cursor fields (all F10-diff verified live)

| Field | Meaning |
|---|---|
| `pShop+0x9E` (s16) | **row cursor while the list has focus (0x0A)**; −1 when unfocused |
| `pShop+0x32` (s16) | **row cursor while the description window is open (0x0B)** — the "April cursor" was the window-mode cursor all along |
| `pShop+0x4E0` (s16) | selected character slot (0=protagonist, 1=Yosuke, …) |
| `pShop+0x4E2` (s16) | buy quantity (amount window) |
| `pShop+0x35C` (i32) | outer cursor: top-menu row / buy-tab index |
| `pShop+0x340` (s16[]) | top-menu OPTION IDS (count at +0x35A) — shop-agnostic: 0-2 equip categories, 3 Buy, 8 Sell, 9/11 Talk, 10 Exit |
| `pShop+0x60` (ptr) | active list; entries stride 0x14: +0x00 price (i32), +0x04 packed (lo16 = GLOBAL item id, hi16 = tag) |
| `pShop+0x08` (s16) | SHOP TYPE (0..5; 3 = materials shop — Shiroku sell side) |
| `pShop+0x2A` (s16) | screen-builder sub-phase |

Item-id blocks (global ids, confirmed): 0-255 weapons, 256-511 armor,
512-767 accessories, **768-1023 consumables** (Medicine=770; desc =
`HelpBmd.Item[id-768]` — Revival Bead 783→Item[15] matches April evidence).
1024+ = materials/key items, no desc BMD mapped yet.

## Other globals

| Address | Meaning |
|---|---|
| `0x1451BCD70` | wallet (u32 yen) — shared with battle ResultReader |
| `0x1451BCD8E` | **protagonist FIRST name** — 16-byte field, FULL-WIDTH Shift-JIS ("ＨＡＲＵ" = 82 67 82 60 82 71 82 74); last name in the preceding field. Plain-ASCII scans find NOTHING. Decoder: `ShopMenu.ProtagonistName()` |

## Architecture of the reader (v3 family)

- The hooked ShopUpdate **STARVES inside item lists** (it's the top-level
  menu update; the game stops calling it when a sub-screen has focus).
  → ALL logic runs on the 50ms poll thread reading the struct live; the hook
  only captures `pShop`. **Never poll anything in the hook.**
- Announce gating by exact state: rows/items only in 0x0A/0x0B; "Character
  select." only on 0x08; character-switch names only while the list is
  focused (DaidaraCharSelect's hook owns the charselect screen itself and is
  gated on `ShopMenu.ShopState == 0x08`).
- Browse announce = short form (name, price, "You have N yen"); description
  window (0x0B) = full form (name + description + price), auto-read on open.
- Amount window: "How many? 1. 850 yen each." on open; "{n}. Total {n×unit}
  yen." per change. Unit price via the window-row (+0x32) list entry.
- Keys: **F** = re-read current item (full in 0x0B) · **G** = wallet ·
  **F10** = diff-dumper (snapshots pShop 0x700 + UI statics 0x140BEA800+
  0x1800, prints changed shorts vs previous press — the tool that cracked
  every field above; KEEP IT).
- Entry/rebuild announces via a deferred countdown (`_announceListIn`),
  fired on list-opened edges (ActiveListPtr/count populate) and tab/character
  switches.

## DEAD ENDS — do not re-walk

1. **`Mode068` (+0x68)** — "mode discriminator" — STALE, never resets. Years
   of heuristics built on it; all wrong.
2. **`ActiveItemCount` (+0x9A)** — LIES transiently (read 1 with 2 items on
   screen, drifts). Never gate announces on it; validate row CONTENT instead
   (plausible id 1..0x2FFF + price 0..9,999,999 + non-empty name).
3. **The "panel-state global" 0x140BEB4EC** — a MIRAGE. It tracks the
   top-menu highlight (≈ cursor35C+1 with pulse flicker). An early 4-snap
   zigzag mapped it to panels by pure coincidence of cursor positions.
4. **"Session-variable state numbers"** — FALSE conclusion caused by the
   starving hook freezing its last-seen state value. The state map above is
   stable; trust pShop+0x04 read live.
5. **`+0x2E + tab*2` per-tab cursor slots** — wrong guess; weapon +0x2E held
   garbage (row 3 → id 65534). The truth is +0x9E / +0x32 by FOCUS MODE,
   not by tab.
6. The 0x1409A2200 region = packed u32 RVA-pair UI widget records (handler
   cluster 0x140281xxx-0x140282xxx: FUN_140282580 screen builder,
   FUN_1402820d0 menu-option builder, FUN_140280210 screen-object factory
   alloc 0x3C00, FUN_14027f170 = list SORT comparator). Interesting but not
   needed — the pShop fields above suffice.

## Sell screen (0x12) — ⚠ REBUILT 2026-07-30/31 (the GOLDEN tabbed screen; user-verified)

**⚠ The 2026-06-18 model was an UNSCROLLED ILLUSION.** The Golden sell screen has CATEGORY
TABS (Materials · Weapons · Armor · Accessories · Expendable, Q/E) and a ~5-row window over
lists of any length. **EVERY pShop cursor (+0x32 AND +0x9E) is WINDOW-relative here** — they
step 0..~4 then FREEZE while further presses scroll the list underneath (the June "stepped
0..N in lockstep" verification used a short early-game inventory that never scrolled →
"first items read, then silence" once a late-game list overflowed, user 2026-07-30).

**The REAL cursor lives in the named task `fcl_shop_base`'s WORK struct** (registry walk,
non-NUL terminator rule; live-verified via a 4-snap scroll hunt — the winning cell landed
INSIDE the work):
- `work+0x04` u16 = 0x12 (sell-state mirror — used to validate the cached work)
- **`work+0x32` u16 = WINDOW row · `work+0x34` u16 = SCROLL → TRUE row = window + scroll**
- `work+0x68` int = the current tab's row count (incl. the Sell-all row)

List DATA is unchanged and indexes 1:1 by the TRUE row:
- **list ptr = `*(pShop+0x60)`**, **count = `*(int*)(pShop+0x68)`**
- **entries stride 0x14: `+0x00` price (int) · `+0x04` itemId (u16) · `+0x06` qty (u16)**
- Row 0 = the **"Sell all"** bulk row (id = the category BLOCK-BASE → `GetName` empty;
  price = the TOTAL) → "Sell all. N yen".
- Builder `FUN_140280f60(pShop+0x18, shopType)` as before.

Reader (`ShopMenu`, state 0x12 block + `AnnounceSellCursor`):
- Row from the work struct (window+scroll); falls back to `pShop+0x32` if the task is missing.
- **Q/E tab switches**: the tabs are BAKED ART — the reader keys on (count, first item id)
  changing and announces the category CLASSIFIED FROM THE ITEM-ID BLOCK (<256 Weapons ·
  <512 Armor · <768 Accessories · <1024 Expendables · else Materials).
- **EMPTY tab** (game shows "There is nothing to sell."): count 0 (work OR pShop) →
  "Nothing to sell." once per visit — the stale list must never be read.

Hunt notes (2026-07-30): the STATICS zigzag only finds window mirrors (0x140BEAA68 = a
selected-widget id, 14/15 — red herring); the scroll was isolated by snapshotting at the
WINDOW EDGE (audible anchor: press Up to "Sell all", 4 Downs to the edge) and stepping
scroll 0,1,2,3 — `cursor_hunt.py find`. No absolute-row cell exists anywhere.

## Shiroku Pub TRADE shop — DONE 2026-06-18 (user-verified)

Goes through the normal shop hook (state 0x0A list / 0x0B desc), list at `*(pShop+0x60)`,
stride 0x14. Two fixes made it fully accessible:
- **Trade cost (no yen):** each entry carries up to two required materials inline —
  **`+0x0C` matId / `+0x0E` count** and **`+0x10` matId / `+0x12` count**. Owned count =
  `*(byte*)(*(nint*)0x141165930 + matId)`. Announce appends "Needs 5 Iolite, have 1"
  (gated on `price==0`; `ShopMenu.TradeRequirement`). Iolite=2128, materials live in the
  Golden Item2 block (2048+).
- **SCROLL (the big one):** the trade list has 16 items but only ~6 are visible, so
  `+0x9E`/`+0x32` is the WINDOW cursor (0..5) and **`+0x34` is the scroll** (first-visible
  absolute index). Real row = **cursor + `+0x34`** (live-verified: Spring Boots = 5+10 =
  15). `EffectiveRow` now adds it, guarded by total count `+0x68` so non-scrolling shops
  (Daidara) are a no-op. This ALSO fixed the "silent on scroll" symptom (window cursor
  didn't change on scroll → dedupe stayed silent). NOTE: total count is **`+0x68`**;
  `ActiveItemCount`/`+0x9A` (=6) is the VISIBLE window size, not the total.

## Yomenaido BOOKSTORE — DONE 2026-06-18 (user-verified "perfectly")

A CUSTOM menu that **reuses the shop menu-object layout** but does NOT go through
ShopUpdate, so the shop hook never saw it. Cracked via Cheat Engine "find what accesses"
the cursor (a 2-byte short) → render fn **`FUN_140213190`**. `BookstoreMenu.cs` hooks it;
the menu object = **`*(*param_1 + 0x48)`**, with the same shop-shaped fields:
- `+0x04` state — **0x0A** list, **0x0B** Show-Info window (read both); 0x03/0x06/0x09 …
  0x1E-0x22 are just open/close transitions (skip).
- `+0x32` window cursor + `+0x34` scroll → **abs row = cursor + scroll**.
- `+0x60` list ptr, `+0x68` count; entries stride **0x14**: `+0x00` price (yen),
  `+0x04` item id (u16).

Books are key-item ids (1024-1280, `HelpBmd.Event`) — e.g. Expert Study Methods 1136,
Beginner Fishing 1146, The Lovely Man 1259. Announces name + price + "you have N yen"
(wallet `0x1451BCD70`) + description on cursor change. **Gated on
`ShopMenu.ShopState == 0xFFFF`** so it never fights the real shop reader. (Its open/close
states show it's a full shop state machine, just behind a different renderer.)

## Parked (future phases)
- Materials/key-item description BMDs (ids 1024+).
- Same-name-different-desc armor verification once shops stock more.
- Buy-confirm (0x0D) content reading — the game dialog already reads it.
