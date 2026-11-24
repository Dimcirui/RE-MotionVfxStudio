"""TypeMesh part animation run by re4.exe (birth 3d30b60, per-frame 3d31049..3d31250).

The per-particle record lives at type block +0x100, 0x38 bytes per slot: +0 part index (float),
+4 play speed (parts per second: PlaySpeed x 60), +8 PlaySpeedCoef, +0xc elapsed time. The wrap limit is the
attribute's MaxPartsNum ([attr + 0x58]); the mesh resource only has to exist (+0x168 non-null), so the
empty stub mesh is enough. Checked here against the closed form of RUNTIME 4.3.11:
  part += speed * dt, then speed *= coef (once per tick while settings +0xeb7 = 0; the other branch
  integrates coef ** (dt * 60) exactly). Past MaxPartsNum: PlayType 1 wraps, 2 stops on the last part and
  clears the alive bit, 3 resets to the last part (and keeps stepping from there).

  python -I -X utf8 -u exp_mesh.py
"""
import json
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import numpy as np  # noqa: E402

import emitter as em  # noqa: E402
import re4native as rn  # noqa: E402
from exp_emitter import STM  # noqa: E402

F = np.float32
TYPE_MESH = 28
RECORD = 0x38
CASES = {
    # PlayType 2 (Finish), MaxPartsNum 28, PartsStartNo 15..16, PlaySpeed 3, coef 1
    "finish": (STM + r"\_anotherorder\vfx\effecteditor\efd_character\efd_ao_chd7\efd_0015_ao_chd7_damage_0000.efx.3539837", 0x23F5),
    # PlayType 3 (hold on the last part), MaxPartsNum 19, PartsStartNo 5..6, PlaySpeed 1.5, coef 0.9, 10 particles
    "hold": (STM + r"\_anotherorder\vfx\effecteditor\efd_character\efd_ao_chd7\efd_0015_ao_chd7_gimmick_mot_0040.efx.3539837", 0xEA8),
}


def run(img, name, ticks=40, dt=1 / 60):
    path, off = CASES[name]
    entry = em.Entry(img, path, off)
    attr = next(a["attr"] for a in entry.attrs if a["type"] == TYPE_MESH)
    e = em.Emitter(img, entry)
    e.play()
    block = e.block_of(TYPE_MESH)
    play_type, limit = rn.u32(block + 0x3C), rn.u32(attr + 0x58)
    print(f"{name}: MaxPartsNum [attr+0x58] = {limit}, PlayType [block+0x3c] = {play_type}, capacity {e.max_particles}")
    expected, bad, seen = {}, 0, 0
    for tick in range(1, ticks + 1):
        e.tick(dt)
        records = rn.u64(block + 0x100)
        for i, p in enumerate(e.particles()):
            alive = rn.u32(p) & 1
            part, speed, coef = struct.unpack("<3f", img.read(records + RECORD * i, 12))
            key = i                     # slot; a slot that was dead (or never used) and is alive now is a birth
            if key not in expected or (alive and not expected[key][4]):
                if not alive:
                    continue
                expected[key] = [F(part), F(speed), F(coef), True, True]   # birth tick: no step (integration skips new particles)
                print(f"  tick {tick:3d} slot {i} born part {part:.3f} speed {speed:.4f} coef {coef:.4f}")
                continue
            ref = expected[key]
            ref[4] = bool(alive)
            if not ref[3]:
                continue
            # settings +0xeb7 = 0 (3d310f8): part += speed * dt with the old speed, then speed *= coef once per tick
            ref[0] = F(ref[0] + F(ref[1] * F(dt)))
            ref[1] = F(ref[1] * ref[2])
            want_alive = True
            if ref[0] >= limit:
                if play_type == 2:
                    ref[0], want_alive = F(limit - 1), False
                elif play_type == 3:
                    ref[0] = F(limit - 1)
                elif play_type == 1:
                    ref[0] = F(ref[0] - int(ref[0] / limit) * limit)
            seen += 1
            ok = abs(part - ref[0]) <= 1e-4 * max(1, abs(ref[0])) and bool(alive) == want_alive
            bad += not ok
            if not ok or tick % 8 == 0 or not want_alive:
                print(f"  tick {tick:3d} slot {i} part {part:8.4f} (ref {ref[0]:8.4f}) speed {speed:.4f} alive {alive} (ref {int(want_alive)}){'' if ok else '  MISMATCH'}")
            ref[3] = want_alive
    print(f"{name}: {seen} particle-steps, {bad} mismatches")
    return bad, seen


def main():
    img = rn.load()
    em.install_runtime(img)
    results = {name: run(img, name) for name in (sys.argv[1:] or CASES)}
    bad = sum(b for b, _ in results.values())
    print('RESULT ' + json.dumps(dict(exp='mesh', status='PASS' if bad == 0 else 'FAIL',
                                      cases={k: dict(steps=s, mismatches=b) for k, (b, s) in results.items()})))
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    main()
