using System.Numerics;
using Roads.World;

namespace Roads.Characters;

/// <summary>
/// Ricerca del percorso A* su una griglia fine centrata sul personaggio, usando
/// <see cref="WorldMap.IsWalkable"/> come mappa degli ostacoli. Il percorso viene poi
/// "tirato" (string pulling) per ottenere pochi tratti rettilinei e naturali.
/// Sentieri e ponti costano meno del prato, quindi il percorso preferisce le strade
/// quando la deviazione non è eccessiva.
/// </summary>
public static class PathFinder
{
    private const float CellSize = 0.35f;
    private const int MaxExpanded = 20000;
    private const int Margin = 30; // celle oltre il rettangolo partenza-arrivo
    private const float OffRoadCost = 1.8f; // un metro di prato "vale" 1.8 metri di strada
    private const float DirectDistance = 6f; // sotto questa distanza si va dritti se possibile

    private static readonly (int Di, int Dj, float Cost)[] Neighbours =
    [
        (1, 0, 1f), (-1, 0, 1f), (0, 1, 1f), (0, -1, 1f),
        (1, 1, 1.4142f), (1, -1, 1.4142f), (-1, 1, 1.4142f), (-1, -1, 1.4142f),
    ];

    /// <summary>
    /// Calcola i punti di passaggio da <paramref name="start"/> verso <paramref name="goal"/>.
    /// Se la meta non è raggiungibile (acqua, albero, ...) punta al punto raggiungibile più vicino,
    /// restituito in <paramref name="reached"/>. Null se non c'è modo di avvicinarsi.
    /// </summary>
    public static List<Vector3>? Find(WorldMap world, Vector3 start, Vector3 goal, float radius, out Vector3 reached)
    {
        reached = goal;
        start.Y = goal.Y = 0f;

        // Meta dentro un ostacolo: si sposta sul punto libero più vicino
        if (!world.IsWalkable(goal, radius))
        {
            if (!TrySnap(world, goal, radius, out goal)) return null;
            reached = goal;
        }

        // Vicino, o tutto su strada: inutile cercare, si va dritti
        if (LineClear(world, start, goal, radius)
            && (Flat(goal - start).Length() < DirectDistance
                || SegmentCost(world, start, goal) <= Flat(goal - start).Length() * 1.01f))
            return [goal];

        var cache = new Dictionary<(int, int), bool>();
        Vector3 Pos((int I, int J) c) => new(start.X + c.I * CellSize, 0, start.Z + c.J * CellSize);
        bool Walkable((int I, int J) c)
        {
            if (c == (0, 0)) return true;
            if (!cache.TryGetValue(c, out bool w))
                cache[c] = w = world.IsWalkable(Pos(c), radius);
            return w;
        }
        var costCache = new Dictionary<(int, int), float>();
        float CellCost((int I, int J) c)
        {
            if (!costCache.TryGetValue(c, out float k))
                costCache[c] = k = world.IsRoad(Pos(c)) ? 1f : OffRoadCost;
            return k;
        }

        var goalCell = ((int)MathF.Round((goal.X - start.X) / CellSize), (int)MathF.Round((goal.Z - start.Z) / CellSize));
        int limit = Math.Max(Math.Abs(goalCell.Item1), Math.Abs(goalCell.Item2)) + Margin;
        float H((int I, int J) c)
        {
            float di = c.I - goalCell.Item1, dj = c.J - goalCell.Item2;
            return MathF.Sqrt(di * di + dj * dj);
        }

        var open = new PriorityQueue<(int, int), float>();
        var g = new Dictionary<(int, int), float> { [(0, 0)] = 0f };
        var parent = new Dictionary<(int, int), (int, int)>();
        var closed = new HashSet<(int, int)>();
        open.Enqueue((0, 0), H((0, 0)));
        var best = (0, 0);
        float bestH = H(best);

        while (closed.Count < MaxExpanded && open.TryDequeue(out var cur, out _))
        {
            if (!closed.Add(cur)) continue;
            float h = H(cur);
            if (h < bestH) { bestH = h; best = cur; }
            if (cur == goalCell) break;

            foreach (var (di, dj, cost) in Neighbours)
            {
                var n = (cur.Item1 + di, cur.Item2 + dj);
                if (Math.Abs(n.Item1) > limit || Math.Abs(n.Item2) > limit || closed.Contains(n)) continue;
                if (!Walkable(n)) continue;
                // In diagonale niente "tagli" sugli spigoli degli ostacoli
                if (di != 0 && dj != 0 && (!Walkable((cur.Item1 + di, cur.Item2)) || !Walkable((cur.Item1, cur.Item2 + dj))))
                    continue;
                float ng = g[cur] + cost * 0.5f * (CellCost(cur) + CellCost(n));
                if (g.TryGetValue(n, out float old) && old <= ng) continue;
                g[n] = ng;
                parent[n] = cur;
                open.Enqueue(n, ng + H(n) * 1.1f);
            }
        }

        if (best == (0, 0)) return null;

        var cells = new List<Vector3>();
        for (var c = best; c != (0, 0); c = parent[c]) cells.Add(Pos(c));
        cells.Reverse();

        Vector3 last = cells[^1];
        if (Vector3.Distance(last, goal) > 0.01f && LineClear(world, last, goal, radius))
            cells.Add(goal);
        else
            reached = last;

        return Smooth(world, start, cells, radius);
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    /// <summary>Costo di un tratto rettilineo: lunghezza pesata dal tipo di terreno attraversato.</summary>
    private static float SegmentCost(WorldMap world, Vector3 a, Vector3 b)
    {
        Vector3 d = Flat(b - a);
        float len = d.Length();
        if (len < 1e-4f) return 0f;
        int steps = Math.Max(1, (int)MathF.Ceiling(len / 0.3f));
        float step = len / steps, total = 0f;
        for (int i = 0; i < steps; i++)
        {
            Vector3 p = a + d * ((i + 0.5f) / steps);
            total += step * (world.IsRoad(p) ? 1f : OffRoadCost);
        }
        return total;
    }

    /// <summary>Cerca, ad anelli crescenti, il punto calpestabile più vicino.</summary>
    private static bool TrySnap(WorldMap world, Vector3 p, float radius, out Vector3 snapped)
    {
        for (float r = 0.3f; r <= 4f; r += 0.3f)
        {
            int samples = Math.Max(8, (int)(MathF.Tau * r / 0.3f));
            float bestDist = float.MaxValue;
            snapped = p;
            for (int s = 0; s < samples; s++)
            {
                float a = MathF.Tau * s / samples;
                Vector3 q = p + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * r;
                if (world.IsWalkable(q, radius)) { snapped = q; bestDist = r; break; }
            }
            if (bestDist < float.MaxValue) return true;
        }
        snapped = p;
        return false;
    }

    /// <summary>Il segmento è percorribile? (campionato a passi corti; ignora l'inizio, già occupato).</summary>
    public static bool LineClear(WorldMap world, Vector3 a, Vector3 b, float radius)
    {
        Vector3 d = b - a;
        d.Y = 0f;
        float len = d.Length();
        if (len < 1e-4f) return true;
        d /= len;
        for (float t = MathF.Min(0.2f, len); t <= len; t += 0.15f)
            if (!world.IsWalkable(a + d * t, radius)) return false;
        return world.IsWalkable(b, radius);
    }

    /// <summary>
    /// String pulling: da ogni punto salta al più lontano raggiungibile in linea retta, ma solo se
    /// la scorciatoia non costa più del tratto che sostituisce (così non si abbandona la strada per il prato).
    /// </summary>
    private static List<Vector3> Smooth(WorldMap world, Vector3 start, List<Vector3> pts, float radius)
    {
        // Costo cumulato lungo il percorso a celle: cum[i] = costo da start fino a pts[i - 1]
        var cum = new float[pts.Count + 1];
        for (int i = 0; i < pts.Count; i++)
            cum[i + 1] = cum[i] + SegmentCost(world, i == 0 ? start : pts[i - 1], pts[i]);

        var result = new List<Vector3>();
        Vector3 anchor = start;
        int anchorIdx = 0, k = 0;
        while (k < pts.Count)
        {
            int m = k;
            while (m + 1 < pts.Count
                   && LineClear(world, anchor, pts[m + 1], radius)
                   && SegmentCost(world, anchor, pts[m + 1]) <= (cum[m + 2] - cum[anchorIdx]) * 1.02f + 0.05f)
                m++;
            result.Add(pts[m]);
            anchor = pts[m];
            anchorIdx = m + 1;
            k = m + 1;
        }
        return result;
    }
}
