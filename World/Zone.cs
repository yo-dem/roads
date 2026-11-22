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
    public const float PathHalf = 1.3f;
    public const float PathRaise = 0.2f;    // spessore dei sentieri sopra l'erba
    private const float PathDepth = 0.1f;   // quanto la lastra affonda sotto il terreno
    public const float HubRadius = 2.1f;

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
    /// <summary>Finestre accese e lampade dei lampioni: si illuminano di notte.</summary>
    public Mesh? Glow { get; private set; }
    public bool Uploaded { get; private set; }

    private MeshData? _opaqueData, _sceneryData, _waterData, _glowData;
    public List<Bridge> Bridges { get; } = new();
    public List<Vector3> Campfires { get; } = new(); // coordinate locali
    /// <summary>Lampade dei lampioni (coordinate del mondo): di notte fanno luce sulla strada.</summary>
    public List<Vector3> LampLights { get; } = new();
    /// <summary>Teste dei semafori del pezzo: le lenti accese le disegna il gioco, fotogramma per fotogramma.</summary>
    public List<TrafficHead> TrafficHeads { get; } = new();

    public Matrix4x4 Model => Matrix4x4.CreateTranslation(Origin);

    /// <summary>Nome del quartiere della mappa in cui sta il pezzo.</summary>
    public string Name => RoadNetwork.Current.SettlementAt(Origin.X, Origin.Z)?.Name ?? GameMap.DistrictName(GameMap.Current.At(X, Z));

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

        if (home && PathDistance(-5f, 5f) > PathHalf + 2.4f)
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
        foreach (var b in _ownBridges)
            b.Build(mb, b.Center - Origin);
        _opaqueData = mb.BuildData();

        var pb = new MeshBuilder { ColorRng = rng, ColorJitter = 0.05f, ComputeEdges = true };
        var gb = new MeshBuilder { ComputeEdges = true }; // spigoli: le finestre accese sfumano col loro edificio
        PlaceProps(pb, gb, rng, home);
        _sceneryData = pb.BuildData();
        _glowData = gb.IsEmpty ? null : gb.BuildData();

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
    /// <summary>
    /// Verifica (test): i punti dei tratti di strada che passano in questo pezzo dove la carreggiata NON verrebbe
    /// disegnata e nessun ponte la copre. Usa esattamente il criterio di <see cref="Strip"/>.
    /// </summary>
    public IEnumerable<(Vector2 Point, RoadEdge Edge)> RoadHoles()
    {
        foreach (var e in RoadNetwork.Current.EdgesNear(X, Z))
            for (float s = 0f; s <= e.Length; s += 0.5f)
            {
                Vector2 w = e.At(s);
                float x = w.X - Origin.X, z = w.Y - Origin.Z;
                if (MathF.Abs(x) > Half || MathF.Abs(z) > Half) continue;
                if (HeightLocal(x, z) > WaterY + 0.2f) continue;
                if (Bridges.Any(b => b.TryGetHeight(new Vector3(w.X, 0f, w.Y), 0f, out _))) continue;
                yield return (w, e);
            }
    }

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

    /// <summary>
    /// Come <see cref="HeightLocal"/>, ma valida anche appena fuori dal pezzo: la griglia del terreno è la stessa per
    /// tutti i pezzi, quindi oltre il bordo si interpola come farebbe il pezzo accanto (per i segni a cavallo del bordo).
    /// </summary>
    private float HeightAnywhere(float x, float z)
    {
        if (MathF.Abs(x) <= Half && MathF.Abs(z) <= Half) return HeightLocal(x, z);
        float fx = (x + Half) / Cell, fz = (z + Half) / Cell;
        int i = (int)MathF.Floor(fx), j = (int)MathF.Floor(fz);
        float u = fx - i, v = fz - j;
        float H(int a, int b) => HeightFn(-Half + a * Cell, -Half + b * Cell);
        float h00 = H(i, j), h10 = H(i + 1, j), h01 = H(i, j + 1), h11 = H(i + 1, j + 1);
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
            // Niente falò in paese, in riva al mare o accanto ai punti di riferimento
            if (Terrain.Urban(WX(x), WZ(z)) > 0.15f || Terrain.Height(WX(x), WZ(z)) < 0.05f) continue;
            if (NearLandmark(x, z, 3f)) continue;
            _clearings.Add((x, z, 1.7f));
            return;
        }
    }

    // ------------------------------------------------------------------ geometria

    /// <summary>
    /// I ponti della rete che toccano il pezzo (per camminarci e guidarci sopra anche a cavallo del bordo); la
    /// geometria la costruisce solo il pezzo in cui sta il centro del ponte.
    /// </summary>
    private void BuildRiverBridge()
    {
        foreach (var b in RoadNetwork.Current.Bridges)
        {
            float reach = b.Span * 0.5f + 2f;
            float dx = MathF.Abs(b.Center.X - Origin.X), dz = MathF.Abs(b.Center.Y - Origin.Z);
            if (dx > Half + reach || dz > Half + reach) continue;
            var bridge = new Bridge
            {
                Center = new Vector3(b.Center.X, 0, b.Center.Y),
                Dir = new Vector3(b.Dir.X, 0, b.Dir.Y),
                Span = b.Span,
                HalfWidth = b.HalfWidth,
                BaseY = PathRaise,
                Arch = 0.4f,
            };
            Bridges.Add(bridge);
            if (dx <= Half && dz <= Half) _ownBridges.Add(bridge);
        }
    }

    private readonly List<Bridge> _ownBridges = new();

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

    internal const float RoadStep = 0.7f; // passo dei vertici lungo le strade

    /// <summary>
    /// Le strade della rete che passano nel pezzo, come lastre con spessore (asfalto o terra battuta), più gli slarghi
    /// agli incroci, le piazze, le aie e gli spiazzi dei falò. Ogni strada si campiona su una griglia globale di
    /// distanze lungo il suo tratto e si taglia esattamente sul bordo del pezzo: il pezzo accanto la riprende da lì.
    /// </summary>
    private void BuildPaths(MeshBuilder mb, Random rng)
    {
        var net = RoadNetwork.Current;
        foreach (var e in net.EdgesNear(X, Z))
        {
            BuildRoad(mb, e);
            Markings(mb, e);
        }
        BuildRail(mb);

        foreach (var n in net.NodesIn(X, Z))
        {
            if (n.Degree == 0) continue;
            float x = n.Pos.X - Origin.X, z = n.Pos.Y - Origin.Z;
            if (HeightLocal(x, z) < WaterY + 0.2f) continue;
            if (n.Junction)
            {
                BuildJunction(mb, n);
                continue;
            }
            Vector3 color = n.Piazza ? Terrain.Paving
                          : n.Edges.Any(e => e.Paved) ? Terrain.Asphalt : Terrain.PaletteAt(n.Pos.X, n.Pos.Y).Path;
            // Dove una via prosegue soltanto (cambia tipo) basta un piccolo disco a coprire la giunzione
            float r = n.IsHub ? n.HubRadius : n.Edges.Max(e => e.HalfWidth) + 0.05f;
            mb.AddDisc(new Vector3(x, HeightLocal(x, z) + PathRaise + 0.006f, z), r, n.IsHub ? 18 : 10, color, rng,
                       n.Piazza ? 0f : 0.06f, PathRaise + PathDepth, color * 0.8f);
            if (n.Piazza)
            {
                // Lastricato a cerchi: una corona più scura e il centro più chiaro
                float y = HeightLocal(x, z) + PathRaise + 0.012f;
                mb.AddDisc(new Vector3(x, y, z), r * 0.72f, 18, color * 0.86f);
                mb.AddDisc(new Vector3(x, y + 0.004f, z), r * 0.6f, 18, color * 1.04f);
                mb.AddDisc(new Vector3(x, y + 0.008f, z), r * 0.18f, 12, color * 0.8f);
            }
        }

        var pal = Terrain.PaletteAt(Origin.X, Origin.Z);
        Vector3 side = pal.Path * 0.8f;
        foreach (var c in _clearings)
            mb.AddDisc(new Vector3(c.X, HeightLocal(c.X, c.Z) + PathRaise + 0.005f, c.Z), c.R, 10, pal.Path, rng, 0.1f,
                       PathRaise + PathDepth, side);
    }

    /// <summary>Triangolo orizzontale con la normale verso l'alto, qualunque sia l'ordine dei vertici.</summary>
    private static void UpTriangle(MeshBuilder mb, Vector3 a, Vector3 b, Vector3 c, Vector3 color)
    {
        if (Vector3.Cross(b - a, c - a).Y < 0f) (b, c) = (c, b);
        mb.AddTriangle(a, b, c, color);
    }

    /// <summary>
    /// Un incrocio: il poligono che unisce i bordi delle carreggiate là dove finiscono (vedi
    /// <see cref="RoadNetwork"/>.TrimJunction), con lo spessore della lastra. Nei centri abitati, tra una strada e la
    /// successiva il marciapiede gira l'angolo: un pezzo di marciapiede che va dal bordo dell'una a quello dell'altra,
    /// col cordolo lungo l'incrocio, raccordato esattamente con i marciapiedi delle due vie.
    /// </summary>
    private void BuildJunction(MeshBuilder mb, RoadNode n)
    {
        Vector3 L(Vector2 p) => new(p.X - Origin.X, 0f, p.Y - Origin.Z);
        var center = L(n.Pos);
        float top = HeightLocal(center.X, center.Z) + PathRaise + 0.006f;
        bool paved = n.Edges.Any(e => e.Paved);
        Vector3 color = n.Piazza ? Terrain.Paving : paved ? Terrain.Asphalt : Terrain.PaletteAt(n.Pos.X, n.Pos.Y).Path;
        Vector3 side = color * 0.8f;
        var depth = new Vector3(0, PathRaise + PathDepth, 0);

        // Per ogni strada: centro della carreggiata dove finisce, la sua destra (guardando fuori dall'incrocio), mezza larghezza
        var arms = n.Edges.Select((e, i) =>
        {
            float s = e.ToS(n, n.Trim[i]);
            Vector2 p = e.At(s), t = e.Tangent(s) * (e.A == n ? 1f : -1f);
            return (P: p, Right: new Vector2(-t.Y, t.X), Hw: e.HalfWidth, Ang: MathF.Atan2(p.Y - n.Pos.Y, p.X - n.Pos.X));
        }).OrderBy(a => a.Ang).ToArray();

        // Poligono: per ogni strada il bordo sinistro e quello destro, girando attorno al centro
        var ring = new List<Vector3>();
        foreach (var a in arms)
        {
            ring.Add(L(a.P - a.Right * a.Hw) with { Y = top });
            ring.Add(L(a.P + a.Right * a.Hw) with { Y = top });
        }
        var c = center with { Y = top };
        for (int i = 0; i < ring.Count; i++)
            UpTriangle(mb, c, ring[i], ring[(i + 1) % ring.Count], color);
        if (n.Piazza)
        {
            // Lastricato a cerchi al centro della piazza (dentro il poligono)
            float r0 = n.Trim.Min() * 0.62f;
            mb.AddDisc(c + new Vector3(0, 0.006f, 0), r0, 18, color * 0.86f);
            mb.AddDisc(c + new Vector3(0, 0.010f, 0), r0 * 0.82f, 18, color * 1.04f);
            mb.AddDisc(c + new Vector3(0, 0.014f, 0), r0 * 0.25f, 12, color * 0.8f);
        }

        var curbUp = new Vector3(0, Terrain.WalkRaise, 0);
        for (int q = 0; q < arms.Length; q++)
        {
            var a = arms[q];
            var b = arms[(q + 1) % arms.Length];
            Vector3 r = ring[2 * q + 1], l = ring[(2 * q + 2) % ring.Count]; // bordo destro di a, sinistro di b
            if (!n.Sidewalks)
            {
                // Fianco della lastra lungo il bordo dell'incrocio (entrambe le facce: si vede da ogni lato)
                mb.AddQuad(r - depth, l - depth, l, r, side);
                mb.AddQuad(l - depth, r - depth, r, l, side);
                continue;
            }
            // Angolo di marciapiede: dal bordo destro di a (e dal suo marciapiede) al bordo sinistro di b
            Vector3 rc = r + curbUp, lc = l + curbUp;
            Vector3 ro = L(a.P + a.Right * (a.Hw + Terrain.WalkWidth)) with { Y = rc.Y };
            Vector3 lo = L(b.P - b.Right * (b.Hw + Terrain.WalkWidth)) with { Y = lc.Y };
            UpTriangle(mb, rc, ro, lo, Sidewalk);
            UpTriangle(mb, rc, lo, lc, Sidewalk);
            mb.AddQuad(r, l, lc, rc, Curb);                 // cordolo verso l'incrocio
            mb.AddQuad(l, r, rc, lc, Curb);
            var down = depth + curbUp;
            mb.AddQuad(ro - down, lo - down, lo, ro, Sidewalk * 0.8f); // fianco esterno
            mb.AddQuad(lo - down, ro - down, ro, lo, Sidewalk * 0.8f);
        }
    }

    /// <summary>Dove comincia il marciapiede del tratto venendo dal nodo: subito fuori dall'incrocio.</summary>
    private static float WalkStart(RoadEdge e, RoadNode n) =>
        n.Junction ? n.TrimFor(e) : n.IsHub ? n.HubRadius + Terrain.WalkWidth + 0.4f : 0f;

    /// <summary>I pezzi del tratto che stanno dentro il pezzo di territorio, con i punti di taglio esatti sul bordo.</summary>
    private void BuildRoad(MeshBuilder mb, RoadEdge e)
    {
        foreach (var r in RoadRuns(e))
        {
            if (r.Count < 2) continue;
            // Sul bordo del pezzo la lastra sconfina appena nel pezzo accanto: le due lastre si sovrappongono e non resta
            // la fessura (sottile come un capello, ma ci si vede l'erba) dovuta agli arrotondamenti delle coordinate
            const float Overlap = 0.04f;
            if (r[0] > 0.001f) r[0] = MathF.Max(0f, r[0] - Overlap);
            if (r[^1] < e.Length - 0.001f) r[^1] = MathF.Min(e.Length, r[^1] + Overlap);
            Strip(mb, e, r);
        }
    }

    /// <summary>
    /// Le distanze lungo il tratto (griglia globale più i punti di taglio sul bordo) dei pezzi di strada dentro questo
    /// pezzo di territorio: ogni lista è un tratto continuo da disegnare.
    /// </summary>
    public List<List<float>> RoadRuns(RoadEdge e)
    {
        bool In(float s)
        {
            var p = e.At(s);
            return MathF.Abs(p.X - Origin.X) <= Half && MathF.Abs(p.Y - Origin.Z) <= Half;
        }

        var runs = new List<List<float>>();
        List<float>? run = null;
        int n = Math.Max(1, (int)MathF.Ceiling(e.Length / RoadStep));
        bool prevIn = false;
        float prevS = 0f;
        // Quanto il punto è lontano dal quadrato del pezzo (0 dentro)
        float Outside(float s)
        {
            var p = e.At(s);
            return MathF.Max(MathF.Max(MathF.Abs(p.X - Origin.X), MathF.Abs(p.Y - Origin.Z)) - Half, 0f);
        }

        for (int k = 0; k <= n; k++)
        {
            float s = MathF.Min(k * RoadStep, e.Length);
            bool inside = In(s);
            if (k > 0)
            {
                // Vicino al bordo si guarda anche tra un campione e l'altro: una strada che taglia di sbieco l'angolo
                // del pezzo ci può entrare e uscire tra due campioni (e nessun pezzo la disegnerebbe)
                int sub = Outside(prevS) < 1.5f || Outside(s) < 1.5f ? 8 : 1;
                float sp = prevS;
                bool ip = prevIn;
                for (int j = 1; j <= sub; j++)
                {
                    float sj = j == sub ? s : prevS + (s - prevS) * j / sub;
                    bool ij = j == sub ? inside : In(sj);
                    if (ij != ip)
                    {
                        // Bisezione: il punto in cui la strada attraversa il bordo (identico per i due pezzi)
                        float a = sp, b = sj;
                        for (int it = 0; it < 24; it++)
                        {
                            float m = (a + b) * 0.5f;
                            if (In(m) == ip) a = m; else b = m;
                        }
                        float edge = (a + b) * 0.5f;
                        if (ij) runs.Add(run = new List<float> { edge });
                        else
                        {
                            run?.Add(edge);
                            run = null;
                        }
                    }
                    sp = sj;
                    ip = ij;
                }
            }
            if (inside)
            {
                if (run == null) runs.Add(run = new List<float>());
                run.Add(s);
            }
            prevIn = inside;
            prevS = s;
        }
        return runs;
    }

    /// <summary>
    /// Una striscia di strada per le distanze <paramref name="ss"/> lungo il tratto. Asfalto con bordi dritti (nei centri
    /// abitati con i marciapiedi e il cordolo, sulle statali con la mezzeria tratteggiata) oppure terra battuta coi
    /// bordi un po' irregolari. Sui tratti bagnati (sotto i ponti) la lastra si interrompe.
    /// </summary>
    private void Strip(MeshBuilder mb, RoadEdge e, List<float> ss)
    {
        float hw = e.HalfWidth;
        bool dirt = e.Kind == RoadKind.Dirt;
        var raise = new Vector3(0, PathRaise, 0);
        var depth = new Vector3(0, PathRaise + PathDepth, 0);
        var curb = new Vector3(0, Terrain.WalkRaise, 0);
        var walkDepth = depth + curb;
        Vector3 prevL = default, prevR = default, prevOL = default, prevOR = default;
        bool prevOk = false, prevPrevOk = false, prevWalk = false;
        // Il marciapiede va da dove finisce un incrocio a dove comincia l'altro: i suoi capi si aggiungono ai punti,
        // così arriva esattamente all'angolo di marciapiede dell'incrocio
        float walkFrom = WalkStart(e, e.A), walkTo = e.Length - WalkStart(e, e.B);
        ss = ss.Concat(new[] { walkFrom, walkTo }.Where(v => v > ss[0] + 0.02f && v < ss[^1] - 0.02f))
               .Distinct().Order().ToList();

        bool DryAt(float s)
        {
            var p = e.At(s);
            return HeightLocal(p.X - Origin.X, p.Y - Origin.Z) > WaterY + 0.2f;
        }

        for (int k = 0; k < ss.Count; k++)
        {
            float s = ss[k];
            Vector2 w = e.At(s), t = e.Tangent(s);
            var c = new Vector3(w.X - Origin.X, 0f, w.Y - Origin.Z);
            c.Y = HeightLocal(c.X, c.Z);
            var perp = new Vector3(-t.Y, 0, t.X);
            // Bordi irregolari degli sterrati: funzione continua della distanza lungo il tratto (uguale nei due pezzi)
            float jl = dirt ? (Terrain.ValueNoise(s * 0.6f, e.Id * 7.3f, 51) - 0.5f) * 0.32f : 0f;
            float jr = dirt ? (Terrain.ValueNoise(s * 0.6f, e.Id * 7.3f + 3.7f, 52) - 0.5f) * 0.32f : 0f;
            Vector3 l = c + perp * (hw + jl), r = c - perp * (hw + jr);
            l.Y = HeightLocal(l.X, l.Z);
            r.Y = HeightLocal(r.X, r.Z);
            l += raise;
            r += raise;
            bool ok = c.Y > WaterY + 0.2f;

            // Marciapiedi: dal bordo della carreggiata per WalkWidth, un gradino più su
            bool walk = ok && !dirt && Railway.Current.Distance(w.X, w.Y) > Railway.BallastHalf + 0.6f
                        && Terrain.Urban(w.X, w.Y) >= Terrain.WalkFrom
                        && s >= walkFrom - 0.01f && s <= walkTo + 0.01f;
            Vector3 ol = c + perp * (hw + Terrain.WalkWidth), or = c - perp * (hw + Terrain.WalkWidth);
            ol.Y = HeightLocal(ol.X, ol.Z);
            or.Y = HeightLocal(or.X, or.Z);
            ol += raise + curb;
            or += raise + curb;
            if (k > 0 && walk && prevWalk)
            {
                mb.AddQuad(prevL + curb, l + curb, ol, prevOL, Sidewalk);                 // piano sinistro
                mb.AddQuad(prevL, l, l + curb, prevL + curb, Curb);                       // cordolo sinistro
                mb.AddQuad(prevOL - walkDepth, ol - walkDepth, ol, prevOL, Sidewalk * 0.8f);
                mb.AddQuad(r + curb, prevR + curb, prevOR, or, Sidewalk);                 // piano destro
                mb.AddQuad(r, prevR, prevR + curb, r + curb, Curb);                       // cordolo destro
                mb.AddQuad(or - walkDepth, prevOR - walkDepth, prevOR, or, Sidewalk * 0.8f);
            }
            // Testate dove il marciapiede si interrompe (agli slarghi)
            if (walk != prevWalk && k > 0)
            {
                var (el, eol, er, eor) = walk ? (l, ol, r, or) : (prevL, prevOL, prevR, prevOR);
                mb.AddQuad(el + curb, eol, eol - walkDepth, el - depth, Curb);
                mb.AddQuad(er + curb, eor, eor - walkDepth, er - depth, Curb);
            }

            if (k > 0 && ok && prevOk)
            {
                Vector3 color = dirt ? Terrain.PaletteAt(w.X, w.Y).Path : Terrain.Asphalt, side = color * 0.8f;
                mb.AddQuad(prevL, l, r, prevR, color);
                mb.AddQuad(prevL - depth, l - depth, l, prevL, side); // fianco sinistro
                mb.AddQuad(prevR, r, r - depth, prevR - depth, side); // fianco destro
                // Testate solo dove la lastra si interrompe davvero (acqua), non sul bordo del pezzo
                if (k >= 2 && !prevPrevOk) mb.AddQuad(prevR - depth, prevL - depth, prevL, prevR, side);
                if (k + 1 < ss.Count && !DryAt(ss[k + 1])) mb.AddQuad(l - depth, r - depth, r, l, side);
            }
            prevOL = ol; prevOR = or; prevWalk = walk;
            prevL = l; prevR = r;
            prevPrevOk = prevOk; prevOk = ok;
        }
    }

    /// <summary>Il cerchio (centro e otto punti del contorno) tocca una superficie rialzata sopra il terreno?</summary>
    private bool OnRaised(float x, float z, float r)
    {
        // Lontano da strade e binari il suolo non è mai rialzato: niente da controllare (è la gran parte dei casi)
        if (PathDistance(x, z) > PathHalf + Terrain.WalkWidth + r + 0.5f
            && Railway.Current.Distance(WX(x), WZ(z)) > Railway.BallastHalf + Railway.PlatformWidth + r + 1.5f) return false;
        if (PathLift(x, z) > 0.001f) return true;
        for (int k = 0; k < 8; k++)
        {
            float a = k * MathF.PI / 4f;
            if (PathLift(x + MathF.Cos(a) * r, z + MathF.Sin(a) * r) > 0.001f) return true;
        }
        return false;
    }

    /// <summary>Rialzo del sentiero in un punto locale (0 fuori dai sentieri o sui tratti bagnati).</summary>
    private float PathLift(float x, float z)
    {
        if (Terrain.OnSidewalk(WX(x), WZ(z)) && HeightLocal(x, z) > WaterY + 0.2f) return PathRaise + Terrain.WalkRaise;
        if (Railway.Current.Distance(WX(x), WZ(z)) < Railway.BallastHalf && PathDistance(x, z) > PathHalf + 0.4f
            && HeightLocal(x, z) > WaterY + 0.2f) return Railway.BallastTop; // sulla massicciata
        if (Railway.Current.OnPlatform(new Vector2(WX(x), WZ(z)))) return Railway.PlatformHeight; // sulla banchina
        float d = MathF.Min(PathDistance(x, z), ClearingDistance(x, z));
        if (d > PathHalf + 0.1f) return 0f;
        if (HeightLocal(x, z) <= WaterY + 0.2f) return 0f;
        return PathRaise * (1f - Noise.Smooth(PathHalf - 0.1f, PathHalf + 0.1f, d));
    }

    private void BuildWater(MeshBuilder mb)
    {
        var water = new Vector3(0.20f, 0.46f, 0.80f);
        // Celle bagnate vicine sulla stessa riga unite in un solo quadrato (al più 4: le onde restano visibili):
        // in mare aperto i vertici sono molti meno
        const int MaxRun = 4;
        bool Wet(int i, int j) =>
            MathF.Min(MathF.Min(_h[i, j], _h[i + 1, j]), MathF.Min(_h[i, j + 1], _h[i + 1, j + 1])) < WaterY;
        for (int j = 0; j < N; j++)
            for (int i = 0; i < N;)
            {
                if (!Wet(i, j)) { i++; continue; }
                int run = 1;
                while (run < MaxRun && i + run < N && Wet(i + run, j)) run++;
                float x0 = -Half + i * Cell, x1 = x0 + run * Cell, z0 = -Half + j * Cell, z1 = z0 + Cell;
                mb.AddQuad(new(x0, WaterY, z1), new(x1, WaterY, z1), new(x1, WaterY, z0), new(x0, WaterY, z0), water);
                i += run;
            }
    }

    // ------------------------------------------------------------------ oggetti di scena

    /// <summary>Il punto locale è sulla spianata di un punto di riferimento (più <paramref name="margin"/>)?</summary>
    private bool NearLandmark(float x, float z, float margin)
    {
        var p = new Vector2(WX(x), WZ(z));
        foreach (var l in GameMap.Current.Landmarks)
            if (Vector2.Distance(p, l.Pos) < l.Radius + margin) return true;
        return false;
    }

    private void PlaceProps(MeshBuilder mb, MeshBuilder glow, Random rng, bool home)
    {
        var placed = new List<(float X, float Z, float R)>();

        bool Free(float x, float z, float r, float pathMargin)
        {
            // Gli oggetti possono stare vicino al bordo: le collisioni guardano anche i pezzi accanto
            float lim = Half - 0.5f;
            if (MathF.Abs(x) > lim || MathF.Abs(z) > lim) return false;
            if (PathDistance(x, z) < PathHalf + pathMargin + r) return false;
            // Nei centri abitati le strade hanno il marciapiede (anche attorno agli incroci): si sta oltre
            if (Terrain.Urban(WX(x), WZ(z)) >= Terrain.WalkFrom * 0.85f
                && PathDistance(x, z) < PathHalf + Terrain.WalkWidth + 0.15f + r) return false;
            // E mai su una superficie rialzata (marciapiede, sentiero, banchina, massicciata): né al centro né sul contorno
            if (OnRaised(x, z, r)) return false;
            if (RiverDistance(x, z) < RiverHalf + 0.5f + r) return false;
            if (HeightLocal(x, z) < WaterY + 0.3f) return false; // niente alberi in mare
            if (NearLandmark(x, z, r + 0.5f)) return false;
            if (Railway.Current.Keepout(WX(x), WZ(z), r)) return false;
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
                     Action<Vector3, float> build, Push kind = Push.None, bool forest = false)
        {
            count = (int)(count * AreaFactor);
            for (int n = 0; n < count; n++)
            {
                for (int t = 0; t < (forest ? 6 : 30); t++)
                {
                    float s = minS + rng.NextSingle() * (maxS - minS);
                    float x = rng.NextSingle() * Half * 2f - Half, z = rng.NextSingle() * Half * 2f - Half;
                    if (biome is { } b && rng.NextSingle() > Terrain.Weight(b, WX(x), WZ(z))) continue;
                    if (forest && rng.NextSingle() > Terrain.Forest(WX(x), WZ(z))) continue;
                    // In paese restano giardini e alberi qua e là; in città pochissimi
                    if (rng.NextSingle() < Noise.Smooth(0.15f, 0.85f, Terrain.Urban(WX(x), WZ(z)))) continue;
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
            if (!Free(x, z, radius, 0.6f)) return; // la rete stradale può passare proprio lì
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

        // Prima le costruzioni (case, palazzi, lampioni, cascine): la vegetazione riempie quel che resta
        BuildStations(mb, glow, rng, placed);
        StreetFurniture(mb, rng, placed);
        StationSigns(mb, placed);
        TrafficSignals(mb, placed);
        BuildTown(mb, glow, rng, placed);
        BuildFarms(mb, glow, rng, placed);

        // Boschi fitti (macchie in campagna e fasce tra un paese e l'altro): alberi del bioma del posto
        Scatter(null, 16, 0.9f, 1.55f, 1.15f, 0.45f, 0.7f, (p, s) =>
        {
            switch (Terrain.BiomeAt(WX(p.X), WZ(p.Z)))
            {
                case Biome.Snowy: Props.Pine(mb, rng, p, s, new Vector3(0.12f, 0.36f, 0.24f), snowy: true); break;
                case Biome.Autumn:
                    if (rng.NextSingle() < 0.65f) Props.RoundTree(mb, rng, p, s, autumn);
                    else Props.Pine(mb, rng, p, s, new Vector3(0.14f, 0.38f, 0.2f));
                    break;
                case Biome.Blossom:
                    if (rng.NextSingle() < 0.6f) Props.RoundTree(mb, rng, p, s, blossom);
                    else Props.Pine(mb, rng, p, s, pineGreen);
                    break;
                default:
                    if (rng.NextSingle() < 0.55f) Props.Pine(mb, rng, p, s, pineGreen);
                    else Props.ConeTree(mb, rng, p, s, coneGreen);
                    break;
            }
        }, forest: true);

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
        return HeightLocal(x, z) + PathLift(x, z) + RoadNetwork.Current.MedianLift(new Vector2(world.X, world.Z));
    }

    /// <summary>Carica le mesh sulla GPU (thread principale) e libera i dati lato CPU.</summary>
    public void Upload(GL gl)
    {
        if (Uploaded) return;
        Opaque = new Mesh(gl, _opaqueData!);
        Scenery = new Mesh(gl, _sceneryData!);
        if (_waterData != null) Water = new Mesh(gl, _waterData);
        if (_glowData != null) Glow = new Mesh(gl, _glowData);
        _sceneryKeep = _sceneryData; // serve per staccare gli oggetti spostati
        _gl = gl;
        _opaqueData = _sceneryData = _waterData = _glowData = null;
        Uploaded = true;
    }

    public void Dispose()
    {
        if (!Uploaded) return;
        Opaque.Dispose();
        Scenery.Dispose();
        Water?.Dispose();
        Glow?.Dispose();
        DisposeMovables();
    }
}
