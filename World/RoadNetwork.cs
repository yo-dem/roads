using System.Numerics;

namespace Roads.World;

/// <summary>Tipo di strada: decide larghezza, fondo e cosa ci cresce attorno.</summary>
public enum RoadKind
{
    /// <summary>Strada statale/provinciale asfaltata, con la mezzeria tratteggiata: collega la città ai paesi.</summary>
    Main,
    /// <summary>Via di città o di paese: asfalto, marciapiedi, case affacciate.</summary>
    Street,
    /// <summary>Sterrato di campagna: tra i paesi, verso le cascine e i punti di riferimento.</summary>
    Dirt,
}

/// <summary>Disposizione delle corsie di un tratto.</summary>
public enum RoadLayout
{
    /// <summary>Doppio senso, una corsia per verso (statali, vie di città e paese).</summary>
    TwoWay,
    /// <summary>Una corsia sola, si passa in entrambi i versi (sterrati).</summary>
    Single,
    /// <summary>Senso unico, una corsia stretta.</summary>
    OneWay,
    /// <summary>Viale: due corsie per verso con lo spartitraffico in mezzo.</summary>
    Avenue,
}

/// <summary>Un centro abitato: la città (grande, al centro) o un paese. La densità cala con la distanza dal centro.</summary>
public sealed class Settlement
{
    public required string Name { get; init; }
    public required bool City { get; init; }
    public Vector2 Pos { get; set; }
    public float Radius => City ? 175f : 58f;
    /// <summary>Asse della via principale (paesi): punta verso la città.</summary>
    public Vector2 Axis { get; set; } = Vector2.UnitX;
}

/// <summary>Una cascina in campagna: l'aia è la fine dello sterrato che ci arriva, attorno casa e fienile.</summary>
public sealed class FarmSite
{
    public Vector2 Yard;
    /// <summary>Direzione da cui arriva lo sterrato (dall'aia verso la strada).</summary>
    public Vector2 Approach;
}

/// <summary>Ponte dove una strada scavalca un fiume: la strada lì è dritta per tutta la campata.</summary>
public sealed class BridgeSite
{
    public Vector2 Center;
    public Vector2 Dir;
    public float Span;
    public float HalfWidth;
}

public sealed class RoadNode
{
    public int Id;
    public Vector2 Pos;
    public readonly List<RoadEdge> Edges = new();
    /// <summary>Raggio dello slargo all'incrocio (o dell'aia, della piazza in fondo a una strada).</summary>
    public float HubRadius;
    /// <summary>Fine strada voluta (cascina, punto di riferimento): non va potata.</summary>
    public bool Destination;
    /// <summary>Piazza di paese o di città: slargo più ampio.</summary>
    public bool Piazza;
    public int Degree => Edges.Count;
    /// <summary>Incrocio, fine strada o piazza: lì c'è uno slargo (a 2 strade la via prosegue soltanto).</summary>
    public bool IsHub => Edges.Count != 2 || Piazza;
}

/// <summary>Un tratto di strada tra due nodi: una spezzata fitta (un punto ogni ~2 unità) da A a B.</summary>
public sealed class RoadEdge
{
    public int Id;
    public required RoadNode A;
    public required RoadNode B;
    public required RoadKind Kind;
    public required List<Vector2> Points;
    /// <summary>Come sono disposte le corsie (di serie: una sola sugli sterrati, doppio senso altrove).</summary>
    public RoadLayout Layout;
    /// <summary>Sensi unici: +1 si va solo da A verso B, -1 solo da B verso A (0 doppio senso).</summary>
    public int OneWay;
    private float[] _cum = [];

    public const float AvenueLane = 1.4f, AvenueMedian = 0.7f;

    public float HalfWidth => Layout switch
    {
        RoadLayout.Avenue => AvenueLane * 2f + AvenueMedian * 0.5f,
        RoadLayout.OneWay => 1.0f,
        RoadLayout.Single => 1.15f,
        _ => Kind == RoadKind.Main ? 1.6f : 1.5f,
    };
    public bool Paved => Kind != RoadKind.Dirt;

    /// <summary>
    /// Le corsie: scostamento del centro corsia dalla mezzeria verso destra (destra di chi va da A a B) e verso di
    /// marcia (+1 da A a B, -1 da B ad A, 0 entrambi). Si guida a destra.
    /// </summary>
    public (float Offset, int Dir)[] Lanes => Layout switch
    {
        RoadLayout.TwoWay => [(HalfWidth * 0.5f, 1), (-HalfWidth * 0.5f, -1)],
        RoadLayout.Avenue =>
        [
            (AvenueMedian * 0.5f + AvenueLane * 0.5f, 1), (AvenueMedian * 0.5f + AvenueLane * 1.5f, 1),
            (-AvenueMedian * 0.5f - AvenueLane * 0.5f, -1), (-AvenueMedian * 0.5f - AvenueLane * 1.5f, -1),
        ],
        RoadLayout.OneWay => [(0f, OneWay)],
        _ => [(0f, 0)],
    };

    /// <summary>
    /// Centri delle corsie per chi viaggia nel verso <paramref name="dir"/> (+1 da A a B), come scostamento verso la
    /// SUA destra. Chi va contromano su un senso unico ha comunque la corsia (non lo si ferma).
    /// </summary>
    public float[] TravelLanes(float dir)
    {
        var mine = Lanes.Where(l => l.Dir == 0 || l.Dir == MathF.Sign(dir)).Select(l => l.Offset * dir).ToArray();
        return mine.Length > 0 ? mine : Lanes.Select(l => l.Offset * dir).ToArray();
    }

    /// <summary>Mezza larghezza di una corsia.</summary>
    public float LaneHalf => Layout switch
    {
        RoadLayout.Avenue => AvenueLane * 0.5f,
        RoadLayout.TwoWay => HalfWidth * 0.5f,
        _ => HalfWidth,
    };
    public float Length => _cum.Length > 0 ? _cum[^1] : 0f;
    public int Count => Points.Count;
    public float CumAt(int i) => _cum[i];

    public void Measure()
    {
        _cum = new float[Points.Count];
        for (int i = 1; i < Points.Count; i++) _cum[i] = _cum[i - 1] + Vector2.Distance(Points[i - 1], Points[i]);
    }

    public RoadNode Other(RoadNode n) => n == A ? B : A;

    private int Segment(float s)
    {
        if (s <= 0f) return 0;
        if (s >= _cum[^1]) return _cum.Length - 2;
        int lo = 0, hi = _cum.Length - 2;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_cum[mid] <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>Punto della mezzeria alla distanza <paramref name="s"/> da A (fuori dal tratto: prosegue dritto).</summary>
    public Vector2 At(float s)
    {
        int i = Segment(s);
        float span = _cum[i + 1] - _cum[i];
        float f = span > 1e-5f ? (s - _cum[i]) / span : 0f;
        return Vector2.Lerp(Points[i], Points[i + 1], f);
    }

    /// <summary>Direzione della strada (da A verso B) alla distanza <paramref name="s"/>, sfumata tra i segmenti.</summary>
    public Vector2 Tangent(float s)
    {
        int i = Segment(s);
        Vector2 d = Points[i + 1] - Points[i];
        // Sfuma con il segmento vicino nella metà più prossima: niente scalini di direzione sui vertici
        float span = _cum[i + 1] - _cum[i];
        float f = span > 1e-5f ? (s - _cum[i]) / span : 0.5f;
        if (f < 0.5f && i > 0) d = Vector2.Lerp(Points[i] - Points[i - 1], d, 0.5f + f);
        else if (f > 0.5f && i + 2 < Points.Count) d = Vector2.Lerp(d, Points[i + 2] - Points[i + 1], f - 0.5f);
        return d.LengthSquared() > 1e-8f ? Vector2.Normalize(d) : Vector2.UnitX;
    }

    /// <summary>Direzione con cui la strada lascia il nodo <paramref name="n"/> (verso l'interno del tratto).</summary>
    public Vector2 LeaveDir(RoadNode n, float probe = 4f)
    {
        float s = n == A ? MathF.Min(probe, Length) : MathF.Max(Length - probe, 0f);
        Vector2 d = At(s) - n.Pos;
        return d.LengthSquared() > 1e-6f ? Vector2.Normalize(d) : (n == A ? Tangent(0f) : -Tangent(Length));
    }

    /// <summary>Distanza dal nodo <paramref name="n"/> lungo il tratto, misurata da quel capo.</summary>
    public float FromEnd(RoadNode n, float s) => n == A ? s : Length - s;
    public float ToS(RoadNode n, float fromEnd) => n == A ? fromEnd : Length - fromEnd;
}

/// <summary>Il punto di una strada più vicino a una posizione.</summary>
public readonly record struct RoadHit(RoadEdge Edge, float S, Vector2 Point, float Distance);

/// <summary>
/// La rete stradale dell'isola, generata una volta all'avvio dalla mappa (dove stanno città e paesi) e poi fissa:
/// tutte le interrogazioni sono di sola lettura e sicure da qualunque thread (i pezzi di territorio si generano in
/// background). Contiene anche i centri abitati, le cascine e i ponti, e i campi "urbano" e "bosco" che ne derivano.
/// </summary>
public sealed class RoadNetwork
{
    public static RoadNetwork Current { get; set; } = new();

    public List<RoadNode> Nodes { get; } = new();
    public List<RoadEdge> Edges { get; } = new();
    public List<Settlement> Settlements { get; } = new();
    public List<FarmSite> Farms { get; } = new();
    public List<BridgeSite> Bridges { get; } = new();

    // ------------------------------------------------------------------ indice spaziale

    // Ogni segmento (e ogni slargo, come segmento di lunghezza zero) è registrato in tutte le celle che la sua
    // fascia di raggio Reach può toccare: un'interrogazione guarda una sola cella.
    private const float HashCell = 8f;
    public const float Reach = 16f;
    private const float Far = 1000f;

    private readonly record struct Seg(Vector2 A, Vector2 B, float HalfWidth, RoadEdge? Edge, int Index, RoadNode? Node);

    private readonly List<Seg> _segs = new();
    private readonly Dictionary<long, List<int>> _hash = new();
    private readonly Dictionary<(int, int), List<RoadEdge>> _byZone = new();
    private readonly Dictionary<(int, int), List<RoadNode>> _nodesByZone = new();
    private static readonly List<RoadEdge> NoEdges = new();
    private static readonly List<RoadNode> NoNodes = new();
    private static readonly List<int> NoSegs = new();

    private static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

    private List<int> CellSegs(float x, float z) =>
        _hash.GetValueOrDefault(Key((int)MathF.Floor(x / HashCell), (int)MathF.Floor(z / HashCell)), NoSegs);

    /// <summary>Prepara le interrogazioni: misura i tratti, calcola gli slarghi e riempie gli indici. Va chiamato a fine generazione.</summary>
    private Settlement[]? _villages;

    public void Finish()
    {
        // Paesi in ordine attorno al loro baricentro: le fasce di bosco stanno tra paesi vicini
        var vs = Settlements.Where(s => !s.City).ToList();
        Vector2 mid = Settlements.FirstOrDefault(s => s.City)?.Pos
                      ?? (vs.Count > 0 ? vs.Aggregate(Vector2.Zero, (a, s) => a + s.Pos) / vs.Count : Vector2.Zero);
        _villages = vs.OrderBy(s => MathF.Atan2(s.Pos.Y - mid.Y, s.Pos.X - mid.X)).ToArray();

        for (int i = 0; i < Nodes.Count; i++) Nodes[i].Id = i;
        for (int i = 0; i < Edges.Count; i++)
        {
            Edges[i].Id = i;
            Edges[i].Measure();
        }
        foreach (var n in Nodes)
        {
            float hw = n.Edges.Count > 0 ? n.Edges.Max(e => e.HalfWidth) : 1.3f;
            n.HubRadius = n.Piazza ? MathF.Max(5.5f, hw + 2.6f) : n.Destination ? hw + 2.2f : hw + 0.95f;
        }

        _segs.Clear();
        _hash.Clear();
        foreach (var e in Edges)
            for (int k = 0; k + 1 < e.Points.Count; k++)
                _segs.Add(new Seg(e.Points[k], e.Points[k + 1], e.HalfWidth, e, k, null));
        foreach (var n in Nodes)
            if (n.IsHub) _segs.Add(new Seg(n.Pos, n.Pos, n.HubRadius, null, 0, n));

        for (int s = 0; s < _segs.Count; s++)
        {
            var g = _segs[s];
            float r = Reach + g.HalfWidth;
            int i0 = (int)MathF.Floor((MathF.Min(g.A.X, g.B.X) - r) / HashCell), i1 = (int)MathF.Floor((MathF.Max(g.A.X, g.B.X) + r) / HashCell);
            int j0 = (int)MathF.Floor((MathF.Min(g.A.Y, g.B.Y) - r) / HashCell), j1 = (int)MathF.Floor((MathF.Max(g.A.Y, g.B.Y) + r) / HashCell);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    if (!_hash.TryGetValue(Key(i, j), out var list)) _hash[Key(i, j)] = list = new();
                    list.Add(s);
                }
        }

        // Tratti e nodi per pezzo di territorio (con un margine: case e marciapiedi sporgono dalla strada)
        _byZone.Clear();
        _nodesByZone.Clear();
        const float C = Terrain.ChunkSize, M = 12f;
        foreach (var e in Edges)
        {
            float x0 = e.Points.Min(p => p.X) - M, x1 = e.Points.Max(p => p.X) + M;
            float z0 = e.Points.Min(p => p.Y) - M, z1 = e.Points.Max(p => p.Y) + M;
            for (int i = (int)MathF.Round(x0 / C); i <= (int)MathF.Round(x1 / C); i++)
                for (int j = (int)MathF.Round(z0 / C); j <= (int)MathF.Round(z1 / C); j++)
                {
                    // Il tratto passa davvero vicino a questo pezzo?
                    var box = new Vector4(i * C - Zone.Half - M, j * C - Zone.Half - M, i * C + Zone.Half + M, j * C + Zone.Half + M);
                    if (!e.Points.Any(p => p.X >= box.X && p.X <= box.Z && p.Y >= box.Y && p.Y <= box.W)) continue;
                    if (!_byZone.TryGetValue((i, j), out var list)) _byZone[(i, j)] = list = new();
                    list.Add(e);
                }
        }
        foreach (var n in Nodes)
        {
            var cell = ((int)MathF.Round(n.Pos.X / C), (int)MathF.Round(n.Pos.Y / C));
            if (!_nodesByZone.TryGetValue(cell, out var list)) _nodesByZone[cell] = list = new();
            list.Add(n);
        }
    }

    /// <summary>I tratti di strada che passano dentro (o appena attorno) al pezzo (i, j).</summary>
    public List<RoadEdge> EdgesNear(int i, int j) => _byZone.GetValueOrDefault((i, j), NoEdges);

    /// <summary>I nodi che stanno nel pezzo (i, j).</summary>
    public List<RoadNode> NodesIn(int i, int j) => _nodesByZone.GetValueOrDefault((i, j), NoNodes);

    private static float SegDistance(Vector2 p, Vector2 a, Vector2 b, out float t)
    {
        Vector2 ab = b - a;
        float len2 = ab.LengthSquared();
        t = len2 > 1e-8f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector2.Distance(p, a + ab * t);
    }

    /// <summary>
    /// Distanza dal bordo della strada più vicina, riportata alla larghezza "di riferimento" <see cref="Zone.PathHalf"/>:
    /// vale PathHalf esattamente sul bordo della carreggiata (qualunque sia la sua larghezza), meno dentro, di più fuori.
    /// Oltre <see cref="Reach"/> dalla strada restituisce un valore grande.
    /// </summary>
    public float Distance(float x, float z)
    {
        var p = new Vector2(x, z);
        float best = Far;
        foreach (int s in CellSegs(x, z))
        {
            var g = _segs[s];
            float d = SegDistance(p, g.A, g.B, out _) - g.HalfWidth + Zone.PathHalf;
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Il punto di strada più vicino entro <paramref name="max"/> dalla mezzeria (solo tratti, non slarghi).</summary>
    public bool Nearest(Vector2 p, float max, out RoadHit hit) => NearestAligned(p, Vector2.Zero, max, out hit);

    /// <summary>
    /// Come <see cref="Nearest"/>, ma tra i tratti vicini preferisce quello allineato a <paramref name="travel"/>
    /// (agli incroci l'auto sta "sulla strada che percorre", non su quella che taglia).
    /// </summary>
    public bool NearestAligned(Vector2 p, Vector2 travel, float max, out RoadHit hit)
    {
        hit = default;
        float bestScore = float.MaxValue;
        bool found = false;
        foreach (int s in CellSegs(p.X, p.Y))
        {
            var g = _segs[s];
            if (g.Edge == null) continue;
            float d = SegDistance(p, g.A, g.B, out float t);
            if (d > max) continue;
            float score = d;
            if (travel != Vector2.Zero)
            {
                Vector2 dir = g.B - g.A;
                if (dir.LengthSquared() > 1e-8f) score += 2.5f * (1f - MathF.Abs(Vector2.Dot(Vector2.Normalize(dir), travel)));
            }
            if (score >= bestScore) continue;
            bestScore = score;
            var e = g.Edge;
            float sAlong = e.CumAt(g.Index) + (e.CumAt(g.Index + 1) - e.CumAt(g.Index)) * t;
            hit = new RoadHit(e, sAlong, Vector2.Lerp(g.A, g.B, t), d);
            found = true;
        }
        return found;
    }

    /// <summary>Lo slargo (incrocio, fine strada, piazza) più vicino entro il suo raggio più <paramref name="extra"/>.</summary>
    public RoadNode? HubAt(Vector2 p, float extra)
    {
        RoadNode? best = null;
        float bestD = float.MaxValue;
        foreach (int s in CellSegs(p.X, p.Y))
        {
            var g = _segs[s];
            if (g.Node == null) continue;
            float d = Vector2.Distance(p, g.Node.Pos);
            if (d < g.Node.HubRadius + extra && d < bestD) { bestD = d; best = g.Node; }
        }
        return best;
    }

    /// <summary>Il fondo della strada nel punto (null fuori strada): per la polvere sollevata dalle gomme.</summary>
    public RoadKind? KindAt(Vector2 p)
    {
        RoadKind? kind = null;
        float best = float.MaxValue;
        foreach (int s in CellSegs(p.X, p.Y))
        {
            var g = _segs[s];
            float d = SegDistance(p, g.A, g.B, out _) - g.HalfWidth;
            if (d > 0f || d >= best) continue;
            best = d;
            kind = g.Edge?.Kind ?? (g.Node!.Edges.Any(e => e.Paved) ? RoadKind.Street : RoadKind.Dirt);
        }
        return kind;
    }

    // ------------------------------------------------------------------ centri abitati e boschi

    /// <summary>
    /// Quanto il punto è costruito: 1 nel cuore della città, ~0.45 nella sua periferia, ~0.6 al centro dei paesi,
    /// 0 in campagna. I bordi sono irregolari (deformati dal rumore), non cerchi perfetti.
    /// </summary>
    public float Urban(float x, float z)
    {
        float best = 0f;
        foreach (var s in Settlements)
        {
            float dx = x - s.Pos.X, dz = z - s.Pos.Y;
            float d2 = dx * dx + dz * dz, reach = s.Radius * 1.35f;
            if (d2 > reach * reach) continue;
            float d = MathF.Sqrt(d2);
            float a = MathF.Atan2(dz, dx);
            float wobble = 1f + 0.18f * (Terrain.ValueNoise(a * 1.6f + 10f, s.Pos.X * 0.01f, 61) * 2f - 1f)
                              + 0.06f * MathF.Sin(a * 5f + s.Pos.Y);
            float r = d / (s.Radius * wobble);
            float u = s.City
                ? Noise.Lerp(1f, 0.42f, Noise.Smooth(0.12f, 0.75f, r)) * (1f - Noise.Smooth(0.85f, 1.15f, r))
                : Noise.Lerp(0.62f, 0.42f, Noise.Smooth(0.1f, 0.7f, r)) * (1f - Noise.Smooth(0.75f, 1.1f, r));
            best = MathF.Max(best, u);
        }
        return best;
    }

    /// <summary>Il centro abitato in cui sta il punto (entro il suo raggio), se c'è.</summary>
    public Settlement? SettlementAt(float x, float z)
    {
        foreach (var s in Settlements)
            if (Vector2.Distance(new Vector2(x, z), s.Pos) < s.Radius * 1.05f) return s;
        return null;
    }

    /// <summary>
    /// Quanto il punto è "bosco fitto" (0..1): macchie sparse in campagna, più dense nelle fasce tra un paese e
    /// l'altro (così i paesi non si vedono tra loro). Mai dentro i centri abitati.
    /// </summary>
    public float Forest(float x, float z)
    {
        float patches = Noise.Smooth(0.52f, 0.7f, Terrain.ValueNoise(x / 62f, z / 62f, 71));
        float band = 0f;
        var p = new Vector2(x, z);
        var villages = _villages ??= Settlements.Where(s => !s.City).ToArray();
        for (int i = 0; i < villages.Length; i++)
        {
            // Fascia sulla perpendicolare a metà strada tra due paesi vicini (in ordine attorno alla città)
            var a = villages[i].Pos;
            var b = villages[(i + 1) % villages.Length].Pos;
            Vector2 mid = (a + b) * 0.5f, ab = b - a;
            float len = ab.Length();
            if (len < 1f) continue;
            Vector2 dir = ab / len;
            float along = MathF.Abs(Vector2.Dot(p - mid, dir));
            float across = MathF.Abs(Vector2.Dot(p - mid, new Vector2(-dir.Y, dir.X)));
            float w = Noise.Smooth(len * 0.24f, len * 0.08f, along) * Noise.Smooth(len * 0.55f, len * 0.2f, across);
            band = MathF.Max(band, w * (0.55f + 0.45f * Terrain.ValueNoise(x / 23f, z / 23f, 72)));
        }
        float f = MathF.Max(patches, band);
        f *= 1f - Noise.Smooth(0.05f, 0.25f, Urban(x, z));
        foreach (var farm in Farms)
            if (Vector2.DistanceSquared(farm.Yard, p) < 22f * 22f) return 0f; // attorno alle cascine si coltiva
        return f;
    }
}
