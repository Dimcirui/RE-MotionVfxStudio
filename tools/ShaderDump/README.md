# ShaderDump：读 RE Engine 的 .vsdf 着色器

把 `.vsdf` 着色器包切成 DXBC 块，用 dxc 反汇编，再转成能直接读公式的伪代码。RE4 的 CPU 类型（Billboard3D、Polygon、条带等）用 `systems/shader/primitivevfx*.vsdf`；GPU 类型用 `systems/shader/vfx/*.vsdf`。

## 用法

```bash
python -I tools/ShaderDump/slice.py "<RE4_EXTRACT>/natives/stm/systems/shader/primitivevfx.vsdf.221007880" <输出目录> --pseudo
```

输出目录里每个块有三个文件：
- `bNNN_<文件偏移>.dxbc`：原始块；
- `.ll`：`dxc -dumpbin` 的反汇编，开头有常量缓冲区布局、输入输出签名和资源绑定；
- `.c`（加 `--pseudo` 时）：`dxil2c.py` 的伪代码。

单独转一个块：

```bash
python -I tools/ShaderDump/dxil2c.py <块.ll> > 块.c
```

dxc 取环境变量 `DXC`，没有时取最新的 Windows SDK（`Windows Kits\10\bin\10.*\x64\dxc.exe`）。脚本只读着色器包。

## 伪代码的约定

- 常量缓冲区读取写成成员名（`cbIntensity3D`、`cbOffset.x`），按 `.ll` 里的反射块换算偏移；
- `loadInput` 写成输入签名名加分量（`in.Color0[3]`），`storeOutput` 写成 `out.SV_Target0[0] = …`；
- 只用一两次、长度不超过 160 字符的纯表达式内联到使用处，`--no-inline` 关闭；
- 控制流保留为编号块：`if %x goto A else B`、`switch`、`phi(值 <- 来源块, …)`；
- `dx.op` 按编号写成函数名（`saturate`、`exp2`、`dot2`…），未收录的写成 `opNNN`。

同时支持 SM 6.0 的 `createHandle` 与 SM 6.6 的 `createHandleFromBinding` + `annotateHandle`。

## 找变体

一个包有几百个变体。先按常量成员或分支结构筛，再读其中一个：

```bash
grep -l "cbEdgeBlendRange3D" <输出目录>/*.c
grep -l "switch i32 ((%[0-9]* >> 11) & 15)" <输出目录>/*.c
```

读出的公式记在 [RE4-EFX-RUNTIME](../../docs/development/RE4-EFX-RUNTIME.zh-CN.md) §7。
