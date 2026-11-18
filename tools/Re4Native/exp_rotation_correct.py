"""EmitterShape3D RotationCorrect on re4.exe with a fake camera.

    python -I -X utf8 -u exp_rotation_correct.py

3d21210 builds the shape rotation (shape block +0x40, three rows of three floats, stride 0x10). The camera lookup
(3a3f920) returns a fake object and both matrix getters (3a17660 for modes 1, 2; 3a17ad0 for modes 3, 4) write a
chosen matrix C (row-vector rows: right, up, forward, position). Modes 3 and 4 are evaluated only on the per-frame
pass (slot 23, 3d24710), which calls 3d21210 with r8b = 1 and r9 = the emitter position; the harness calls 3d21210
that way directly, after LocalRotation and RotationOrder are written into the block.

Checked on a temporary copy of csa013 saliva (RotationCorrect patched), two cameras x two emitter positions x three
LocalRotation / RotationOrder settings. With L = the mode-0 result (LocalRotation alone), R = L . B, where B is
  mode 1 ParallelCamera       transpose of C's 3x3
  mode 2 ParallelCameraAxisY  z = normalize(C[0][2], 0, C[2][2]); B = ((z.z, 0, -z.x), (0, 1, 0), z)
  mode 3 ToCamera             d = C[3] - emitter; z = normalize(d); x = normalize(C[1] x d); B = (x, z x x, z)
  mode 4 ToCameraAxisY        z = normalize(d.x, 0, d.z); B = ((z.z, 0, -z.x), (0, 1, 0), z)
The build pass (one tick, LocalRotation 0) is checked for modes 0-2: it leaves modes 3 and 4 at identity.
"""
import ctypes
import json
import math
import os
import struct
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
STM = r"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\RE4_EXTRACT\natives\stm"
SRC = STM + r"\_chainsaw\vfx\effecteditor\efd_event\cs\efd_csa013_s00\efd_0015_csa013_s00_saliva_0000.efx.3539837"
OFF = 137
SHAPE = 102


def patched(mode):
    """The source with RotationCorrect = mode in the entry's EmitterShape3D (payload found by its known values)."""
    data = bytearray(open(SRC, "rb").read())
    head = struct.pack("<6f", 0, 0.02, 0, 0, 0, 0) + struct.pack("<5I", 0, 0, 0, 0, 0) + struct.pack("<3f", 0, 0, 0)
    i = data.find(head, OFF)
    if not OFF < i < OFF + 0x800 or struct.unpack_from("<2I", data, i + 56) != (2, 0):
        raise RuntimeError("EmitterShape3D payload not where expected")
    struct.pack_into("<I", data, i + 60, mode)
    out = os.path.join(tempfile.gettempdir(), "re4native-rc", f"saliva_rc{mode}.efx.3539837")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    open(out, "wb").write(data)
    return out


def camera(yaw, pitch, roll, pos):
    """Row-vector rotation roll (Z), then pitch (X), then yaw (Y); rows right, up, forward, then the position."""
    def rx(a): return [[1, 0, 0], [0, math.cos(a), math.sin(a)], [0, -math.sin(a), math.cos(a)]]
    def ry(a): return [[math.cos(a), 0, -math.sin(a)], [0, 1, 0], [math.sin(a), 0, math.cos(a)]]
    def rz(a): return [[math.cos(a), math.sin(a), 0], [-math.sin(a), math.cos(a), 0], [0, 0, 1]]
    def mul(a, b): return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]
    R = mul(mul(rz(roll), rx(pitch)), ry(yaw))
    return [R[0] + [0], R[1] + [0], R[2] + [0], list(pos) + [1]]


def run(mode, C, pos, local, order):
    import emitter as em
    import re4native as rn
    img = rn.load()
    em.install_runtime(img)
    fake = img.alloc(0x400)
    buf = struct.pack("<16f", *[v for row in C for v in row])

    def write(rcx, rdx, r8, r9):
        ctypes.memmove(rdx, buf, 64)
        return rdx
    em.stub(img, 0x3A3F920, "camera lookup", lambda *a: fake)
    em.stub(img, 0x3A17660, "camera matrix", write)
    em.stub(img, 0x3A17AD0, "camera matrix (position)", write)
    m = em.Emitter(img, em.Entry(img, patched(mode), OFF))
    m.play()
    m.tick(1 / 60)
    blk = m.block_of(SHAPE)

    def rows():
        return [list(struct.unpack("<3f", img.read(blk + 0x40 + 0x10 * i, 12))) for i in range(3)]
    build = rows()
    ctypes.memmove(blk + 0x2c, struct.pack("<3f", *local), 12)
    ctypes.memmove(blk + 0x38, struct.pack("<I", order), 4)
    p = img.alloc(16)
    ctypes.memmove(p, struct.pack("<4f", *pos, 0), 16)
    fn = img.function(0x3D21210, None, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint8, ctypes.c_void_p)
    img.call(fn, None, blk, 1, p)
    return dict(build=build, frame=rows(), calls=sorted({c[0] for c in em.CALLS if c[0].startswith("camera")}))


def mul(a, b):
    return [[sum(a[i][k] * b[k][j] for k in range(3)) for j in range(3)] for i in range(3)]


def norm(v):
    n = math.sqrt(sum(x * x for x in v))
    return [x / n for x in v]


def cross(a, b):
    return [a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]]


def basis(mode, C, pos):
    """B for one mode; the shape rotation is L . B."""
    if mode == 0:
        return [[1, 0, 0], [0, 1, 0], [0, 0, 1]]
    if mode in (1, 2):
        T = [[C[j][i] for j in range(3)] for i in range(3)]
        if mode == 1:
            return T
        z = norm([T[2][0], 0.0, T[2][2]])
    else:
        d = [C[3][i] - pos[i] for i in range(3)]
        if mode == 3:
            z = norm(d)
            x = norm(cross(C[1][:3], d))
            return [x, cross(z, x), z]
        z = norm([d[0], 0.0, d[2]])
    return [[z[2], 0.0, -z[0]], [0.0, 1.0, 0.0], z]


def worst(a, b):
    return max(abs(x - y) for ra, rb in zip(a, b) for x, y in zip(ra, rb))


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "run":
        print("RESULT " + json.dumps(run(**json.loads(sys.argv[2]))))
        return
    cams = [camera(math.radians(30), math.radians(20), 0, (3, 2, 5)), camera(math.radians(-50), math.radians(-35), math.radians(10), (-1, 4, 2))]
    positions = [[1, 0.5, -2], [-4, 3, 6]]
    locals_ = [([0, 0, 0], 0), ([0.3, 0.5, 0.2], 0), ([-0.7, 0.4, 1.1], 3)]

    def one(**a):
        p = subprocess.run([sys.executable, "-I", "-X", "utf8", os.path.abspath(__file__), "run", json.dumps(a)],
                           capture_output=True, text=True, encoding="utf-8", errors="replace")
        line = next((l for l in p.stdout.splitlines() if l.startswith("RESULT ")), None)
        if line is None:
            raise RuntimeError(p.stdout[-400:] + p.stderr[-800:])
        return json.loads(line[7:])
    ok = True
    for ci, C in enumerate(cams):
        for pi, pos in enumerate(positions):
            for li, (local, order) in enumerate(locals_):
                L = one(mode=0, C=C, pos=pos, local=local, order=order)["frame"]
                for mode in range(1, 5):
                    r = one(mode=mode, C=C, pos=pos, local=local, order=order)
                    err = worst(r["frame"], mul(L, basis(mode, C, pos)))
                    if li == 0:
                        err = max(err, worst(r["build"], basis(mode if mode < 3 else 0, C, pos)))
                    ok &= err < 1e-5
                    print(json.dumps(dict(camera=ci, position=pi, local=li, mode=mode, worst=err, calls=r["calls"])))
    print("RESULT " + json.dumps(dict(exp="rotation_correct", checked_modes=[0, 1, 2, 3, 4], status="PASS" if ok else "FAIL")))


if __name__ == "__main__":
    main()
