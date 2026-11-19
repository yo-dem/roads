using System.Numerics;
using Roads.World;

namespace Roads.Characters;

/// <summary>
/// Traiettoria della derapata assistita: la mezzeria della strada su cui si viaggia fino al prossimo incrocio,
/// una curva morbida dentro lo slargo verso la strada scelta (a sinistra, a destra o dritto), poi la mezzeria
/// della nuova strada. L'auto la segue come su una rotaia: in derapata resta sempre in corsia.
/// </summary>
public sealed class DriftLine
{
    private const float Step = 0.5f;          // passo dei punti lungo la linea
    private const float CornerReach = 3f;     // la curva parte e finisce a questa distanza dal centro dell'incrocio
    private const float TurnWindow = 24f;     // l'aiuto si attiva solo entro questa distanza da un incrocio
    private const float ExitLength = 26f;     // tratto di strada dopo la curva
    private const float MaxOffRoad = 2.6f;    // oltre questa distanza dalla mezzeria non si è "in strada"

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

    private enum Axis { EastWest, NorthSouth }

    private static float C => Terrain.ChunkSize;

    private static Vector3 Center(Axis axis, int index, float s) => axis == Axis.EastWest
        ? new Vector3(s, 0f, Terrain.RoadZ(index, s))
        : new Vector3(Terrain.RoadX(index, s), 0f, s);

    private void Add(Vector3 p)
    {
        if (_points.Count > 0 && Vector3.Distance(_points[^1], p) < 1e-3f) return;
        _cum.Add(_points.Count == 0 ? 0f : _cum[^1] + Vector3.Distance(_points[^1], p));
        _points.Add(p);
    }

    /// <summary>
    /// Traccia la linea partendo dalla strada su cui si trova l'auto. <paramref name="steer"/>: +1 destra,
    /// -1 sinistra, 0 dritto (si sceglie la strada al prossimo incrocio, se esiste e se è abbastanza vicino).
    /// Restituisce null se l'auto non è su una strada o se non c'è un incrocio vicino dove svoltare:
    /// l'aiuto vale solo in prossimità degli incroci.
    /// </summary>
    public static DriftLine? Plan(Vector3 pos, Vector3 travel, float steer)
    {
        travel = Vector3.Normalize(new Vector3(travel.X, 0f, travel.Z));

        if (!Locate(pos, travel, out var axis, out int index, out float s, out float dir)) return null;

        var line = new DriftLine();

        // Prossimo incrocio davanti
        int hub = dir > 0f ? (int)MathF.Floor(s / C) + 1 : (int)MathF.Ceiling(s / C) - 1;
        float hubS = hub * C;
        float hubDist = MathF.Abs(hubS - s);
        int hx = axis == Axis.EastWest ? hub : index, hz = axis == Axis.EastWest ? index : hub;
        Dir forward = axis == Axis.EastWest ? (dir > 0f ? Dir.East : Dir.West) : (dir > 0f ? Dir.South : Dir.North);

        // Uscita scelta all'incrocio: il lato dello sterzo se c'è la strada, altrimenti dritto, altrimenti la curva obbligata
        Dir? exit = null;
        if (hubDist < TurnWindow)
        {
            line.HubAhead = true;
            Dir left = TurnLeft(forward), right = left.Opposite();
            Dir? wanted = steer > 0.3f ? right : steer < -0.3f ? left : null;
            if (wanted is { } w && WorldMap.EdgeOpen(hx, hz, w)) exit = w;
            else if (wanted == null && !WorldMap.EdgeOpen(hx, hz, forward))
            {
                // Senza direzione, se la strada all'incrocio curva per forza si segue la curva
                if (WorldMap.EdgeOpen(hx, hz, left)) exit = left;
                else if (WorldMap.EdgeOpen(hx, hz, right)) exit = right;
            }
        }

        if (exit is { } turn)
        {
            line.TurnSign = turn == TurnLeft(forward) ? -1f : 1f;
            // Mezzeria fino all'inizio della curva
            float until = hubS - dir * CornerReach;
            for (float t = s; dir * (until - t) > 0f; t += dir * Step) line.Add(Center(axis, index, t));
            Vector3 p0 = Center(axis, index, until);
            Vector3 h = new(hx * C, 0f, hz * C);

            Axis newAxis = turn is Dir.East or Dir.West ? Axis.EastWest : Axis.NorthSouth;
            int newIndex = newAxis == Axis.EastWest ? hz : hx;
            float newDir = turn is Dir.East or Dir.South ? 1f : -1f;
            float newHubS = newAxis == Axis.EastWest ? hx * C : hz * C;
            Vector3 p2 = Center(newAxis, newIndex, newHubS + newDir * CornerReach);

            // Curva: Bézier quadratica con il centro dell'incrocio come punto di controllo (resta dentro lo slargo)
            line.Add(p0);
            line.CornerStart = line.Length;
            for (int k = 1; k <= 16; k++)
            {
                float u = k / 16f;
                line.Add((1 - u) * (1 - u) * p0 + 2 * (1 - u) * u * h + u * u * p2);
            }
            line.CornerEnd = line.Length;
            for (float t = newHubS + newDir * (CornerReach + Step); MathF.Abs(t - newHubS) < CornerReach + ExitLength; t += newDir * Step)
                line.Add(Center(newAxis, newIndex, t));
        }
        else return null; // nessuna svolta vicina: niente aiuto, il freno a mano fa una scivolata libera
        return line._points.Count >= 4 ? line : null;
    }

    /// <summary>
    /// Su quale strada è l'auto? (Si preferisce quella allineata alla direzione di marcia.) Restituisce l'asse,
    /// l'indice della strada, la coordinata lungo di essa e il verso di marcia (+1 / -1); false se non è in strada.
    /// </summary>
    private static bool Locate(Vector3 pos, Vector3 travel, out Axis axis, out int index, out float s, out float dir)
    {
        axis = MathF.Abs(travel.X) >= MathF.Abs(travel.Z) ? Axis.EastWest : Axis.NorthSouth;
        float off;
        bool open;
        if (axis == Axis.EastWest)
        {
            index = (int)MathF.Round(pos.Z / C);
            s = pos.X;
            off = MathF.Abs(pos.Z - Terrain.RoadZ(index, s));
            open = WorldMap.EdgeOpen((int)MathF.Floor(s / C), index, Dir.East);
        }
        else
        {
            index = (int)MathF.Round(pos.X / C);
            s = pos.Z;
            off = MathF.Abs(pos.X - Terrain.RoadX(index, s));
            open = WorldMap.EdgeOpen(index, (int)MathF.Floor(s / C), Dir.South);
        }
        dir = axis == Axis.EastWest ? MathF.Sign(travel.X) : MathF.Sign(travel.Z);
        if (dir == 0f) dir = 1f;
        return (open || NearHub(pos)) && off <= MaxOffRoad;
    }

    /// <summary>
    /// Linea lungo la mezzeria della strada su cui si viaggia, per <paramref name="length"/> unità davanti
    /// (per l'inversione a U: l'auto scivola lungo la corsia mentre si gira). Null se non si è in strada.
    /// </summary>
    public static DriftLine? AlongRoad(Vector3 pos, Vector3 travel, float length)
    {
        travel = Vector3.Normalize(new Vector3(travel.X, 0f, travel.Z));
        if (!Locate(pos, travel, out var axis, out int index, out float s, out float dir)) return null;
        var line = new DriftLine();
        for (float t = s; MathF.Abs(t - s) <= length + Step; t += dir * Step) line.Add(Center(axis, index, t));
        return line._points.Count >= 2 ? line : null;
    }

    private static bool NearHub(Vector3 p)
    {
        float hx = MathF.Round(p.X / C) * C, hz = MathF.Round(p.Z / C) * C;
        return new Vector2(p.X - hx, p.Z - hz).Length() < Zone.HubRadius + 1.5f;
    }

    private static Dir TurnLeft(Dir d) => d switch
    {
        // Sinistra rispetto alla marcia (vista dall'alto, nord = -Z): verso est la sinistra è nord
        Dir.East => Dir.North,
        Dir.North => Dir.West,
        Dir.West => Dir.South,
        _ => Dir.East,
    };

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
