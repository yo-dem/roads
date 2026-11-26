using System.Numerics;

namespace Roads.World;

/// <summary>
/// Genera la rete stradale dell'isola a partire da dove la mappa mette città e paesi. L'ordine conta, come nella
/// realtà: prima il reticolo della città, poi i paesi (via principale verso la città, una via dietro e la piazza),
/// poi le statali che dai paesi raggiungono la città (si innestano sulla prima strada che incontrano, così si
/// diramano in modo naturale), gli sterrati che legano i paesi tra loro attraverso i boschi e quelli che portano
/// alle cascine e ai punti di riferimento. Nessuna strada finisce nel nulla: un vicolo cieco senza motivo si pota.
/// I percorsi in campagna si cercano su una griglia (A*) che evita mare e punti di riferimento, attraversa i fiumi
/// solo di traverso (lì nasce un ponte) e preferisce, con un po' di rumore, certi passaggi ad altri: le curve sono
/// "del terreno", non di un righello. Tutto è deterministico: stessa mappa, stessa rete.
/// </summary>
public sealed class RoadBuilder
{
    private const float G = 4f;            // passo della griglia di ricerca
    private const float Step = 2f;         // passo dei punti delle strade
    private const int CityComp = 0;

    private readonly RoadNetwork _net;
    private readonly GameMap _map;
    private readonly float _x0, _z0;
    private readonly int _w, _h;
    private readonly bool[] _blocked, _river;
    private readonly float[] _cost, _urban;
    private readonly int[] _occ;          // componente della strada che occupa la cella (-1 nessuna)
    private readonly int[] _near;         // componente della strada che passa a meno di ~14 unità (-1 nessuna)
    private readonly Dictionary<RoadNode, int> _compOf = new();
    private readonly Dictionary<Settlement, List<RoadNode>> _villageNodes = new();
    private readonly Dictionary<Settlement, int> _villageComp = new();
    private int _nextComp = 100;

    private RoadBuilder(RoadNetwork net, GameMap map)
    {
        _net = net;
        _map = map;
        const float C = Terrain.ChunkSize;
        _x0 = (map.MinI - 0.5f) * C;
        _z0 = (map.MinJ - 0.5f) * C;
        _w = (int)MathF.Ceiling((map.MaxI - map.MinI + 1) * C / G);
        _h = (int)MathF.Ceiling((map.MaxJ - map.MinJ + 1) * C / G);
        int n = _w * _h;
        _blocked = new bool[n];
        _river = new bool[n];
        _cost = new float[n];
        _urban = new float[n];
        _occ = new int[n];
        _near = new int[n];
        Array.Fill(_occ, -1);
        Array.Fill(_near, -1);
        for (int j = 0; j < _h; j++)
            for (int i = 0; i < _w; i++)
            {
                int c = j * _w + i;
                Vector2 p = CellCenter(i, j);
                _blocked[c] = map.Sample(p.X, p.Y).Land < 0.8f;
                _river[c] = Terrain.RiverDistance(p.X, p.Y) < Zone.RiverHalf + 3.5f;
                _urban[c] = net.Urban(p.X, p.Y);
                // Rumore a grana larga: le strade di campagna scelgono "valli" e "dossi" e serpeggiano
                _cost[c] = 1f + 1.3f * Terrain.ValueNoise(p.X / 46f, p.Y / 46f, 91) + 0.4f * Terrain.ValueNoise(p.X / 13f, p.Y / 13f, 92);
                foreach (var l in map.Landmarks)
                    if (Vector2.Distance(p, l.Pos) < l.Radius + 2.5f) _blocked[c] = true;
            }
    }

    /// <summary>Costruisce la rete (le settlement sono già nella rete) e la prepara alle interrogazioni.</summary>
    public static void Build(RoadNetwork net, GameMap map)
    {
        var b = new RoadBuilder(net, map);
        var city = net.Settlements.FirstOrDefault(s => s.City);
        if (city != null) b.BuildCity(city);
        var villages = net.Settlements.Where(s => !s.City).ToList();
        Vector2 hub = city?.Pos ?? (villages.Count > 0 ? villages.Aggregate(Vector2.Zero, (a, s) => a + s.Pos) / villages.Count : Vector2.Zero);
        foreach (var v in villages.OrderBy(v => Vector2.Distance(v.Pos, hub))) b.BuildVillage(v, hub);
        foreach (var v in villages.OrderBy(v => Vector2.Distance(v.Pos, hub))) b.ConnectVillage(v, hub, city != null);
        b.BuildRing(villages, hub);
        b.BuildFarms();
        b.ConnectLandmarks();
        b.PruneDeadEnds();
        b.FindBridges();
        net.Finish();
        Console.WriteLine($"Strade: {net.Edges.Count} tratti, {net.Nodes.Count(n => n.Degree >= 3)} incroci, "
                          + $"{net.Bridges.Count} ponti, {net.Farms.Count} cascine, "
                          + $"{net.Edges.Sum(e => e.Length) / 1000f:F1} km");
        foreach (var g in net.Edges.GroupBy(e => e.Layout))
            Console.WriteLine($"  {g.Key}: {g.Count()} tratti, {g.Sum(e => e.Length):F0} unità");    }

    // ------------------------------------------------------------------ griglia

    private Vector2 CellCenter(int i, int j) => new(_x0 + (i + 0.5f) * G, _z0 + (j + 0.5f) * G);
    private bool CellOf(Vector2 p, out int i, out int j)
    {
        i = (int)MathF.Floor((p.X - _x0) / G);
        j = (int)MathF.Floor((p.Y - _z0) / G);
        return i >= 0 && j >= 0 && i < _w && j < _h;
    }

    private void Occupy(RoadEdge e, int comp)
    {
        const float R = 4.5f, Near = 14f;
        int r = (int)MathF.Ceiling(Near / G);
        for (int k = 0; k < e.Count; k += 2)
        {
            var p = e.Points[k];
            if (!CellOf(p, out int ci, out int cj)) continue;
            for (int i = ci - r; i <= ci + r; i++)
                for (int j = cj - r; j <= cj + r; j++)
                {
                    if (i < 0 || j < 0 || i >= _w || j >= _h) continue;
                    float d = Vector2.Distance(CellCenter(i, j), p);
                    if (d > Near) continue;
                    int c = j * _w + i;
                    if (_near[c] < 0) _near[c] = comp;
                    if (d <= R && _occ[c] < 0) _occ[c] = comp;
                }
        }
    }

    // ------------------------------------------------------------------ nodi e tratti

    private RoadNode NewNode(Vector2 p, int comp)
    {
        var n = new RoadNode { Pos = p };
        _net.Nodes.Add(n);
        _compOf[n] = comp;
        return n;
    }

    private RoadEdge Connect(RoadNode a, RoadNode b, List<Vector2> pts, RoadKind kind, int comp,
                             RoadLayout? layout = null, int oneWay = 0)
    {
        pts[0] = a.Pos;
        pts[^1] = b.Pos;
        var e = new RoadEdge
        {
            A = a, B = b, Kind = kind, Points = pts, OneWay = oneWay,
            Layout = layout ?? (kind == RoadKind.Dirt ? RoadLayout.Single : RoadLayout.TwoWay),
        };
        e.Measure();
        a.Edges.Add(e);
        b.Edges.Add(e);
        _net.Edges.Add(e);
        Occupy(e, comp);
        return e;
    }

    private void Remove(RoadEdge e)
    {
        e.A.Edges.Remove(e);
        e.B.Edges.Remove(e);
        _net.Edges.Remove(e);
    }

    /// <summary>Spezza il tratto alla distanza <paramref name="s"/>: nasce un nodo (un incrocio) in quel punto.</summary>
    private RoadNode Split(RoadEdge e, float s)
    {
        Vector2 at = e.At(s);
        var first = new List<Vector2>();
        var second = new List<Vector2> { at };
        for (int k = 0; k < e.Count; k++)
        {
            if (e.CumAt(k) < s - 0.3f) first.Add(e.Points[k]);
            else if (e.CumAt(k) > s + 0.3f) second.Add(e.Points[k]);
        }
        first.Add(at);
        if (first.Count < 2 || second.Count < 2) return s < e.Length * 0.5f ? e.A : e.B;
        int comp = _compOf.GetValueOrDefault(e.A, CityComp);
        var n = NewNode(at, comp);
        Remove(e);
        Connect(e.A, n, first, e.Kind, comp, e.Layout, e.OneWay);
        Connect(n, e.B, second, e.Kind, comp, e.Layout, e.OneWay);
        return n;
    }

    /// <summary>Retta da a a b campionata ogni Step (estremi compresi).</summary>
    private static List<Vector2> Line(Vector2 a, Vector2 b)
    {
        int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) / Step));
        var pts = new List<Vector2>(n + 1);
        for (int k = 0; k <= n; k++) pts.Add(Vector2.Lerp(a, b, k / (float)n));
        return pts;
    }

    /// <summary>Curva f(t) per t da t0 a t1 campionata ogni Step circa.</summary>
    private static List<Vector2> Curve(Func<float, Vector2> f, float t0, float t1)
    {
        int n = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(t1 - t0) / Step));
        var pts = new List<Vector2>(n + 1);
        for (int k = 0; k <= n; k++) pts.Add(f(Noise.Lerp(t0, t1, k / (float)n)));
        return pts;
    }

    /// <summary>
    /// Un tratto è costruibile? Asciutto, lontano dai punti di riferimento e, se attraversa un fiume, solo di traverso
    /// (quasi est-ovest, una volta sola) e non accanto ad altre strade già fatte (per i nuovi quartieri).
    /// </summary>
    private bool Valid(List<Vector2> pts, bool avoidRoads, int ownComp = -1, float margin = 0f)
    {
        int crossings = 0;
        for (int k = 0; k < pts.Count; k++)
        {
            var p = pts[k];
            if (_map.Sample(p.X, p.Y).Land < 0.88f) return false;
            foreach (var l in _map.Landmarks)
                if (Vector2.Distance(p, l.Pos) < l.Radius + 3.5f + margin) return false;
            if (avoidRoads && CellOf(p, out int i, out int j) && _occ[j * _w + i] >= 0 && _occ[j * _w + i] != ownComp) return false;
            if (k == 0) continue;
            if (RiverSide(pts[k - 1]) != RiverSide(p))
            {
                crossings++;
                Vector2 d = Vector2.Normalize(pts[^1] - pts[0]);
                if (MathF.Abs(d.X) < 0.85f) return false;
            }
        }
        // Gli estremi non devono stare nel fiume
        return crossings <= 1 && Terrain.RiverDistance(pts[0].X, pts[0].Y) > Zone.RiverHalf + 4f
                              && Terrain.RiverDistance(pts[^1].X, pts[^1].Y) > Zone.RiverHalf + 4f;
    }

    /// <summary>Da che parte di ciascun fiume sta il punto (cambia quando se ne attraversa uno).</summary>
    private static int RiverSide(Vector2 p)
    {
        int side = 0, bit = 0;
        foreach (int k in GameMap.Current.RiverColumns)
        {
            if (p.X > Terrain.RiverX(k, p.Y)) side |= 1 << bit;
            bit++;
        }
        return side;
    }

    // ------------------------------------------------------------------ città

    /// <summary>
    /// Reticolo appena ruotato e deformato: isolati più piccoli al centro e più larghi verso la periferia, contorno
    /// irregolare, qualche via in meno qua e là (isolati più grandi), una grande piazza al centro.
    /// </summary>
    private void BuildCity(Settlement city)
    {
        var rng = new Random(1234 + (int)city.Pos.X);
        float theta = (rng.NextSingle() - 0.5f) * 0.45f;
        Vector2 ax = new(MathF.Cos(theta), MathF.Sin(theta)), az = new(-ax.Y, ax.X);
        float R = city.Radius;

        // Coordinate delle vie lungo ciascun asse: passo che cresce allontanandosi dal centro
        var coords = new List<float> { 0f };
        for (float u = 0f, gap = 25f; u < R * 1.3f; gap += 3.2f)
        {
            u += gap;
            coords.Add(u);
            coords.Insert(0, -u);
        }
        int n = coords.Count, mid0 = coords.IndexOf(0f);
        var grid = new RoadNode?[n, n];
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                Vector2 local = new(coords[a], coords[b]);
                // Deformazione a grana larga (le vie piegano insieme, come quartieri cresciuti in epoche diverse)
                // più una piccola, incrocio per incrocio
                Vector2 bend = new(Terrain.ValueNoise(a * 0.32f, b * 0.32f, 81) - 0.5f, Terrain.ValueNoise(a * 0.32f, b * 0.32f, 82) - 0.5f);
                Vector2 jit = new(Terrain.ValueNoise(a * 0.9f, b * 0.9f, 84) - 0.5f, Terrain.ValueNoise(a * 0.9f, b * 0.9f, 85) - 0.5f);
                float outer = Noise.Smooth(0f, R, local.Length()); // al centro più regolare, in periferia più libero
                Vector2 p = city.Pos + ax * local.X + az * local.Y + bend * (10f + 22f * outer) + jit * (4f + 6f * outer);
                float ang = MathF.Atan2(local.Y, local.X);
                float edge = R * (0.88f + 0.22f * Terrain.ValueNoise(ang * 1.3f + 5f, 0.5f, 83));
                if (Vector2.Distance(p, city.Pos) > edge) continue;
                if (_map.Sample(p.X, p.Y).Land < 0.93f) continue;
                if (Terrain.RiverDistance(p.X, p.Y) < Zone.RiverHalf + 6f) continue;
                if (_map.Landmarks.Any(l => Vector2.Distance(p, l.Pos) < l.Radius + 6f)) continue;
                grid[a, b] = new RoadNode { Pos = p };
            }

        // Tipi di via: le due linee che passano per il centro sono viali (2+2 corsie con lo spartitraffico); nel
        // cuore della città le vie alternate sono sensi unici, una in un verso e la successiva nell'altro (come a
        // Manhattan, così si arriva comunque dappertutto); le altre sono a doppio senso.
        (RoadLayout Layout, int OneWay) StreetType(int line, Vector2 mid)
        {
            int off = line - mid0;
            if (off == 0) return (RoadLayout.Avenue, 0);
            if ((off & 1) != 0 && Vector2.Distance(mid, city.Pos) < R * 0.72f)
                return (RoadLayout.OneWay, ((off % 4) + 4) % 4 == 1 ? 1 : -1);
            return (RoadLayout.TwoWay, 0);
        }

        var links = new List<(RoadNode A, RoadNode B, List<Vector2> Pts, RoadLayout Layout, int OneWay)>();
        for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
            {
                if (grid[a, b] is not { } p) continue;
                foreach (var (da, db) in new[] { (1, 0), (0, 1) })
                {
                    if (a + da >= n || b + db >= n || grid[a + da, b + db] is not { } q) continue;
                    var pts = Line(p.Pos, q.Pos);
                    var (layout, oneWay) = StreetType(da == 1 ? b : a, (p.Pos + q.Pos) * 0.5f);
                    float margin = layout == RoadLayout.Avenue ? 2f : 0f;
                    if (!Valid(pts, avoidRoads: false, margin: margin))
                    {
                        if (layout != RoadLayout.Avenue || !Valid(pts, avoidRoads: false)) continue;
                        layout = RoadLayout.TwoWay; // un viale che passerebbe troppo vicino a un monumento si restringe
                    }
                    links.Add((p, q, pts, layout, oneWay));
                }
            }

        // Qualche via in meno lontano dal centro: isolati più grandi, ma senza creare vicoli ciechi
        var degree = new Dictionary<RoadNode, int>();
        foreach (var (a, b, _, _, _) in links)
        {
            degree[a] = degree.GetValueOrDefault(a) + 1;
            degree[b] = degree.GetValueOrDefault(b) + 1;
        }
        var kept = new List<(RoadNode A, RoadNode B, List<Vector2> Pts, RoadLayout Layout, int OneWay)>();
        foreach (var link in links.OrderBy(_ => rng.Next()))
        {
            Vector2 m = (link.A.Pos + link.B.Pos) * 0.5f;
            float far = Noise.Smooth(R * 0.2f, R * 0.9f, Vector2.Distance(m, city.Pos));
            bool drop = far > 0f && link.Layout != RoadLayout.Avenue && rng.NextSingle() < 0.1f + 0.22f * far
                        && degree[link.A] >= 3 && degree[link.B] >= 3;
            if (drop)
            {
                degree[link.A]--;
                degree[link.B]--;
            }
            else kept.Add(link);
        }

        // Si tiene solo il pezzo più grande del reticolo (un fiume o la costa possono staccarne dei brandelli)
        var adj = new Dictionary<RoadNode, List<RoadNode>>();
        foreach (var (a, b, _, _, _) in kept)
        {
            (adj.TryGetValue(a, out var la) ? la : adj[a] = new()).Add(b);
            (adj.TryGetValue(b, out var lb) ? lb : adj[b] = new()).Add(a);
        }
        HashSet<RoadNode> best = new();
        var seen = new HashSet<RoadNode>();
        foreach (var start in adj.Keys)
        {
            if (!seen.Add(start)) continue;
            var comp = new HashSet<RoadNode> { start };
            var stack = new Stack<RoadNode>();
            stack.Push(start);
            while (stack.Count > 0)
                foreach (var q in adj[stack.Pop()])
                    if (seen.Add(q)) { comp.Add(q); stack.Push(q); }
            if (comp.Count > best.Count) best = comp;
        }

        foreach (var node in best)
        {
            _net.Nodes.Add(node);
            _compOf[node] = CityComp;
        }
        foreach (var (a, b, pts, layout, oneWay) in kept)
            if (best.Contains(a) && best.Contains(b)) Connect(a, b, pts, RoadKind.Street, CityComp, layout, oneWay);

        // Niente vicoli ciechi in città
        PruneDeadEnds();
        if (_net.Nodes.Where(nd => _compOf[nd] == CityComp && nd.Degree > 0).MinBy(nd => Vector2.Distance(nd.Pos, city.Pos)) is { } center)
            center.Piazza = true;
        Console.WriteLine($"Città {city.Name} ({city.Pos.X:F0}, {city.Pos.Y:F0}): {_net.Nodes.Count(nd => _compOf[nd] == CityComp && nd.Degree > 0)} incroci");
    }

    // ------------------------------------------------------------------ paesi

    /// <summary>
    /// Un paese: la via principale (puntata verso la città, appena curva) con la piazza al centro, una via parallela
    /// dietro le case e i raccordi tra le due. Se il posto non lo permette (fiume, costa) si prova a ruotarlo.
    /// </summary>
    private void BuildVillage(Settlement v, Vector2 toward)
    {
        Vector2 baseAxis = Vector2.Distance(toward, v.Pos) > 1f ? Vector2.Normalize(toward - v.Pos) : Vector2.UnitX;
        int comp = _nextComp++;
        float bend = 2.5f + 3f * Noise.Hash01((int)v.Pos.X, (int)v.Pos.Y, 5);

        // Si prova il paese completo (via principale, una via dietro da un lato e una dall'altro), poi più piccolo,
        // ruotandolo un po' alla volta finché non trova posto
        foreach (int level in new[] { 2, 1, 0 })
            foreach (float rot in new[] { 0f, 0.35f, -0.35f, 0.7f, -0.7f, 1.05f, -1.05f, 1.57f })
                foreach (float side in new[] { 1f, -1f })
                {
                    float c = MathF.Cos(rot), s = MathF.Sin(rot);
                    Vector2 ax = new(baseAxis.X * c - baseAxis.Y * s, baseAxis.X * s + baseAxis.Y * c);
                    Vector2 pp = new(-ax.Y, ax.X);
                    Vector2 Main(float t) => v.Pos + ax * t + pp * (bend * MathF.Sin(t / 60f * MathF.PI));
                    Vector2 Back(float t) => v.Pos + ax * t + pp * side * (22f + 1.8f * MathF.Sin(t / 17f));
                    Vector2 Other(float t) => v.Pos + ax * t - pp * side * (20f + 1.5f * MathF.Cos(t / 13f));

                    // Vie: [0..3] principale, [4..8] via dietro e raccordi, [9..11] via dall'altro lato e raccordi
                    var all = new List<List<Vector2>>
                    {
                        Curve(Main, -60f, -38f), Curve(Main, -38f, 0f), Curve(Main, 0f, 38f), Curve(Main, 38f, 60f),
                    };
                    if (level >= 1)
                    {
                        all.Add(Curve(Back, -22f, 0f));
                        all.Add(Curve(Back, 0f, 22f));
                        all.Add(Line(Main(-38f), Back(-22f)));
                        all.Add(Line(Back(22f), Main(38f)));
                        all.Add(Line(Main(0f), Back(0f)));
                    }
                    if (level >= 2)
                    {
                        all.Add(Curve(Other, -18f, 18f));
                        all.Add(Line(Main(-38f), Other(-18f)));
                        all.Add(Line(Other(18f), Main(38f)));
                    }
                    if (!all.All(pts => Valid(pts, avoidRoads: true))) continue;

                    // Costruzione: nodi condivisi tra le vie
                    var m0 = NewNode(Main(-60f), comp);
                    var m1 = NewNode(Main(-38f), comp);
                    var mc = NewNode(Main(0f), comp);
                    var m2 = NewNode(Main(38f), comp);
                    var m3 = NewNode(Main(60f), comp);
                    mc.Piazza = true;
                    Connect(m0, m1, all[0], RoadKind.Street, comp);
                    Connect(m1, mc, all[1], RoadKind.Street, comp);
                    Connect(mc, m2, all[2], RoadKind.Street, comp);
                    Connect(m2, m3, all[3], RoadKind.Street, comp);
                    var nodes = new List<RoadNode> { m0, m1, mc, m2, m3 };
                    if (level >= 1)
                    {
                        var b1 = NewNode(Back(-22f), comp);
                        var bc = NewNode(Back(0f), comp);
                        var b2 = NewNode(Back(22f), comp);
                        Connect(b1, bc, all[4], RoadKind.Street, comp);
                        Connect(bc, b2, all[5], RoadKind.Street, comp);
                        Connect(m1, b1, all[6], RoadKind.Street, comp);
                        Connect(b2, m2, all[7], RoadKind.Street, comp);
                        Connect(mc, bc, all[8], RoadKind.Street, comp);
                        nodes.AddRange([b1, bc, b2]);
                    }
                    if (level >= 2)
                    {
                        var o1 = NewNode(Other(-18f), comp);
                        var o2 = NewNode(Other(18f), comp);
                        Connect(o1, o2, all[9], RoadKind.Street, comp);
                        Connect(m1, o1, all[10], RoadKind.Street, comp);
                        Connect(o2, m2, all[11], RoadKind.Street, comp);
                        nodes.AddRange([o1, o2]);
                    }
                    v.Axis = ax;
                    _villageNodes[v] = nodes;
                    _villageComp[v] = comp;
                    return;
                }
        Console.WriteLine($"Paese {v.Name}: nessun posto per le vie, resta senza");
    }

    // ------------------------------------------------------------------ percorsi in campagna (A*)

    /// <summary>
    /// Cerca un percorso da <paramref name="from"/> a una cella che soddisfa <paramref name="goal"/>. Le strade
    /// esistenti si possono attraversare ma costano (non ci si corre accanto), i centri abitati altrui pure.
    /// </summary>
    private List<Vector2>? Route(Vector2 from, Func<int, bool> goal, Vector2? toward, int ownComp)
    {
        if (!CellOf(from, out int si, out int sj)) return null;
        int start = sj * _w + si;
        var dist = new Dictionary<int, float> { [start] = 0f };
        var prev = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, 0f);
        int found = -1;
        float H(int c)
        {
            if (toward is not { } t) return 0f;
            return Vector2.Distance(CellCenter(c % _w, c / _w), t) * 0.95f;
        }

        int[] di = [1, -1, 0, 0, 1, 1, -1, -1], dj = [0, 0, 1, -1, 1, -1, 1, -1];
        int expanded = 0;
        while (open.TryDequeue(out int c, out float pri))
        {
            float dc = dist[c];
            if (pri > dc + H(c) + 1e-3f) continue; // voce superata
            if (c != start && goal(c)) { found = c; break; }
            if (++expanded > 400_000) break;
            int ci = c % _w, cj = c / _w;
            for (int k = 0; k < 8; k++)
            {
                int ni = ci + di[k], nj = cj + dj[k];
                if (ni < 0 || nj < 0 || ni >= _w || nj >= _h) continue;
                int nc = nj * _w + ni;
                if (_blocked[nc]) continue;
                // Fiumi: solo di traverso (passi est-ovest), e costano (un ponte non si fa per niente)
                if ((_river[nc] || _river[c]) && dj[k] != 0) continue;
                float step = (k < 4 ? 1f : 1.414f) * G * _cost[nc];
                int occ = _occ[nc], near = _near[nc];
                // Un ponte accanto a un altro non si fa: si usa quello che c'è (ci si innesta prima del fiume)
                if (_river[nc]) step += (BridgeNearby(ni, nj, ownComp) ? 90f : 10f) * G;
                if (occ >= 0 && !goal(nc)) step *= occ == ownComp ? 2f : 7f;
                else if (near >= 0 && near != ownComp && !goal(nc)) step *= 1.7f; // niente strade parallele a pochi metri
                if (_urban[nc] > 0.25f && occ < 0) step *= 1.6f; // dentro i paesi altrui si passa solo per le vie
                float nd = dc + step;
                if (dist.TryGetValue(nc, out float old) && old <= nd) continue;
                dist[nc] = nd;
                prev[nc] = c;
                open.Enqueue(nc, nd + H(nc));
            }
        }
        if (found < 0) return null;

        var cells = new List<int>();
        for (int c = found; c != start; c = prev[c]) cells.Add(c);
        cells.Reverse();
        var pts = new List<Vector2> { from };
        pts.AddRange(cells.Select(c => CellCenter(c % _w, c / _w)));
        return pts;
    }

    /// <summary>Sul fiume, a monte o a valle di questa cella (fino a ~24 unità), passa già un'altra strada?</summary>
    private bool BridgeNearby(int i, int j, int ownComp)
    {
        for (int dj = -6; dj <= 6; dj++)
        {
            int jj = j + dj;
            if (jj < 0 || jj >= _h) continue;
            int c = jj * _w + i;
            if (_river[c] && _near[c] >= 0 && _near[c] != ownComp) return true;
        }
        return false;
    }

    /// <summary>Spezzata a scalini della griglia → strada morbida: tolti i punti allineati, arrotondata e ricampionata.</summary>
    private static List<Vector2> Smooth(List<Vector2> raw)
    {
        var pts = new List<Vector2> { raw[0] };
        for (int k = 1; k + 1 < raw.Count; k++)
        {
            Vector2 a = Vector2.Normalize(raw[k] - pts[^1]), b = Vector2.Normalize(raw[k + 1] - raw[k]);
            if (Vector2.Dot(a, b) < 0.999f) pts.Add(raw[k]);
        }
        pts.Add(raw[^1]);
        for (int it = 0; it < 4; it++) // Chaikin, estremi fissi
        {
            var next = new List<Vector2> { pts[0] };
            for (int k = 0; k + 1 < pts.Count; k++)
            {
                next.Add(Vector2.Lerp(pts[k], pts[k + 1], 0.25f));
                next.Add(Vector2.Lerp(pts[k], pts[k + 1], 0.75f));
            }
            next.Add(pts[^1]);
            pts = next;
        }
        return Resample(pts);
    }

    private static List<Vector2> Resample(List<Vector2> pts)
    {
        float total = 0f;
        for (int k = 1; k < pts.Count; k++) total += Vector2.Distance(pts[k - 1], pts[k]);
        int n = Math.Max(1, (int)MathF.Ceiling(total / Step));
        var res = new List<Vector2> { pts[0] };
        float acc = 0f, next = total / n;
        for (int k = 1; k < pts.Count; k++)
        {
            float seg = Vector2.Distance(pts[k - 1], pts[k]);
            while (seg > 1e-6f && acc + seg >= next - 1e-4f && res.Count < n)
            {
                res.Add(Vector2.Lerp(pts[k - 1], pts[k], (next - acc) / seg));
                next += total / n;
            }
            acc += seg;
        }
        res.Add(pts[^1]);
        return res;
    }

    /// <summary>
    /// Attorno a ogni attraversamento di un fiume la strada diventa dritta per tutta la campata del ponte (e un po'
    /// oltre): il ponte è rettilineo e la strada ci deve entrare e uscire allineata.
    /// </summary>
    private static List<Vector2> StraightenAtRivers(List<Vector2> pts)
    {
        const float Half = 10f;
        for (int k = 1; k < pts.Count; k++)
        {
            if (RiverSide(pts[k - 1]) == RiverSide(pts[k])) continue;
            int a = k - 1, b = k;
            float len = 0f;
            while (a > 0 && len < Half) { len += Vector2.Distance(pts[a - 1], pts[a]); a--; }
            len = 0f;
            while (b < pts.Count - 1 && len < Half) { len += Vector2.Distance(pts[b], pts[b + 1]); b++; }
            var line = Line(pts[a], pts[b]);
            pts = pts.Take(a).Concat(line).Concat(pts.Skip(b + 1)).ToList();
            k = a + line.Count;
        }
        return Resample(pts);
    }

    /// <summary>Il punto di strada esistente più vicino (ricerca diretta: durante la costruzione l'indice non c'è).</summary>
    private (RoadEdge Edge, float S, float Dist)? NearestEdge(Vector2 p, Func<RoadEdge, bool>? accept = null)
    {
        (RoadEdge, float, float)? best = null;
        float bestD = float.MaxValue;
        foreach (var e in _net.Edges)
        {
            if (accept != null && !accept(e)) continue;
            for (int k = 0; k + 1 < e.Count; k++)
            {
                Vector2 a = e.Points[k], ab = e.Points[k + 1] - a;
                float len2 = ab.LengthSquared();
                float t = len2 > 1e-8f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
                float d = Vector2.Distance(p, a + ab * t);
                if (d < bestD)
                {
                    bestD = d;
                    best = (e, e.CumAt(k) + MathF.Sqrt(len2) * t, d);
                }
            }
        }
        return best;
    }

    private static bool SegCross(Vector2 p, Vector2 q, Vector2 a, Vector2 b, out float t, out float u)
    {
        Vector2 r = q - p, s = b - a;
        float den = r.X * s.Y - r.Y * s.X;
        t = u = 0f;
        if (MathF.Abs(den) < 1e-8f) return false;
        Vector2 ap = a - p;
        t = (ap.X * s.Y - ap.Y * s.X) / den;
        u = (ap.X * r.Y - ap.Y * r.X) / den;
        return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
    }

    /// <summary>
    /// Aggiunge alla rete una strada già tracciata, da <paramref name="start"/> alla rete esistente (si innesta sul
    /// nodo vicino o spezza il tratto più vicino) o a un nuovo capolinea. Dove ne attraversa altre nasce un incrocio.
    /// </summary>
    private RoadNode? AddRoad(List<Vector2> pts, RoadNode start, RoadKind kind, int comp, bool snapEnd,
                              Func<RoadEdge, bool>? endAccept = null)
    {
        pts = StraightenAtRivers(Smooth(pts));
        if (pts.Count < 3) return null;

        // Estremo finale: aggancio alla rete
        RoadNode? endNode = null;
        if (snapEnd)
        {
            if (NearestEdge(pts[^1], endAccept) is not { } hit || hit.Dist > 9f) return null;
            Vector2 at = hit.Edge.At(hit.S);
            // Gli ultimi metri puntano dritti sull'aggancio (niente innesti radenti)
            while (pts.Count > 3 && Vector2.Distance(pts[^1], at) < 7f) pts.RemoveAt(pts.Count - 1);
            var near = new[] { hit.Edge.A, hit.Edge.B }.MinBy(nd => Vector2.Distance(nd.Pos, at))!;
            if (Vector2.Distance(near.Pos, at) < 8f) endNode = near;
            else endNode = Split(hit.Edge, hit.S);
            pts.AddRange(Line(pts[^1], endNode.Pos).Skip(1));
            pts = Resample(pts);
        }

        // Incroci con le strade attraversate
        var cuts = new List<(float Along, Vector2 At)>();
        float along = 0f;
        for (int k = 0; k + 1 < pts.Count; k++)
        {
            Vector2 p = pts[k], q = pts[k + 1];
            float seg = Vector2.Distance(p, q);
            foreach (var e in _net.Edges)
            {
                for (int m = 0; m + 1 < e.Count; m++)
                {
                    Vector2 a = e.Points[m], b = e.Points[m + 1];
                    if (MathF.Max(a.X, b.X) < MathF.Min(p.X, q.X) || MathF.Min(a.X, b.X) > MathF.Max(p.X, q.X)
                        || MathF.Max(a.Y, b.Y) < MathF.Min(p.Y, q.Y) || MathF.Min(a.Y, b.Y) > MathF.Max(p.Y, q.Y)) continue;
                    if (SegCross(p, q, a, b, out float t, out float u))
                        cuts.Add((along + seg * t, Vector2.Lerp(a, b, u)));
                }
            }
            along += seg;
        }
        cuts.Sort((x, y) => x.Along.CompareTo(y.Along));

        // Si costruisce a pezzi tra un incrocio e l'altro
        var nodes = new List<(float Along, RoadNode Node)> { (0f, start) };
        foreach (var cut in cuts)
        {
            if (cut.Along - nodes[^1].Along < 6f || along - cut.Along < 6f) continue;
            // Il tratto attraversato si cerca adesso: un incrocio precedente può averlo già spezzato
            if (NearestEdge(cut.At) is not { } hit || hit.Dist > 0.6f) continue;
            nodes.Add((cut.Along, Split(hit.Edge, hit.S)));
        }
        endNode ??= NewNode(pts[^1], comp);
        nodes.Add((along, endNode));

        float acc = 0f;
        int idx = 0;
        for (int n = 0; n + 1 < nodes.Count; n++)
        {
            var piece = new List<Vector2> { nodes[n].Node.Pos };
            while (idx < pts.Count && acc < nodes[n + 1].Along - 0.5f)
            {
                if (acc > nodes[n].Along + 0.5f) piece.Add(pts[idx]);
                if (idx + 1 < pts.Count) acc += Vector2.Distance(pts[idx], pts[idx + 1]);
                idx++;
            }
            piece.Add(nodes[n + 1].Node.Pos);
            if (piece.Count >= 2 && nodes[n].Node != nodes[n + 1].Node)
                Connect(nodes[n].Node, nodes[n + 1].Node, Resample(piece), kind, comp);
        }
        return endNode;
    }

    // ------------------------------------------------------------------ collegamenti

    /// <summary>
    /// Dopo la ferrovia: a ogni stazione arriva una via asfaltata dalla strada più vicina. Parte da un piazzale dietro
    /// il fabbricato (dalla parte della banchina) e non attraversa mai i binari. Poi la rete si riprepara.
    /// </summary>
    public static void ConnectStations(RoadNetwork net, GameMap map, Railway rail)
    {
        if (!rail.Exists || rail.Stations.Count == 0) return;
        var b = new RoadBuilder(net, map);
        foreach (var e in net.Edges) b.Occupy(e, CityComp);
        // I binari non si attraversano: le celle della ferrovia (con un margine) sono vietate
        for (int j = 0; j < b._h; j++)
            for (int i = 0; i < b._w; i++)
            {
                Vector2 p = b.CellCenter(i, j);
                if (rail.Distance(p.X, p.Y) < Railway.BallastHalf + Railway.PlatformWidth + 3f) b._blocked[j * b._w + i] = true;
            }

        // Piazzale lastricato subito dietro la banchina (ci si arriva a piedi dalla piazza)
        const float Back = Railway.BallastHalf + 0.2f + Railway.PlatformWidth + 0.2f + RailStation.PlazaRadius;
        foreach (var st in rail.Stations)
        {
            bool done = false;
            foreach (float ds in new[] { 0f, 7f, -7f, 13f, -13f })
            {
                Vector2 t = rail.Tangent(st.S + ds), right = new(-t.Y, t.X);
                Vector2 f = rail.At(st.S + ds) + right * st.Side * Back;
                if (map.Sample(f.X, f.Y).Land < 0.97f || Terrain.RiverDistance(f.X, f.Y) < Zone.RiverHalf + 6f) continue;
                if (rail.Distance(f.X, f.Y) < Back - 0.5f) continue; // l'anello curva: qui si avvicina di nuovo ai binari
                if (map.Landmarks.Any(l => Vector2.Distance(f, l.Pos) < l.Radius + 6f)) continue;
                if (b.NearestEdge(f) is { } near && near.Dist < RailStation.PlazaRadius + 7f)
                {
                    // Dietro la stazione passa già una via: il piazzale si affaccia su quella
                    st.Forecourt = st.AccessJunction = near.Edge.At(near.S);
                    st.AccessRoadDir = near.Edge.Tangent(near.S);
                    st.AccessDir = -right * st.Side;
                    Console.WriteLine($"  la {st.Name} si affaccia su una via che c'è già");
                    done = true;
                    break;
                }

                int comp = b._nextComp++;
                var start = b.NewNode(f, comp);
                var route = b.Route(f, c => b._occ[c] >= 0 && b._occ[c] != comp, null, comp);
                if (route == null || route.Count > 45 || route.Any(p => rail.Distance(p.X, p.Y) < Railway.BallastHalf + 2.5f))
                {
                    net.Nodes.Remove(start);
                    continue;
                }
                var end = b.AddRoad(route, start, RoadKind.Street, comp, snapEnd: true);
                if (end == null || start.Degree == 0)
                {
                    net.Nodes.Remove(start);
                    continue;
                }
                start.Destination = true;
                start.Piazza = true;
                st.Forecourt = f;
                st.PlazaS = rail.Wrap(st.S + ds);
                // L'innesto sulla rete: il primo incrocio che s'incontra uscendo dal piazzale
                var e = start.Edges[0];
                var junction = e.Other(start);
                st.AccessJunction = junction.Pos;
                st.AccessDir = e.LeaveDir(junction);
                var main = junction.Edges.Where(x => x != e).Select(x => x.LeaveDir(junction)).ToList();
                st.AccessRoadDir = main.Count > 0 ? main[0] : new Vector2(-st.AccessDir.Y, st.AccessDir.X);
                Console.WriteLine($"  via per la {st.Name}: {e.Length:F0} unità, piazzale a {f.X:F0}, {f.Y:F0}");
                done = true;
                break;
            }
            if (!done) Console.WriteLine($"  {st.Name}: nessuna via d'accesso possibile");
        }
        net.Finish();
        rail.PlaceStationBuildings(net);
    }

    /// <summary>Statale dal capo del paese rivolto alla città fino alla prima strada che incontra in quella direzione.</summary>
    private void ConnectVillage(Settlement v, Vector2 hub, bool hasCity)
    {
        if (!_villageNodes.TryGetValue(v, out var nodes)) return;
        int own = _villageComp[v];
        var start = nodes[4]; // capo verso la città
        int comp = _nextComp++;
        var route = Route(start.Pos, c => _occ[c] >= 0 && _occ[c] != own, hub, own);
        if (route == null)
        {
            Console.WriteLine($"Paese {v.Name}: nessuna strada verso la città");
            return;
        }
        var end = AddRoad(route, start, hasCity ? RoadKind.Main : RoadKind.Dirt, comp, snapEnd: true,
                          e => _compOf.GetValueOrDefault(e.A, -1) != own || _compOf.GetValueOrDefault(e.B, -1) != own);
        Console.WriteLine(end == null
            ? $"Paese {v.Name}: la statale non si aggancia alla rete"
            : $"Paese {v.Name} ({v.Pos.X:F0}, {v.Pos.Y:F0}): statale di {route.Count * G:F0} unità circa");
    }

    /// <summary>Sterrati tra paesi vicini (in ordine attorno alla città): passano nei boschi tra un paese e l'altro.</summary>
    private void BuildRing(List<Settlement> villages, Vector2 hub)
    {
        var ring = villages.Where(_villageNodes.ContainsKey)
                           .OrderBy(v => MathF.Atan2(v.Pos.Y - hub.Y, v.Pos.X - hub.X)).ToList();
        if (ring.Count < 2) return;
        for (int k = 0; k < ring.Count; k++)
        {
            var a = ring[k];
            var b = ring[(k + 1) % ring.Count];
            if (ring.Count == 2 && k == 1) break;
            // Partenza dal capo del paese più vicino all'altro (i capi delle vie, non la piazza)
            var start = _villageNodes[a].Where(nd => !nd.Piazza).MinBy(nd => Vector2.Distance(nd.Pos, b.Pos))!;
            int target = _villageComp[b], comp = _nextComp++;
            var route = Route(start.Pos, c => _occ[c] == target, b.Pos, _villageComp[a]);
            if (route == null) continue;
            var bNodes = _villageNodes[b];
            AddRoad(route, start, RoadKind.Dirt, comp, snapEnd: true,
                    e => bNodes.Contains(e.A) && bNodes.Contains(e.B));
        }
    }

    /// <summary>
    /// Cascine sparse nella campagna, lontane dai paesi e tra loro: a ognuna arriva uno sterrato dalla strada più
    /// vicina e lì finisce, nell'aia. Stanno ben dentro un pezzo di territorio (le case non scavalcano i bordi).
    /// </summary>
    private void BuildFarms()
    {
        const float C = Terrain.ChunkSize;
        var candidates = new List<(float Key, Vector2 P)>();
        for (int i = _map.MinI; i <= _map.MaxI; i++)
            for (int j = _map.MinJ; j <= _map.MaxJ; j++)
            {
                // Un candidato per pezzo, spostato a caso ma lontano dai suoi bordi
                var p = new Vector2(i * C + (Noise.Hash01(i, j, 41) - 0.5f) * (C - 40f),
                                    j * C + (Noise.Hash01(i, j, 42) - 0.5f) * (C - 40f));
                candidates.Add((Noise.Hash01(i, j, 43), p));
            }
        foreach (var (_, p) in candidates.OrderBy(c => c.Key))
        {
            if (_net.Farms.Count >= 18) break;
            bool ok = true;
            for (int k = 0; k < 8 && ok; k++)
            {
                float a = k * MathF.PI / 4f;
                var q = p + new Vector2(MathF.Cos(a), MathF.Sin(a)) * 16f;
                if (_map.Sample(q.X, q.Y).Land < 0.97f) ok = false;
            }
            if (!ok || _map.Sample(p.X, p.Y).Land < 0.97f) continue;
            if (_net.Settlements.Any(s => Vector2.Distance(p, s.Pos) < s.Radius + 40f)) continue;
            if (Terrain.RiverDistance(p.X, p.Y) < 22f) continue;
            if (_map.Landmarks.Any(l => Vector2.Distance(p, l.Pos) < l.Radius + 28f)) continue;
            if (_net.Farms.Any(f => Vector2.Distance(p, f.Yard) < 130f)) continue;
            if (NearestEdge(p) is { } near && near.Dist < 32f) continue;

            var start = NewNode(p, _nextComp);
            int comp = _nextComp++;
            var route = Route(p, c => _occ[c] >= 0, null, comp);
            if (route == null || route.Count > 160) { _net.Nodes.Remove(start); continue; }
            if (AddRoad(route, start, RoadKind.Dirt, comp, snapEnd: true) == null || start.Degree == 0)
            {
                _net.Nodes.Remove(start);
                continue;
            }
            start.Destination = true;
            var e = start.Edges[0];
            _net.Farms.Add(new FarmSite { Yard = p, Approach = e.LeaveDir(start) });        }
    }

    /// <summary>Uno sterrato porta a ogni punto di riferimento e finisce davanti (al castello: davanti alla porta).</summary>
    private void ConnectLandmarks()
    {
        foreach (var l in _map.Landmarks)
        {
            if (NearestEdge(l.Pos) is not { } near) continue;
            Vector2 toward = Vector2.Normalize(near.Edge.At(near.S) - l.Pos);
            if (l.Kind == LandmarkKind.Castle)
                toward = Footprint.ToWorld(Vector2.Zero, l.Yaw, new Vector2(0f, 1f)); // la porta è a sud del castello
            // Il capolinea davanti al punto di riferimento: si prova la direzione buona, poi si gira attorno
            RoadNode? start = null;
            for (int k = 0; k < 12 && start == null; k++)
            {
                float a = MathF.Atan2(toward.Y, toward.X) + (k % 2 == 0 ? 1 : -1) * ((k + 1) / 2) * 0.5f;
                var p = l.Pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (l.Radius + 5f);
                if (_map.Sample(p.X, p.Y).Land < 0.9f || Terrain.RiverDistance(p.X, p.Y) < Zone.RiverHalf + 4f) continue;
                if (!CellOf(p, out int ci, out int cj) || _blocked[cj * _w + ci]) continue;
                start = NewNode(p, _nextComp);
            }
            if (start == null) continue;
            // Già sulla strada? Basta un raccordo corto
            int comp = _nextComp++;
            if (near.Dist < l.Radius + 14f)
            {
                var hit = NearestEdge(start.Pos)!.Value;
                var end = Vector2.Distance(hit.Edge.A.Pos, hit.Edge.At(hit.S)) < 8f ? hit.Edge.A
                        : Vector2.Distance(hit.Edge.B.Pos, hit.Edge.At(hit.S)) < 8f ? hit.Edge.B
                        : Split(hit.Edge, hit.S);
                if (Vector2.Distance(end.Pos, start.Pos) > 3f)
                    Connect(start, end, Line(start.Pos, end.Pos), hit.Edge.Kind == RoadKind.Dirt ? RoadKind.Dirt : RoadKind.Street, comp);
            }
            else if (Route(start.Pos, c => _occ[c] >= 0, null, comp) is { } route)
                AddRoad(route, start, RoadKind.Dirt, comp, snapEnd: true);
            if (start.Degree == 0) { _net.Nodes.Remove(start); continue; }
            start.Destination = true;
        }
    }

    /// <summary>Toglie i vicoli ciechi che non portano a niente (finché ce ne sono) e i nodi rimasti soli.</summary>
    private void PruneDeadEnds()
    {
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var n in _net.Nodes.ToList())
            {
                if (n.Degree != 1 || n.Destination) continue;
                Remove(n.Edges[0]);
                changed = true;
            }
        }
        _net.Nodes.RemoveAll(n => n.Degree == 0);
        // Le celle delle strade tolte non devono restare "occupate": la ricerca ci punterebbe senza trovarle
        Array.Fill(_occ, -1);
        Array.Fill(_near, -1);
        foreach (var e in _net.Edges) Occupy(e, _compOf.GetValueOrDefault(e.A, CityComp));
    }

    /// <summary>Un ponte dove una strada attraversa un fiume (la strada lì è già dritta).</summary>
    private void FindBridges()
    {
        foreach (var e in _net.Edges)
            for (int k = 1; k < e.Count; k++)
            {
                Vector2 p = e.Points[k - 1], q = e.Points[k];
                if (RiverSide(p) == RiverSide(q)) continue;
                // Punto esatto di attraversamento (bisezione sul lato del fiume)
                Vector2 a = p, b = q;
                for (int it = 0; it < 12; it++)
                {
                    Vector2 m = (a + b) * 0.5f;
                    if (RiverSide(m) == RiverSide(a)) a = m; else b = m;
                }
                Vector2 at = (a + b) * 0.5f;
                Vector2 dir = Vector2.Normalize(q - p);
                float across = MathF.Max(MathF.Abs(dir.X), 0.45f);
                _net.Bridges.Add(new BridgeSite
                {
                    Center = at, Dir = dir, HalfWidth = e.HalfWidth,
                    Span = Zone.RiverHalf * 2f / across + 3.4f,
                });            }
    }
}
