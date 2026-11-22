using System.Numerics;
using Roads.World;

namespace Roads.Characters;

/// <summary>
/// Traiettoria della derapata assistita: la mezzeria della strada su cui si viaggia fino al prossimo incrocio,
/// una curva morbida dentro lo slargo verso la strada scelta (a sinistra, a destra o dritto), poi la mezzeria
/// della nuova strada. L'auto la segue come su una rotaia: in derapata resta sempre in corsia.
/// Le strade sono quelle della rete (<see cref="RoadNetwork"/>): agli incroci le uscite hanno angoli qualsiasi.
/// </summary>
public sealed class DriftLine
{
    private const float Step = 0.5f;          // passo dei punti lungo la linea
    private const float TurnWindow = 15f;     // l'aiuto alla derapata vede un incrocio entro questa distanza
    private const float ExitLength = 26f;     // tratto di strada dopo la curva
    private const float MaxOffRoad = 2.8f;    // oltre questa distanza dalla mezzeria non si è "in strada"
    private const float DeadEndOverrun = 1.2f; // in fondo a un vicolo cieco si può scivolare fin poco oltre il centro dello slargo
    private const float StraightDot = 0.8f;   // un'uscita è "dritto" se devia meno di ~37°
    private const float SideDot = 0.3f;       // un'uscita è a destra/sinistra se ci punta almeno così
    private const float HairpinDot = -0.55f;  // oltre ~125° rispetto a dove si va è un gomito, non una svolta
    private const float UTurnJunctionMargin = 3f; // niente inversione guidata se un incrocio è più vicino di scivolata + questo
    private const float UTurnShortest = 2.6f;    // sotto questo spazio di scivolata niente inversione guidata

    private readonly List<Vector3> _points = new();
    private readonly List<float> _cum = new();

    /// <summary>Tratto (in lunghezza lungo la linea) della curva all'incrocio; senza svolta CornerEnd &lt; 0.</summary>
    public float CornerStart { get; private set; } = -1f;
    public float CornerEnd { get; private set; } = -1f;
    public bool HasCorner => CornerEnd > 0f;
    public float Length => _cum.Count > 0 ? _cum[^1] : 0f;
    /// <summary>La linea arriva fino a un incrocio raggiungibile entro la finestra (per scegliere la svolta dopo).</summary>
    public bool HubAhead { get; private set; }
    /// <summary>Lato della svolta scelta: +1 destra, -1 sinistra (come lo sterzo).</summary>
    public float TurnSign { get; private set; }

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
    /// Traccia la linea partendo dalla strada su cui si trova l'auto. <paramref name="steer"/>: +1 destra,
    /// -1 sinistra, 0 dritto (si sceglie la strada al prossimo incrocio, se esiste e se è abbastanza vicino).
    /// Restituisce null se l'auto non è su una strada o se non c'è un incrocio vicino dove svoltare:
    /// l'aiuto vale solo in prossimità degli incroci.
    /// </summary>
    public static DriftLine? Plan(Vector3 pos, Vector3 travel, float steer)
    {
        if (!Locate(pos, travel, out var c)) return null;
        float want = steer > 0.3f ? 1f : steer < -0.3f ? -1f : 0f;
        if (want == 0f) return null;

        // La prima svolta dal lato dello sterzo entro la finestra: agli incroci che da quel lato non ne hanno si
        // prosegue dritti e si guarda il successivo. Da quel lato si prende la svolta più netta, ma non i gomiti
        // (strade che tornano indietro): quelli non sono una svolta.
        var probe = new DriftLine();
        var from = c;
        Cursor atHub;
        RoadNode hub;
        Vector2 travelIn;
        (RoadEdge Edge, Vector2 Dir, float Ahead, float Right) turn;
        while (true)
        {
            probe.Follow(from, TurnWindow - probe.Length, stopAtJunction: true, out atHub, out _);
            hub = atHub.Ahead;
            if (atHub.ToAhead > 0.01f || hub.Degree < 2 || (hub.Degree == 2 && !hub.Piazza) || probe.Length >= TurnWindow)
                return null;
            travelIn = atHub.Edge.Tangent(atHub.Dir > 0f ? atHub.Edge.Length : 0f) * atHub.Dir;
            var exits = Exits(hub, atHub.Edge, travelIn).ToList();
            var side = exits.Where(x => want * x.Right > SideDot && x.Ahead > HairpinDot).ToList();
            if (side.Count > 0)
            {
                turn = side.MaxBy(x => want * x.Right);
                break;
            }
            var straight = exits.Where(x => x.Ahead > StraightDot).OrderByDescending(x => x.Ahead).ToList();
            if (straight.Count == 0) return null; // da qui non si prosegue dritti: niente svolta davanti
            var next = straight[0].Edge;
            float nd = next.A == hub ? 1f : -1f;
            from = new Cursor(next, next.A == hub ? 0f : next.Length, nd, NearestLane(next, nd, atHub.Lane));
        }
        float hubDist = probe.Length;

        var line = new DriftLine { HubAhead = true, TurnSign = turn.Right >= 0f ? 1f : -1f };
        float reach = hub.HubRadius + 0.9f; // la curva parte e finisce poco fuori dallo slargo
        // Fin quasi all'incrocio scelto, passando dritti per quelli prima
        line.Follow(c, MathF.Max(hubDist - reach, 0f), stopAtJunction: false, out _, out _);
        Vector3 p0 = line._points.Count > 0 ? line._points[^1] : To3(c.PointAt(c.S));
        // Corsia d'uscita: girando a destra la più esterna, a sinistra la più interna (come si fa davvero)
        float exitDir = turn.Edge.A == hub ? 1f : -1f;
        var lanes = turn.Edge.TravelLanes(exitDir);
        float exitLane = steer > 0.3f ? lanes.Max() : steer < -0.3f ? lanes.Min() : NearestLane(turn.Edge, exitDir, atHub.Lane);
        var exitC = new Cursor(turn.Edge, turn.Edge.A == hub ? 0f : turn.Edge.Length, exitDir, exitLane);
        float outS = MathF.Min(reach, turn.Edge.Length * 0.5f);
        exitC = exitC with { S = exitC.S + exitC.Dir * outS };
        Vector3 p2 = To3(exitC.PointAt(exitC.S));
        // Punto di controllo: dove si incrociano le due corsie (per strade ad angolo retto è esatto)
        Vector3 h = To3(hub.Pos + RightOf(travelIn) * atHub.Lane + RightOf(turn.Dir) * exitLane);

        // Curva: Bézier quadratica col punto d'incontro delle corsie come controllo (resta dentro lo slargo)
        line.Add(p0);
        line.CornerStart = line.Length;
        for (int k = 1; k <= 16; k++)
        {
            float u = k / 16f;
            line.Add((1 - u) * (1 - u) * p0 + 2 * (1 - u) * u * h + u * u * p2);
        }
        line.CornerEnd = line.Length;
        line.Follow(exitC, ExitLength, stopAtJunction: false, out _, out _);
        return line._points.Count >= 4 ? line : null;
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
