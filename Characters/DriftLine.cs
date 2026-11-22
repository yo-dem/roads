using System.Numerics;
using Roads.World;

namespace Roads.Characters;

/// <summary>
/// Traiettoria lungo la propria corsia per l'inversione a U (spazio + indietro): l'auto ci scivola sopra mentre la
/// carrozzeria gira di 180°. Agli incroci prosegue dritta; in fondo a un vicolo cieco si ferma.
/// Le strade sono quelle della rete (<see cref="RoadNetwork"/>).
/// </summary>
public sealed class DriftLine
{
    private const float Step = 0.5f;          // passo dei punti lungo la linea
    private const float MaxOffRoad = 2.8f;    // oltre questa distanza dalla mezzeria non si è "in strada"
    private const float DeadEndOverrun = 1.2f; // in fondo a un vicolo cieco si può scivolare fin poco oltre il centro dello slargo
    private const float StraightDot = 0.8f;   // un'uscita è "dritto" se devia meno di ~37°
    private const float UTurnJunctionMargin = 3f; // niente inversione guidata se un incrocio è più vicino di scivolata + questo
    private const float UTurnShortest = 2.6f;    // sotto questo spazio di scivolata niente inversione guidata

    private readonly List<Vector3> _points = new();
    private readonly List<float> _cum = new();

    public float Length => _cum.Count > 0 ? _cum[^1] : 0f;

    private static RoadNetwork Net => RoadNetwork.Current;

    private static Vector3 To3(Vector2 p) => new(p.X, 0f, p.Y);

    private void Add(Vector3 p)
    {
        if (_points.Count > 0 && Vector3.Distance(_points[^1], p) < 1e-3f) return;
        _cum.Add(_points.Count == 0 ? 0f : _cum[^1] + Vector3.Distance(_points[^1], p));
        _points.Add(p);
    }

    /// <summary>
    /// Un punto su un tratto, il verso di marcia (+1 da A verso B, -1 al contrario) e la corsia: scostamento del suo
    /// centro dalla mezzeria verso la destra di chi guida.
    /// </summary>
    private readonly record struct Cursor(RoadEdge Edge, float S, float Dir, float Lane = 0f)
    {
        public RoadNode Ahead => Dir > 0f ? Edge.B : Edge.A;
        public float ToAhead => Dir > 0f ? Edge.Length - S : S;
        public Vector2 Heading => Edge.Tangent(S) * Dir;
        /// <summary>Centro della corsia alla distanza <paramref name="s"/> lungo il tratto.</summary>
        public Vector2 PointAt(float s) => Edge.At(s) + RightOf(Edge.Tangent(s) * Dir) * Lane;
    }

    /// <summary>La corsia del tratto (per il verso dato) più vicina allo scostamento <paramref name="want"/>.</summary>
    private static float NearestLane(RoadEdge e, float dir, float want) =>
        // Sul viale ogni metà carreggiata è una strada a sé: si resta dalla parte dello spartitraffico in cui si è,
        // qualunque sia il verso (dopo un'inversione a U non si viene trascinati di là dall'aiuola)
        (e.Layout == RoadLayout.Avenue ? e.Lanes.Select(l => l.Offset * dir) : e.TravelLanes(dir))
            .MinBy(o => MathF.Abs(o - want));

    /// <summary>Destra della direzione d sul piano (x est, z sud; vista dall'alto col nord in alto).</summary>
    private static Vector2 RightOf(Vector2 d) => new(-d.Y, d.X);

    /// <summary>
    /// Le uscite da un nodo per chi ci arriva da <paramref name="from"/> viaggiando in direzione <paramref name="travel"/>:
    /// tratto, direzione con cui lascia il nodo e quanto punta dritto / a destra.
    /// </summary>
    private static IEnumerable<(RoadEdge Edge, Vector2 Dir, float Ahead, float Right)> Exits(RoadNode node, RoadEdge from, Vector2 travel)
    {
        foreach (var e in node.Edges)
        {
            if (e == from) continue;
            Vector2 d = e.LeaveDir(node, 5f);
            yield return (e, d, Vector2.Dot(d, travel), Vector2.Dot(d, RightOf(travel)));
        }
    }

    /// <summary>
    /// Cammina lungo la rete da <paramref name="c"/> per <paramref name="length"/> aggiungendo i punti: ai nodi dove la
    /// via prosegue soltanto (2 tratti) va avanti, agli incroci prosegue dritto se si può. Restituisce dove si è fermata
    /// e se si è fermata a un vicolo cieco o a un incrocio senza uscita dritta.
    /// </summary>
    private float Follow(Cursor c, float length, bool stopAtJunction, out Cursor end, out bool blocked)
    {
        float done = 0f;
        blocked = false;
        while (true)
        {
            float left = length - done;
            if (c.ToAhead >= left)
            {
                for (float t = 0f; t < left; t += Step) Add(To3(c.PointAt(c.S + c.Dir * t)));
                c = c with { S = c.S + c.Dir * left };
                Add(To3(c.PointAt(c.S)));
                end = c;
                return length;
            }
            for (float t = 0f; t < c.ToAhead; t += Step) Add(To3(c.PointAt(c.S + c.Dir * t)));
            done += c.ToAhead;
            var node = c.Ahead;
            Add(To3(c.PointAt(c.Dir > 0f ? c.Edge.Length : 0f)));
            Vector2 travel = c.Edge.Tangent(c.Dir > 0f ? c.Edge.Length : 0f) * c.Dir;
            RoadEdge? next = null;
            if (node.Degree == 2 && !node.Piazza) next = node.Edges.First(e => e != c.Edge);
            else if (!stopAtJunction)
            {
                var straight = Exits(node, c.Edge, travel).Where(x => x.Ahead > StraightDot).OrderByDescending(x => x.Ahead).FirstOrDefault();
                next = straight.Edge;
            }
            if (next == null)
            {
                end = new Cursor(c.Edge, c.Dir > 0f ? c.Edge.Length : 0f, c.Dir);
                blocked = node.Degree == 1 || !stopAtJunction;
                return done;
            }
            float nd = next.A == node ? 1f : -1f;
            c = new Cursor(next, next.A == node ? 0f : next.Length, nd, NearestLane(next, nd, c.Lane));
        }
    }

    /// <summary>
    /// Su quale strada è l'auto? (Si preferisce quella allineata alla direzione di marcia.) False se non è in strada.
    /// </summary>
    private static bool Locate(Vector3 pos, Vector3 travel, out Cursor cursor)
    {
        cursor = default;
        var t = new Vector2(travel.X, travel.Z);
        if (t.LengthSquared() < 1e-8f) return false;
        t = Vector2.Normalize(t);
        var p = new Vector2(pos.X, pos.Z);
        if (!Net.NearestAligned(p, t, MaxOffRoad + 2f, out var hit)) return false;
        if (hit.Distance > hit.Edge.HalfWidth + MaxOffRoad - Zone.PathHalf) return false;
        float dir = Vector2.Dot(hit.Edge.Tangent(hit.S), t) >= 0f ? 1f : -1f;
        // Corsia: tra quelle del proprio verso, la più vicina a dove sta l'auto (sui viali si cambia corsia liberamente;
        // sui doppi sensi si torna a destra)
        float side = Vector2.Dot(p - hit.Point, RightOf(hit.Edge.Tangent(hit.S) * dir));
        cursor = new Cursor(hit.Edge, hit.S, dir, NearestLane(hit.Edge, dir, side));
        return true;
    }

    /// <summary>
    /// Linea lungo la mezzeria della strada su cui si viaggia, per <paramref name="length"/> unità davanti
    /// (per l'inversione a U: l'auto scivola lungo la corsia mentre si gira). Null se non si è in strada.
    /// </summary>
    public static DriftLine? AlongRoad(Vector3 pos, Vector3 travel, float length, bool anywhere = false)
    {
        if (!Locate(pos, travel, out var c)) return null;
        if (anywhere)
        {
            // Inversione chiesta col suo comando (spazio + indietro): si fa sempre, anche vicino o dentro un incrocio,
            // seguendo la propria corsia (agli incroci si va dritti; in fondo a un vicolo ci si ferma)
            var open = new DriftLine();
            open.Follow(c, length, stopAtJunction: false, out var stop, out bool closed);
            if (closed) open.Add(To3(stop.Edge.At(stop.S) + stop.Edge.Tangent(stop.S) * stop.Dir * DeadEndOverrun));
            return open._points.Count >= 2 ? open : null;
        }
        // Inversione guidata solo dove è ovvia: lontano dagli incroci, né davanti né sotto l'auto (lì il freno a mano
        // potrebbe voler dire "svolta": derapata libera). Vale su ogni strada, viali e sensi unici compresi.
        if (NearHub(pos)) return null;
        var probe = new DriftLine();
        bool avenue = c.Edge.Layout == RoadLayout.Avenue;
        float look = length + UTurnJunctionMargin + (avenue ? 15f : 0f); // sul viale si guarda fino all'incrocio
        float free = probe.Follow(c, look, stopAtJunction: true, out var atHub, out bool deadEnd);
        if (free < look - 0.01f && !deadEnd)
        {
            if (!avenue || atHub.Edge.Layout != RoadLayout.Avenue)
            {
                // Incrocio o piazza davanti: lì si gira, non si inverte (derapata libera)
                if (free < length + UTurnJunctionMargin) return null;
            }
            else
            {
                // Sul viale agli incroci non c'è l'aiuto alla svolta: invece di una derapata libera (che finisce di
                // traverso o oltre lo spartitraffico) l'inversione si accorcia quanto serve per finire prima
                // dell'imbocco dell'incrocio, nella propria metà carreggiata
                float stop = free - atHub.Ahead.TrimFor(atHub.Edge) - 0.6f;
                if (stop < UTurnShortest) return null;
                length = MathF.Min(length, stop);
            }
        }
        var line = new DriftLine();
        float went = line.Follow(c, length, stopAtJunction: false, out var end, out bool blocked);
        if (blocked)
        {
            // In fondo a un vicolo cieco (o a un incrocio senza uscita dritta) la scivolata va appena oltre il centro
            Vector2 dir = end.Edge.Tangent(end.S) * end.Dir;
            line.Add(To3(end.Edge.At(end.S) + dir * DeadEndOverrun));
            if (went < 0.5f) return null; // oltre la fine della strada: niente inversione guidata
        }
        if (line._points.Count < 2) return null;
        // Sul viale la manovra resta nella propria metà carreggiata (lo spartitraffico è un bordo, non si scavalca)
        return line;
    }

    /// <summary>Linea dritta da <paramref name="pos"/> nella direzione di marcia (inversione fuori strada).</summary>
    public static DriftLine Straight(Vector3 pos, Vector3 travel, float length)
    {
        var line = new DriftLine();
        var d = Vector3.Normalize(new Vector3(travel.X, 0f, travel.Z));
        var p = new Vector3(pos.X, 0f, pos.Z);
        for (float t = 0f; t <= length + 2f; t += Step) line.Add(p + d * t);
        return line;
    }

    private static bool NearHub(Vector3 p)
    {
        var hub = Net.HubAt(new Vector2(p.X, p.Z), 1.5f);
        return hub != null && (hub.Degree != 2 || hub.Piazza);
    }

    /// <summary>Punto della linea alla lunghezza <paramref name="u"/> (oltre la fine prosegue dritto).</summary>
    public Vector3 At(float u)
    {
        int i = Segment(u);
        float span = _cum[i + 1] - _cum[i];
        float f = span > 1e-5f ? (u - _cum[i]) / span : 0f;
        return Vector3.Lerp(_points[i], _points[i + 1], f);
    }

    /// <summary>Direzione (angolo yaw) della linea alla lunghezza <paramref name="u"/>.</summary>
    public float Heading(float u)
    {
        int i = Segment(u);
        Vector3 d = _points[i + 1] - _points[i];
        return MathF.Atan2(d.X, d.Z);
    }

    /// <summary>Lunghezza lungo la linea del punto più vicino a <paramref name="p"/>.</summary>
    public float Project(Vector3 p)
    {
        float best = float.MaxValue, bestU = 0f;
        for (int i = 0; i + 1 < _points.Count; i++)
        {
            Vector3 a = _points[i], b = _points[i + 1], ab = b - a;
            float len2 = ab.X * ab.X + ab.Z * ab.Z;
            float t = len2 > 1e-6f ? Math.Clamp(((p.X - a.X) * ab.X + (p.Z - a.Z) * ab.Z) / len2, 0f, 1f) : 0f;
            float dx = p.X - (a.X + ab.X * t), dz = p.Z - (a.Z + ab.Z * t);
            float d = dx * dx + dz * dz;
            if (d < best) { best = d; bestU = _cum[i] + (_cum[i + 1] - _cum[i]) * t; }
        }
        return bestU;
    }

    private int Segment(float u)
    {
        int lo = 0, hi = _cum.Count - 2;
        if (u <= 0f) return 0;
        if (u >= _cum[^1]) return hi;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_cum[mid] <= u) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
