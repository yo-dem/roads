using System.Numerics;
using Silk.NET.OpenGL;

namespace Roads.World;

/// <summary>
/// Il territorio dell'isola (la mappa in <see cref="GameMap"/>), diviso in pezzi quadrati (<see cref="Zone"/>) che
/// combaciano senza giunture. I pezzi attorno al giocatore si generano in background man mano che si avanza (sul
/// thread principale resta solo il caricamento sulla GPU) e quelli lontani si liberano: se si torna indietro si
/// rigenerano identici, perché tutto deriva dalla mappa, da funzioni del mondo e da semi fissi.
/// I punti di riferimento invece esistono sempre: si vedono da lontano, anche oltre la nebbia.
/// </summary>
public sealed class WorldMap : IDisposable
{
    public const float Spacing = Terrain.ChunkSize;
    private const float LoadDistance = 125f;    // pezzi entro questa distanza dal giocatore: caricati
    private const float UnloadDistance = 195f;  // oltre questa: liberati (isteresi rispetto al caricamento)

    private readonly GL _gl;
    private readonly Dictionary<(int, int), Zone> _zones = new();
    private readonly HashSet<(int, int)> _explored = new();
    private readonly Dictionary<(int, int), Task<Zone>> _pending = new();
    // Oggetti spostati dei pezzi liberati: quando il pezzo si rigenera tornano dove li si era lasciati
    private readonly Dictionary<(int, int), List<(int Index, Vector2 Offset, Quaternion Rotation)>> _moved = new();

    /// <summary>Pezzi caricati.</summary>
    public IEnumerable<Zone> Zones => _zones.Values;
    /// <summary>Pezzi esplorati in tutto, anche quelli liberati perché lontani.</summary>
    public int Count => _explored.Count;
    public int LoadedCount => _zones.Count;

    /// <summary>La cella (incrocio i, j) è già stata vista? (per la mappa: si scopre girando)</summary>
    public bool IsExplored(int i, int j) => _explored.Contains((i, j));

    private readonly List<LandmarkModel> _landmarks = new();
    public IReadOnlyList<LandmarkModel> Landmarks => _landmarks;

    private static (int, int) CellOf(Vector3 p) => ((int)MathF.Round(p.X / Spacing), (int)MathF.Round(p.Z / Spacing));

    public WorldMap(GL gl, Vector3 start)
    {
        _gl = gl;
        // All'avvio i pezzi visibili si generano subito, in parallelo, così non ci sono buchi
        var (sx, sz) = CellOf(start);
        var cells = new List<(int, int)>();
        for (int x = sx - 2; x <= sx + 2; x++)
            for (int z = sz - 2; z <= sz + 2; z++)
                if (CellDistance(x, z, start) < LoadDistance) cells.Add((x, z));
        foreach (var zone in cells.AsParallel().Select(c => new Zone(c.Item1, c.Item2)).ToList())
            Register(zone);
        foreach (var site in GameMap.Current.Landmarks)
            _landmarks.Add(new LandmarkModel(gl, site));
    }

    /// <summary>Il fondo della strada nel punto (null fuori strada o su un ponte): sugli sterrati le gomme alzano polvere.</summary>
    public RoadKind? SurfaceAt(Vector3 p)
    {
        foreach (var b in ZoneAt(p)?.Bridges ?? [])
            if (b.TryGetHeight(p, 0f, out _)) return null;
        return RoadNetwork.Current.KindAt(new Vector2(p.X, p.Z));
    }

    private void Register(Zone zone)
    {
        zone.Upload(_gl);
        if (_moved.Remove((zone.X, zone.Z), out var moved)) zone.RestoreMoved(moved);
        _zones[(zone.X, zone.Z)] = zone;
        _explored.Add((zone.X, zone.Z));
    }

    /// <summary>Solo per test: crea subito tutti i pezzi entro <paramref name="radius"/> celle dalla partenza.</summary>
    public void PreSpawn(int radius)
    {
        for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
                if (!_zones.ContainsKey((x, z))) Register(new Zone(x, z));
    }

    public Zone? ZoneAt(Vector3 p) => _zones.GetValueOrDefault(CellOf(p));

    /// <summary>Distanza sul piano tra il punto e il quadrato del pezzo (x, z).</summary>
    private static float CellDistance(int x, int z, Vector3 p)
    {
        float dx = MathF.Max(MathF.Abs(p.X - x * Spacing) - Zone.Half, 0f);
        float dz = MathF.Max(MathF.Abs(p.Z - z * Spacing) - Zone.Half, 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Completa le generazioni finite (al più una per fotogramma), avvia quelle dei pezzi che si avvicinano
    /// e libera quelli lontani. Restituisce l'eventuale pezzo esplorato per la prima volta.
    /// </summary>
    public Zone? Update(float dt, Vector3 player)
    {
        Zone? discovered = null;
        foreach (var (cell, job) in _pending)
        {
            if (!job.IsCompleted) continue;
            _pending.Remove(cell);
            if (job.IsFaulted)
                Console.WriteLine($"Generazione del pezzo {cell} fallita: {job.Exception?.GetBaseException().Message}");
            else
            {
                bool first = !_explored.Contains(cell);
                Register(job.Result);
                if (first) discovered = job.Result;
            }
            break;
        }

        foreach (var zone in _zones.Values) zone.UpdateMovables(dt);

        var (px, pz) = CellOf(player);
        for (int x = px - 2; x <= px + 2; x++)
            for (int z = pz - 2; z <= pz + 2; z++)
            {
                if (_zones.ContainsKey((x, z)) || _pending.ContainsKey((x, z))) continue;
                if (CellDistance(x, z, player) >= LoadDistance) continue;
                int cx = x, cz = z;
                _pending[(x, z)] = Task.Run(() => new Zone(cx, cz));
            }

        List<Zone>? far = null;
        foreach (var zone in _zones.Values)
            if (CellDistance(zone.X, zone.Z, player) > UnloadDistance) (far ??= new()).Add(zone);
        if (far != null)
            foreach (var zone in far)
            {
                var moved = zone.MovedState();
                if (moved.Count > 0) _moved[(zone.X, zone.Z)] = moved;
                _zones.Remove((zone.X, zone.Z));
                zone.Dispose();
            }

        return discovered;
    }

    /// <summary>
    /// Un cerchio di raggio <paramref name="radius"/> può stare nel punto? Terreno asciutto (o ponte) e
    /// nessun ostacolo: vicino al bordo di un pezzo si guardano anche gli ostacoli dei pezzi accanto.
    /// </summary>
    public bool IsWalkable(Vector3 p, float radius)
    {
        var zone = ZoneAt(p);
        if (zone == null || !zone.IsGround(p)) return false;
        if (zone.HitsObstacle(p, radius)) return false;
        if (Trains.Current?.Blocks(new Vector2(p.X, p.Z), radius) == true) return false; // treno, sbarre abbassate
        foreach (var l in GameMap.Current.Landmarks)
            if (l.Hits(new Vector2(p.X, p.Z), radius)) return false;

        const float reach = 2f; // raggio massimo di un ostacolo + margine
        float lx = p.X - zone.Origin.X, lz = p.Z - zone.Origin.Z, edge = Zone.Half - radius - reach;
        if (MathF.Abs(lx) < edge && MathF.Abs(lz) < edge) return true;
        for (int dx = -1; dx <= 1; dx++)
            for (int dz = -1; dz <= 1; dz++)
            {
                if (dx == 0 && dz == 0) continue;
                if (_zones.TryGetValue((zone.X + dx, zone.Z + dz), out var other)
                    && CellDistance(other.X, other.Z, p) < radius + reach && other.HitsObstacle(p, radius))
                    return false;
            }
        return true;
    }

    public bool IsRoad(Vector3 p) => ZoneAt(p)?.IsRoad(p) ?? false;

    /// <summary>
    /// Il punto (allargato di <paramref name="margin"/>) sta dentro un edificio o un punto di riferimento? Per la camera:
    /// non deve mai entrarci. Si guardano anche i pezzi accanto (un edificio vicino al bordo può sconfinare).
    /// </summary>
    public bool InsideSolid(Vector3 p, float margin)
    {
        const float reach = 16f; // metà dell'edificio più grande + margine
        foreach (var zone in _zones.Values)
            if (CellDistance(zone.X, zone.Z, p) < margin + reach && zone.InsideBuilding(p, margin)) return true;
        foreach (var l in GameMap.Current.Landmarks)
            if (l.Hits(new Vector2(p.X, p.Z), margin)) return true;
        return false;
    }

    /// <summary>
    /// Un cerchio in movimento (l'auto) urta rocce e cespugli: li spinge via secondo le masse e restituisce la
    /// propria velocità ridotta. Ritorna la velocità d'impatto più forte (0 se nessun urto) e il punto d'urto.
    /// </summary>
    public float PushMovables(Vector3 center, float radius, ref Vector2 velocity, float mass, out Vector3 contact)
    {
        contact = default;
        float worst = 0f;
        foreach (var zone in _zones.Values)
        {
            if (CellDistance(zone.X, zone.Z, center) > radius + 2f) continue;
            float hit = zone.PushFrom(center, radius, ref velocity, mass, out var c, _broken);
            if (hit > worst) { worst = hit; contact = c; }
        }
        return worst;
    }

    private readonly List<(Vector3 At, Vector2 Dir, float Speed)> _broken = new();

    /// <summary>I pali abbattuti dall'ultima chiamata (punto d'urto, velocità dell'auto): per le schegge.</summary>
    public List<(Vector3 At, Vector2 Dir, float Speed)> TakeBroken()
    {
        var list = _broken.ToList();
        _broken.Clear();
        return list;
    }

    /// <summary>Il ponte più vicino entro <paramref name="range"/> (sul piano), se c'è.</summary>
    public Bridge? NearestBridge(Vector3 p, float range)
    {
        Bridge? best = null;
        float bestD = range * range;
        foreach (var zone in _zones.Values)
        {
            if (CellDistance(zone.X, zone.Z, p) > range) continue;
            foreach (var b in zone.Bridges)
            {
                float dx = b.Center.X - p.X, dz = b.Center.Z - p.Z, d = dx * dx + dz * dz;
                if (d < bestD) { bestD = d; best = b; }
            }
        }
        return best;
    }

    public float GroundHeight(Vector3 p) => ZoneAt(p)?.GroundHeight(p) ?? 0f;

    /// <summary>Il terreno o, se c'è, la cima della roccia o del cespuglio in quel punto (per la camera bassa a piedi).</summary>
    public float? ObstacleTop(Vector3 p) => ZoneAt(p)?.ObstacleTop(p);

    public float GroundOrObstacle(Vector3 p)
    {
        if (ZoneAt(p) is not { } zone) return 0f;
        float g = zone.GroundHeight(p);
        return zone.ObstacleTop(p) is { } top ? MathF.Max(g, top) : g;
    }

    public void Dispose()
    {
        foreach (var z in _zones.Values) z.Dispose();
        foreach (var l in _landmarks) l.Dispose();
    }
}
