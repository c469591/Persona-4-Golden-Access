#!/usr/bin/env python3
"""Extract the post-boss FLOOR ITEM DROPS for every dungeon floor, offline.

The invisible weapon/item that appears on a dungeon's scripted/boss floor after
the boss ("mash Space to find it") is a SCRIPT-BOUND trigger box (proven on
Secret Lab 27/2, 2026-07-29):
  - the floor's flow has a pickup proc (castle_item/sauna_item/playhouse_item/
    bone_object/base_item/heaven_item/...) recognizable by MSG(FIND_ITEM);
    it computes the item id `(A + B)` and clears the presence BIT via BIT_OFF(N)
    (the floor init BIT_ONs the same bit once the boss-defeat bit is set);
  - h{maj}_{min}.bin (inside the floor's fd arc) row i binds HBN cat-13 trigger
    box i to a BF procedure index (u16 @ row+16, 44-byte rows);
  - f{maj}_{min}.HBN blob 0 section 0 (record size 0x20) holds the boxes'
    world X/Y/Z.
Validated anchor: 27_2 base_item -> procIdx 10 -> h-row 3 -> box 3 @ (-1, 47),
matching the user's live "standing next to the item" capture of 2026-07-24.

Inputs:  extract/dungeon_area_arcs/field/pack/fd0MM_NNN.arc (CpkExtractor)
         extract/bf_per_area/fd0MM_NNN.flow (decompiled floor scripts)
Output:  database/dungeon_floor_items.json  { "MM_NN": [ {x,z,bit,item,proc} ] }
"""
import json
import os
import re
import struct
import sys

DB = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ARCS = os.path.join(DB, "extract", "dungeon_area_arcs", "field", "pack")
FLOWS = os.path.join(DB, "extract", "bf_per_area")
OUT = os.path.join(DB, "dungeon_floor_items.json")

HBN_MAGIC = 0x00010001  # not used; blob magic checked below


def unpack_arc(path):
    """[u32 count] + entries [name 32B][u32 size][data] -> {name: bytes}."""
    d = open(path, "rb").read()
    n, = struct.unpack_from("<I", d, 0)
    p = 4
    out = {}
    for _ in range(n):
        name = d[p:p + 32].split(b"\0")[0].decode("ascii", "replace")
        size, = struct.unpack_from("<I", d, p + 32)
        out[name] = d[p + 36:p + 36 + size]
        p += 36 + size
    return out


def parse_hbn_c13(blob):
    """First HBN blob's section-0 (0x20-stride) records -> [(x, y, z)] by index."""
    magic, _ver = struct.unpack_from("<2I", blob, 0)
    toc = []
    p = 8
    for _ in range(8):
        cnt, rsz = struct.unpack_from("<2I", blob, p)
        toc.append((cnt, rsz))
        p += 8
    boxes = []
    for cnt, rsz in toc:
        for i in range(cnt):
            base = p + i * rsz
            if rsz == 0x20 and base + rsz <= len(blob):
                x, y, z = struct.unpack_from("<3f", blob, base + 8)
                boxes.append((x, y, z))
        if boxes:
            break               # section 0 with records = the c13 trigger boxes
        p += cnt * rsz
    return boxes


def parse_h_rows(blob):
    """44-byte rows -> [procIdx] by row index (u16 @ +16)."""
    return [struct.unpack_from("<H", blob, i * 44 + 16)[0]
            for i in range(len(blob) // 44)]


PROC_RE = re.compile(
    r"// Procedure Index: (\d+)\s*\nvoid (\w+)\(\)\s*\{(.*?)\n\}", re.DOTALL)
ID_RE = re.compile(r"=\s*\((0x[0-9A-Fa-f]+|\d+)\s*\+\s*(0x[0-9A-Fa-f]+|\d+)\)\s*;")
BITOFF_RE = re.compile(r"BIT_OFF\((\d+)\)")
BITON_RE = re.compile(r"BIT_ON\((\d+)\)")


def find_item_procs(flow_text):
    """[(procIdx, name, itemId, bit, presentWhenSet)] for every MSG(FIND_ITEM) proc.

    Two bit conventions exist: the standard boss-drop clears its presence bit on
    pickup (BIT_OFF -> present while SET); the tutorial liquor-store pickup marks
    itself collected instead (BIT_ON guarded by BIT_CHK==0 -> present while CLEAR).
    """
    found = []
    for m in PROC_RE.finditer(flow_text):
        idx, name, body = int(m.group(1)), m.group(2), m.group(3)
        if "MSG(FIND_ITEM)" not in body:
            continue
        ids = ID_RE.search(body)
        item = (int(ids.group(1), 0) + int(ids.group(2), 0)) if ids else -1
        off = BITOFF_RE.search(body)
        on = BITON_RE.search(body)
        if off:
            bit, present_when_set = int(off.group(1)), True
        elif on:
            bit, present_when_set = int(on.group(1)), False
        else:
            bit, present_when_set = -1, True
        found.append((idx, name, item, bit, present_when_set))
    return found


def main():
    result = {}
    for arc in sorted(os.listdir(ARCS)):
        m = re.match(r"fd0?(\d+)_0?(\d+)\.arc$", arc)
        if not m:
            continue
        major, minor = int(m.group(1)), int(m.group(2))
        flow_path = os.path.join(FLOWS, f"fd{major:03d}_{minor:03d}.flow")
        if not os.path.exists(flow_path):
            print(f"fd{major:03d}_{minor:03d}: no decompiled flow — SKIPPED")
            continue
        procs = find_item_procs(open(flow_path, encoding="utf-8").read())
        if not procs:
            continue
        files = unpack_arc(os.path.join(ARCS, arc))
        hbin = files.get(f"h{major:03d}_{minor:03d}.bin")
        hbn = files.get(f"f{major:03d}_{minor:03d}.HBN")
        if hbin is None or hbn is None:
            print(f"{major}_{minor}: item proc but MISSING h/HBN in arc — CHECK")
            continue
        rows = parse_h_rows(hbin)
        boxes = parse_hbn_c13(hbn)
        floor = []
        for idx, name, item, bit, present_when_set in procs:
            try:
                box_i = rows.index(idx)
            except ValueError:
                # Shared per-dungeon flow: the proc exists in every floor's script but
                # only the floor whose h-table binds it actually has the item. Silent.
                continue
            if box_i >= len(boxes):
                print(f"{major}_{minor}: box {box_i} out of range ({len(boxes)} boxes) — CHECK")
                continue
            x, y, z = boxes[box_i]
            floor.append({"X": round(x, 2), "Z": round(z, 2), "Bit": bit,
                          "PresentWhenSet": present_when_set,
                          "ItemId": item, "Proc": name})
            print(f"{major}_{minor}: {name} item={item} bit={bit} "
                  f"presentWhenSet={present_when_set} box{box_i} @ ({x:.1f},{z:.1f})")
        if floor:
            result[f"{major}_{minor}"] = floor
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(result, f, indent=2)
    print(f"\nwrote {OUT}: {sum(len(v) for v in result.values())} item(s) "
          f"on {len(result)} floor(s)")


if __name__ == "__main__":
    main()
