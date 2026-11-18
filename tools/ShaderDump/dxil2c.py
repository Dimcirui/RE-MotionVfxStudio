"""Turn `dxc -dumpbin` DXIL text into readable pseudocode.

    python -I dxil2c.py <file.ll> [--no-inline] > out.c

cbuffer loads become member names (from the reflection comment block), loadInput becomes the input signature
name, single-use pure values are inlined. Control flow stays as labelled blocks with phi lines.
"""
import re
import sys
from collections import Counter, defaultdict

src = open(sys.argv[1], encoding="utf-8", errors="replace").read()
INLINE = "--no-inline" not in sys.argv
lines = src.splitlines()

# --- reflection: cbuffer members per cbuffer name -----------------------------------
members = defaultdict(dict)   # cbname -> {byte offset: (name, size)}
cur = None
for l in lines:
    m = re.match(r";\s+cbuffer (\w+)", l)
    if m:
        cur = m.group(1)
        continue
    if cur:
        m = re.match(r";\s+(\w+)\s+(\w+)(\[\d+\])?;\s+; Offset:\s+(\d+)", l)
        if m:
            t, n, arr, off = m.group(1), m.group(2), m.group(3), int(m.group(4))
            comps = {"float": 1, "uint": 1, "int": 1, "float2": 2, "uint2": 2, "int2": 2, "float3": 3, "uint3": 3,
                     "int3": 3, "float4": 4, "uint4": 4, "int4": 4, "float4x4": 16, "float3x4": 12, "float4x3": 12,
                     "row_major": 16}.get(t, 4)
            members[cur][off] = (n, comps)
res_names = {}  # (class, range id) -> name ; class 0 = SRV, 1 = UAV, 2 = CBV, 3 = sampler
reg_names = {}  # (class, register, space) -> name
for l in lines:
    m = re.match(r";\s+(\w+)\s+(texture|sampler|cbuffer|UAV)\s+\S+\s+\S+\s+\S+\s+([tsub]|cb)(\d+)(?:,space(\d+))?", l)
    if m:
        cls = {"texture": 0, "UAV": 1, "cbuffer": 2, "sampler": 3}[m.group(2)]
        reg_names[(cls, int(m.group(4)), int(m.group(5) or 0))] = m.group(1)
for l in lines:
    m = re.match(r";\s+(\w+)\s+(texture|sampler|cbuffer|UAV)\s+\S+\s+\S+\s+([TSU]|CB)(\d+)", l)
    if m:
        cls = {"texture": 0, "UAV": 1, "cbuffer": 2, "sampler": 3}[m.group(2)]
        res_names[(cls, int(m.group(4)))] = m.group(1)

def member_name(cb, row, comp):
    off = row * 16 + comp * 4
    best = None
    for o, (n, c) in members.get(cb, {}).items():
        if o <= off < o + 4 * c:
            k = (off - o) // 4
            best = n if c == 1 else f"{n}.{'xyzw'[k]}" if c <= 4 else f"{n}[{k}]"
    return best or f"{cb}[{row}].{'xyzw'[comp]}"

# --- signatures -----------------------------------------------------------------------------------------------
def signature(title):
    out = {}
    i = next((k for k, l in enumerate(lines) if l.startswith(f"; {title}:")), None)
    if i is None:
        return out
    idx = 0
    for l in lines[i + 4:]:
        if not l.startswith(";") or l.strip() == ";":
            break
        parts = l[1:].split()
        if len(parts) >= 4:
            out[idx] = f"{parts[0]}{parts[1]}"
            idx += 1
    return out
ins = signature("Input signature")
outs = signature("Output signature")

# --- function body --------------------------------------------------------------------------------------------
body_start = next(k for k, l in enumerate(lines) if l.startswith("define void @"))
body = []
for l in lines[body_start + 1:]:
    if l.startswith("}"):
        break
    body.append(l)

handles = {}  # %n -> resource name
defs = {}     # %n -> expr string
pure = {}
uses = Counter()
stmts = []    # (label or None, text, defined var or None)
for l in body:
    for v in re.findall(r"%[\w.]+", l.split(";")[0]):
        uses[v] += 1
OPS = {"fadd": "+", "fsub": "-", "fmul": "*", "fdiv": "/", "add": "+", "sub": "-", "mul": "*", "and": "&", "or": "|",
       "xor": "^", "shl": "<<", "lshr": ">>", "ashr": ">>a", "udiv": "/u", "urem": "%u", "sdiv": "/", "srem": "%"}
CMP = {"oeq": "==", "one": "!=", "olt": "<", "ole": "<=", "ogt": ">", "oge": ">=", "ueq": "==", "une": "!=",
       "ult": "<", "ule": "<=", "ugt": ">", "uge": ">=", "eq": "==", "ne": "!=", "slt": "<", "sle": "<=",
       "sgt": ">", "sge": ">="}
UNARY = {6: "abs", 7: "saturate", 13: "cos", 12: "sin", 21: "exp2", 23: "log2", 24: "sqrt", 25: "rsqrt",
         26: "round_ne", 27: "floor", 28: "ceil", 29: "trunc", 22: "frac", 35: "fmax", 36: "fmin", 37: "imax",
         38: "imin", 39: "umax", 40: "umin", 46: "mad", 47: "fma", 48: "imad", 49: "umad", 55: "dot2", 56: "dot3",
         57: "dot4", 83: "ddx_coarse", 84: "ddy_coarse", 85: "ddx_fine", 86: "ddy_fine", 14: "tan", 17: "atan",
         15: "acos", 16: "asin", 31: "countbits", 30: "reversebits", 32: "firstbithi", 33: "firstbitlo"}

def val(t):
    t = t.strip()
    if " " in t and t.split()[-1].startswith("%"):
        return t.split()[-1]
    m = re.match(r"(?:float|i32|i1|half|i16|i64|double|i8)\s+(.*)", t)
    if m:
        t = m.group(1).strip()
    if t.startswith("%"):
        return t
    if re.match(r"0x[0-9A-Fa-f]{16}$", t):
        import struct
        d = struct.unpack("<d", int(t, 16).to_bytes(8, "little"))[0]
        return repr(float(f"{d:.7g}"))
    return t

def split_args(s):
    out, depth, cur = [], 0, ""
    for ch in s:
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        if ch == "," and depth == 0:
            out.append(cur)
            cur = ""
        else:
            cur += ch
    if cur.strip():
        out.append(cur)
    return out

label = "entry"
for raw in body:
    l = raw.split(";")[0].rstrip()
    m = re.match(r"^(\d+):", raw) or re.match(r"^; <label>:(\d+)", raw)
    if m:
        label = m.group(1)
        stmts.append((label, None, None))
        continue
    if not l.strip():
        continue
    m = re.match(r"\s*(%[\w.]+) = (.*)", l)
    lhs, rhs = (m.group(1), m.group(2)) if m else (None, l.strip())
    e, is_pure = None, True
    if "@dx.op.createHandle(" in rhs:
        a = [x.strip() for x in split_args(re.search(r"createHandle\((.*)\)", rhs).group(1))]
        cls, rid = int(val(a[1])), int(val(a[2]))
        handles[lhs] = res_names.get((cls, rid), f"res{cls}_{rid}")
        continue
    if "@dx.op.createHandleFromBinding(" in rhs:
        mm = re.search(r"ResBind (?:\{ i32 (\d+), i32 \d+, i32 (\d+), i8 (\d+) \}|zeroinitializer)", rhs)
        reg, space, cls = (int(mm.group(1)), int(mm.group(2)), int(mm.group(3))) if mm.group(1) else (0, 0, 0)
        handles[lhs] = reg_names.get((cls, reg, space), f"res{cls}_r{reg}")
        continue
    if "@dx.op.annotateHandle(" in rhs:
        src_h = re.search(r"annotateHandle\(i32 \d+, %dx.types.Handle (%[\w.]+)", rhs).group(1)
        handles[lhs] = handles.get(src_h, src_h)
        continue
    m2 = re.match(r"call [^@]*@dx\.op\.(\w+)(?:\.\w+)?\((.*)\)", rhs)
    if m2:
        op, a = m2.group(1), [val(x) for x in split_args(m2.group(2))]
        if op == "cbufferLoadLegacy":
            row = int(a[2]) if a[2].isdigit() else a[2]   # a computed row stays an SSA name
            defs[lhs] = ("CB", handles.get(a[1], a[1]), row)
            continue
        if op == "loadInput":
            e = f"in.{ins.get(int(a[1]), 'in' + a[1])}[{a[3]}]" + (f"@{a[2]}" if a[2] != "0" else "")
        elif op == "storeOutput":
            e = f"out.{outs.get(int(a[1]), 'o' + a[1])}[{a[3]}] = {a[4]}"
            is_pure = False
        elif op in ("unary", "binary", "tertiary", "dot2", "dot3", "dot4", "unaryBits", "binaryWithCarry", "quaternary",
                    "isSpecialFloat", "legacyF32ToF16", "legacyF16ToF32", "bitcastI32toF32"):
            code = int(a[0])
            name = UNARY.get(code, f"op{code}")
            e = f"{name}({', '.join(a[1:])})"
        elif op.startswith("sample") or op.startswith("textureLoad") or op.startswith("bufferLoad") or op.startswith("textureGather"):
            e = f"{op}({handles.get(a[1], a[1])}, {', '.join(x for x in a[2:] if x not in ('undef',))})"
            is_pure = False
        else:
            e = f"{op}({', '.join(a[1:])})"
            is_pure = op not in ("discard", "storeOutput", "bufferStore", "textureStore")
    elif rhs.startswith("extractvalue"):
        mm = re.match(r"extractvalue \S+ (%[\w.]+), (\d+)", rhs)
        src_v, idx = mm.group(1), int(mm.group(2))
        if src_v in defs and isinstance(defs[src_v], tuple):
            _, cb, row = defs[src_v]
            e = member_name(cb, row, idx) if isinstance(row, int) else f"{cb}[{row}].{'xyzw'[idx]}"
        else:
            e = f"{src_v}.{'xyzw'[idx] if idx < 4 else idx}"
    elif re.match(r"(f?(add|sub|mul|div)|and|or|xor|shl|lshr|ashr|udiv|urem|sdiv|srem)\b", rhs):
        mm = re.match(r"(\w+)(?: (?:fast|nsz|arcp|contract|reassoc|nnan|ninf|afn|nuw|nsw|exact))* (\w+) (.*)", rhs)
        a = [val(x) for x in split_args(mm.group(3))]
        e = f"({a[0]} {OPS[mm.group(1)]} {a[1]})"
    elif rhs.startswith(("fcmp", "icmp")):
        mm = re.match(r"[fi]cmp(?: fast)? (\w+) \w+ (.*)", rhs)
        a = [val(x) for x in split_args(mm.group(2))]
        e = f"({a[0]} {CMP.get(mm.group(1), mm.group(1))} {a[1]})"
    elif rhs.startswith("select"):
        a = [val(x) for x in split_args(rhs[len("select "):])]
        e = f"({a[0]} ? {a[1]} : {a[2]})"
    elif re.match(r"(sitofp|uitofp|fptosi|fptoui|bitcast|zext|sext|trunc|fpext|fptrunc)\b", rhs):
        mm = re.match(r"(\w+) .*?(%[\w.]+|-?[\w.+]+) to .+$", rhs)
        e = f"{mm.group(1)}({val(mm.group(2))})"
    elif rhs.startswith("phi"):
        pairs = re.findall(r"\[\s*([^,\]]+),\s*%(\d+|\w+)\s*\]", rhs)
        e = "phi(" + ", ".join(f"{val(v)} <- {b}" for v, b in pairs) + ")"
        is_pure = False
    elif rhs.startswith("br "):
        mm = re.match(r"br i1 (%[\w.]+), label %(\w+), label %(\w+)", rhs)
        if mm:
            e = f"if {mm.group(1)} goto {mm.group(2)} else {mm.group(3)}"
        else:
            e = "goto " + re.search(r"label %(\w+)", rhs).group(1)
        is_pure = False
    elif rhs.startswith("ret"):
        e = "return"
        is_pure = False
    else:
        e = rhs
        is_pure = False
    if lhs:
        defs[lhs] = e
        pure[lhs] = is_pure
    stmts.append((None, e, lhs))

def expand(e, depth=0):
    if not INLINE or depth > 40:
        return e
    def rep(m):
        v = m.group(0)
        d = defs.get(v)
        if isinstance(d, str) and pure.get(v) and uses[v] <= 2 and len(d) < 160:
            return expand(d, depth + 1)
        return v
    return re.sub(r"%[\w.]+", rep, e)

for lab, e, lhs in stmts:
    if e is None:
        print(f"\n{lab}:")
        continue
    if lhs and INLINE and pure.get(lhs) and uses[lhs] <= 2 and len(defs[lhs]) < 160:
        continue
    text = expand(e)
    print(f"  {lhs} = {text}" if lhs else f"  {text}")
