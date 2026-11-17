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

    public bool IsEmpty => _indices.Count == 0;

    private uint VertexCount => (uint)(_vertices.Count / Mesh.FloatsPerVertex);

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

    public Mesh Build(GL gl) =>
        new(gl, CollectionsMarshal.AsSpan(_vertices), CollectionsMarshal.AsSpan(_indices));
}
