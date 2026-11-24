using System.Numerics;
using ReeLib;
using ReeLib.Common;
using ReeLib.Efx.Structs.Main;

namespace VfxStudio.Re4Render;

/// <summary>Geometry and material of a TypeMesh entry. A TypeMesh part is one mesh group of LOD0: MaxPartsNum equals
/// the group count in the corpus (liquid splashes play the groups as a sequence; debris picks one), and the particle's
/// part index (FrameDecoder MeshPart) selects the group to draw.</summary>
/// <param name="Vertices">Interleaved position (3), normal (3), UV0 (2), tangent (3) and bitangent sign (1) per vertex.</param>
/// <param name="Groups">Index range of each mesh group in <paramref name="Indices"/>, in group order.</param>
/// <param name="Albedo">Base colour texture of the material, or the checker when it is missing.</param>
/// <param name="NormalRoughness">NormalRoughnessMap (NRMR: tangent-space normal in RGB, roughness in A), or null.</param>
/// <param name="MetalInAlpha">The albedo is a BaseMetalMap (ALBM): its alpha is metalness, scaled by <paramref name="Metallic"/>.</param>
/// <param name="ColorParam">The material's ColorParam (RGBA), after the entry's overrides; white when absent.</param>
/// <param name="Metallic">The material's Metallic parameter (a scale on the ALBM alpha, or the value itself).</param>
/// <param name="Roughness">The material's Roughness parameter (a scale on the NRMR alpha, or the value itself).</param>
/// <param name="Emissive">TypeMesh EmissiveColor x EmissiveRate (linear RGB) when Flags bit 27 copies the emissive into
/// the particle (RUNTIME 4.3.11); zero otherwise.</param>
/// <param name="UvScroll">The material's UV transform (shader_basic_standard and its _alpha variant: use_UVScroll,
/// U/V_Scale, U/V_Offset, U/V_Speed), after the entry's overrides; null when use_UVScroll is off or absent.</param>
public sealed record PreviewMeshData(string MeshPath, float[] Vertices, uint[] Indices, (int First, int Count)[] Groups,
    PreviewTexture Albedo, PreviewTexture? NormalRoughness, bool MetalInAlpha, Vector4 ColorParam, float Metallic, float Roughness,
    Vector3 Emissive, IReadOnlyList<string> Missing, MeshUvScroll? UvScroll = null)
{
    public const int VertexFloats = 12;
    public const string MeshVersion = ".221108797", MaterialVersion = ".32", TextureVersion = ".143221013";

    /// <summary>Texture slots read as the base colour, in preference order (shader_basic_standard: BaseMetalMap ALBM,
    /// whose alpha is metalness; TranslucencyLiquid: BaseMap ALB; Character / footstep: BaseDielectricMap).</summary>
    public static readonly string[] AlbedoSlots = ["BaseMetalMap", "BaseMap", "BaseDielectricMap", "BaseDiffuseMap", "BaseColorMap", "BaseAlphaMap", "AlbedoMap", "ColorMap"];
    /// <summary>Texture slots read as NRMR (normal + roughness).</summary>
    public static readonly string[] NormalSlots = ["NormalRoughnessMap", "NormalRoughnessCavityMap"];

    /// <summary>Textures the renderer binds: [0] albedo, [1] NRMR when present.</summary>
    public IReadOnlyList<PreviewTexture> Textures => NormalRoughness == null ? [Albedo] : [Albedo, NormalRoughness];

    /// <summary>Cache key of the stand-in cube (shared by every entry whose mesh is missing).</summary>
    public const string PlaceholderKey = "<占位方块>";
    /// <summary>Half edge of the stand-in cube, in metres before the particle's scale.</summary>
    public const float PlaceholderHalfSize = 0.1f;

    /// <summary>A checker cube standing in for a mesh that is not set, not found or not readable, so the entry's particles
    /// stay visible (like the checker texture for a missing texture). One group: every part index draws it.
    /// <paramref name="missing"/> names what is missing.</summary>
    public static PreviewMeshData Placeholder(string missing, Vector3 emissive = default)
    {
        float h = PlaceholderHalfSize;
        var vertices = new List<float>();
        var indices = new List<uint>();
        // Per face: normal, tangent (U direction), and the in-face axis for V; 4 corners, 2 triangles.
        (Vector3 N, Vector3 T, Vector3 B)[] faces =
        [
            (Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ), (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY),
        ];
        foreach (var (n, t, b) in faces)
        {
            uint first = (uint)(vertices.Count / VertexFloats);
            foreach (var (u, v) in new[] { (0f, 0f), (1f, 0f), (1f, 1f), (0f, 1f) })
            {
                var p = (n + t * (u * 2 - 1) + b * (v * 2 - 1)) * h;
                vertices.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z, u, 1 - v, t.X, t.Y, t.Z, 1]);
            }
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }
        return new(PlaceholderKey, vertices.ToArray(), indices.ToArray(), [(0, indices.Count)], PreviewTexture.Missing(missing), null, false,
            Vector4.One, 0, 1, emissive, [missing]);
    }

    public static PreviewMeshData Load(EFXAttributeTypeMeshV2 type, Re4ResourceSource source)
    {
        var e0 = type.EmissiveColor;
        var emissiveIn = (type.Flags & (1u << 27)) != 0 ? new Vector3(e0.R, e0.G, e0.B) / 255f * type.EmissiveRate : Vector3.Zero;
        if (string.IsNullOrWhiteSpace(type.MeshPath)) return Placeholder("（TypeMesh 没有 MeshPath）", emissiveIn);
        PreviewTexture meshBytes;
        try { meshBytes = source.Read(type.MeshPath, MeshVersion); }
        catch (FileNotFoundException) { return Placeholder(type.MeshPath + MeshVersion, emissiveIn); }
        float[] vertices; uint[] indices; (int First, int Count)[] groups;
        try { (vertices, indices, groups) = Geometry(meshBytes.Bytes, type.MeshPath); }
        catch (Exception ex) when (ex is InvalidDataException or IndexOutOfRangeException or ArgumentException or EndOfStreamException or OverflowException or NotSupportedException)
        { return Placeholder(type.MeshPath + MeshVersion + "（无法读取：" + ex.Message + "）", emissiveIn); }
        var missing = new List<string>();
        var m = Material(type, source, missing);
        PreviewTexture albedo = m.Albedo == null ? PreviewTexture.Missing(type.MaterialPath ?? type.MeshPath)
            : Texture(m.Albedo, source, missing) ?? PreviewTexture.Missing(m.Albedo);
        var normal = m.Normal == null ? null : Texture(m.Normal, source, missing);
        var e = type.EmissiveColor;
        var emissive = (type.Flags & (1u << 27)) != 0 ? new Vector3(e.R, e.G, e.B) / 255f * type.EmissiveRate : Vector3.Zero;
        return new(type.MeshPath, vertices, indices, groups, albedo, normal, m.MetalInAlpha, m.Color, m.Metallic, m.Roughness, emissive, missing, m.UvScroll);
    }

    private static PreviewTexture? Texture(string path, Re4ResourceSource source, List<string> missing)
    {
        try { return source.Read(path, TextureVersion); }
        catch (FileNotFoundException) { missing.Add(path + TextureVersion); return null; }
    }

    /// <summary>LOD0 of a .mesh: one index range per mesh group, submeshes of a group concatenated.</summary>
    public static (float[] Vertices, uint[] Indices, (int First, int Count)[] Groups) Geometry(byte[] bytes, string path)
    {
        using var memory = new MemoryStream(bytes, writable: false);
        var mesh = new MeshFile(new FileHandler(memory, path + MeshVersion));
        if (!mesh.Read() || mesh.MeshData == null || mesh.MeshData.LODs.Count == 0) throw new InvalidDataException("无法读取网格 " + path);
        var lod = mesh.MeshData.LODs[0];
        var vertices = new List<float>();
        var indices = new List<uint>();
        var groups = new List<(int, int)>();
        foreach (var group in lod.MeshGroups)
        {
            int first = indices.Count;
            foreach (var sub in group.Submeshes)
            {
                var buffer = sub.Buffer;
                int count = Math.Min(sub.vertCount, buffer.Positions.Length - sub.vertsIndexOffset);
                if (count <= 0 || sub.indicesCount <= 0) continue;
                uint baseVertex = (uint)(vertices.Count / VertexFloats);
                for (int i = 0; i < count; i++)
                {
                    int v = sub.vertsIndexOffset + i;
                    var p = buffer.Positions[v];
                    bool hasFrame = v < buffer.NormalsTangents.Length;
                    var n = hasFrame ? buffer.NormalsTangents[v].Normal : Vector3.UnitY;
                    var t = hasFrame ? buffer.NormalsTangents[v].Tangent : Vector3.UnitX;
                    float sign = hasFrame && buffer.NormalsTangents[v].BiTangentSign < 0 ? -1 : 1;
                    var uv = v < buffer.UV0.Length ? buffer.UV0[v].AsVector2 : Vector2.Zero;
                    vertices.AddRange([p.X, p.Y, p.Z, n.X, n.Y, n.Z, uv.X, uv.Y, t.X, t.Y, t.Z, sign]);
                }
                int faces = buffer.Faces?.Length ?? buffer.IntegerFaces?.Length ?? 0;
                for (int i = 0; i + 2 < sub.indicesCount; i += 3)
                {
                    int f = sub.facesIndexOffset + i;
                    if (f + 2 >= faces) break;
                    uint a = Face(buffer, f), b = Face(buffer, f + 1), c = Face(buffer, f + 2);
                    if (a < count && b < count && c < count) indices.AddRange([baseVertex + a, baseVertex + b, baseVertex + c]);
                }
            }
            groups.Add((first, indices.Count - first));
        }
        if (groups.Count == 0 || indices.Count == 0) throw new InvalidDataException("网格没有三角形：" + path);
        return (vertices.ToArray(), indices.ToArray(), groups.ToArray());

        static uint Face(ReeLib.Mesh.MeshBuffer b, int i) => b.Faces != null ? b.Faces[i] : (uint)b.IntegerFaces![i];
    }

    private sealed record MaterialInfo(string? Albedo, string? Normal, bool MetalInAlpha, Vector4 Color, float Metallic, float Roughness,
        MeshUvScroll? UvScroll);

    /// <summary>Material parameters the preview reads, by name; the entry's overrides match them by the UTF-8 MurMur3
    /// hash of the name.</summary>
    private static readonly string[] Scalars = ["Metallic", "Roughness", "use_UVScroll", "U_Scale", "V_Scale", "U_Offset", "V_Offset", "U_Speed", "V_Speed"];

    /// <summary>The value an entry's property override sets. Float (MaterialParameterType 1) is (min, max) for a scalar or
    /// an RGBA colour; Range (2) is (min_a, min_b, max_a, max_b). The corpus only has min = max for Float scalars and
    /// a zero first pair for Range, so the preview takes the midpoint of (min, max) / (max_a, max_b) as a fixed value
    /// (whether the engine draws a random value per particle or per emitter is not read).</summary>
    public static float OverrideScalar(ReeLib.Efx.Structs.Common.MdfProperty p)
    {
        var v = p.VectorValue;
        return p.parameterType == ReeLib.Efx.Structs.Common.MaterialParameterType.Range ? (v.Z + v.W) / 2 : (v.X + v.Y) / 2;
    }

    /// <summary>The entry's material: the .mdf2's first material (albedo / NRMR slots, ColorParam, Metallic,
    /// Roughness), then the entry's property overrides (matched by the UTF-8 MurMur3 hash of the slot name).
    /// Without a material: white, dielectric, roughness 1.</summary>
    private static MaterialInfo Material(EFXAttributeTypeMeshV2 type, Re4ResourceSource source, List<string> missing)
    {
        string? albedo = null, normal = null, albedoSlot = null;
        var color = Vector4.One;
        var scalars = new Dictionary<string, float>();
        if (!string.IsNullOrWhiteSpace(type.MaterialPath))
        {
            try
            {
                var bytes = source.Read(type.MaterialPath, MaterialVersion).Bytes;
                using var memory = new MemoryStream(bytes, writable: false);
                var mdf = new MdfFile(new FileHandler(memory, type.MaterialPath + MaterialVersion));
                if (mdf.Read() && mdf.Materials.Count > 0)
                {
                    var material = mdf.Materials[0];
                    string? Slot(string[] slots, out string? which)
                    {
                        foreach (string slot in slots)
                        {
                            var hit = material.Textures.FirstOrDefault(t => t.texType == slot && !string.IsNullOrWhiteSpace(t.texPath));
                            if (hit != null) { which = slot; return hit.texPath; }
                        }
                        which = null; return null;
                    }
                    albedo = Slot(AlbedoSlots, out albedoSlot);
                    normal = Slot(NormalSlots, out _);
                    foreach (var q in material.Parameters.Where(q => Scalars.Contains(q.paramName))) scalars[q.paramName] = q.parameter.X;
                    var param = material.Parameters.FirstOrDefault(p => p.paramName == "ColorParam");
                    if (param != null && param.componentCount >= 3)
                        color = param.componentCount >= 4 ? param.parameter : new(param.parameter.X, param.parameter.Y, param.parameter.Z, 1);
                }
            }
            catch (FileNotFoundException) { missing.Add(type.MaterialPath + MaterialVersion); }
        }
        uint colorHash = MurMur3HashUtils.GetUTF8Hash("ColorParam");
        foreach (var p in type.properties)
        {
            if (p.parameterType == ReeLib.Efx.Structs.Common.MaterialParameterType.Texture)
            {
                if (string.IsNullOrWhiteSpace(p.texturePath)) continue;
                if (AlbedoSlots.FirstOrDefault(s => MurMur3HashUtils.GetUTF8Hash(s) == p.PropertyNameUTF8Hash) is { } slot) { albedo = p.texturePath; albedoSlot = slot; }
                else if (NormalSlots.Any(s => MurMur3HashUtils.GetUTF8Hash(s) == p.PropertyNameUTF8Hash)) normal = p.texturePath;
            }
            else if (p.PropertyNameUTF8Hash == colorHash) color = p.VectorValue;
            else if (Scalars.FirstOrDefault(n => MurMur3HashUtils.GetUTF8Hash(n) == p.PropertyNameUTF8Hash) is { } name) scalars[name] = OverrideScalar(p);
        }
        float Get(string name, float fallback) => scalars.TryGetValue(name, out float v) ? v : fallback;
        float metallic = Get("Metallic", albedoSlot == "BaseMetalMap" ? 1 : 0), roughness = Get("Roughness", 1);
        // shader_basic_standard (mmtr PS, e.g. block b048): use_UVScroll > 0 switches the whole transform on, scale
        // and offset included.
        MeshUvScroll? scroll = Get("use_UVScroll", 0) > 0
            ? new(new(Get("U_Scale", 1), Get("V_Scale", 1)), new(Get("U_Offset", 0), Get("V_Offset", 0)), new(Get("U_Speed", 0), Get("V_Speed", 0)))
            : null;
        return new(albedo, normal, albedoSlot == "BaseMetalMap", color, Math.Clamp(metallic, 0, 1), Math.Clamp(roughness, 0, 1), scroll);
    }
}

/// <summary>The UV transform of shader_basic_standard's pixel shader (read from the .mmtr, block b048):
/// <c>uv' = Offset + Scale · uv + frac(Speed · 0.001 · timeMillisecond)</c>, the speed term only for a non-zero speed;
/// both textures are sampled at uv'. timeMillisecond is the scene clock (the preview passes its playback time).</summary>
public sealed record MeshUvScroll(Vector2 Scale, Vector2 Offset, Vector2 Speed)
{
    /// <summary>The scroll term at <paramref name="milliseconds"/>, computed in double as the shader's uint → float would
    /// lose precision after a few hours.</summary>
    public Vector2 Shift(double milliseconds) => new(Term(Speed.X, milliseconds), Term(Speed.Y, milliseconds));

    private static float Term(float speed, double milliseconds)
    {
        if (speed == 0) return 0;
        double t = speed * 0.001 * Math.Floor(Math.Max(0, milliseconds));
        return (float)(t - Math.Floor(t));
    }
}

/// <summary>One mesh particle for the GPU: world matrix (row-vector convention, uploaded as is and read as a
/// column-major mat4, i.e. transposed) and colour | id | life byte.</summary>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack = 4, Size = 80)]
public struct MeshInstance
{
    public Matrix4x4 World;
    public uint Color, Id, Life, Reserved;

    /// <summary>The rotation the mesh instance builder (3d2f8f0) composes, row vectors: R · Q.
    /// R = 3d13e50(angles, [p+4] &amp; 7) via 3d296f0: angle slots +0x9c..+0xa4, plus +0xa8..+0xb0 when [p+4] &amp; 0x2000,
    /// Y and Z negated when the emitter is mirrored (E+0x16c bit 7). 3d13e50 equals the matrix of the Polygon
    /// quaternion rule. Q = dfbc10(mesh record +0x10), the FrontAxis-to-velocity quaternion the post-process
    /// 3d31390 keeps (identity unless TypeMesh Flags bit 1). Both checked natively by exp_mesh_rotation.py.</summary>
    public static Matrix4x4 Rotation(ReadOnlySpan<byte> raw, PreviewEntryState entry, int slot)
    {
        float x = FrameDecoder.F32(raw, 0x9c), y = FrameDecoder.F32(raw, 0xa0), z = FrameDecoder.F32(raw, 0xa4);
        uint flags = FrameDecoder.U32(raw, 4);
        if ((flags & 0x2000) != 0) { x += FrameDecoder.F32(raw, 0xa8); y += FrameDecoder.F32(raw, 0xac); z += FrameDecoder.F32(raw, 0xb0); }
        if ((entry.Flags16c & 0x80) != 0) { y = -y; z = -z; }
        var rotation = Matrix4x4.CreateFromQuaternion(BillboardInstance.PolygonRotation(x, y, z, flags));
        var front = entry.MeshOrientation(slot);
        return front.IsIdentity ? rotation : rotation * Matrix4x4.CreateFromQuaternion(front);
    }

    /// <summary>Particle slots: +0x80 size U, +0x84/+0x88/+0x8c X/Y/Z (times +0x90..+0x98 when [p+4] &amp; 0x4000, as the
    /// builder does), +0x100 reference matrix P. World = scale · <see cref="Rotation"/> · P's linear part, translated to
    /// the particle's world position (the order of the scale and P is the builder's main path; its other branches are
    /// not followed).</summary>
    public static MeshInstance Decode(ParticleRecord p, PreviewEntryState entry)
    {
        var raw = p.Raw.Span;
        float u = FrameDecoder.F32(raw, 0x80);
        var scale = new Vector3(u * FrameDecoder.F32(raw, 0x84), u * FrameDecoder.F32(raw, 0x88), u * FrameDecoder.F32(raw, 0x8c));
        if ((FrameDecoder.U32(raw, 4) & 0x4000) != 0)
            scale *= new Vector3(FrameDecoder.F32(raw, 0x90), FrameDecoder.F32(raw, 0x94), FrameDecoder.F32(raw, 0x98));
        var world = Matrix4x4.CreateScale(scale) * Rotation(raw, entry, p.Slot);
        if (FrameDecoder.F32(raw, 0x13c) == 1f)
        {
            var reference = new Matrix4x4(FrameDecoder.F32(raw, 0x100), FrameDecoder.F32(raw, 0x104), FrameDecoder.F32(raw, 0x108), 0,
                FrameDecoder.F32(raw, 0x110), FrameDecoder.F32(raw, 0x114), FrameDecoder.F32(raw, 0x118), 0,
                FrameDecoder.F32(raw, 0x120), FrameDecoder.F32(raw, 0x124), FrameDecoder.F32(raw, 0x128), 0, 0, 0, 0, 1);
            world *= reference;
        }
        world.Translation = p.Position;
        var colour = BillboardInstance.Decode(p, entry, 0).Color;
        return new() { World = world, Color = colour, Id = p.Id, Life = p.LifeAlpha };
    }

    public readonly bool Finite =>
        float.IsFinite(World.M11) && float.IsFinite(World.M22) && float.IsFinite(World.M33) &&
        float.IsFinite(World.M41) && float.IsFinite(World.M42) && float.IsFinite(World.M43) &&
        float.IsFinite(World.M12) && float.IsFinite(World.M13) && float.IsFinite(World.M21) &&
        float.IsFinite(World.M23) && float.IsFinite(World.M31) && float.IsFinite(World.M32);
}
