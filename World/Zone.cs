using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

public enum Biome { Meadow, Blossom, Autumn, Snowy }

/// <summary>
/// Un pezzo (chunk) quadrato del territorio continuo: terreno low poly, sentieri, tratti di fiume con i
/// loro ponti e oggetti di scena. Altezze, strade, fiumi e colori vengono da <see cref="Terrain"/>,
/// funzioni globali del mondo: i pezzi vicini combaciano senza giunture.
/// </summary>
public sealed partial class Zone : IDisposable
{
    public const float Half = 48f;           // pezzo di 96 x 96
    public const int N = 128;                // celle del terreno per lato (0.75 ciascuna)
    private const float AreaFactor = Half * Half / 144f; // oggetti di scena in proporzione all'area
    public const float Cell = Half * 2f / N;
    public const float WaterY = -0.3f;
    public const float RiverHalf = 2.0f;
    public const float PathHalf = 1.1f;
    public const float PathRaise = 0.2f;    // spessore dei sentieri sopra l'erba
    private const float PathDepth = 0.1f;   // quanto la lastra affonda sotto il terreno
    public const float HubRadius = 1.9f;

    public int X { get; }
    public int Z { get; }
    public Vector3 Origin { get; }
    /// <summary>Bioma prevalente al centro del pezzo (per il nome mostrato).</summary>
    public Biome Biome { get; }

    // Il territorio non ha animazioni di comparsa: i pezzi nascono oltre la nebbia
    public bool Ready => true;
    public float YOffset => 0f;

    // Le mesh esistono solo dopo Upload(): la generazione avviene in background senza OpenGL
    public Mesh Opaque { get; private set; } = null!;
    /// <summary>Alberi, rocce e oggetti di scena: separati dal terreno per renderli trasparenti quando coprono il giocatore.</summary>
    public Mesh Scenery { get; private set; } = null!;
    public Mesh? Water { get; private set; }
    public bool Uploaded { get; private set; }

    private MeshData? _opaqueData, _sceneryData, _waterData;
    public List<Bridge> Bridges { get; } = new();
    public List<Vector3> Campfires { get; } = new(); // coordinate locali

    public Matrix4x4 Model => Matrix4x4.CreateTranslation(Origin);

    public string Name => Biome switch
    {
        Biome.Meadow => "Prato",
        Biome.Blossom => "Bosco fiorito",
        Biome.Autumn => "Bosco d'autunno",
        _ => "Valle innevata",
    };

    private readonly float[,] _h = new float[N + 1, N + 1];
    private readonly List<(float X, float Z, float R)> _clearings = new();

    /// <summary>Genera il pezzo (solo CPU, sicuro su un thread in background). Poi va chiamato <see cref="Upload"/>.</summary>
    public Zone(int x, int z)
    {
        X = x;
        Z = z;
        Origin = new Vector3(x * WorldMap.Spacing, 0, z * WorldMap.Spacing);
        Biome = Terrain.BiomeAt(Origin.X, Origin.Z);

        var rng = new Random((int)(Noise.Hash(x, z, 7) & 0x7FFFFFFF));
        bool home = x == 0 && z == 0;

        if (home)
            _clearings.Add((-5f, 5f, 1.7f));
        for (int c = 0; c < 3; c++)
            if (rng.NextSingle() < 0.45f) TryAddClearing(rng);
        foreach (var c in _clearings)
            Campfires.Add(new Vector3(c.X, HeightFn(c.X, c.Z) + PathRaise, c.Z));

        for (int i = 0; i <= N; i++)
            for (int j = 0; j <= N; j++)
                _h[i, j] = HeightFn(-Half + i * Cell, -Half + j * Cell);

        BuildRiverBridge();

        var mb = new MeshBuilder { ColorRng = rng, ColorJitter = 0.05f };
        BuildTerrain(mb);
        BuildPaths(mb, rng);
        foreach (var b in Bridges)
            b.Build(mb, b.Center - Origin);
        _opaqueData = mb.BuildData();

        var pb = new MeshBuilder { ColorRng = rng, ColorJitter = 0.05f, ComputeEdges = true };
        PlaceProps(pb, rng, home);
        _sceneryData = pb.BuildData();

        var wb = new MeshBuilder();
        BuildWater(wb);
        _waterData = wb.IsEmpty ? null : wb.BuildData();
    }

    public void Update(float dt) { }

    // ------------------------------------------------------------------ campi di distanza (coordinate locali)

    private float WX(float x) => Origin.X + x;
    private float WZ(float z) => Origin.Z + z;

    private float PathDistance(float x, float z) => Terrain.RoadDistance(WX(x), WZ(z));
    private float RiverDistance(float x, float z) => Terrain.RiverDistance(WX(x), WZ(z));

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

    /// <summary>Altezza globale del territorio, spianata anche attorno agli spiazzi dei falò (interni al pezzo).</summary>
    private float HeightFn(float x, float z)
    {
        float h = Terrain.Height(WX(x), WZ(z));
        if (_clearings.Count > 0 && h > WaterY)
            h *= Noise.Smooth(PathHalf + 0.2f, PathHalf + 1.8f, ClearingDistance(x, z));
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
            // Lo spiazzo (e la spianata attorno) restano dentro il pezzo: niente giunture
            float x = (rng.NextSingle() * 2f - 1f) * (Half - 6f), z = (rng.NextSingle() * 2f - 1f) * (Half - 6f);
            if (PathDistance(x, z) < PathHalf + 2.4f) continue;
            if (RiverDistance(x, z) < RiverHalf + 3f) continue;
            if (ClearingDistance(x, z) < 8f) continue;
            _clearings.Add((x, z, 1.7f));
            return;
        }
    }

    // ------------------------------------------------------------------ geometria

    /// <summary>Ponte dove il fiume della colonna attraversa la strada est-ovest del pezzo.</summary>
    private void BuildRiverBridge()
    {
        if (!Terrain.HasRiver(X)) return;
        // Il ponte sta dove il fiume incrocia la strada, che lì è dritta e allineata a lui (vedi Terrain.RoadZ)
        float x = Terrain.BridgeX(X, Z), z = Terrain.RoadZ(Z, x);
        bool east = x > Origin.X;
        if (!WorldMap.EdgeOpen(X, Z, east ? Dir.East : Dir.West)) return;
        Bridges.Add(new Bridge
        {
            Center = new Vector3(x, 0, z),
            Dir = Vector3.UnitX,
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
        var pal = Terrain.PaletteAt(WX(mid.X), WZ(mid.Z)); // i biomi sfumano l'uno nell'altro
        bool nearRiver = RiverDistance(mid.X, mid.Z) < RiverHalf + 0.6f;
        Vector3 color;
        if (nearRiver && avg < WaterY - 0.15f) color = bed;
        else if (nearRiver && avg < WaterY + 0.25f) color = pal.Bank;
        else color = pal.Grass * (1f + avg * 0.35f);
        mb.AddTriangle(a, b, c, color);
    }

    /// <summary>
    /// Sentieri come lastre di terra battuta con spessore: si seguono le strade globali (ondulate) dentro
    /// il pezzo. Sul bordo del pezzo i vertici non hanno variazioni casuali, così il pezzo accanto riprende
    /// la lastra esattamente da lì.
    /// </summary>
    private void BuildPaths(MeshBuilder mb, Random rng)
    {
        const int steps = (int)(Half * 2f / 0.6f);

        // Strada est-ovest della riga Z: metà ovest (tratto dall'incrocio precedente) e metà est
        bool west = WorldMap.EdgeOpen(X, Z, Dir.West), east = WorldMap.EdgeOpen(X, Z, Dir.East);
        if (west || east)
            Strip(rng, steps, k =>
            {
                float x = -Half + Half * 2f * k / steps;
                bool open = x < 0f ? west : east;
                return (new Vector3(x, 0, Terrain.RoadZ(Z, WX(x)) - Origin.Z), Vector3.UnitX, open);
            }, mb);

        // Strada nord-sud della colonna X
        bool north = WorldMap.EdgeOpen(X, Z, Dir.North), south = WorldMap.EdgeOpen(X, Z, Dir.South);
        if (north || south)
            Strip(rng, steps, k =>
            {
                float z = -Half + Half * 2f * k / steps;
                bool open = z < 0f ? north : south;
                return (new Vector3(Terrain.RoadX(X, WZ(z)) - Origin.X, 0, z), Vector3.UnitZ, open);
            }, mb);

        var pal = Terrain.PaletteAt(Origin.X, Origin.Z);
        Vector3 side = pal.Path * 0.8f;
        var raise = new Vector3(0, PathRaise, 0);
        if (Terrain.HasHub(X, Z))
            mb.AddDisc(raise + new Vector3(0, HeightLocal(0, 0) + 0.006f, 0), HubRadius, 11, pal.Path, rng, 0.08f, PathRaise + PathDepth, side);
        foreach (var c in _clearings)
            mb.AddDisc(new Vector3(c.X, HeightLocal(c.X, c.Z) + PathRaise + 0.005f, c.Z), c.R, 10, pal.Path, rng, 0.1f,
                       PathRaise + PathDepth, side);
    }

    /// <summary>Una striscia di sentiero lungo la linea data da <paramref name="at"/> (centro, direzione, tratto aperto).</summary>
    private void Strip(Random rng, int steps, Func<int, (Vector3 Center, Vector3 Axis, bool Open)> at, MeshBuilder mb)
    {
        var raise = new Vector3(0, PathRaise, 0);
        var depth = new Vector3(0, PathRaise + PathDepth, 0);
        Vector3 prevL = default, prevR = default;
        bool prevOk = false;

        bool Dry(Vector3 c) => HeightLocal(c.X, c.Z) > WaterY + 0.2f;

        for (int k = 0; k <= steps; k++)
        {
            var (c, axis, open) = at(k);
            // Direzione locale della strada (segue l'ondulazione) e sua perpendicolare
            Vector3 next = at(Math.Min(k + 1, steps)).Center, prev = at(Math.Max(k - 1, 0)).Center;
            Vector3 dir = next - prev;
            dir = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : axis;
            var perp = new Vector3(-dir.Z, 0, dir.X);

            bool border = k == 0 || k == steps;
            float jl = border ? 0f : (rng.NextSingle() - 0.5f) * 0.24f, jr = border ? 0f : (rng.NextSingle() - 0.5f) * 0.24f;
            Vector3 l = c + perp * (PathHalf + jl), r = c - perp * (PathHalf + jr);
            l.Y = HeightLocal(l.X, l.Z);
            r.Y = HeightLocal(r.X, r.Z);
            l += raise;
            r += raise;

            bool ok = open && Dry(c);
            if (k > 0 && ok && prevOk)
            {
                Vector3 color = Terrain.PaletteAt(WX(c.X), WZ(c.Z)).Path, side = color * 0.8f;
                mb.AddQuad(prevL, l, r, prevR, color);
                mb.AddQuad(prevL - depth, l - depth, l, prevL, side); // fianco sinistro
                mb.AddQuad(prevR, r, r - depth, prevR - depth, side); // fianco destro
                // Testate solo dove la lastra si interrompe (acqua, fine del tratto), non sul bordo del pezzo
                var (pc, _, popen) = at(k - 2 < 0 ? 0 : k - 2);
                if (k >= 2 && !(popen && Dry(pc)))
                    mb.AddQuad(prevR - depth, prevL - depth, prevL, prevR, side);
                var (nc, _, nopen) = at(Math.Min(k + 1, steps));
                if (k < steps && !(nopen && Dry(nc)))
                    mb.AddQuad(l - depth, r - depth, r, l, side);
            }
            prevL = l; prevR = r; prevOk = ok;
        }
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
        for (int i = 0; i < N; i++)
            for (int j = 0; j < N; j++)
            {
                float min = MathF.Min(MathF.Min(_h[i, j], _h[i + 1, j]), MathF.Min(_h[i, j + 1], _h[i + 1, j + 1]));
                if (min >= WaterY) continue;
                float x0 = -Half + i * Cell, x1 = x0 + Cell, z0 = -Half + j * Cell, z1 = z0 + Cell;
                mb.AddQuad(new(x0, WaterY, z1), new(x1, WaterY, z1), new(x1, WaterY, z0), new(x0, WaterY, z0), water);
            }
    }

    // ------------------------------------------------------------------ oggetti di scena

    private void PlaceProps(MeshBuilder mb, Random rng, bool home)
    {
        var placed = new List<(float X, float Z, float R)>();

        bool Free(float x, float z, float r, float pathMargin)
        {
            // Gli oggetti possono stare vicino al bordo: le collisioni guardano anche i pezzi accanto
            float lim = Half - 0.5f;
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

        // Prova a piazzare 'count' oggetti del bioma dato: un punto viene accettato con probabilità pari al
        // peso del bioma lì, così la vegetazione sfuma insieme ai colori del terreno.
        void Scatter(Biome? biome, int count, float minS, float maxS, float radius, float colliderR, float pathMargin,
                     Action<Vector3, float> build, Push kind = Push.None)
        {
            count = (int)(count * AreaFactor);
            for (int n = 0; n < count; n++)
            {
                for (int t = 0; t < 30; t++)
                {
                    float s = minS + rng.NextSingle() * (maxS - minS);
                    float x = rng.NextSingle() * Half * 2f - Half, z = rng.NextSingle() * Half * 2f - Half;
                    if (biome is { } b && rng.NextSingle() > Terrain.Weight(b, WX(x), WZ(z))) continue;
                    if (!Free(x, z, radius * s, pathMargin)) continue;
                    placed.Add((x, z, radius * s));
                    if (colliderR > 0f && kind == Push.None) AddCollider(x, z, colliderR * s);
                    // Solo gli alberi diventano trasparenti quando coprono il giocatore (rocce e cespugli no)
                    bool fades = colliderR > 0f && kind == Push.None;
                    if (fades) mb.BeginObject();
                    int start = mb.IndexCount;
                    build(At(x, z), s);
                    if (fades) mb.EndObject();
                    // Rocce e cespugli si possono spostare urtandoli: hanno un ingombro "mobile"
                    if (kind != Push.None) AddMovable(kind, At(x, z), colliderR * s, s, start, mb.IndexCount - start);
                    break;
                }
            }
        }

        void Fixed(float x, float z, float radius, float colliderR, Action<Vector3> build, Push kind = Push.None, float size = 1f)
        {
            placed.Add((x, z, radius));
            if (colliderR > 0f && kind == Push.None) AddCollider(x, z, colliderR);
            if (kind == Push.None) mb.BeginObject(); // solo gli alberi diventano trasparenti
            int start = mb.IndexCount;
            build(At(x, z));
            if (kind == Push.None) mb.EndObject();
            if (kind != Push.None) AddMovable(kind, At(x, z), colliderR, size, start, mb.IndexCount - start);
        }

        var pineGreen = new Vector3(0.16f, 0.48f, 0.20f);
        var coneGreen = new Vector3(0.74f, 0.86f, 0.38f);
        var bushGreen = new Vector3(0.45f, 0.76f, 0.26f);
        var rockGrey = new Vector3(0.56f, 0.62f, 0.61f);
        Vector3[] blossom = [new(0.80f, 0.38f, 0.86f), new(0.62f, 0.30f, 0.82f), new(0.94f, 0.58f, 0.86f)];
        Vector3[] autumn = [new(0.95f, 0.52f, 0.14f), new(0.86f, 0.30f, 0.12f), new(0.96f, 0.74f, 0.22f)];

        Vector3 FlowerColor(Vector3 p) => Terrain.BiomeAt(WX(p.X), WZ(p.Z)) switch
        {
            Biome.Blossom => new[] { new Vector3(0.95f, 0.55f, 0.85f), new(0.75f, 0.45f, 0.95f), new(1f, 1f, 1f) }[rng.Next(3)],
            Biome.Autumn => new[] { new Vector3(0.95f, 0.6f, 0.15f), new(0.9f, 0.3f, 0.2f) }[rng.Next(2)],
            Biome.Snowy => new[] { new Vector3(0.7f, 0.8f, 1f), new(1f, 1f, 1f) }[rng.Next(2)],
            _ => new[] { new Vector3(1f, 1f, 1f), new(1f, 0.85f, 0.3f), new(0.95f, 0.5f, 0.7f) }[rng.Next(3)],
        };

        foreach (var c in _clearings)
        {
            Props.Campfire(mb, rng, new Vector3(c.X, HeightLocal(c.X, c.Z) + PathRaise, c.Z));
            AddCollider(c.X, c.Z, 0.85f);
        }

        if (home)
        {
            // La partenza conserva la sua disposizione riconoscibile
            Fixed(-9.5f, -8.5f, 1.3f, 0.45f, p => Props.Pine(mb, rng, p, 1.5f, pineGreen));
            Fixed(-10f, -4.5f, 1.1f, 0.4f, p => Props.Pine(mb, rng, p, 1.15f, pineGreen));
            Fixed(-6f, -6.8f, 1.0f, 0.4f, p => Props.Pine(mb, rng, p, 1.0f, pineGreen));
            Fixed(-7.2f, -10f, 0.9f, 0.4f, p => Props.ConeTree(mb, rng, p, 1.25f, coneGreen));
            Fixed(-3.6f, -9.4f, 0.9f, 0.4f, p => Props.ConeTree(mb, rng, p, 1.1f, coneGreen));
            Fixed(-1.4f, -8.6f, 1.4f, 1.1f, p => Props.Rock(mb, rng, p, 1.4f, rockGrey), Push.Rock, 1.4f);
            Fixed(9.2f, -4.2f, 2.0f, 0.5f, p => Props.RoundTree(mb, rng, p, 1.25f, blossom));
            Fixed(8.6f, -8.8f, 1.5f, 0.45f, p => Props.RoundTree(mb, rng, p, 0.95f, blossom));
            Fixed(9.6f, 4.5f, 0.9f, 0.7f, p => Props.Rock(mb, rng, p, 0.9f, rockGrey), Push.Rock, 0.9f);
            Fixed(9.2f, 8.8f, 0.9f, 0.4f, p => Props.ConeTree(mb, rng, p, 1.0f, coneGreen));
            Fixed(-9.6f, 9.4f, 1.1f, 0.4f, p => Props.Pine(mb, rng, p, 1.1f, pineGreen));
        }

        // Prato
        Scatter(Biome.Meadow, 7, 0.9f, 1.5f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, pineGreen));
        Scatter(Biome.Meadow, 6, 0.9f, 1.3f, 0.8f, 0.4f, 0.6f, (p, s) => Props.ConeTree(mb, rng, p, s, coneGreen));
        Scatter(Biome.Meadow, 2, 0.9f, 1.3f, 1.6f, 0.45f, 0.6f, (p, s) => Props.RoundTree(mb, rng, p, s, blossom));
        Scatter(Biome.Meadow, 10, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, bushGreen), Push.Bush);
        Scatter(Biome.Meadow, 7, 0.35f, 1.3f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, rockGrey), Push.Rock);
        // Bosco fiorito
        Scatter(Biome.Blossom, 6, 0.9f, 1.3f, 1.6f, 0.45f, 0.6f, (p, s) => Props.RoundTree(mb, rng, p, s, blossom));
        Scatter(Biome.Blossom, 3, 0.9f, 1.3f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, pineGreen));
        Scatter(Biome.Blossom, 10, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, bushGreen), Push.Bush);
        Scatter(Biome.Blossom, 5, 0.35f, 1.0f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, rockGrey), Push.Rock);
        // Bosco d'autunno
        Scatter(Biome.Autumn, 7, 0.9f, 1.3f, 1.6f, 0.45f, 0.6f, (p, s) => Props.RoundTree(mb, rng, p, s, autumn));
        Scatter(Biome.Autumn, 4, 0.9f, 1.4f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, new Vector3(0.14f, 0.38f, 0.2f)));
        Scatter(Biome.Autumn, 8, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, new Vector3(0.78f, 0.48f, 0.16f)), Push.Bush);
        Scatter(Biome.Autumn, 5, 0.35f, 1.0f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, rockGrey), Push.Rock);
        Scatter(Biome.Autumn, 12, 0.8f, 1.4f, 0.3f, 0f, 0.15f, (p, s) => Props.Mushroom(mb, rng, p, s));
        // Valle innevata
        Scatter(Biome.Snowy, 12, 0.9f, 1.6f, 1.1f, 0.4f, 0.6f, (p, s) => Props.Pine(mb, rng, p, s, new Vector3(0.12f, 0.36f, 0.24f), snowy: true));
        Scatter(Biome.Snowy, 8, 0.35f, 1.3f, 0.9f, 0.8f, 0.4f, (p, s) => Props.Rock(mb, rng, p, s, new Vector3(0.6f, 0.64f, 0.72f)), Push.Rock);
        Scatter(Biome.Snowy, 5, 0.8f, 1.2f, 0.6f, 0.45f, 0.4f, (p, s) => Props.Bush(mb, rng, p, s, new Vector3(0.82f, 0.88f, 0.94f)), Push.Bush);
        // Fiori ovunque (meno sulla neve), del colore del bioma in cui spuntano
        Scatter(null, 12, 1f, 1f, 0.5f, 0f, 0.15f, (p, _) => Props.Flowers(mb, rng, p, FlowerColor(p)));

        // Massi nel tratto di fiume del pezzo (lontani dai ponti)
        for (int k = X - 1; k <= X + 1; k++)
        {
            if (!Terrain.HasRiver(k)) continue;
            int rocks = rng.Next(1, 4) * 4;
            for (int n = 0; n < rocks; n++)
            {
                float z = rng.NextSingle() * (Half * 2f - 4f) - (Half - 2f);
                float x = Terrain.RiverX(k, WZ(z)) - Origin.X + (rng.NextSingle() - 0.5f) * 1.6f;
                if (MathF.Abs(x) > Half - 1f) continue;
                if (PathDistance(x, z) < 5f) continue;
                Props.Rock(mb, rng, new Vector3(x, WaterY - 0.25f, z), 0.5f + rng.NextSingle() * 0.4f, rockGrey);
            }
        }
    }

    // ------------------------------------------------------------------ interrogazioni dal gameplay

    /// <summary>Il terreno nel punto è percorribile (asciutto, oppure su un ponte)? Gli ostacoli si controllano a parte.</summary>
    public bool IsGround(Vector3 world)
    {
        foreach (var b in Bridges)
            if (b.TryGetHeight(world, 0.15f, out _)) return true;
        float x = world.X - Origin.X, z = world.Z - Origin.Z;
        return HeightLocal(x, z) >= WaterY + 0.05f;
    }

    /// <summary>Un cerchio di raggio <paramref name="radius"/> nel punto tocca un ostacolo di questo pezzo?</summary>
    public bool HitsObstacle(Vector3 world, float radius)
    {
        foreach (var b in Bridges)
            if (b.TryGetHeight(world, 0.15f, out _)) return false;
        float x = world.X - Origin.X, z = world.Z - Origin.Z;
        return HitsStatic(x, z, radius) || HitsMovable(x, z, radius);
    }

    // Griglia di secchielli per gli ostacoli: ogni ostacolo è registrato in tutte le celle che
    // il suo raggio (più il massimo raggio di chi si muove) può toccare.
    private const float BucketSize = 3f;
    private const int BucketCount = (int)(Half * 2f / BucketSize) + 2;
    private const float MaxQueryRadius = 1f;
    private readonly List<(float X, float Z, float R)>?[,] _buckets = new List<(float, float, float)>?[BucketCount, BucketCount];

    // Fuori dal pezzo si usa il secchiello di bordo, dove sono registrati gli ostacoli vicini al bordo
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
        return Terrain.RoadDistance(world.X, world.Z) < PathHalf;
    }

    public float GroundHeight(Vector3 world)
    {
        foreach (var b in Bridges)
            if (b.TryGetHeight(world, 0f, out float h)) return h;
        float x = world.X - Origin.X, z = world.Z - Origin.Z;
        return HeightLocal(x, z) + PathLift(x, z);
    }

    /// <summary>Carica le mesh sulla GPU (thread principale) e libera i dati lato CPU.</summary>
    public void Upload(GL gl)
    {
        if (Uploaded) return;
        Opaque = new Mesh(gl, _opaqueData!);
        Scenery = new Mesh(gl, _sceneryData!);
        if (_waterData != null) Water = new Mesh(gl, _waterData);
        _sceneryKeep = _sceneryData; // serve per staccare gli oggetti spostati
        _gl = gl;
        _opaqueData = _sceneryData = _waterData = null;
        Uploaded = true;
    }

    public void Dispose()
    {
        if (!Uploaded) return;
        Opaque.Dispose();
        Scenery.Dispose();
        Water?.Dispose();
        DisposeMovables();
    }
}
