using System.Numerics;
using Silk.NET.OpenGL;

namespace Roads.World;

/// <summary>
/// Territorio unico e infinito, diviso in pezzi quadrati (<see cref="Zone"/>) che combaciano senza giunture.
/// I pezzi attorno al giocatore si generano in background man mano che si avanza (sul thread principale
/// resta solo il caricamento sulla GPU) e quelli lontani si liberano: se si torna indietro si rigenerano
/// identici, perché tutto deriva da funzioni del mondo e da semi fissi.
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
    }

    // Tratto tra (x,z) e (x+1,z): la riga z = 0 è sempre aperta (via principale verso est/ovest)
    private static bool HorizontalOpen(int x, int z) => z == 0 || Noise.Hash01(x, z, 101) < 0.5f;

    // Tratto tra (x,z) e (x,z+1): attorno alla partenza niente strade nord-sud
    private static bool VerticalOpen(int x, int z) =>
        !(x == 0 && (z == 0 || z == -1)) && Noise.Hash01(x, z, 202) < 0.45f;

    /// <summary>La strada dall'incrocio (x, z) verso la direzione data esiste?</summary>
    public static bool EdgeOpen(int x, int z, Dir d) => d switch
    {
        Dir.East => HorizontalOpen(x, z),
        Dir.West => HorizontalOpen(x - 1, z),
        Dir.South => VerticalOpen(x, z),
        _ => VerticalOpen(x, z - 1),
    };

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

    /// <summary>Altezza della superficie cliccabile (terreno, ponti o pelo dell'acqua); false dove non c'è terreno caricato.</summary>
    public bool TryGetSurface(Vector3 p, out float height)
    {
        height = 0f;
        var zone = ZoneAt(p);
        if (zone == null) return false;
        height = MathF.Max(zone.GroundHeight(p), Zone.WaterY);
        return true;
    }

    /// <summary>Interseca un raggio con il terreno (ray marching + bisezione).</summary>
    public bool Raycast(Vector3 origin, Vector3 dir, out Vector3 hit)
    {
        const float step = 0.2f;
        Vector3 prev = origin;
        for (float t = 0f; t < 200f; t += step)
        {
            Vector3 p = origin + dir * t;
            if (p.Y < -6f) break;
            if (TryGetSurface(p, out float h) && p.Y <= h)
            {
                Vector3 a = prev, b = p;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 m = (a + b) * 0.5f;
                    if (TryGetSurface(m, out float hm) && m.Y <= hm) b = m; else a = m;
                }
                TryGetSurface(b, out float hb);
                hit = b with { Y = hb };
                return true;
            }
            prev = p;
        }
        hit = default;
        return false;
    }

    public bool IsRoad(Vector3 p) => ZoneAt(p)?.IsRoad(p) ?? false;

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
            float hit = zone.PushFrom(center, radius, ref velocity, mass, out var c);
            if (hit > worst) { worst = hit; contact = c; }
        }
        return worst;
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

    public void Dispose()
    {
        foreach (var z in _zones.Values) z.Dispose();
    }
}
