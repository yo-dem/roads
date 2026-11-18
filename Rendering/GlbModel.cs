using System.Numerics;
using System.Text;
using System.Text.Json;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace Roads.Rendering;

/// <summary>
/// Modello glTF binario (.glb) con scheletro "rigido": ogni vertice segue un solo osso, come nei personaggi
/// Kenney. Il modello viene diviso in una parte per osso (nello spazio dell'osso), così ogni parte si disegna
/// con la matrice del suo osso e non serve lo skinning nello shader. Il colore dei vertici è letto dalla
/// texture a strisce del modello (colormap), perché il motore usa colori per vertice e non texture.
/// </summary>
public sealed class GlbModel : IDisposable
{
    private sealed class Node
    {
        public string Name = "";
        public int Parent = -1;
        public Vector3 T;
        public Quaternion R = Quaternion.Identity;
        public Vector3 S = Vector3.One;
    }

    internal enum Path { Translation, Rotation, Scale }

    internal sealed record Channel(int Node, Path Path, float[] Times, float[] Values);

    public sealed class Clip
    {
        public string Name { get; }
        public float Duration { get; }
        internal List<Channel> Channels { get; }
        internal Clip(string name, float duration, List<Channel> channels) { Name = name; Duration = duration; Channels = channels; }
    }

    /// <summary>Posa: traslazione, rotazione e scala locali di ogni nodo.</summary>
    public sealed class Pose
    {
        internal readonly Vector3[] T, S;
        internal readonly Quaternion[] R;
        internal Pose(int n) { T = new Vector3[n]; S = new Vector3[n]; R = new Quaternion[n]; }

        /// <summary>Moltiplica lo spostamento verticale del nodo (es. per attenuare i saltelli di un'animazione).</summary>
        public void ScaleHeight(int node, float factor, float rest)
        {
            if (node >= 0) T[node].Y = rest + (T[node].Y - rest) * factor;
        }
    }

    private readonly List<Node> _nodes = new();
    private readonly List<(Mesh Mesh, int Node)> _parts = new();
    private readonly Dictionary<string, Clip> _clips = new();

    public int NodeCount => _nodes.Count;
    public Clip? this[string name] => _clips.GetValueOrDefault(name);

    public GlbModel(GL gl, string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (BitConverter.ToUInt32(file, 0) != 0x46546C67) throw new InvalidDataException($"{path}: non è un file .glb");
        int jsonLen = BitConverter.ToInt32(file, 12);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(file, 20, jsonLen));
        var root = doc.RootElement;
        int binStart = 20 + jsonLen + 8;

        // ---------------------------------------------------------------- lettura degli accessor
        float[] Floats(int index, out int width)
        {
            var acc = root.GetProperty("accessors")[index];
            var view = root.GetProperty("bufferViews")[acc.GetProperty("bufferView").GetInt32()];
            int count = acc.GetProperty("count").GetInt32();
            width = acc.GetProperty("type").GetString() switch
            {
                "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT4" => 16,
                var t => throw new InvalidDataException($"tipo {t} non gestito"),
            };
            int type = acc.GetProperty("componentType").GetInt32();
            int size = type switch { 5126 or 5125 => 4, 5123 or 5122 => 2, _ => 1 };
            int stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : size * width;
            int offset = binStart + (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                         + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            var result = new float[count * width];
            for (int i = 0; i < count; i++)
                for (int k = 0; k < width; k++)
                {
                    int o = offset + i * stride + k * size;
                    result[i * width + k] = type switch
                    {
                        5126 => BitConverter.ToSingle(file, o),
                        5125 => BitConverter.ToUInt32(file, o),
                        5123 => BitConverter.ToUInt16(file, o),
                        5122 => BitConverter.ToInt16(file, o),
                        5121 => file[o],
                        _ => (sbyte)file[o],
                    };
                }
            return result;
        }

        // ---------------------------------------------------------------- nodi
        var nodes = root.GetProperty("nodes");
        for (int i = 0; i < nodes.GetArrayLength(); i++)
        {
            var n = nodes[i];
            var node = new Node { Name = n.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "" };
            if (n.TryGetProperty("translation", out var t)) node.T = new(t[0].GetSingle(), t[1].GetSingle(), t[2].GetSingle());
            if (n.TryGetProperty("rotation", out var r)) node.R = new(r[0].GetSingle(), r[1].GetSingle(), r[2].GetSingle(), r[3].GetSingle());
            if (n.TryGetProperty("scale", out var s)) node.S = new(s[0].GetSingle(), s[1].GetSingle(), s[2].GetSingle());
            _nodes.Add(node);
        }
        for (int i = 0; i < nodes.GetArrayLength(); i++)
            if (nodes[i].TryGetProperty("children", out var ch))
                foreach (var c in ch.EnumerateArray()) _nodes[c.GetInt32()].Parent = i;

        // ---------------------------------------------------------------- texture dei colori
        string dir = System.IO.Path.GetDirectoryName(path) ?? ".";
        string uri = root.GetProperty("images")[0].GetProperty("uri").GetString()!;
        ImageResult image;
        using (var stream = File.OpenRead(System.IO.Path.Combine(dir, uri)))
            image = ImageResult.FromStream(stream, ColorComponents.RedGreenBlue);
        Vector3 Texel(float u, float v)
        {
            int x = Math.Clamp((int)(u * image.Width), 0, image.Width - 1);
            int y = Math.Clamp((int)(v * image.Height), 0, image.Height - 1);
            int o = (y * image.Width + x) * 3;
            return new Vector3(image.Data[o], image.Data[o + 1], image.Data[o + 2]) / 255f;
        }

        // ---------------------------------------------------------------- mesh divise per osso
        for (int i = 0; i < nodes.GetArrayLength(); i++)
        {
            var n = nodes[i];
            if (!n.TryGetProperty("mesh", out var meshIndex)) continue;
            var skin = n.TryGetProperty("skin", out var sk) ? root.GetProperty("skins")[sk.GetInt32()] : (JsonElement?)null;
            int[]? joints = skin?.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
            float[]? ibm = skin != null ? Floats(skin.Value.GetProperty("inverseBindMatrices").GetInt32(), out _) : null;

            foreach (var prim in root.GetProperty("meshes")[meshIndex.GetInt32()].GetProperty("primitives").EnumerateArray())
            {
                var attr = prim.GetProperty("attributes");
                float[] pos = Floats(attr.GetProperty("POSITION").GetInt32(), out _);
                float[] nor = Floats(attr.GetProperty("NORMAL").GetInt32(), out _);
                float[] uv = Floats(attr.GetProperty("TEXCOORD_0").GetInt32(), out _);
                float[]? jnt = attr.TryGetProperty("JOINTS_0", out var ja) ? Floats(ja.GetInt32(), out _) : null;
                float[]? wgt = attr.TryGetProperty("WEIGHTS_0", out var wa) ? Floats(wa.GetInt32(), out _) : null;
                float[] idx = Floats(prim.GetProperty("indices").GetInt32(), out _);

                // Osso dominante di ogni vertice (nodo del mondo, -1 = il nodo della mesh stessa)
                int Bone(int v)
                {
                    if (joints == null || jnt == null || wgt == null) return -1;
                    int best = 0;
                    for (int k = 1; k < 4; k++) if (wgt[v * 4 + k] > wgt[v * 4 + best]) best = k;
                    return (int)jnt[v * 4 + best];
                }

                var builders = new Dictionary<int, MeshBuilder>();
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = (int)idx[t], b = (int)idx[t + 1], c = (int)idx[t + 2];
                    int bone = Bone(a);
                    if (!builders.TryGetValue(bone, out var mb))
                    {
                        mb = new MeshBuilder();
                        // Dallo spazio del modello (posa di riposo) allo spazio dell'osso
                        if (bone >= 0 && ibm != null) mb.Transform = ReadMatrix(ibm, bone);
                        builders[bone] = mb;
                    }
                    Vector3 P(int v) => new(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                    Vector3 N(int v) => new(nor[v * 3], nor[v * 3 + 1], nor[v * 3 + 2]);
                    Vector3 C(int v) => Texel(uv[v * 2], uv[v * 2 + 1]);
                    mb.AddTriangle(P(a), P(b), P(c), N(a), N(b), N(c), C(a), C(b), C(c));
                }
                foreach (var (bone, mb) in builders)
                    _parts.Add((mb.Build(gl), bone >= 0 && joints != null ? joints[bone] : i));
            }
        }

        // ---------------------------------------------------------------- animazioni
        if (root.TryGetProperty("animations", out var anims))
            foreach (var an in anims.EnumerateArray())
            {
                var samplers = an.GetProperty("samplers");
                var channels = new List<Channel>();
                float duration = 0f;
                foreach (var ch in an.GetProperty("channels").EnumerateArray())
                {
                    var target = ch.GetProperty("target");
                    if (!target.TryGetProperty("node", out var tn)) continue;
                    Path p = target.GetProperty("path").GetString() switch
                    {
                        "translation" => Path.Translation,
                        "rotation" => Path.Rotation,
                        "scale" => Path.Scale,
                        _ => (Path)(-1),
                    };
                    if ((int)p < 0) continue;
                    var sampler = samplers[ch.GetProperty("sampler").GetInt32()];
                    float[] times = Floats(sampler.GetProperty("input").GetInt32(), out _);
                    float[] values = Floats(sampler.GetProperty("output").GetInt32(), out _);
                    channels.Add(new Channel(tn.GetInt32(), p, times, values));
                    if (times.Length > 0) duration = MathF.Max(duration, times[^1]);
                }
                string name = an.GetProperty("name").GetString() ?? "";
                _clips[name] = new Clip(name, duration, channels);
            }
    }

    /// <summary>Le matrici glTF sono per colonne: lette in ordine danno la convenzione a vettori riga di System.Numerics.</summary>
    private static Matrix4x4 ReadMatrix(float[] m, int index)
    {
        int o = index * 16;
        return new Matrix4x4(m[o], m[o + 1], m[o + 2], m[o + 3], m[o + 4], m[o + 5], m[o + 6], m[o + 7],
                             m[o + 8], m[o + 9], m[o + 10], m[o + 11], m[o + 12], m[o + 13], m[o + 14], m[o + 15]);
    }

    public Pose NewPose() => new(_nodes.Count);

    /// <summary>Indice del nodo con questo nome (-1 se non c'è).</summary>
    public int NodeIndex(string name) => _nodes.FindIndex(n => n.Name == name);

    /// <summary>Traslazione di riposo del nodo.</summary>
    public Vector3 RestTranslation(int node) => node >= 0 ? _nodes[node].T : Vector3.Zero;

    /// <summary>Scrive in <paramref name="pose"/> la posa della clip al tempo dato (in loop), partendo da quella di riposo.</summary>
    public void Sample(Clip? clip, float time, Pose pose)
    {
        for (int i = 0; i < _nodes.Count; i++)
        {
            pose.T[i] = _nodes[i].T;
            pose.R[i] = _nodes[i].R;
            pose.S[i] = _nodes[i].S;
        }
        if (clip == null) return;
        if (clip.Duration > 0f) time = ((time % clip.Duration) + clip.Duration) % clip.Duration;

        foreach (var ch in clip.Channels)
        {
            var times = ch.Times;
            int k = 0;
            while (k < times.Length - 2 && times[k + 1] <= time) k++;
            int k1 = Math.Min(k + 1, times.Length - 1);
            float span = times[k1] - times[k];
            float f = span > 0f ? Math.Clamp((time - times[k]) / span, 0f, 1f) : 0f;
            switch (ch.Path)
            {
                case Path.Rotation:
                    var q0 = new Quaternion(ch.Values[k * 4], ch.Values[k * 4 + 1], ch.Values[k * 4 + 2], ch.Values[k * 4 + 3]);
                    var q1 = new Quaternion(ch.Values[k1 * 4], ch.Values[k1 * 4 + 1], ch.Values[k1 * 4 + 2], ch.Values[k1 * 4 + 3]);
                    pose.R[ch.Node] = Quaternion.Normalize(Quaternion.Slerp(q0, q1, f));
                    break;
                default:
                    var v0 = new Vector3(ch.Values[k * 3], ch.Values[k * 3 + 1], ch.Values[k * 3 + 2]);
                    var v1 = new Vector3(ch.Values[k1 * 3], ch.Values[k1 * 3 + 1], ch.Values[k1 * 3 + 2]);
                    if (ch.Path == Path.Translation) pose.T[ch.Node] = Vector3.Lerp(v0, v1, f);
                    else pose.S[ch.Node] = Vector3.Lerp(v0, v1, f);
                    break;
            }
        }
    }

    /// <summary>Mescola <paramref name="other"/> dentro <paramref name="pose"/> con peso <paramref name="w"/> (0 = resta com'è).</summary>
    public static void Blend(Pose pose, Pose other, float w)
    {
        if (w <= 0f) return;
        for (int i = 0; i < pose.T.Length; i++)
        {
            pose.T[i] = Vector3.Lerp(pose.T[i], other.T[i], w);
            pose.R[i] = Quaternion.Normalize(Quaternion.Slerp(pose.R[i], other.R[i], w));
            pose.S[i] = Vector3.Lerp(pose.S[i], other.S[i], w);
        }
    }

    private Matrix4x4[] _world = Array.Empty<Matrix4x4>();

    /// <summary>Aggiunge le parti del modello nella posa data, con <paramref name="root"/> come trasformazione del modello.</summary>
    public void Collect(List<DrawItem> items, Pose pose, Matrix4x4 root, bool smooth = true)
    {
        if (_world.Length != _nodes.Count) _world = new Matrix4x4[_nodes.Count];
        // I genitori vengono prima dei figli nei file Kenney; per sicurezza si risale la catena se non è così
        for (int i = 0; i < _nodes.Count; i++) _world[i] = default;
        for (int i = 0; i < _nodes.Count; i++) World(i, pose, root);
        foreach (var (mesh, node) in _parts)
            items.Add(new DrawItem(mesh, _world[node], Smooth: smooth));
    }

    private Matrix4x4 World(int i, Pose pose, Matrix4x4 root)
    {
        if (_world[i] != default) return _world[i];
        var local = Matrix4x4.CreateScale(pose.S[i]) * Matrix4x4.CreateFromQuaternion(pose.R[i]) * Matrix4x4.CreateTranslation(pose.T[i]);
        int p = _nodes[i].Parent;
        return _world[i] = local * (p >= 0 ? World(p, pose, root) : root);
    }

    public void Dispose()
    {
        foreach (var (mesh, _) in _parts) mesh.Dispose();
    }
}
