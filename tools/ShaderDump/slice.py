"""Cut a RE Engine .vsdf shader package into its DXBC containers and disassemble each with dxc.

    python -I slice.py <file.vsdf.*> <output folder> [--pseudo]

Every container found at a "DXBC" magic (its size is the u32 at +24) is written as bNNN_<file offset>.dxbc, and
`dxc -dumpbin` output goes next to it as .ll. With --pseudo, each pixel and vertex shader is also run through
dxil2c.py into a .c file. dxc comes from the DXC environment variable, else the newest Windows SDK. The package
is only read.
"""
import glob
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def find_dxc():
    if os.environ.get("DXC"):
        return os.environ["DXC"]
    kits = sorted(glob.glob(r"C:\Program Files (x86)\Windows Kits\10\bin\10.*\x64\dxc.exe"))
    if not kits:
        sys.exit("dxc.exe not found: install the Windows SDK or set DXC")
    return kits[-1]


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    if len(args) != 2:
        sys.exit(__doc__)
    src, out = args
    pseudo = "--pseudo" in sys.argv
    dxc = find_dxc()
    os.makedirs(out, exist_ok=True)
    data = open(src, "rb").read()
    i = n = 0
    while True:
        i = data.find(b"DXBC", i)
        if i < 0:
            break
        size = struct.unpack_from("<I", data, i + 24)[0]
        if not 32 < size <= len(data) - i:
            i += 4
            continue
        path = os.path.join(out, f"b{n:03d}_{i:x}.dxbc")
        open(path, "wb").write(data[i:i + size])
        r = subprocess.run([dxc, "-dumpbin", path], capture_output=True, text=True, errors="replace")
        ll = path[:-5] + ".ll"
        open(ll, "w", encoding="utf-8").write(r.stdout + r.stderr)
        if pseudo and "define void @" in r.stdout:
            c = subprocess.run([sys.executable, "-I", os.path.join(HERE, "dxil2c.py"), ll], capture_output=True,
                               text=True, encoding="utf-8", errors="replace")
            open(path[:-5] + ".c", "w", encoding="utf-8").write(c.stdout + c.stderr)
        n += 1
        i += size
    print(f"{n} blocks -> {out}")


if __name__ == "__main__":
    main()
