using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

public enum Biome { Meadow, Blossom, Autumn, Snowy }

/// <summary>
/// Un'isola-diorama quadrata generata proceduralmente: terreno low poly con spessore,
/// eventuale fiume con cascate, sentieri verso le uscite, ponti e oggetti di scena.
/// </summary>
public sealed class Zone : IDisposable
{
    public const float Half = 48f;           // isola di 96 x 96
    public const int N = 128;                // celle del terreno per lato (0.75 ciascuna)
    private const float AreaFactor = Half * Half / 144f; // oggetti di scena in proporzione all'area
    public const float Cell = Half * 2f / N;
    public const float WaterY = -0.3f;
    public const float RiverHalf = 2.0f;
    public const float PathHalf = 1.1f;
    public const float PathRaise = 0.2f;    // spessore dei sentieri sopra l'erba
    private const float PathDepth = 0.1f;   // quanto la lastra affonda sotto il terreno
    private const float HubRadius = 1.9f;
    private const float RiseDuration = 1.6f;

    public int X { get; }
    public int Z { get; }
    public Vector3 Origin { get; }
    public Biome Biome { get; }
    public bool[] Exits { get; } = new bool[4];

    // Le mesh esistono solo dopo Upload(): la generazione avviene in background senza OpenGL
    public Mesh Opaque { get; private set; } = null!;
    /// <summary>Alberi, rocce e oggetti di scena: separati dal terreno per renderli trasparenti quando coprono il giocatore.</summary>
    public Mesh Scenery { get; private set; } = null!;
    public Mesh? Water { get; private set; }
    public bool Uploaded { get; private set; }

    private MeshData? _opaqueData, _sceneryData, _waterData;
    public List<Bridge> Bridges { get; } = new();
    public List<Vector3> Campfires { get; } = new(); // coordinate locali

    /// <summary>Avanzamento dell'animazione di comparsa (0 → 1).</summary>
    public float Rise;
    public bool Ready => Rise >= 1f;
    public float YOffset => -22f * (1f - EaseOutBack(Math.Clamp(Rise, 0f, 1f)));
    public Matrix4x4 Model => Matrix4x4.CreateTranslation(Origin + new Vector3(0, YOffset, 0));

    public string Name => Biome switch
    {
        Biome.Meadow => "Prato",
        Biome.Blossom => "Bosco fiorito",
        Biome.Autumn => "Bosco d'autunno",
        _ => "Valle innevata",
    };

    private readonly bool _hub;
    private readonly bool _hasRiver;
    private readonly bool _riverAlongZ;
    private readonly float _riverOffset, _riverPhase;
    private readonly float _p1, _p2, _p3;
    private readonly float[,] _h = new float[N + 1, N + 1];
    private readonly List<(float X, float Z, float R)> _clearings = new();
    private readonly Palette _pal;

    private readonly record struct Palette(Vector3 Grass, Vector3 GrassSide, Vector3 Path, Vector3 Bank);

    /// <summary>Genera l'isola (solo CPU, sicuro su un thread in background). Poi va chiamato <see cref="Upload"/>.</summary>
    public Zone(int x, int z, bool[] exits)
    {
        X = x;
        Z = z;
        Origin = new Vector3(x * WorldMap.Spacing, 0, z * WorldMap.Spacing);
        Array.Copy(exits, Exits, 4);

        var rng = new Random((int)(Noise.Hash(x, z, 7) & 0x7FFFFFFF));
        bool home = x == 0 && z == 0;
        Biome = home ? Biome.Meadow : (Biome)(Noise.Hash(x, z, 13) % 4);
        _pal = Biome switch
        {
            Biome.Snowy => new Palette(new(0.90f, 0.93f, 0.97f), new(0.78f, 0.83f, 0.90f), new(0.78f, 0.72f, 0.62f), new(0.62f, 0.66f, 0.72f)),
            Biome.Autumn => new Palette(new(0.58f, 0.66f, 0.22f), new(0.45f, 0.52f, 0.16f), new(0.90f, 0.78f, 0.52f), new(0.55f, 0.48f, 0.32f)),
            Biome.Blossom => new Palette(new(0.45f, 0.74f, 0.26f), new(0.33f, 0.58f, 0.18f), new(0.92f, 0.82f, 0.58f), new(0.55f, 0.50f, 0.36f)),
            _ => new Palette(new(0.34f, 0.62f, 0.15f), new(0.27f, 0.52f, 0.12f), new(0.88f, 0.77f, 0.52f), new(0.55f, 0.50f, 0.34f)),
        };

        _p1 = rng.NextSingle() * 10f;
        _p2 = rng.NextSingle() * 10f;
        _p3 = rng.NextSingle() * 10f;

        if (home)
        {
            _hasRiver = true;
            _riverAlongZ = true;
            _riverOffset = 14f;
            _riverPhase = 0.8f;
        }
        else
        {
            _hasRiver = rng.NextSingle() < 0.55f;
            _riverAlongZ = rng.Next(2) == 0;
            _riverOffset = (rng.Next(2) == 0 ? -1f : 1f) * Half * (0.22f + rng.NextSingle() * 0.4f);
            _riverPhase = rng.NextSingle() * MathF.Tau;
        }

        int exitCount = Exits.Count(e => e);
        bool straight = exitCount == 2 && ((Exits[0] && Exits[2]) || (Exits[1] && Exits[3]));
        _hub = !straight;

        if (home)
            _clearings.Add((-5f, 5f, 1.7f));
        for (int c = 0; c < 3; c++)
            if (rng.NextSingle() < 0.45f) TryAddClearing(rng);
        foreach (var c in _clearings)
            Campfires.Add(new Vector3(c.X, HeightFn(c.X, c.Z) + PathRaise, c.Z));

        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
                _h[i, j] = HeightFn(-Half + i * Cell, -Half + j * Cell);

        BuildRiverBridges();

        var mb = new MeshBuilder { ColorRng = rng, ColorJitter = 0.05f };
        BuildTerrain(mb);
        BuildSides(mb);
        BuildPaths(mb, rng);
        foreach (var b in Bridges)
            b.Build(mb, b.Center - Origin);
        _opaqueData = mb.BuildData();

        // Stesso Random: la sequenza dei numeri casuali (e quindi il mondo) resta identica
        var pb = new MeshBuilder { ColorRng = rng, ColorJitter = 0.05f, ComputeEdges = true };
        PlaceProps(pb, rng, home);
        _sceneryData = pb.BuildData();

        var wb = new MeshBuilder();
        if (_hasRiver) BuildWater(wb);
        _waterData = wb.IsEmpty ? null : wb.BuildData();
    }

    internal static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    public void Update(float dt)
    {
        if (Rise < 1f) Rise = MathF.Min(1f, Rise + dt / RiseDuration);
    }

    public Vector3 ExitPoint(Dir d) => Origin + d.Vec() * Half;

    // ------------------------------------------------------------------ campi di distanza

    private float PathDistance(float x, float z)
    {
        float best = float.MaxValue;
        foreach (Dir d in DirExtensions.All)
        {
            if (!Exits[(int)d]) continue;
            Vector3 v = d.Vec();
            float t = Math.Clamp(x * v.X + z * v.Z, 0f, Half + 1f);
            float dx = x - v.X * t, dz = z - v.Z * t;
            best = MathF.Min(best, MathF.Sqrt(dx * dx + dz * dz));
        }
        if (_hub)
            best = MathF.Min(best, MathF.Max(0f, MathF.Sqrt(x * x + z * z) - HubRadius + PathHalf));
        return best;
    }

    private float ClearingDistance(float x, float z)
    {
        float best = float.MaxValue;
        foreach (var c in _clearings)
        {
            float d = MathF.Sqrt((x - c.X) * (x - c.X) + (z - c.Z) * (z - c.Z)) - c.R + PathHalf;
            best = MathF.Min(best, MathF.Max(0f, d));
        }
        return best;
    }

    private float RiverLine(float t) =>
        _riverOffset + 2.2f * MathF.Sin(t * 0.11f + _riverPhase) + 0.8f * MathF.Sin(t * 0.3f + _riverPhase * 2f);

    private float RiverDistance(float x, float z)
    {
        if (!_hasRiver) return float.MaxValue;
        return _riverAlongZ ? MathF.Abs(x - RiverLine(z)) : MathF.Abs(z - RiverLine(x));
    }

    private float HeightFn(float x, float z)
    {
        float h = 0.16f * MathF.Sin(x * 0.55f + _p1) * MathF.Cos(z * 0.47f + _p2)
                + 0.08f * MathF.Sin(x * 1.3f + z * 0.9f + _p3)
                + 0.35f * (MathF.Sin(x * 0.11f + _p2) * MathF.Cos(z * 0.13f + _p3) + 1f) // dolci colline
                + 0.05f;
        float flat = MathF.Min(PathDistance(x, z), ClearingDistance(x, z));
        h *= Noise.Smooth(PathHalf + 0.2f, PathHalf + 1.8f, flat);

        if (_hasRiver)
        {
            float t = 1f - Noise.Smooth(RiverHalf - 0.9f, RiverHalf + 0.3f, RiverDistance(x, z));
            h = Noise.Lerp(h, -0.9f, t);
        }
        return h;
    }

    /// <summary>Altezza del terreno (locale), interpolata esattamente come i triangoli della mesh.</summary>
    public float HeightLocal(float x, float z)
    {
        float fx = (x + Half) / Cell, fz = (z + Half) / Cell;
        int i = Math.Clamp((int)MathF.Floor(fx), 0, N - 1);
        int j = Math.Clamp((int)MathF.Floor(fz), 0, N - 1);
        float u = Math.Clamp(fx - i, 0f, 1f), v = Math.Clamp(fz - j, 0f, 1f);
        float h00 = _h[i, j], h10 = _h[i + 1, j], h01 = _h[i, j + 1], h11 = _h[i + 1, j + 1];
        return u >= v
            ? h00 + (h10 - h00) * u + (h11 - h10) * v
            : h00 + (h11 - h01) * u + (h01 - h00) * v;
    }

    private void TryAddClearing(Random rng)
    {
        for (int t = 0; t < 40; t++)
        {
            float x = (rng.NextSingle() * 2f - 1f) * (Half - 4f), z = (rng.NextSingle() * 2f - 1f) * (Half - 4f);
            if (PathDistance(x, z) < PathHalf + 2.4f) continue;
            if (RiverDistance(x, z) < RiverHalf + 3f) continue;
            if (ClearingDistance(x, z) < 8f) continue;
            _clearings.Add((x, z, 1.7f));
            return;
        }
    }

    // ------------------------------------------------------------------ geometria

    private void BuildRiverBridges()
    {
        if (!_hasRiver) return;
        // Il fiume interseca il braccio di sentiero perpendicolare dal lato del suo offset
        Dir crossing = _riverAlongZ
            ? (_riverOffset > 0 ? Dir.East : Dir.West)
            : (_riverOffset > 0 ? Dir.South : Dir.North);
        if (!Exits[(int)crossing]) return;

        float line = RiverLine(0f);
        Vector3 local = _riverAlongZ ? new Vector3(line, 0, 0) : new Vector3(0, 0, line);
        Bridges.Add(new Bridge
        {
            Center = Origin + local,
            Dir = _riverAlongZ ? Vector3.UnitX : Vector3.UnitZ,
            Span = RiverHalf * 2f + 3.4f,
            BaseY = PathRaise,
            Arch = 0.4f,
        });
    }

    private Vector3 GridPoint(int i, int j) => new(-Half + i * Cell, _h[i, j], -Half + j * Cell);

    private void BuildTerrain(MeshBuilder mb)
    {
        var bed = new Vector3(0.20f, 0.32f, 0.45f);
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                Vector3 p00 = GridPoint(i, j), p10 = GridPoint(i + 1, j), p01 = GridPoint(i, j + 1), p11 = GridPoint(i + 1, j + 1);
                AddTerrainTri(mb, p00, p11, p10, bed);
                AddTerrainTri(mb, p00, p01, p11, bed);
            }
    }

    private void AddTerrainTri(MeshBuilder mb, Vector3 a, Vector3 b, Vector3 c, Vector3 bed)
    {
        float avg = (a.Y + b.Y + c.Y) / 3f;
        Vector3 mid = (a + b + c) / 3f;
        bool nearRiver = RiverDistance(mid.X, mid.Z) < RiverHalf + 0.6f;
        Vector3 color;
        if (nearRiver && avg < WaterY - 0.15f) color = bed;
        else if (nearRiver && avg < WaterY + 0.25f) color = _pal.Bank;
        else color = _pal.Grass * (1f + avg * 0.35f);
        mb.AddTriangle(a, b, c, color);
    }

    private void BuildSides(MeshBuilder mb)
    {
        var dirt = new Vector3(0.78f, 0.48f, 0.20f);
        var dirtDark = new Vector3(0.46f, 0.28f, 0.13f);
        const float lip = -0.45f, band = -1.25f;

        foreach (Dir d in DirExtensions.All)
        {
            Vector3 outward = d.Vec();
            for (int k = 0; k < N; k++)
            {
                (int ia, int ja, int ib, int jb) = d switch
                {
                    Dir.North => (k + 1, 0, k, 0),
                    Dir.South => (k, N, k + 1, N),
                    Dir.West => (0, k, 0, k + 1),
                    _ => (N, k + 1, N, k),
                };
                Vector3 a = GridPoint(ia, ja), b = GridPoint(ib, jb);
                Vector3 aFlat = a with { Y = 0 }, bFlat = b with { Y = 0 };

                if (MathF.Max(a.Y, b.Y) > lip)
                {
                    Vector3 color = (a.Y + b.Y) * 0.5f < WaterY ? dirt : _pal.GrassSide;
                    mb.AddQuad(aFlat with { Y = lip }, bFlat with { Y = lip },
                               bFlat with { Y = MathF.Max(b.Y, lip) }, aFlat with { Y = MathF.Max(a.Y, lip) }, color);
                }

                float topA = MathF.Min(a.Y, lip), topB = MathF.Min(b.Y, lip);
                mb.AddQuad(aFlat with { Y = band }, bFlat with { Y = band }, bFlat with { Y = topB }, aFlat with { Y = topA }, dirt);

                // Parte inferiore rastremata e irregolare
                float botA = -2.3f - Noise.Hash01(ia, ja, X * 31 + Z) * 1.3f;
                float botB = -2.3f - Noise.Hash01(ib, jb, X * 31 + Z) * 1.3f;
                Vector3 inA = aFlat - outward * 0.7f, inB = bFlat - outward * 0.7f;
                mb.AddQuad(inA with { Y = botA }, inB with { Y = botB }, bFlat with { Y = band }, aFlat with { Y = band }, dirtDark);
            }
        }
    }

    /// <summary>Sentieri come lastre di terra battuta con spessore e bordo visibile.</summary>
    private void BuildPaths(MeshBuilder mb, Random rng)
    {
        Vector3 side = _pal.Path * 0.8f;
        var raise = new Vector3(0, PathRaise, 0);
        var depth = new Vector3(0, PathRaise + PathDepth, 0);

        foreach (Dir d in DirExtensions.All)
        {
            if (!Exits[(int)d]) continue;
            Vector3 v = d.Vec();
            var perp = new Vector3(-v.Z, 0, v.X);
            const int steps = (int)(Half / 0.6f);
            Vector3 prevL = default, prevR = default;
            bool prevDry = false;
            for (int k = 0; k <= steps; k++)
            {
                Vector3 c = v * (Half * k / steps);
                float jl = (rng.NextSingle() - 0.5f) * 0.24f, jr = (rng.NextSingle() - 0.5f) * 0.24f;
                Vector3 l = c + perp * (PathHalf + jl), r = c - perp * (PathHalf + jr);
                l.Y = HeightLocal(l.X, l.Z);
                r.Y = HeightLocal(r.X, r.Z);
                l += raise;
                r += raise;
                bool dry = HeightLocal(c.X, c.Z) > WaterY + 0.2f;
                bool draw = k > 0 && dry && prevDry;
                if (draw)
                {
                    mb.AddQuad(prevL, l, r, prevR, _pal.Path);
                    mb.AddQuad(prevL - depth, l - depth, l, prevL, side); // fianco sinistro
                    mb.AddQuad(prevR, r, r - depth, prevR - depth, side); // fianco destro
                    // Testate: all'inizio di un tratto e alla fine (bordo dell'isola o fiume)
                    if (k == 1 || !(HeightLocal(c.X - v.X * Half / steps, c.Z - v.Z * Half / steps) > WaterY + 0.2f))
                        mb.AddQuad(prevR - depth, prevL - depth, prevL, prevR, side);
                    Vector3 next = v * (Half * (k + 1) / steps);
                    if (k == steps || HeightLocal(next.X, next.Z) <= WaterY + 0.2f)
                        mb.AddQuad(l - depth, r - depth, r, l, side);
                }
                prevL = l; prevR = r; prevDry = dry;
            }
        }

        if (_hub)
            mb.AddDisc(raise + new Vector3(0, 0.006f, 0), HubRadius, 11, _pal.Path, rng, 0.08f, PathRaise + PathDepth, side);
        foreach (var c in _clearings)
            mb.AddDisc(new Vector3(c.X, HeightLocal(c.X, c.Z) + PathRaise + 0.005f, c.Z), c.R, 10, _pal.Path, rng, 0.1f,
                       PathRaise + PathDepth, side);
    }

    /// <summary>Rialzo del sentiero in un punto locale (0 fuori dai sentieri o sui tratti bagnati).</summary>
    private float PathLift(float x, float z)
    {
        float d = MathF.Min(PathDistance(x, z), ClearingDistance(x, z));
        if (d > PathHalf + 0.1f) return 0f;
        if (HeightLocal(x, z) <= WaterY + 0.2f) return 0f;
        return PathRaise * (1f - Noise.Smooth(PathHalf - 0.1f, PathHalf + 0.1f, d));
    }

    private void BuildWater(MeshBuilder mb)
    {
        var water = new Vector3(0.20f, 0.46f, 0.80f);
        var fall = new Vector3(0.42f, 0.66f, 0.95f);

        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                float min = MathF.Min(MathF.Min(_h[i, j], _h[i + 1, j]), MathF.Min(_h[i, j + 1], _h[i + 1, j + 1]));
                if (min >= WaterY) continue;
                float x0 = -Half + i * Cell, x1 = x0 + Cell, z0 = -Half + j * Cell, z1 = z0 + Cell;
                mb.AddQuad(new(x0, WaterY, z1), new(x1, WaterY, z1), new(x1, WaterY, z0), new(x0, WaterY, z0), water);
            }

        // Cascate dove il fiume raggiunge il bordo dell'isola
        foreach (Dir d in DirExtensions.All)
        {
            Vector3 o = d.Vec();
            for (int k = 0; k < N; k++)
            {
                (int ia, int ja, int ib, int jb) = d switch
                {
                    Dir.North => (k, 0, k + 1, 0),
                    Dir.South => (k, N, k + 1, N),
                    Dir.West => (0, k, 0, k + 1),
                    _ => (N, k, N, k + 1),
                };
                if (MathF.Min(_h[ia, ja], _h[ib, jb]) >= WaterY) continue;
                Vector3 a = GridPoint(ia, ja) with { Y = 0 }, b = GridPoint(ib, jb) with { Y = 0 };
                mb.AddQuad(a + o * 0.06f + new Vector3(0, WaterY, 0), b + o * 0.06f + new Vector3(0, WaterY, 0),
                           b + o * 0.5f + new Vector3(0, -8f, 0), a + o * 0.5f + new Vector3(0, -8f, 0), fall);
            }
        }
    }

    // ------------------------------------------------------------------ oggetti di scena

    private void PlaceProps(MeshBuilder mb, Random rng, bool home)
    {
        var placed = new List<(float X, float Z, float R)>();

        bool Free(float x, float z, float r, float pathMargin)
        {
            float lim = Half - 0.6f - r * 0.6f;
            if (MathF.Abs(x) > lim || MathF.Abs(z) > lim) return false;
            if (PathDistance(x, z) < PathHalf + pathMargin + r) return false;
            if (RiverDistance(x, z) < RiverHalf + 0.5f + r) return false;
            foreach (var c in _clearings)
                if (MathF.Sqrt((x - c.X) * (x - c.X) + (z - c.Z) * (z - c.Z)) < c.R + r + 0.3f) return false;
            foreach (var p in placed)
                if (MathF.Sqrt((x - p.X) * (x - p.X) + (z - p.Z) * (z - p.Z)) < (r + p.R) * 0.95f) return false;
            return true;
        }

        Vector3 At(float x, float z) => new(x, HeightLocal(x, z) - 0.05f, z);

        // Prova a piazzare 'count' oggetti con raggio di ingombro r (scala casuale)
        void Scatter(int count, float minS, float maxS, float radius, float colliderR, float pathMargin, Action<Vector3, float> build)
        {
            count = (int)(count * AreaFactor);
            for (int n = 0; n < count; n++)
            {
                for (int t = 0; t < 30; t++)
                {
                    float s = minS + rng.NextSingle() * (maxS - minS);
                    float x = rng.NextSingle() * Half * 2f - Half, z = rng.NextSingle() * Half * 2f - Half;
                    if (!Free(x, z, radius * s, pathMargin)) continue;
                    placed.Add((x, z, radius * s));
                    if (colliderR > 0f) AddCollider(x, z, colliderR * s);
                    // Solo ciò che ha un ingombro (alberi, rocce, cespugli) può nascondere il giocatore
                    if (colliderR > 0f) mb.BeginObject();
                    build(At(x, z), s);
                    if (colliderR > 0f) mb.EndObject();
                    break;
                }
            }
        }

        void Fixed(float x, float z, float radius, float colliderR, Action<Vector3> build)
        {
            placed.Add((x, z, radius));
            if (colliderR > 0f) AddCollider(x, z, colliderR);
            mb.BeginObject();
            build(At(x, z));
            mb.EndObject();
        }

        var pineGreen = new Vector3(0.16f, 0.48f, 0.20f);
        var coneGreen = new Vector3(0.74f, 0.86f, 0.38f);
        var bushGreen = new Vector3(0.45f, 0.76f, 0.26f);
        var rockGrey = new Vector3(0.56f, 0.62f, 0.61f);
        Vector3[] blossom = [new(0.80f, 0.38f, 0.86f), new(0.62f, 0.30f, 0.82f), new(0.94f, 0.58f, 0.86f)];
        Vector3[] autumn = [new(0.95f, 0.52f, 0.14f), new(0.86f, 0.30f, 0.12f), new(0.96f, 0.74f, 0.22f)];
        Vector3[] flowerColors = Biome switch
        {
            Biome.Blossom => [new(0.95f, 0.55f, 0.85f), new(0.75f, 0.45f, 0.95f), new(1f, 1f, 1f)],
            Biome.Autumn => [new(0.95f, 0.6f, 0.15f), new(0.9f, 0.3f, 0.2f)],
            Biome.Snowy => [new(0.7f, 0.8f, 1f), new(1f, 1f, 1f)],
            _ => [new(1f, 1f, 1f), new(1f, 0.85f, 0.3f), new(0.95f, 0.5f, 0.7f)],
        };

        foreach (var c in _clearings)
        {
            Props.Campfire(mb, rng, new Vector3(c.X, HeightLocal(c.X, c.Z) + PathRaise, c.Z));
            AddCollider(c.X, c.Z, 0.85f);
        }

        if (home)
        {
            // Disposizione ispirata allo screenshot di riferimento
            Fixed(-9.5f, -8.5f, 1.3f, 0.45f, p => Props.Pine(mb, rng, p, 1.5f, pineGreen));
            Fixed(-10f, -4.5f, 1.1f, 0.4f, p => Props.Pine(mb, rng, p, 1.15f, pineGreen));
            Fixed(-6f, -6.8f, 1.0f, 0.4f, p => Props.Pine(mb, rng, p, 1.0f, pineGreen));
            Fixed(-7.2f, -10f, 0.9f, 0.4f, p => Props.ConeTree(mb, rng, p, 1.25f, coneGreen));
            Fixed(-3.6f, -9.4f, 0.9f, 0.4f, p => Props.ConeTree(mb, rng, p, 1.1f, coneGreen));
            Fixed(-1.4f, -8.6f, 1.4f, 1.1f, p => Props.Rock(mb, rng, p, 1.4f, rockGrey));
            Fixed(9.2f, -4.2f, 2.0f, 0.5f, p => Props.RoundTree(mb, rng, p, 1.25f, blossom));
            Fixed(8.6f, -8.8f, 1.5f, 0.45f, p => Props.RoundTree(mb, rng, p, 0.95f, blossom));
            Fixed(9.6f, 4.5f, 0.9f, 0.7f, p => Props.Rock(mb, rng, p, 0.9f, rockGrey));
            Fixed(9.2f, 8.8f, 0.9f, 0.4f, p => Props.ConeTree(mb, rng, p, 1.0f, coneGreen));
            Fixed(-9.6f, 9.4f, 1.1f, 0.4f, p => Props.Pine(mb, rng, p, 1.1f, pineGreen));
            // Il resto dell'isola, più grande, si riempie come un prato con qualche albero fiorito
            Scatter(5, 0.9f, 1.5f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, pineGreen));
            Scatter(4, 0.9f, 1.3f, 0.8f, 0.4f, 0.6f, (p, s) => Props.ConeTree(mb, rng, p, s, coneGreen));
            Scatter(2, 0.9f, 1.3f, 1.6f, 0.45f, 0.6f, (p, s) => Props.RoundTree(mb, rng, p, s, blossom));
            Scatter(10, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, bushGreen));
            Scatter(6, 0.35f, 0.6f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, rockGrey));
            Scatter(14, 1f, 1f, 0.5f, 0f, 0.15f, (p, _) => Props.Flowers(mb, rng, p, flowerColors[rng.Next(flowerColors.Length)]));
        }
        else
        {
            switch (Biome)
            {
                case Biome.Meadow:
                    Scatter(7, 0.9f, 1.5f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, pineGreen));
                    Scatter(6, 0.9f, 1.3f, 0.8f, 0.4f, 0.6f, (p, s) => Props.ConeTree(mb, rng, p, s, coneGreen));
                    Scatter(10, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, bushGreen));
                    Scatter(7, 0.35f, 1.3f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, rockGrey));
                    break;
                case Biome.Blossom:
                    Scatter(6, 0.9f, 1.3f, 1.6f, 0.45f, 0.6f, (p, s) => Props.RoundTree(mb, rng, p, s, blossom));
                    Scatter(3, 0.9f, 1.3f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, pineGreen));
                    Scatter(10, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, bushGreen));
                    Scatter(5, 0.35f, 1.0f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, rockGrey));
                    break;
                case Biome.Autumn:
                    Scatter(7, 0.9f, 1.3f, 1.6f, 0.45f, 0.6f, (p, s) => Props.RoundTree(mb, rng, p, s, autumn));
                    Scatter(4, 0.9f, 1.4f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, new Vector3(0.14f, 0.38f, 0.2f)));
                    Scatter(8, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, new Vector3(0.78f, 0.48f, 0.16f)));
                    Scatter(5, 0.35f, 1.0f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, rockGrey));
                    Scatter(12, 0.8f, 1.4f, 0.3f, 0f, 0.15f, (p, s) => Props.Mushroom(mb, rng, p, s));
                    break;
                case Biome.Snowy:
                    Scatter(12, 0.9f, 1.6f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, new Vector3(0.12f, 0.36f, 0.24f), snowy: true));
                    Scatter(8, 0.35f, 1.3f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, new Vector3(0.6f, 0.64f, 0.72f)));
                    Scatter(5, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, new Vector3(0.82f, 0.88f, 0.94f)));
                    break;
            }
            Scatter(Biome == Biome.Snowy ? 5 : 14, 1f, 1f, 0.5f, 0f, 0.15f,
                (p, _) => Props.Flowers(mb, rng, p, flowerColors[rng.Next(flowerColors.Length)]));
        }

        // Massi nel fiume (lontani dai ponti)
        if (_hasRiver)
        {
            int rocks = (int)(rng.Next(1, 4) * Half / 12f);
            for (int n = 0; n < rocks; n++)
            {
                float t = rng.NextSingle() * (Half * 2f - 4f) - (Half - 2f);
                if (MathF.Abs(t) < 3.5f) continue;
                float c = RiverLine(t) + (rng.NextSingle() - 0.5f) * 1.6f;
                float x = _riverAlongZ ? c : t, z = _riverAlongZ ? t : c;
                Props.Rock(mb, rng, new Vector3(x, WaterY - 0.25f, z), 0.5f + rng.NextSingle() * 0.4f, rockGrey);
            }
        }
    }

    // ------------------------------------------------------------------ interrogazioni dal gameplay

    public bool IsWalkable(Vector3 world, float radius)
    {
        foreach (var b in Bridges)
            if (b.TryGetHeight(world, 0.15f, out _)) return true;

        float x = world.X - Origin.X, z = world.Z - Origin.Z;
        float lim = Half - 0.25f;
        if (MathF.Abs(x) > lim || MathF.Abs(z) > lim)
        {
            // Oltre il bordo si può andare solo nel corridoio di un'uscita
            bool corridor = false;
            if (MathF.Abs(x) <= Half + 0.05f && MathF.Abs(z) <= Half + 0.05f)
            {
                foreach (Dir d in DirExtensions.All)
                {
                    if (!Exits[(int)d]) continue;
                    Vector3 v = d.Vec();
                    float along = x * v.X + z * v.Z;
                    float lateral = MathF.Abs(x * v.Z - z * v.X);
                    if (along > 0f && lateral < PathHalf - 0.2f) corridor = true;
                }
            }
            if (!corridor) return false;
        }

        if (HeightLocal(x, z) < WaterY + 0.05f) return false;

        var bucket = _buckets[BucketIndex(x), BucketIndex(z)];
        if (bucket != null)
            foreach (var c in bucket)
            {
                float dx = x - c.X, dz = z - c.Z;
                if (dx * dx + dz * dz < (c.R + radius) * (c.R + radius)) return false;
            }
        return true;
    }

    // Griglia di secchielli per gli ostacoli: ogni ostacolo è registrato in tutte le celle che
    // il suo raggio (più il massimo raggio di chi si muove) può toccare.
    private const float BucketSize = 3f;
    private const int BucketCount = (int)(Half * 2f / BucketSize) + 2;
    private const float MaxQueryRadius = 1f;
    private readonly List<(float X, float Z, float R)>?[,] _buckets = new List<(float, float, float)>?[BucketCount, BucketCount];

    private static int BucketIndex(float v) => Math.Clamp((int)MathF.Floor((v + Half) / BucketSize), 0, BucketCount - 1);

    private void AddCollider(float x, float z, float r)
    {
        float reach = r + MaxQueryRadius;
        for (int i = BucketIndex(x - reach); i <= BucketIndex(x + reach); i++)
            for (int j = BucketIndex(z - reach); j <= BucketIndex(z + reach); j++)
                (_buckets[i, j] ??= new()).Add((x, z, r));
    }

    /// <summary>Il punto è su un sentiero o su un ponte? (le strade sono preferite dalla ricerca del percorso)</summary>
    public bool IsRoad(Vector3 world)
    {
        foreach (var b in Bridges)
            if (b.TryGetHeight(world, 0f, out _)) return true;
        return PathDistance(world.X - Origin.X, world.Z - Origin.Z) < PathHalf;
    }

    public float GroundHeight(Vector3 world)
    {
        foreach (var b in Bridges)
            if (b.TryGetHeight(world, 0f, out float h)) return h + YOffset;
        float x = world.X - Origin.X, z = world.Z - Origin.Z;
        return HeightLocal(x, z) + PathLift(x, z) + YOffset;
    }

    /// <summary>Carica le mesh sulla GPU (thread principale) e libera i dati lato CPU.</summary>
    public void Upload(GL gl)
    {
        if (Uploaded) return;
        Opaque = new Mesh(gl, _opaqueData!);
        Scenery = new Mesh(gl, _sceneryData!);
        if (_waterData != null) Water = new Mesh(gl, _waterData);
        _opaqueData = _sceneryData = _waterData = null;
        Uploaded = true;
    }

    public void Dispose()
    {
        if (!Uploaded) return;
        Opaque.Dispose();
        Scenery.Dispose();
        Water?.Dispose();
    }
}
