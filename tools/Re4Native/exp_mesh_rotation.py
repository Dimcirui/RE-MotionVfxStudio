"""TypeMesh orientation, as the mesh instance builder (3d2f8f0, caller of 3d296f0) composes it:

  R = 3d13e50(angles, RotationOrder)     angles = particle +0x9c..+0xa4 (+ +0xa8..+0xb0 when [p+4] & 0x2000),
                                         Y and Z negated when the emitter is mirrored (E+0x16c bit 7)
  Q = dfbc10(record +0x10)               the FrontAxis quaternion of the mesh record (identity unless Flags bit 1)
  rotation = R * Q                       row vectors: R applied first

Checked here: 3d13e50 against the System.Numerics matrix of the Polygon quaternion rule (exp_polygon_quat), and
dfbc10 against the System.Numerics quaternion matrix, both row-vector (rows = transformed basis vectors).

  python -I -X utf8 -u exp_mesh_rotation.py
"""
import ctypes, json, math, random, sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parent))
import re4native as rn  # noqa: E402
from exp_polygon_quat import rule  # noqa: E402


def matrix(q):
    """System.Numerics Matrix4x4.CreateFromQuaternion, 3x3 rows."""
    x, y, z, w = q
    return [[1 - 2 * (y * y + z * z), 2 * (x * y + z * w), 2 * (x * z - y * w)],
            [2 * (x * y - z * w), 1 - 2 * (x * x + z * z), 2 * (y * z + x * w)],
            [2 * (x * z + y * w), 2 * (y * z - x * w), 1 - 2 * (x * x + y * y)]]


def rows(img, out, stride=16):
    return [[rn.f32(out + stride * r + 4 * c) for c in range(3)] for r in range(3)]


def error(a, b):
    return max(abs(a[r][c] - b[r][c]) for r in range(3) for c in range(3))


def main():
    img = rn.load(rn.DEFAULT_EXE)
    euler = img.function(0x3D13E50, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32)
    quat = img.function(0xDFBC10, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p)
    out, eul, q = img.alloc(64), img.alloc(16), img.alloc(16)
    random.seed(11)
    worst_euler, worst_euler_t, worst_quat, worst_quat_t = 0.0, 0.0, 0.0, 0.0
    for order in range(6):
        for _ in range(200):
            x, y, z = (random.uniform(-6.3, 6.3) for _ in range(3))
            for i, v in enumerate((x, y, z, 0.0)): rn.set_f32(eul + 4 * i, v)
            img.call(euler, out, eul, order)
            n = rows(img, out)
            ref = matrix(rule(x, y, z, order))
            worst_euler = max(worst_euler, error(n, ref))
            worst_euler_t = max(worst_euler_t, error(n, [list(c) for c in zip(*ref)]))
    for _ in range(500):
        v = [random.uniform(-1, 1) for _ in range(4)]
        k = math.sqrt(sum(c * c for c in v)); v = [c / k for c in v]
        for i, c in enumerate(v): rn.set_f32(q + 4 * i, c)
        img.call(quat, out, q)
        n = rows(img, out)
        ref = matrix(v)
        worst_quat = max(worst_quat, error(n, ref))
        worst_quat_t = max(worst_quat_t, error(n, [list(c) for c in zip(*ref)]))
    ok = worst_euler < 1e-5 and worst_quat < 1e-5
    print(f"3d13e50 vs Polygon rule matrix: {worst_euler:.3g} (transposed {worst_euler_t:.3g}); "
          f"dfbc10 vs quaternion matrix: {worst_quat:.3g} (transposed {worst_quat_t:.3g})")
    print('RESULT ' + json.dumps(dict(exp='mesh_rotation', status='PASS' if ok else 'FAIL',
                                      euler_error=worst_euler, quat_error=worst_quat)))
    sys.exit(0 if ok else 1)


if __name__ == '__main__':
    main()
