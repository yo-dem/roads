using System.Numerics;

namespace Roads.World;

/// <summary>Un passaggio a livello: dove il binario taglia una strada.</summary>
public sealed class RailCrossing
{
    /// <summary>Distanza lungo l'anello del binario.</summary>
    public float S;
    public Vector2 Point;
    /// <summary>Direzione della strada nel punto (normalizzata).</summary>
    public Vector2 RoadDir;
    public float RoadHalfWidth;
    /// <summary>Quanto lontano dal binario, lungo la strada, stanno i pali con le sbarre.</summary>
    public float Setback;
}

/// <summary>Una stazione: la banchina sta da un lato del binario, centrata in S.</summary>
public sealed class RailStation
{
    public float S;
    /// <summary>+1 banchina a destra del verso di marcia (s crescente), -1 a sinistra.</summary>
    public float Side;
    /// <summary>Mezza lunghezza della banchina (le fermate l'hanno più corta delle stazioni).</summary>
    public float HalfLength = 10.5f;
    public required string Name;
}

/// <summary>
/// La ferrovia della città: un anello chiuso attorno al centro, a metà strada tra il centro e la periferia, che evita
/// incroci, fiumi, costa e monumenti e curva dolcemente. Taglia le vie con passaggi a livello e ha quattro stazioni.
/// Generata all'avvio dopo le strade; poi è di sola lettura (sicura dai thread che generano il territorio).
/// </summary>
public sealed class Railway
{
    public static Railway Current { get; set; } = new();

    public const float Gauge = 0.55f;          // le rotaie stanno a ±Gauge dalla mezzeria
    public const float BallastHalf = 1.25f;    // mezza larghezza della massicciata
    public const float BallastTop = 0.18f;     // sopra il terreno
    public const float RailTop = 0.32f;        // piano del ferro (dove poggiano le ruote)
    public const float PlatformWidth = 1.8f;
    public const float PlatformHeight = 0.45f;

    /// <summary>Anello chiuso: il segmento i va da Points[i] a Points[(i+1) % n].</summary>
    public List<Vector2> Points { get; } = new();
    public List<RailCrossing> Crossings { get; } = new();
    public List<RailStation> Stations { get; } = new();
    private float[] _cum = [];
    public float Length { get; private set; }
    public bool Exists => Points.Count >= 3;

    // ------------------------------------------------------------------ geometria lungo l'anello

    public float Wrap(float s) => Length > 0f ? ((s % Length) + Length) % Length : 0f;

    private int Segment(float s)
    {
        int lo = 0, hi = Points.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_cum[mid] <= s) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    public Vector2 At(float s)
    {
        s = Wrap(s);
        int i = Segment(s);
        Vector2 a = Points[i], b = Points[(i + 1) % Points.Count];
        float len = _cum[i + 1] - _cum[i];
        return Vector2.Lerp(a, b, len > 1e-5f ? (s - _cum[i]) / len : 0f);
    }

    public Vector2 Tangent(float s)
    {
        Vector2 d = At(s + 0.5f) - At(s - 0.5f);
        return d.LengthSquared() > 1e-8f ? Vector2.Normalize(d) : Vector2.UnitX;
    }

    /// <summary>Quanto manca, andando avanti lungo l'anello, da <paramref name="from"/> a <paramref name="to"/>.</summary>
    public float Ahead(float from, float to) => Wrap(to - from);

    // ------------------------------------------------------------------ interrogazioni (indice spaziale)

    private const float HashCell = 8f, Reach = 8f, Far = 1000f;
    private readonly Dictionary<long, List<int>> _hash = new();
    private static readonly List<int> NoSegs = new();
    private static long Key(int i, int j) => ((long)i << 32) ^ (uint)j;

    private void Index()
    {
        _cum = new float[Points.Count + 1];
        for (int i = 0; i < Points.Count; i++)
            _cum[i + 1] = _cum[i] + Vector2.Distance(Points[i], Points[(i + 1) % Points.Count]);
        Length = _cum[^1];
        _hash.Clear();
        for (int s = 0; s < Points.Count; s++)
        {
            Vector2 a = Points[s], b = Points[(s + 1) % Points.Count];
            int i0 = (int)MathF.Floor((MathF.Min(a.X, b.X) - Reach) / HashCell), i1 = (int)MathF.Floor((MathF.Max(a.X, b.X) + Reach) / HashCell);
            int j0 = (int)MathF.Floor((MathF.Min(a.Y, b.Y) - Reach) / HashCell), j1 = (int)MathF.Floor((MathF.Max(a.Y, b.Y) + Reach) / HashCell);
            for (int i = i0; i <= i1; i++)
                for (int j = j0; j <= j1; j++)
                {
                    if (!_hash.TryGetValue(Key(i, j), out var list)) _hash[Key(i, j)] = list = new();
                    list.Add(s);
                }
        }
    }

    /// <summary>Distanza dalla mezzeria del binario (grande se più lontano di qualche unità) e distanza lungo l'anello.</summary>
    public float Distance(float x, float z, out float along)
    {
        along = 0f;
        if (!Exists) return Far;
        var p = new Vector2(x, z);
        float best = Far;
        foreach (int s in _hash.GetValueOrDefault(Key((int)MathF.Floor(x / HashCell), (int)MathF.Floor(z / HashCell)), NoSegs))
        {
            Vector2 a = Points[s], ab = Points[(s + 1) % Points.Count] - a;
            float len2 = ab.LengthSquared();
            float t = len2 > 1e-8f ? Math.Clamp(Vector2.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
            float d = Vector2.Distance(p, a + ab * t);
            if (d < best)
            {
                best = d;
                along = _cum[s] + (_cum[s + 1] - _cum[s]) * t;
            }
        }
        return best;
    }

    public float Distance(float x, float z) => Distance(x, z, out _);

    /// <summary>La stazione la cui banchina è vicina al punto lungo l'anello (entro <paramref name="extra"/> oltre la sua lunghezza).</summary>
    public RailStation? StationNear(float along, float extra)
    {
        foreach (var st in Stations)
        {
            float d = MathF.Min(Ahead(st.S, along), Ahead(along, st.S));
            if (d < st.HalfLength + extra) return st;
        }
        return null;
    }

    /// <summary>
    /// Il punto è nella fascia della ferrovia (massicciata, margine, banchine e pensiline delle stazioni)? Lì non si
    /// costruisce e non crescono alberi.
    /// </summary>
    public bool Keepout(float x, float z, float margin = 0f)
    {
        float d = Distance(x, z, out float along);
        if (d < BallastHalf + 1.4f + margin) return true;
        if (d > 8f + margin) return false;
        return StationNear(along, 4f) != null && d < BallastHalf + PlatformWidth + 4.5f + margin;
    }

    // ------------------------------------------------------------------ generazione

    /// <summary>Traccia l'anello attorno alla città, trova i passaggi a livello e mette le stazioni.</summary>
    public static Railway Build(RoadNetwork net, GameMap map)
    {
        var rail = new Railway();
        var city = net.Settlements.FirstOrDefault(s => s.City);
        if (city == null) return rail;

        // Per ogni angolo si sceglie un raggio: programmazione dinamica su (angolo, raggio) con un costo che
        // tiene lontani incroci, fiumi, costa e monumenti e una penalità per i salti di raggio (curve dolci).
        const int A = 180;
        float R = city.Radius;
        var radii = new List<float>();
        for (float r = R * 0.36f; r <= R * 0.7f; r += 2.5f) radii.Add(r);
        int K = radii.Count;
        float phase = Noise.Hash01((int)city.Pos.X, (int)city.Pos.Y, 17) * MathF.Tau;
        Vector2 PointAt(int a, int k)
        {
            float ang = MathF.Tau * a / A;
            float wobble = 1f + 0.05f * MathF.Sin(3f * ang + phase);
            return city.Pos + new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * radii[k] * wobble;
        }

        var nodes = net.Nodes.Where(n => n.Degree > 0 && Vector2.Distance(n.Pos, city.Pos) < R * 0.9f).ToList();
        var cost = new float[A, K];
        for (int a = 0; a < A; a++)
            for (int k = 0; k < K; k++)
            {
                var p = PointAt(a, k);
                float c = MathF.Abs(radii[k] - R * 0.55f) * 0.3f;
                if (map.Sample(p.X, p.Y).Land < 0.95f) c += 1e5f;
                if (Terrain.RiverDistance(p.X, p.Y) < Zone.RiverHalf + 6f) c += 1e5f;
                foreach (var l in map.Landmarks)
                    if (Vector2.Distance(p, l.Pos) < l.Radius + 7f) c += 1e5f;
                foreach (var n in nodes)
                {
                    float d = Vector2.Distance(p, n.Pos);
                    if (d < n.HubRadius + 6f) c += 3000f + 200f * (n.HubRadius + 6f - d);
                }
                // Correre lungo una strada no: attraversarla sì (il costo spinge a tagliarla in fretta)
                if (net.Distance(p.X, p.Y) < Zone.PathHalf + 2f) c += 40f;
                cost[a, k] = c;
            }

        float bestTotal = float.MaxValue;
        int[]? bestPath = null;
        for (int start = 0; start < K; start += 2)
        {
            var dp = new float[A, K];
            var from = new int[A, K];
            for (int k = 0; k < K; k++) dp[0, k] = k == start ? cost[0, k] : float.MaxValue;
            for (int a = 1; a < A; a++)
                for (int k = 0; k < K; k++)
                {
                    float best = float.MaxValue;
                    int arg = -1;
                    for (int j = Math.Max(0, k - 1); j <= Math.Min(K - 1, k + 1); j++)
                    {
                        if (dp[a - 1, j] == float.MaxValue) continue;
                        float v = dp[a - 1, j] + 25f * MathF.Abs(j - k);
                        if (v < best) { best = v; arg = j; }
                    }
                    dp[a, k] = best == float.MaxValue ? float.MaxValue : best + cost[a, k];
                    from[a, k] = arg;
                }
            // L'anello si chiude: l'ultimo raggio deve stare accanto al primo
            for (int k = Math.Max(0, start - 1); k <= Math.Min(K - 1, start + 1); k++)
            {
                if (dp[A - 1, k] == float.MaxValue) continue;
                float total = dp[A - 1, k] + 25f * MathF.Abs(k - start);
                if (total >= bestTotal) continue;
                bestTotal = total;
                bestPath = new int[A];
                int cur = k;
                for (int a = A - 1; a >= 0; a--)
                {
                    bestPath[a] = cur;
                    if (a > 0) cur = from[a, cur];
                }
            }
        }
        if (bestPath == null || bestTotal > 1e5f)
        {
            Console.WriteLine("Ferrovia: nessun tracciato possibile attorno alla città");
            return rail;
        }

        // Spezzata chiusa, arrotondata (Chaikin) e ricampionata ogni unità
        var pts = Enumerable.Range(0, A).Select(a => PointAt(a, bestPath[a])).ToList();
        for (int it = 0; it < 3; it++)
        {
            var next = new List<Vector2>(pts.Count * 2);
            for (int i = 0; i < pts.Count; i++)
            {
                Vector2 p = pts[i], q = pts[(i + 1) % pts.Count];
                next.Add(Vector2.Lerp(p, q, 0.25f));
                next.Add(Vector2.Lerp(p, q, 0.75f));
            }
            pts = next;
        }
        rail.Points.AddRange(pts);
        rail.Index();
        int n2 = (int)MathF.Round(rail.Length);
        var even = Enumerable.Range(0, n2).Select(i => rail.At(i * rail.Length / n2)).ToList();
        rail.Points.Clear();
        rail.Points.AddRange(even);
        rail.Index();

        rail.FindCrossings(net);
        rail.PlaceStations(net, city);
        Console.WriteLine($"Ferrovia: anello di {rail.Length:F0} unità, {rail.Crossings.Count} passaggi a livello, "
                          + $"{rail.Stations.Count} stazioni");
        foreach (var st in rail.Stations)
            Console.WriteLine($"  {st.Name} a {rail.At(st.S).X:F0}, {rail.At(st.S).Y:F0}");        return rail;
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

    private void FindCrossings(RoadNetwork net)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Vector2 p = Points[i], q = Points[(i + 1) % Points.Count];
            foreach (var e in net.Edges)
                for (int m = 0; m + 1 < e.Count; m++)
                {
                    Vector2 a = e.Points[m], b = e.Points[m + 1];
                    if (MathF.Max(a.X, b.X) < MathF.Min(p.X, q.X) || MathF.Min(a.X, b.X) > MathF.Max(p.X, q.X)
                        || MathF.Max(a.Y, b.Y) < MathF.Min(p.Y, q.Y) || MathF.Min(a.Y, b.Y) > MathF.Max(p.Y, q.Y)) continue;
                    if (!SegCross(p, q, a, b, out float t, out float u)) continue;
                    float s = _cum[i] + (_cum[i + 1] - _cum[i]) * t;
                    if (Crossings.Any(c => MathF.Min(Ahead(c.S, s), Ahead(s, c.S)) < 3f)) continue;
                    float sRoad = e.CumAt(m) + Vector2.Distance(a, b) * u;
                    Vector2 roadDir = e.Tangent(sRoad), railDir = Tangent(s);
                    float sin = MathF.Max(MathF.Abs(roadDir.X * railDir.Y - roadDir.Y * railDir.X), 0.35f);
                    Crossings.Add(new RailCrossing
                    {
                        S = s, Point = Vector2.Lerp(a, b, u), RoadDir = roadDir, RoadHalfWidth = e.HalfWidth,
                        Setback = (BallastHalf + 0.6f) / sin + 0.6f,
                    });
                }
        }
        Crossings.Sort((x, y) => x.S.CompareTo(y.S));
    }

    /// <summary>
    /// Quattro stazioni a un quarto di giro l'una dall'altra; ciascuna si sposta un po' finché la banchina non tocca
    /// strade né passaggi a livello. Dove il reticolo è troppo fitto per una stazione intera si fa una fermata, con la
    /// banchina corta. Il nome dice dove sta rispetto al centro della città.
    /// </summary>
    private void PlaceStations(RoadNetwork net, Settlement city)
    {
        for (int q = 0; q < 4; q++)
        {
            float s0 = Length * q / 4f;
            RailStation? found = null;
            foreach (float half in new[] { 10.5f, 6.5f })
            {
                for (int k = 0; k < 80 && found == null; k++)
                {
                    float s = s0 + (k % 2 == 0 ? 1 : -1) * (k / 2) * 2f;
                    if (Crossings.Any(c => MathF.Min(Ahead(c.S, s), Ahead(s, c.S)) < half + 1.8f)) continue;
                    if (Stations.Any(st => MathF.Min(Ahead(st.S, s), Ahead(s, st.S)) < Length / 8f)) continue;
                    foreach (float side in new[] { 1f, -1f })
                    {
                        bool ok = true;
                        for (float d = -half - 2f; d <= half + 2f && ok; d += 2f)
                        {
                            Vector2 c = At(s + d), right = new(-Tangent(s + d).Y, Tangent(s + d).X);
                            foreach (float off in new[] { BallastHalf + 0.5f, BallastHalf + PlatformWidth + 1.2f })
                            {
                                var p = c + right * side * off;
                                if (net.Distance(p.X, p.Y) < Zone.PathHalf + 1.2f) ok = false;
                            }
                        }
                        if (!ok) continue;
                        Vector2 rel = At(s) - city.Pos;
                        string where = MathF.Abs(rel.X) > MathF.Abs(rel.Y)
                            ? (rel.X > 0f ? "Est" : "Ovest")
                            : (rel.Y > 0f ? "Sud" : "Nord");
                        found = new RailStation
                        {
                            S = Wrap(s), Side = side, HalfLength = half,
                            Name = (half > 8f ? "Stazione di Roda " : "Fermata di Roda ") + where,
                        };
                        break;
                    }
                }
                if (found != null) break;
            }
            if (found != null) Stations.Add(found);
        }
        Stations.Sort((x, y) => x.S.CompareTo(y.S));
    }
}
