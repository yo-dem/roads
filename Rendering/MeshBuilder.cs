using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Roads.Rendering;

/// <summary>
/// Accumula geometria low poly (ogni triangolo ha vertici propri, quindi shading piatto)
/// e la carica in una <see cref="Mesh"/>. Supporta una trasformazione corrente e
/// una leggera variazione casuale di colore per faccia, tipica dello stile low poly.
/// </summary>
public sealed class MeshBuilder
{
    private readonly List<float> _vertices = new();
    private readonly List<uint> _indices = new();

    /// <summary>Trasformazione applicata a tutta la geometria aggiunta.</summary>
    public Matrix4x4 Transform = Matrix4x4.Identity;

    /// <summary>Variazione di luminosità per faccia (0 = nessuna).</summary>
    public float ColorJitter;
    public Random? ColorRng;

    /// <summary>
    /// Se vero, <see cref="Build"/> calcola gli spigoli "veri" (tra facce non complanari) e li
    /// codifica con coordinate baricentriche, così lo shader può disegnarli quando l'oggetto è trasparente.
    /// </summary>
    public bool ComputeEdges;

    /// <summary>
    /// Se vero, le normali dei vertici che coincidono vengono mediate: con l'ombreggiatura morbida dello
    /// shader (DrawItem.Smooth) la superficie appare liscia invece che a facce piatte (per il personaggio).
    /// </summary>
    public bool SmoothNormals;

    public bool IsEmpty => _indices.Count == 0;

    /// <summary>Indici aggiunti finora (per ricordare dove inizia e finisce un oggetto nella mesh).</summary>
    public int IndexCount => _indices.Count;

    private const int BaseFloats = 9;   // posizione, normale, colore (come vengono accumulati)
    private const int ObjectFloats = 5; // centro x/z, raggio, quota minima e massima dell'oggetto

    private readonly List<float> _objects = new();
    private int _objectStart = -1;

    private uint VertexCount => (uint)(_vertices.Count / BaseFloats);

    private Vector3 Jitter(Vector3 c)
    {
        if (ColorRng == null || ColorJitter <= 0f) return c;
        return c * (1f + (ColorRng.NextSingle() * 2f - 1f) * ColorJitter);
    }

    private void AddVertex(Vector3 p, Vector3 n, Vector3 c)
    {
        _vertices.Add(p.X); _vertices.Add(p.Y); _vertices.Add(p.Z);
        _vertices.Add(n.X); _vertices.Add(n.Y); _vertices.Add(n.Z);
        _vertices.Add(c.X); _vertices.Add(c.Y); _vertices.Add(c.Z);
        for (int k = 0; k < ObjectFloats; k++) _objects.Add(0f);
    }

    /// <summary>Inizia un oggetto (albero, roccia...): la geometria aggiunta fino a <see cref="EndObject"/> ne fa parte.</summary>
    public void BeginObject() => _objectStart = (int)VertexCount;

    /// <summary>
    /// Chiude l'oggetto e scrive nei suoi vertici il suo ingombro (cilindro verticale), usato dallo shader
    /// per renderlo trasparente per intero quando copre il giocatore.
    /// </summary>
    /// <param name="building">Edificio: a piedi non diventa trasparente (vedi lo shader).</param>
    public void EndObject(bool building = false) => EndObject(null, building);

    /// <summary>
    /// Come <see cref="EndObject()"/>, ma con un ingombro dato (coordinate prima di <see cref="Transform"/>): serve a
    /// parti staccate di un oggetto (le finestre accese di un palazzo) che devono sfumare insieme a lui.
    /// </summary>
    public void EndObject(Vector3 boundsMin, Vector3 boundsMax, bool building = false) =>
        EndObject((boundsMin, boundsMax), building);

    private void EndObject((Vector3 Min, Vector3 Max)? bounds, bool building)
    {
        int start = _objectStart, end = (int)VertexCount;
        _objectStart = -1;
        if (start < 0 || end <= start) return;

        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        if (bounds is { } b)
        {
            for (int k = 0; k < 8; k++)
            {
                var corner = new Vector3((k & 1) == 0 ? b.Min.X : b.Max.X, (k & 2) == 0 ? b.Min.Y : b.Max.Y, (k & 4) == 0 ? b.Min.Z : b.Max.Z);
                var p = Vector3.Transform(corner, Transform);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        }
        else
            for (int v = start; v < end; v++)
            {
                var p = new Vector3(_vertices[v * BaseFloats], _vertices[v * BaseFloats + 1], _vertices[v * BaseFloats + 2]);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        float cx = (min.X + max.X) * 0.5f, cz = (min.Z + max.Z) * 0.5f;
        float radius = MathF.Max(max.X - min.X, max.Z - min.Z) * 0.5f;
        for (int v = start; v < end; v++)
        {
            int o = v * ObjectFloats;
            _objects[o] = cx;
            _objects[o + 1] = cz;
            _objects[o + 2] = building ? -radius : radius; // il segno distingue gli edifici
            _objects[o + 3] = min.Y;
            _objects[o + 4] = max.Y;
        }
    }

    private static Vector3 FaceNormal(Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        return len < 1e-8f ? Vector3.UnitY : n / len;
    }

    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 color)
    {
        a = Vector3.Transform(a, Transform);
        b = Vector3.Transform(b, Transform);
        c = Vector3.Transform(c, Transform);
        Vector3 n = FaceNormal(a, b, c);
        color = Jitter(color);
        uint i = VertexCount;
        AddVertex(a, n, color);
        AddVertex(b, n, color);
        AddVertex(c, n, color);
        _indices.AddRange([i, i + 1, i + 2]);
    }

    /// <summary>Triangolo con normali e colori propri per ogni vertice (modelli importati, ombreggiatura morbida).</summary>
    public void AddTriangle(Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Vector3 ca, Vector3 cb, Vector3 cc)
    {
        uint i = VertexCount;
        AddVertex(Vector3.Transform(a, Transform), SafeNormal(Vector3.TransformNormal(na, Transform)), ca);
        AddVertex(Vector3.Transform(b, Transform), SafeNormal(Vector3.TransformNormal(nb, Transform)), cb);
        AddVertex(Vector3.Transform(c, Transform), SafeNormal(Vector3.TransformNormal(nc, Transform)), cc);
        _indices.AddRange([i, i + 1, i + 2]);
    }

    private static Vector3 SafeNormal(Vector3 n)
    {
        float len = n.Length();
        return len < 1e-8f ? Vector3.UnitY : n / len;
    }

    /// <summary>Quad (a,b,c,d) in senso antiorario visto dal lato della normale.</summary>
    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 color)
    {
        a = Vector3.Transform(a, Transform);
        b = Vector3.Transform(b, Transform);
        c = Vector3.Transform(c, Transform);
        d = Vector3.Transform(d, Transform);
        Vector3 n = FaceNormal(a, b, c);
        color = Jitter(color);
        uint i = VertexCount;
        AddVertex(a, n, color);
        AddVertex(b, n, color);
        AddVertex(c, n, color);
        AddVertex(d, n, color);
        _indices.AddRange([i, i + 1, i + 2, i, i + 2, i + 3]);
    }

    public void AddBox(Vector3 center, Vector3 size, Vector3 color)
    {
        Vector3 h = size * 0.5f;
        Vector3 P(float x, float y, float z) => center + new Vector3(x * h.X, y * h.Y, z * h.Z);

        AddQuad(P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), color);
        AddQuad(P(1, -1, -1), P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), color);
        AddQuad(P(1, -1, 1), P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), color);
        AddQuad(P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), color);
        AddQuad(P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), P(-1, 1, -1), color);
        AddQuad(P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), color);
    }

    /// <summary>Prisma verticale (tronco di cono a facce piatte) con base in <paramref name="baseCenter"/>.</summary>
    public void AddPrism(Vector3 baseCenter, float rBottom, float rTop, float height, int sides, Vector3 color, float angle = 0f)
    {
        Vector3 top = baseCenter + new Vector3(0, height, 0);
        for (int s = 0; s < sides; s++)
        {
            float a0 = angle + MathF.Tau * s / sides;
            float a1 = angle + MathF.Tau * (s + 1) / sides;
            var d0 = new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0));
            var d1 = new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1));
            Vector3 b0 = baseCenter + d0 * rBottom, b1 = baseCenter + d1 * rBottom;
            Vector3 t0 = top + d0 * rTop, t1 = top + d1 * rTop;
            AddQuad(b1, b0, t0, t1, color);
            if (rTop > 0f) AddTriangle(top, t1, t0, color);
            AddTriangle(baseCenter, b0, b1, color);
        }
    }

    public void AddCone(Vector3 baseCenter, float radius, float height, int sides, Vector3 color, float angle = 0f)
    {
        Vector3 tip = baseCenter + new Vector3(0, height, 0);
        for (int s = 0; s < sides; s++)
        {
            float a0 = angle + MathF.Tau * s / sides;
            float a1 = angle + MathF.Tau * (s + 1) / sides;
            Vector3 b0 = baseCenter + new Vector3(MathF.Cos(a0), 0, MathF.Sin(a0)) * radius;
            Vector3 b1 = baseCenter + new Vector3(MathF.Cos(a1), 0, MathF.Sin(a1)) * radius;
            AddTriangle(b1, b0, tip, color);
            AddTriangle(baseCenter, b0, b1, color);
        }
    }

    /// <summary>Disco piatto orizzontale con bordo irregolare.</summary>
    /// <param name="thickness">Se maggiore di zero aggiunge un bordo laterale verso il basso (lastra).</param>
    public void AddDisc(Vector3 center, float radius, int sides, Vector3 color, Random? rng = null, float jitter = 0f,
                        float thickness = 0f, Vector3? sideColor = null)
    {
        var pts = new Vector3[sides];
        for (int s = 0; s < sides; s++)
        {
            float a = MathF.Tau * s / sides;
            float r = radius * (1f + (rng == null ? 0f : (rng.NextSingle() * 2f - 1f) * jitter));
            pts[s] = center + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * r;
        }
        for (int s = 0; s < sides; s++)
            AddTriangle(center, pts[(s + 1) % sides], pts[s], color);

        if (thickness <= 0f) return;
        var down = new Vector3(0, thickness, 0);
        for (int s = 0; s < sides; s++)
        {
            Vector3 a = pts[s], b = pts[(s + 1) % sides];
            AddQuad(a - down, b - down, b, a, sideColor ?? color);
        }
    }

    // --- Icosfera (rocce, cespugli, chiome, testa del personaggio) ---

    private static readonly float Phi = (1f + MathF.Sqrt(5f)) / 2f;

    private static readonly Vector3[] IcoVerts =
    [
        new(-1, Phi, 0), new(1, Phi, 0), new(-1, -Phi, 0), new(1, -Phi, 0),
        new(0, -1, Phi), new(0, 1, Phi), new(0, -1, -Phi), new(0, 1, -Phi),
        new(Phi, 0, -1), new(Phi, 0, 1), new(-Phi, 0, -1), new(-Phi, 0, 1),
    ];

    private static readonly int[] IcoFaces =
    [
        0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
        1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
        3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
        4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
    ];

    /// <summary>Ellissoide low poly. <paramref name="jitter"/> deforma i vertici (condivisi, quindi senza buchi).</summary>
    public void AddIcosphere(Vector3 center, Vector3 radii, int subdivisions, Vector3 color, Random? rng = null, float jitter = 0f)
    {
        var verts = IcoVerts.Select(Vector3.Normalize).ToList();
        var faces = new List<(int A, int B, int C)>();
        for (int i = 0; i < IcoFaces.Length; i += 3)
            faces.Add((IcoFaces[i], IcoFaces[i + 1], IcoFaces[i + 2]));

        for (int s = 0; s < subdivisions; s++)
        {
            var cache = new Dictionary<long, int>();
            int Mid(int a, int b)
            {
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!cache.TryGetValue(key, out int m))
                {
                    m = verts.Count;
                    verts.Add(Vector3.Normalize(verts[a] + verts[b]));
                    cache[key] = m;
                }
                return m;
            }

            var next = new List<(int, int, int)>(faces.Count * 4);
            foreach (var (a, b, c) in faces)
            {
                int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                next.Add((a, ab, ca));
                next.Add((b, bc, ab));
                next.Add((c, ca, bc));
                next.Add((ab, bc, ca));
            }
            faces = next;
        }

        var pts = new Vector3[verts.Count];
        for (int i = 0; i < verts.Count; i++)
        {
            float j = rng == null ? 1f : 1f + (rng.NextSingle() * 2f - 1f) * jitter;
            pts[i] = center + verts[i] * radii * j;
        }

        foreach (var (ia, ib, ic) in faces)
        {
            Vector3 a = pts[ia], b = pts[ib], c = pts[ic];
            // Garantisce l'orientamento verso l'esterno
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - center) < 0f)
                (b, c) = (c, b);
            AddTriangle(a, b, c, color);
        }
    }

    public Mesh Build(GL gl) => new(gl, BuildData());

    /// <summary>
    /// Prepara i dati dei vertici senza toccare OpenGL: si può chiamare da un thread in background
    /// e caricare poi la mesh sul thread principale con <see cref="Mesh(GL, MeshData)"/>.
    /// </summary>
    public MeshData BuildData()
    {
        if (ComputeEdges) return BuildDataWithEdges();

        int count = (int)VertexCount;
        var data = new float[count * Mesh.FloatsPerVertex];
        for (int v = 0; v < count; v++)
            WriteVertex(data, v, v, Vector3.One); // nessuno spigolo: baricentriche mai a zero
        if (SmoothNormals) AverageNormals(data, count);
        return new MeshData(data, _indices.ToArray());
    }

    /// <summary>Media le normali dei vertici nella stessa posizione (somma delle normali delle facce).</summary>
    private static void AverageNormals(float[] data, int count)
    {
        const int F = Mesh.FloatsPerVertex;
        static (int, int, int) Key(float[] d, int o) =>
            ((int)MathF.Round(d[o] * 4000f), (int)MathF.Round(d[o + 1] * 4000f), (int)MathF.Round(d[o + 2] * 4000f));

        var sums = new Dictionary<(int, int, int), Vector3>();
        for (int v = 0; v < count; v++)
        {
            int o = v * F;
            var key = Key(data, o);
            sums[key] = sums.GetValueOrDefault(key) + new Vector3(data[o + 3], data[o + 4], data[o + 5]);
        }
        for (int v = 0; v < count; v++)
        {
            int o = v * F;
            Vector3 n = sums[Key(data, o)];
            n = n.LengthSquared() > 1e-10f ? Vector3.Normalize(n) : new Vector3(data[o + 3], data[o + 4], data[o + 5]);
            data[o + 3] = n.X; data[o + 4] = n.Y; data[o + 5] = n.Z;
        }
    }

    private void WriteVertex(float[] data, int dst, int src, Vector3 bary)
    {
        int o = dst * Mesh.FloatsPerVertex;
        for (int k = 0; k < BaseFloats; k++) data[o + k] = _vertices[src * BaseFloats + k];
        data[o + 9] = bary.X;
        data[o + 10] = bary.Y;
        data[o + 11] = bary.Z;
        for (int k = 0; k < ObjectFloats; k++) data[o + 12 + k] = _objects[src * ObjectFloats + k];
    }

    /// <summary>
    /// Ogni triangolo riceve vertici propri con coordinate baricentriche (1,0,0), (0,1,0), (0,0,1).
    /// Il lato opposto al vertice k è dove la componente k vale zero; se quel lato è condiviso con un
    /// triangolo complanare (es. la diagonale di un quad) la componente viene alzata di 1, così lo
    /// shader non lo disegna.
    /// </summary>
    private MeshData BuildDataWithEdges()
    {
        int triCount = _indices.Count / 3;
        Vector3 Pos(uint v) => new(_vertices[(int)v * BaseFloats], _vertices[(int)v * BaseFloats + 1], _vertices[(int)v * BaseFloats + 2]);
        Vector3 Nrm(uint v) => new(_vertices[(int)v * BaseFloats + 3], _vertices[(int)v * BaseFloats + 4], _vertices[(int)v * BaseFloats + 5]);

        static (int, int, int) Q(Vector3 p) =>
            ((int)MathF.Round(p.X * 2000f), (int)MathF.Round(p.Y * 2000f), (int)MathF.Round(p.Z * 2000f));
        static ((int, int, int), (int, int, int)) Key((int, int, int) a, (int, int, int) b) =>
            a.CompareTo(b) <= 0 ? (a, b) : (b, a);

        var edges = new Dictionary<((int, int, int), (int, int, int)), List<int>>(); // lato -> triangoli*3 + k
        for (int t = 0; t < triCount; t++)
            for (int k = 0; k < 3; k++)
            {
                uint a = _indices[t * 3 + (k + 1) % 3], b = _indices[t * 3 + (k + 2) % 3];
                var key = Key(Q(Pos(a)), Q(Pos(b)));
                if (!edges.TryGetValue(key, out var list)) edges[key] = list = new List<int>(2);
                list.Add(t * 3 + k);
            }

        var hidden = new bool[triCount * 3];
        foreach (var list in edges.Values)
        {
            if (list.Count != 2) continue;
            int t0 = list[0] / 3, t1 = list[1] / 3;
            if (Vector3.Dot(Nrm(_indices[t0 * 3]), Nrm(_indices[t1 * 3])) > 0.999f)
                hidden[list[0]] = hidden[list[1]] = true;
        }

        var data = new float[triCount * 3 * Mesh.FloatsPerVertex];
        var indices = new uint[triCount * 3];
        for (int t = 0; t < triCount; t++)
        {
            var hide = new Vector3(hidden[t * 3] ? 1 : 0, hidden[t * 3 + 1] ? 1 : 0, hidden[t * 3 + 2] ? 1 : 0);
            for (int k = 0; k < 3; k++)
            {
                int dst = t * 3 + k;
                var bary = new Vector3(k == 0 ? 1 : 0, k == 1 ? 1 : 0, k == 2 ? 1 : 0) + hide;
                WriteVertex(data, dst, (int)_indices[dst], bary);
                indices[dst] = (uint)dst;
            }
        }
        return new MeshData(data, indices);
    }
}

/// <summary>Vertici e indici pronti da caricare sulla GPU.</summary>
public sealed record MeshData(float[] Vertices, uint[] Indices)
{
    public bool IsEmpty => Indices.Length == 0;
}
