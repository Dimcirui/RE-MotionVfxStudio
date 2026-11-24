// TypeMesh in the native preview: documents, the worker's part records, and the instance decode.
// Usage: dotnet run -c Release --project tools/Re4MeshTests [-- --corpus [limit]]
using System.Numerics;
using ReeLib;
using ReeLib.Efx;
using ReeLib.Efx.Structs.Main;
using VfxStudio.Re4Render;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

Console.OutputEncoding = new System.Text.UTF8Encoding(false);
string exe = Environment.GetEnvironmentVariable("RE4_EXE") ?? @"E:\Program\Steam\steamapps\common\RESIDENT EVIL 4  BIOHAZARD RE4\re4.exe";
string game = Path.GetDirectoryName(exe)!;
string repo = FindRepository();
string script = Path.Combine(repo, "tools", "Re4Native", "re4host.py");
string stm = Path.Combine(game, "RE4_EXTRACT", "natives", "stm");
using var resources = new Re4ResourceSource(game, Path.Combine(game, "RE4_EXTRACT"));
int checks = 0, failures = 0;
void Check(bool ok, string name, object? detail = null)
{
    checks++;
    Console.WriteLine((ok ? "ok   " : "FAIL ") + name + (detail == null ? "" : " :: " + detail));
    if (!ok) failures++;
}

// 1. Part = mesh group: MaxPartsNum equals the LOD0 group count of the mesh it names.
string damage = Path.Combine(stm, @"_anotherorder\vfx\effecteditor\efd_character\efd_ao_chd7\efd_0015_ao_chd7_damage_0000.efx.3539837");
const int DamageEntry = 0x23F5;   // PlayType 2 (Finish), MaxPartsNum 28, PartsStartNo 15..16, PlaySpeed 3
var doc = PreviewDocument.Read(damage, DamageEntry, resources);
var meshType = (EFXAttributeTypeMeshV2)doc.Entry.TypeAttribute!;
Check(doc.Kind == PreviewTypeKind.Mesh && doc.Mesh != null, "TypeMesh entry reads as a Mesh document");
Check(doc.Mesh!.Groups.Length == meshType.MaxPartsNum, "mesh groups = MaxPartsNum", $"{doc.Mesh.Groups.Length} groups, MaxPartsNum {meshType.MaxPartsNum}, {doc.Mesh.MeshPath}");
Check(doc.Mesh.Groups.All(g => g.Count > 0 && g.Count % 3 == 0 && g.First + g.Count <= doc.Mesh.Indices.Length), "every group has whole triangles inside the index buffer");
Check(doc.Mesh.Indices.All(i => i < doc.Mesh.Vertices.Length / PreviewMeshData.VertexFloats), "indices stay inside the vertex buffer");
Check(!doc.Mesh.Albedo.Placeholder, "albedo found through the .mdf2 (pak)", doc.Mesh.Albedo.Source);
Check(ColorHeader.From(new PreviewEntryState(doc.Entry.index, 0, 0, new byte[0x2f8], Type: 28), doc) is { Blend: 0 } h && h.ColorRate == meshType.ColorRate,
    "colour header from the file (opaque, ColorRate)");

// 2. Native worker: the part records follow PlayType 2 (15 -> 27 by 3 per tick, then the particle dies).
using var client = new NativePreviewClient("python", script, exe);
var excluded = new Dictionary<int, string>();
var scene = await PreviewSceneLoader.LoadAsync(client, damage, resources, excluded);
int table = Enumerable.Range(0, scene.Shown.Count).FirstOrDefault(t => scene.Shown[t].Offset == DamageEntry, -1);
Check(table >= 0 && scene.Shown[table].Document?.Kind == PreviewTypeKind.Mesh, "the loader shows the TypeMesh entry",
    string.Join("; ", scene.Entries.Where(e => !e.Shown).Select(e => $"{e.Label} {e.Type}: {e.Excluded}")));
if (table >= 0)
{
    var parts = new List<int>(); int bornAt = -1, diedAt = -1;
    for (int tick = 1; tick <= 120 && diedAt < 0; tick++)
    {
        using var frame = await client.FrameAsync(tick);
        var state = frame.EntryStates![table];
        Check(state.Type == 28, $"tick {tick}: entry type 28", null);
        if (state.Count > 0)
        {
            var p = frame.Particles[state.First];
            if (bornAt < 0) bornAt = tick;
            parts.Add(state.MeshPart(p.Slot));
            var item = MeshInstance.Decode(p, state);
            if (!item.Finite) Check(false, $"tick {tick}: finite instance matrix");
            if (tick == bornAt) Console.WriteLine($"     born tick {tick}: pos {p.Position}, size U {FrameDecoder.F32(p.Raw.Span, 0x80)}, world scale {new Vector3(item.World.M11, item.World.M22, item.World.M33)}, colour 0x{item.Color:x8}");
        }
        else if (bornAt >= 0) diedAt = tick;
    }
    Check(parts.Count > 0 && parts[0] == 15 && parts.Zip(parts.Skip(1)).All(z => z.Second == Math.Min(z.First + 3, 27)) && parts[^1] == 27,
        "part sequence 15, 18, ... 27", string.Join(",", parts));
    Check(diedAt == bornAt + parts.Count, "the particle dies after its last part (PlayType 2)", $"born {bornAt}, gone at {diedAt}");
}

// 1b. A mesh that is not set, not found or not readable becomes a checker cube (one group), named in Missing, so the
// entry still runs and shows its particles.
{
    var missingType = new EFXAttributeTypeMeshV2 { Version = EfxVersion.RE4, MeshPath = "Mods/NoSuch/mesh_missing_00.mesh", MaterialPath = meshType.MaterialPath };
    var cube = PreviewMeshData.Load(missingType, resources);
    Check(cube.MeshPath == PreviewMeshData.PlaceholderKey && cube.Groups.Length == 1 && cube.Indices.Length == 36 && cube.Albedo.Placeholder &&
          cube.Missing.Single().StartsWith("Mods/NoSuch/mesh_missing_00.mesh"), "a missing mesh becomes the checker cube and is listed as missing", cube.Missing.FirstOrDefault());
    var empty = PreviewMeshData.Load(new EFXAttributeTypeMeshV2 { Version = EfxVersion.RE4 }, resources);
    Check(empty.MeshPath == PreviewMeshData.PlaceholderKey, "a TypeMesh without MeshPath becomes the checker cube too", empty.Missing.FirstOrDefault());

    // End to end: a copy of the damage file whose splash entry names a mesh that does not exist. Read and written
    // through path-less memory handlers (ReeLib's WriteTo saves to the handler's path otherwise).
    var efx = new EfxFile(new FileHandler(new MemoryStream(File.ReadAllBytes(damage))) { FileVersion = (int)EfxVersion.RE4 });
    efx.Read();
    var splash = efx.Entries.Single(e => e.Start == DamageEntry).Attributes.OfType<EFXAttributeTypeMeshV2>().Single();
    splash.MeshPath = "Mods/NoSuch/mesh_missing_00.mesh";
    var output = new MemoryStream();
    Check(efx.WriteTo(new FileHandler(output) { FileVersion = (int)EfxVersion.RE4 }, saveFile: false), "the edited copy serialises");
    string copy = Path.Combine(Path.GetTempPath(), "re4meshtests", Path.GetFileName(damage));
    Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
    File.WriteAllBytes(copy, output.ToArray());
    using var probe = new NativePreviewClient("python", script, exe);
    var copyScene = await PreviewSceneLoader.LoadAsync(probe, copy, resources, new Dictionary<int, string>());
    var shownSplash = copyScene.Shown.FirstOrDefault(e => e.Document?.Mesh?.MeshPath == PreviewMeshData.PlaceholderKey);
    int alive = 0;
    if (shownSplash != null)
        for (int tick = 1; tick <= 3; tick++)
        {
            using var frame = await probe.FrameAsync(tick);
            alive = Math.Max(alive, frame.EntryStates![copyScene.Shown.ToList().IndexOf(shownSplash)].Count);
        }
    Check(shownSplash != null && shownSplash.Missing.Any(m => m.Contains("mesh_missing_00")) && alive > 0,
        "the entry with the missing mesh is still shown, simulated and reports the missing path", $"{shownSplash?.Label}, alive {alive}");
}

// 2a. Material UV transform (shader_basic_standard, mmtr PS b048): use_UVScroll gates Scale / Offset / Speed.
{
    // Wood debris whose entry turns use_UVScroll on and overrides U_Scale 3 / V_Scale 1 (Range type: <0, 0, v, v>).
    string tiled = Path.Combine(stm, @"_chainsaw\vfx\effecteditor\efd_setmodel\efd_sm23_507_10\efd_0015_sm23_507_10_0000.efx.3539837");
    var scroll = PreviewDocument.Read(tiled, 0x5B1, resources).Mesh!.UvScroll;
    Check(scroll is { } u && u.Scale == new Vector2(3, 1) && u.Offset == Vector2.Zero && u.Speed == Vector2.Zero,
        "use_UVScroll override switches the UV transform on with the overridden scale", scroll);
    // The damage splash uses the meat material as is: use_UVScroll 0, no transform.
    Check(doc.Mesh.UvScroll == null, "material with use_UVScroll 0 keeps the mesh UV");
    var s1 = new MeshUvScroll(Vector2.One, Vector2.Zero, new Vector2(0.25f, -1.5f));
    var shift = s1.Shift(5000);   // frac(0.25 · 5) = 0.25, frac(-1.5 · 5) = frac(-7.5) = 0.5
    Check(Math.Abs(shift.X - 0.25f) < 1e-6f && Math.Abs(shift.Y - 0.5f) < 1e-6f, "scroll term frac(Speed · 0.001 · ms)", shift);
}

// 2b. FrontAxis (Flags bit 1): the record quaternion Q turns the file's FrontAxis onto the particle's velocity
// (+0x5c, same emitter-local frame), as the post-process 3d31390 keeps it; the Euler rotation R is a local
// pre-rotation (vertex · R · Q, the builder's row products), so the spear's RotationX 80° tilts the mesh off it.
string spear = Path.Combine(stm, @"_anotherorder\vfx\effecteditor\efd_character\efd_ao_chfa\efd_0015_ao_chfa_0000_spear_0000.efx.3539837");
const int SpearEntry = 0x1B11;   // TypeMesh Flags 0x102, FrontAxis Z, RotationX 1.396
{
    var spearScene = await PreviewSceneLoader.LoadAsync(client, spear, resources, new Dictionary<int, string>());
    int t = Enumerable.Range(0, spearScene.Shown.Count).FirstOrDefault(i => spearScene.Shown[i].Offset == SpearEntry, -1);
    var axisOf = ((EFXAttributeTypeMeshV2)spearScene.Shown[Math.Max(t, 0)].Document!.Entry.TypeAttribute!).FrontAxis;
    var local = (int)axisOf switch { 0 => Vector3.UnitX, 1 => Vector3.UnitY, _ => Vector3.UnitZ };
    // Q is smoothed towards the velocity (min(dt·k, 1)), so it trails a path bent by gravity: compare on average.
    int samples = 0; double dots = 0; float worst = 1, composed = float.NaN;
    for (int tick = 1; t >= 0 && tick <= 60; tick++)
    {
        using var frame = await client.FrameAsync(tick);
        var st = frame.EntryStates![t];
        for (int k = st.First; k < st.First + st.Count; k++)
        {
            var p = frame.Particles[k];
            var v = new Vector3(FrameDecoder.F32(p.Raw.Span, 0x5c), FrameDecoder.F32(p.Raw.Span, 0x60), FrameDecoder.F32(p.Raw.Span, 0x64));
            var q = st.MeshOrientation(p.Slot);
            if (v.LengthSquared() < 1e-10f || q.IsIdentity) continue;
            var front = Vector3.Normalize(Vector3.Transform(local, q));
            float dot = Vector3.Dot(front, Vector3.Normalize(v));
            dots += dot; worst = Math.Min(worst, dot);
            // (local axis) · R · Q against (local axis) · Q: the angle R puts between the mesh and its front.
            composed = Vector3.Dot(Vector3.Normalize(Vector3.TransformNormal(local, MeshInstance.Rotation(p.Raw.Span, st, p.Slot))), front);
            samples++;
        }
    }
    Check(t >= 0 && samples > 10 && dots / samples > 0.99 && worst > 0.95f, "FrontAxis quaternion follows the velocity",
        $"{samples} particle-frames, mean dot {dots / Math.Max(1, samples):F4}, worst {worst:F4}");
    Check(Math.Abs(composed - MathF.Cos(1.3962635f)) < 1e-3f, "R · Q keeps RotationX 80° between the mesh axis and its front",
        $"{composed:F4}, cos 80° = {MathF.Cos(1.3962635f):F4}");
}

// 3. Corpus sample: files with TypeMesh entries load, mesh entries are shown, frames stay finite.
if (args.Contains("--corpus"))
{
    int limit = args.SkipWhile(a => a != "--corpus").Skip(1).Select(a => int.TryParse(a, out int n) ? n : 0).FirstOrDefault(40);
    int files = 0, shown = 0, shownChildren = 0, meshEntries = 0, drawnParticles = 0, frameFaults = 0, otherFaults = 0, simulationNaN = 0;
    var reasons = new Dictionary<string, int>();
    foreach (var path in Directory.EnumerateFiles(stm, "*.efx.3539837", SearchOption.AllDirectories))
    {
        if (files >= limit) break;
        EfxFile efx;
        try { efx = new EfxFile(new FileHandler(File.OpenRead(path), path) { FileVersion = (int)EfxVersion.RE4 }); efx.Read(); } catch { continue; }
        if (!efx.Entries.Any(e => e.TypeAttribute is EFXAttributeTypeMeshV2)) continue;
        if (args.SkipWhile(a => a != "--only").Skip(1).FirstOrDefault() is { } only && !path.Contains(only)) continue;
        files++;
        var ex = new Dictionary<int, string>();
        PreviewScene s;
        try { s = await PreviewSceneLoader.LoadAsync(client, path, resources, ex); }
        catch (Exception e) { reasons["load: " + e.GetType().Name] = reasons.GetValueOrDefault("load: " + e.GetType().Name) + 1; continue; }
        int[] MeshTables() => Enumerable.Range(0, s.Shown.Count).Where(t => s.Shown[t].Document?.Kind == PreviewTypeKind.Mesh).ToArray();
        var meshTables = MeshTables();
        meshEntries += s.Entries.Count(e => e.Type == "TypeMesh");
        shown += meshTables.Count(t => !s.Shown[t].Embedded);
        shownChildren += meshTables.Count(t => s.Shown[t].Embedded);
        foreach (var e in s.Entries.Where(e => e.Type == "TypeMesh" && !e.Shown))
        {
            string key = (e.Excluded ?? "?").Split('：', ':')[0];
            reasons[key] = reasons.GetValueOrDefault(key) + 1;
        }
        bool finite = true;
        for (int tick = 1; tick <= 90; tick += 1)
        {
            PreviewFrame frame;
            try { frame = await client.FrameAsync(tick); }
            catch (Exception e) when (PreviewSceneLoader.ExcludeFailedEntry(s, e, ex, tick) is { } failed)
            {
                // As in the window: the failing entry is excluded and the file reloads. Faults in mesh entries count.
                // PtColliderAction's child-action walk faults at 3d62ce8 for Billboard3D entries as well: a shared issue,
                // counted with the other entries.
                bool collider = failed.Document?.Entry.Attributes.Any(x => x is ReeLib.Efx.Structs.Pt.EFXAttributePtColliderAction) == true;
                if (failed.Type == "TypeMesh" && !collider)
                {
                    frameFaults++;
                    Console.WriteLine($"     mesh entry {failed.Label} fault at tick {tick} in {Path.GetFileName(path)}: {Short(PreviewSceneLoader.Summarize(e.Message))}");
                }
                else
                {
                    otherFaults++;
                    Console.WriteLine($"     other entry{(collider ? " (PtColliderAction)" : "")} {failed.Label} {failed.Type} fault at tick {tick} in {Path.GetFileName(path)}: {Short(PreviewSceneLoader.Summarize(e.Message))}");
                }
                try { s = await PreviewSceneLoader.LoadAsync(client, path, resources, ex); }
                catch (NotSupportedException) { break; }   // nothing previewable is left in the file
                meshTables = MeshTables();
                tick = 0;
                continue;
            }
            using (frame)
                foreach (int t in meshTables)
                {
                    var st = frame.EntryStates![t];
                    for (int k = st.First; k < st.First + st.Count; k++)
                    {
                        var item = MeshInstance.Decode(frame.Particles[k], st);
                        var pos = frame.Particles[k].Position;
                        if (!float.IsFinite(pos.X) || !float.IsFinite(pos.Y) || !float.IsFinite(pos.Z)) { simulationNaN++; continue; }   // the engine's own NaN (e.g. Transform3DExpression); the renderer skips it
                        if (!item.Finite && finite)
                        {
                            var raw = frame.Particles[k].Raw;
                            string F(int o) => FrameDecoder.F32(raw.Span, o).ToString("G4");
                            var q = st.MeshOrientation(frame.Particles[k].Slot);
                            Console.WriteLine($"     non-finite instance: {s.Shown[t].Label} tick {tick} slot {frame.Particles[k].Slot} pos {pos} " +
                                $"U {F(0x80)} XYZ {F(0x84)},{F(0x88)},{F(0x8c)} ang {F(0x9c)},{F(0xa0)},{F(0xa4)} p4 0x{FrameDecoder.U32(raw.Span, 4):x} q {q} P00 {F(0x100)} P11 {F(0x114)} w {F(0x13c)}");
                        }
                        finite &= item.Finite;
                        drawnParticles++;
                    }
                }
        }
        if (!finite) Check(false, "finite mesh instances in " + Path.GetFileName(path));
    }
    Check(shown > 0 && frameFaults == 0, $"corpus sample: {files} files, {shown}/{meshEntries} TypeMesh entries shown (+{shownChildren} in child Actions), {drawnParticles} mesh particle-frames ({simulationNaN} with a NaN position from the simulation, skipped), {frameFaults} faults in mesh entries ({otherFaults} in other entries, excluded as the window does)",
        string.Join("; ", reasons.OrderByDescending(r => r.Value).Select(r => $"{r.Key} x{r.Value}")));
}

Console.WriteLine($"{checks - failures}/{checks} checks passed");
return failures == 0 ? 0 : 1;

static IEnumerable<string> PreviewDocumentTypes(string path, int offset)
{
    var efx = new EfxFile(new FileHandler(File.OpenRead(path), path) { FileVersion = (int)EfxVersion.RE4 }); efx.Read();
    var entry = efx.Entries.FirstOrDefault(e => (int)e.Start == offset);
    return entry?.Attributes.Select(a => a.type.ToString()) ?? ["(child entry)"];
}

static string Short(string text) => text.Length <= 200 ? text : text[..200];

static string FindRepository()
{
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        if (File.Exists(Path.Combine(folder.FullName, "tools", "Re4Native", "re4host.py"))) return folder.FullName;
    return Directory.GetCurrentDirectory();
}
