using System.Numerics;
using System.Text;
using System.Text.Json;
using Silk.NET.OpenGL;
using StbImageSharp;

namespace Roads.Rendering;

/// <summary>
/// Modello glTF binario (.glb) animato con uno scheletro. Il motore usa colori per vertice e non texture,
/// quindi il colore di ogni vertice si legge una volta dalla texture del modello (esterna o incorporata nel
/// file) o dal colore del suo materiale.
/// Due modi di animare, scelti per ogni mesh:
/// - scheletro "rigido" (ogni vertice segue un solo osso, come i personaggi Kenney): la mesh è divisa in una
///   parte per osso, disegnata con la matrice del suo osso; nessun calcolo per vertice;
/// - scheletro "morbido" (pesi sfumati su più ossa, come i KayKit: gomiti e ginocchia che si piegano): i vertici
///   vengono deformati sulla CPU a ogni fotogramma e ricaricati sulla GPU (poche migliaia di vertici).
/// Le mesh attaccate alle mani (armi, attrezzi) non si disegnano.
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

    /// <summary>Mesh deformata dallo scheletro sulla CPU.</summary>
    private sealed class Skinned
    {
        public required Mesh Mesh;
        public required float[] Data;          // vertici nel formato del motore (riscritti a ogni posa)
        public required Vector3[] Pos, Nrm;    // posa di riposo
        public required int[] Joints;          // 4 per vertice: indici nei nodi
        public required float[] Weights;       // 4 per vertice
        public required int[] SkinJoints;      // nodi dello scheletro
        public required Matrix4x4[] InverseBind;
    }

    private readonly List<Node> _nodes = new();
    private readonly List<(Mesh Mesh, int Node)> _parts = new();
    private readonly List<Skinned> _skinned = new();
    private readonly Dictionary<string, Clip> _clips = new(StringComparer.OrdinalIgnoreCase);

    public int NodeCount => _nodes.Count;
    public Clip? this[string name] => _clips.GetValueOrDefault(name);

    /// <summary>La prima animazione esistente tra i nomi dati (i pacchetti li chiamano in modi diversi).</summary>
    public Clip? Find(params string[] names)
    {
        foreach (var n in names)
            if (_clips.TryGetValue(n, out var c)) return c;
        return null;
    }

    /// <summary>Altezza del modello nella posa di riposo (per riportarlo alla scala del gioco).</summary>
    public float Height { get; }

    public GlbModel(GL gl, string path)
    {
        byte[] file = File.ReadAllBytes(path);
        if (BitConverter.ToUInt32(file, 0) != 0x46546C67) throw new InvalidDataException($"{path}: non è un file .glb");
        int jsonLen = BitConverter.ToInt32(file, 12);
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(file, 20, jsonLen));
        var root = doc.RootElement;
        int binStart = 20 + jsonLen + 8;

        // ---------------------------------------------------------------- lettura dei dati binari
        (int Offset, int Length) View(int index)
        {
            var view = root.GetProperty("bufferViews")[index];
            return (binStart + (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0), view.GetProperty("byteLength").GetInt32());
        }

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
            bool normalized = acc.TryGetProperty("normalized", out var nz) && nz.GetBoolean();
            int size = type switch { 5126 or 5125 => 4, 5123 or 5122 => 2, _ => 1 };
            int stride = view.TryGetProperty("byteStride", out var bs) ? bs.GetInt32() : size * width;
            int offset = binStart + (view.TryGetProperty("byteOffset", out var vo) ? vo.GetInt32() : 0)
                         + (acc.TryGetProperty("byteOffset", out var ao) ? ao.GetInt32() : 0);
            var result = new float[count * width];
            for (int i = 0; i < count; i++)
                for (int k = 0; k < width; k++)
                {
                    int o = offset + i * stride + k * size;
                    float v = type switch
                    {
                        5126 => BitConverter.ToSingle(file, o),
                        5125 => BitConverter.ToUInt32(file, o),
                        5123 => BitConverter.ToUInt16(file, o),
                        5122 => BitConverter.ToInt16(file, o),
                        5121 => file[o],
                        _ => (sbyte)file[o],
                    };
                    if (normalized) v /= type switch { 5121 => 255f, 5123 => 65535f, 5120 => 127f, 5122 => 32767f, _ => 1f };
                    result[i * width + k] = v;
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

        // ---------------------------------------------------------------- materiali: texture o colore
        string dir = System.IO.Path.GetDirectoryName(path) ?? ".";
        var images = new Dictionary<int, ImageResult>();
        ImageResult? Image(int index)
        {
            if (images.TryGetValue(index, out var cached)) return cached;
            var img = root.GetProperty("images")[index];
            ImageResult? result = null;
            if (img.TryGetProperty("bufferView", out var bv))
            {
                var (offset, length) = View(bv.GetInt32());
                result = ImageResult.FromMemory(file.AsSpan(offset, length).ToArray(), ColorComponents.RedGreenBlue);
            }
            else if (img.TryGetProperty("uri", out var uri))
            {
                using var stream = File.OpenRead(System.IO.Path.Combine(dir, uri.GetString()!));
                result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlue);
            }
            if (result != null) images[index] = result;
            return result;
        }

        Func<float, float, Vector3> MaterialColor(JsonElement prim)
        {
            Vector3 factor = Vector3.One;
            if (!prim.TryGetProperty("material", out var mi)) return (_, _) => factor;
            var mat = root.GetProperty("materials")[mi.GetInt32()];
            if (mat.TryGetProperty("pbrMetallicRoughness", out var pbr))
            {
                if (pbr.TryGetProperty("baseColorFactor", out var bf))
                    factor = new Vector3(bf[0].GetSingle(), bf[1].GetSingle(), bf[2].GetSingle());
                if (pbr.TryGetProperty("baseColorTexture", out var bt))
                {
                    var tex = root.GetProperty("textures")[bt.GetProperty("index").GetInt32()];
                    if (tex.TryGetProperty("source", out var src) && Image(src.GetInt32()) is { } image)
                        return (u, v) =>
                        {
                            int x = Math.Clamp((int)(u * image.Width), 0, image.Width - 1);
                            int y = Math.Clamp((int)(v * image.Height), 0, image.Height - 1);
                            int o = (y * image.Width + x) * 3;
                            return new Vector3(image.Data[o], image.Data[o + 1], image.Data[o + 2]) / 255f * factor;
                        };
                }
            }
            // Materiali senza texture e senza colore (es. "Glow"): se emettono luce si usa quel colore
            if (factor == Vector3.One && mat.TryGetProperty("emissiveFactor", out var ef))
            {
                var e = new Vector3(ef[0].GetSingle(), ef[1].GetSingle(), ef[2].GetSingle());
                if (e.LengthSquared() > 0.01f) factor = e;
            }
            return (_, _) => factor;
        }

        // Le mesh attaccate alle mani (armi, attrezzi) non si disegnano
        bool InHand(int node)
        {
            for (int n = _nodes[node].Parent; n >= 0; n = _nodes[n].Parent)
                if (_nodes[n].Name.StartsWith("handslot", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------------------------------------------------------------- mesh
        float maxY = 0f;
        for (int i = 0; i < nodes.GetArrayLength(); i++)
        {
            var n = nodes[i];
            if (!n.TryGetProperty("mesh", out var meshIndex) || InHand(i)) continue;
            var skin = n.TryGetProperty("skin", out var sk) ? root.GetProperty("skins")[sk.GetInt32()] : (JsonElement?)null;
            int[]? joints = skin?.GetProperty("joints").EnumerateArray().Select(j => j.GetInt32()).ToArray();
            float[]? ibm = skin != null ? Floats(skin.Value.GetProperty("inverseBindMatrices").GetInt32(), out _) : null;

            foreach (var prim in root.GetProperty("meshes")[meshIndex.GetInt32()].GetProperty("primitives").EnumerateArray())
            {
                var attr = prim.GetProperty("attributes");
                float[] pos = Floats(attr.GetProperty("POSITION").GetInt32(), out _);
                float[] nor = Floats(attr.GetProperty("NORMAL").GetInt32(), out _);
                float[]? uv = attr.TryGetProperty("TEXCOORD_0", out var ta) ? Floats(ta.GetInt32(), out _) : null;
                float[]? jnt = attr.TryGetProperty("JOINTS_0", out var ja) ? Floats(ja.GetInt32(), out _) : null;
                float[]? wgt = attr.TryGetProperty("WEIGHTS_0", out var wa) ? Floats(wa.GetInt32(), out _) : null;
                float[] idx = prim.TryGetProperty("indices", out var ia)
                    ? Floats(ia.GetInt32(), out _)
                    : Enumerable.Range(0, pos.Length / 3).Select(v => (float)v).ToArray();
                var color = MaterialColor(prim);
                int count = pos.Length / 3;
                for (int v = 0; v < count; v++) maxY = MathF.Max(maxY, pos[v * 3 + 1]);

                Vector3 P(int v) => new(pos[v * 3], pos[v * 3 + 1], pos[v * 3 + 2]);
                Vector3 N(int v) => new(nor[v * 3], nor[v * 3 + 1], nor[v * 3 + 2]);
                Vector3 C(int v) => uv != null ? color(uv[v * 2], uv[v * 2 + 1]) : color(0f, 0f);

                bool skinned = joints != null && ibm != null && jnt != null && wgt != null;
                bool soft = false;
                if (skinned)
                    for (int v = 0; v < count && !soft; v++)
                    {
                        float max = 0f, sum = 0f;
                        for (int k = 0; k < 4; k++) { max = MathF.Max(max, wgt![v * 4 + k]); sum += wgt[v * 4 + k]; }
                        if (max < 0.99f * sum) soft = true;
                    }

                if (soft)
                {
                    // Scheletro morbido: vertici deformati sulla CPU (un vertice per indice, colori fissati ora)
                    var data = new float[idx.Length * Mesh.FloatsPerVertex];
                    var bindPos = new Vector3[idx.Length];
                    var bindNrm = new Vector3[idx.Length];
                    var jj = new int[idx.Length * 4];
                    var ww = new float[idx.Length * 4];
                    for (int k = 0; k < idx.Length; k++)
                    {
                        int v = (int)idx[k];
                        bindPos[k] = P(v);
                        bindNrm[k] = N(v);
                        Vector3 c = C(v);
                        int o = k * Mesh.FloatsPerVertex;
                        data[o + 6] = c.X; data[o + 7] = c.Y; data[o + 8] = c.Z;
                        data[o + 9] = data[o + 10] = data[o + 11] = 1f; // nessuno spigolo da disegnare
                        float sum = 0f;
                        for (int q = 0; q < 4; q++) sum += wgt![v * 4 + q];
                        for (int q = 0; q < 4; q++)
                        {
                            jj[k * 4 + q] = (int)jnt![v * 4 + q];
                            ww[k * 4 + q] = sum > 0f ? wgt![v * 4 + q] / sum : (q == 0 ? 1f : 0f);
                        }
                    }
                    var indices = Enumerable.Range(0, idx.Length).Select(v => (uint)v).ToArray();
                    _skinned.Add(new Skinned
                    {
                        Mesh = new Mesh(gl, data, indices),
                        Data = data,
                        Pos = bindPos,
                        Nrm = bindNrm,
                        Joints = jj,
                        Weights = ww,
                        SkinJoints = joints!,
                        InverseBind = Enumerable.Range(0, joints!.Length).Select(j => ReadMatrix(ibm!, j)).ToArray(),
                    });
                    continue;
                }

                // Scheletro rigido (o mesh senza scheletro): una parte per osso, nello spazio dell'osso
                int Bone(int v)
                {
                    if (!skinned) return -1;
                    int best = 0;
                    for (int k = 1; k < 4; k++) if (wgt![v * 4 + k] > wgt[v * 4 + best]) best = k;
                    return (int)jnt![v * 4 + best];
                }
                var builders = new Dictionary<int, MeshBuilder>();
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = (int)idx[t], b = (int)idx[t + 1], c = (int)idx[t + 2];
                    int bone = Bone(a);
                    if (!builders.TryGetValue(bone, out var mb))
                    {
                        mb = new MeshBuilder();
                        if (bone >= 0) mb.Transform = ReadMatrix(ibm!, bone); // dalla posa di riposo allo spazio dell'osso
                        builders[bone] = mb;
                    }
                    mb.AddTriangle(P(a), P(b), P(c), N(a), N(b), N(c), C(a), C(b), C(c));
                }
                foreach (var (bone, mb) in builders)
                    _parts.Add((mb.Build(gl), bone >= 0 ? joints![bone] : i));
            }
        }
        Height = maxY;

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
                string name = an.TryGetProperty("name", out var anm) ? anm.GetString() ?? "" : "";
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
    private bool[] _done = Array.Empty<bool>();

    /// <summary>Aggiunge il modello nella posa data, con <paramref name="root"/> come trasformazione del modello.</summary>
    public void Collect(List<DrawItem> items, Pose pose, Matrix4x4 root, bool smooth = true)
    {
        if (_world.Length != _nodes.Count) { _world = new Matrix4x4[_nodes.Count]; _done = new bool[_nodes.Count]; }
        Array.Clear(_done);
        for (int i = 0; i < _nodes.Count; i++) World(i, pose, root);
        foreach (var (mesh, node) in _parts)
            items.Add(new DrawItem(mesh, _world[node], Smooth: smooth));

        // Mesh morbide: vertici deformati qui (posizioni già nel mondo, quindi matrice identità)
        foreach (var s in _skinned)
        {
            var skin = new Matrix4x4[s.SkinJoints.Length];
            for (int j = 0; j < skin.Length; j++) skin[j] = s.InverseBind[j] * _world[s.SkinJoints[j]];
            var data = s.Data;
            for (int v = 0; v < s.Pos.Length; v++)
            {
                Vector3 p = Vector3.Zero, n = Vector3.Zero;
                for (int q = 0; q < 4; q++)
                {
                    float w = s.Weights[v * 4 + q];
                    if (w <= 0f) continue;
                    ref Matrix4x4 m = ref skin[s.Joints[v * 4 + q]];
                    p += Vector3.Transform(s.Pos[v], m) * w;
                    n += Vector3.TransformNormal(s.Nrm[v], m) * w;
                }
                float len = n.Length();
                if (len > 1e-6f) n /= len;
                int o = v * Mesh.FloatsPerVertex;
                data[o] = p.X; data[o + 1] = p.Y; data[o + 2] = p.Z;
                data[o + 3] = n.X; data[o + 4] = n.Y; data[o + 5] = n.Z;
            }
            s.Mesh.Update(data);
            items.Add(new DrawItem(s.Mesh, Matrix4x4.Identity, Smooth: smooth));
        }
    }

    private Matrix4x4 World(int i, Pose pose, Matrix4x4 root)
    {
        if (_done[i]) return _world[i];
        var local = Matrix4x4.CreateScale(pose.S[i]) * Matrix4x4.CreateFromQuaternion(pose.R[i]) * Matrix4x4.CreateTranslation(pose.T[i]);
        int p = _nodes[i].Parent;
        _world[i] = local * (p >= 0 ? World(p, pose, root) : root);
        _done[i] = true;
        return _world[i];
    }

    public void Dispose()
    {
        foreach (var (mesh, _) in _parts) mesh.Dispose();
        foreach (var s in _skinned) s.Mesh.Dispose();
    }
}
