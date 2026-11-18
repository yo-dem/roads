using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

/// <summary>Ponte che collega due isole adiacenti.</summary>
public sealed class Connector : IDisposable
{
    private const float RiseDelay = 0.15f;    // il ponte parte poco dopo l'arrivo delle isole
    private const float RiseDuration = 1.0f;
    private const float RiseDepth = 12f;      // da quanto in basso sale

    public required Zone A { get; init; }
    public required Zone B { get; init; }
    public required Bridge Bridge { get; init; }
    public required Mesh Mesh { get; init; }

    /// <summary>
    /// Avanzamento della comparsa (0 → 1): il ponte, già costruito, sale dal basso con lo stesso
    /// rimbalzo delle isole. Parte solo quando entrambe le isole sono arrivate.
    /// </summary>
    public float Rise;
    private float _wait;

    public bool Ready => A.Ready && B.Ready && Rise >= 1f;
    public bool Visible => Rise > 0f;
    public float YOffset => MathF.Min(A.YOffset, B.YOffset);

    public void Update(float dt)
    {
        if (Rise >= 1f || !A.Ready || !B.Ready) return;
        _wait += dt;
        if (_wait >= RiseDelay) Rise = MathF.Min(1f, Rise + dt / RiseDuration);
    }

    public Matrix4x4 Model
    {
        get
        {
            float p = Math.Clamp(Rise, 0f, 1f);
            float lift = -RiseDepth * (1f - Zone.EaseOutBack(p));
            // Durante la salita il ponte è appena più corto e sta tutto nel varco tra le isole (non ne
            // attraversa le pareti); alla fine si allunga fino a poggiare sui sentieri.
            float fit = WorldMap.Gap * 0.95f / Bridge.Span;
            float length = Noise.Lerp(fit, 1f, Noise.Smooth(0.8f, 1f, p));
            Vector3 axis = Bridge.Dir; // asse di attraversamento, componenti 0 o 1
            var scale = new Vector3(1f - axis.X * (1f - length), 1f, 1f - axis.Z * (1f - length));
            return Matrix4x4.CreateTranslation(-Bridge.Center) * Matrix4x4.CreateScale(scale)
                   * Matrix4x4.CreateTranslation(Bridge.Center) * Matrix4x4.CreateTranslation(0, YOffset + lift, 0);
        }
    }

    public void Dispose() => Mesh.Dispose();
}

/// <summary>
/// Griglia infinita di isole. Le isole nuove nascono quando il giocatore si avvicina a un'uscita;
/// le uscite sono proprietà dei lati tra due celle, quindi sempre coerenti.
/// La generazione avviene in background (sul thread principale resta solo il caricamento sulla GPU);
/// le isole lontane vengono liberate e, se già scoperte, ricaricate identiche (dal loro seme) prima
/// di tornare in vista.
/// </summary>
public sealed class WorldMap : IDisposable
{
    public const float Gap = 4f;
    public const float Spacing = Zone.Half * 2f + Gap;
    private const float SpawnDistance = 32f;    // a piena velocità isola e ponte sono saliti prima di arrivarci
    private const float LoadDistance = 115f;    // isole già scoperte entro questa distanza: caricate
    private const float UnloadDistance = 185f;  // oltre questa: liberate (isteresi rispetto al caricamento)

    private readonly GL _gl;
    private readonly Dictionary<(int, int), Zone> _zones = new();
    private readonly HashSet<(int, int)> _discovered = new();
    private readonly Dictionary<(int, int), (Task<Zone> Job, bool IsNew)> _pending = new();

    /// <summary>Isole caricate (le altre scoperte sono solo ricordate).</summary>
    public IEnumerable<Zone> Zones => _zones.Values;
    public List<Connector> Connectors { get; } = new();
    /// <summary>Isole scoperte in tutto, anche quelle liberate perché lontane.</summary>
    public int Count => _discovered.Count;
    public int LoadedCount => _zones.Count;

    // Ponti indicizzati per cella: ogni ponte sta a cavallo del bordo tra due isole ed è registrato
    // in entrambe, così le interrogazioni guardano solo quelli vicini invece di tutto il mondo.
    private readonly Dictionary<(int, int), List<Connector>> _connectorsByCell = new();
    private static readonly List<Connector> NoConnectors = new();

    private static (int, int) CellOf(Vector3 p) => ((int)MathF.Round(p.X / Spacing), (int)MathF.Round(p.Z / Spacing));

    private List<Connector> ConnectorsNear(Vector3 p) => _connectorsByCell.GetValueOrDefault(CellOf(p), NoConnectors);

    public WorldMap(GL gl)
    {
        _gl = gl;
        Register(Generate(0, 0), isNew: false);
    }

    // Lato tra (x,z) e (x+1,z): la riga z = 0 è sempre aperta (via principale verso est/ovest)
    private static bool HorizontalOpen(int x, int z) => z == 0 || Noise.Hash01(x, z, 101) < 0.5f;

    // Lato tra (x,z) e (x,z+1): la zona iniziale resta chiusa a nord e a sud
    private static bool VerticalOpen(int x, int z) =>
        !(x == 0 && (z == 0 || z == -1)) && Noise.Hash01(x, z, 202) < 0.45f;

    public static bool EdgeOpen(int x, int z, Dir d) => d switch
    {
        Dir.East => HorizontalOpen(x, z),
        Dir.West => HorizontalOpen(x - 1, z),
        Dir.South => VerticalOpen(x, z),
        _ => VerticalOpen(x, z - 1),
    };

    /// <summary>Genera un'isola senza toccare OpenGL (sicuro su un thread in background).</summary>
    private static Zone Generate(int x, int z)
    {
        var exits = DirExtensions.All.Select(d => EdgeOpen(x, z, d)).ToArray();
        return new Zone(x, z, exits);
    }

    /// <summary>Avvia la generazione in background di un'isola.</summary>
    private void Request(int x, int z, bool isNew)
    {
        if (_zones.ContainsKey((x, z)) || _pending.ContainsKey((x, z))) return;
        _pending[(x, z)] = (Task.Run(() => Generate(x, z)), isNew);
    }

    /// <summary>Carica l'isola sulla GPU, la aggiunge al mondo e costruisce i ponti verso le vicine caricate.</summary>
    private void Register(Zone zone, bool isNew)
    {
        zone.Upload(_gl);
        zone.Rise = isNew ? 0f : 1f; // le isole ricaricate compaiono già al loro posto (sono oltre la nebbia)
        _zones[(zone.X, zone.Z)] = zone;
        _discovered.Add((zone.X, zone.Z));

        foreach (Dir d in DirExtensions.All)
        {
            if (!zone.Exits[(int)d]) continue;
            var (dx, dz) = d.Offset();
            if (_zones.TryGetValue((zone.X + dx, zone.Z + dz), out var other))
                AddConnector(zone, other, d, animate: isNew);
        }
    }

    private void AddConnector(Zone a, Zone b, Dir d, bool animate)
    {
        Vector3 v = d.Vec(); // da a verso b
        var bridge = new Bridge
        {
            Center = a.Origin + v * (Zone.Half + Gap * 0.5f),
            Dir = new Vector3(MathF.Abs(v.X), 0, MathF.Abs(v.Z)),
            Span = Gap + 1.2f,
            BaseY = Zone.PathRaise,
            Arch = 0.15f,
        };
        // Colori legati alla posizione del ponte, non all'ordine di caricamento: ricaricato resta identico
        var (cx, cz) = (Math.Min(a.X, b.X), Math.Min(a.Z, b.Z));
        var mb = new MeshBuilder { ColorRng = new Random(cx * 7 + cz * 13 + (v.X != 0 ? 0 : 5)), ColorJitter = 0.05f };
        bridge.Build(mb, bridge.Center);
        var connector = new Connector
        {
            A = a, B = b, Bridge = bridge, Mesh = mb.Build(_gl),
            Rise = animate ? 0f : 1f,
        };
        Connectors.Add(connector);
        foreach (var cell in new[] { (a.X, a.Z), (b.X, b.Z) })
        {
            if (!_connectorsByCell.TryGetValue(cell, out var list)) _connectorsByCell[cell] = list = new List<Connector>(4);
            list.Add(connector);
        }
    }

    /// <summary>Libera un'isola e i ponti che la toccano.</summary>
    private void Unload(Zone zone)
    {
        _zones.Remove((zone.X, zone.Z));
        for (int i = Connectors.Count - 1; i >= 0; i--)
        {
            var c = Connectors[i];
            if (c.A != zone && c.B != zone) continue;
            Connectors.RemoveAt(i);
            foreach (var cell in new[] { (c.A.X, c.A.Z), (c.B.X, c.B.Z) })
                if (_connectorsByCell.TryGetValue(cell, out var list))
                {
                    list.Remove(c);
                    if (list.Count == 0) _connectorsByCell.Remove(cell);
                }
            c.Dispose();
        }
        zone.Dispose();
    }

    /// <summary>Solo per test: crea subito tutte le isole entro <paramref name="radius"/> celle dalla partenza.</summary>
    public void PreSpawn(int radius)
    {
        for (int x = -radius; x <= radius; x++)
            for (int z = -radius; z <= radius; z++)
                if (!_zones.ContainsKey((x, z))) Register(Generate(x, z), isNew: false);
    }

    public Zone? ZoneAt(Vector3 p) => _zones.GetValueOrDefault(CellOf(p));

    /// <summary>Distanza sul piano tra il punto e il rettangolo dell'isola nella cella (x, z).</summary>
    private static float CellDistance(int x, int z, Vector3 p)
    {
        float dx = MathF.Max(MathF.Abs(p.X - x * Spacing) - Zone.Half, 0f);
        float dz = MathF.Max(MathF.Abs(p.Z - z * Spacing) - Zone.Half, 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    /// <summary>
    /// Anima isole e ponti, completa le generazioni finite, fa nascere le isole nuove vicino alle uscite,
    /// ricarica quelle già scoperte che si avvicinano e libera quelle lontane.
    /// Restituisce l'eventuale isola appena scoperta.
    /// </summary>
    public Zone? Update(float dt, Vector3 player)
    {
        foreach (var z in _zones.Values) z.Update(dt);
        foreach (var c in Connectors) c.Update(dt);

        // 1. Al più un'isola pronta per fotogramma: il caricamento sulla GPU resta distribuito
        Zone? discovered = null;
        foreach (var (cell, (job, isNew)) in _pending)
        {
            if (!job.IsCompleted) continue;
            _pending.Remove(cell);
            if (job.IsFaulted)
                Console.WriteLine($"Generazione dell'isola {cell} fallita: {job.Exception?.GetBaseException().Message}");
            else
            {
                Register(job.Result, isNew);
                if (isNew) discovered = job.Result;
            }
            break;
        }

        int px = (int)MathF.Round(player.X / Spacing), pz = (int)MathF.Round(player.Z / Spacing);

        // 2. Isole nuove: nascono quando ci si avvicina a un'uscita dell'isola in cui si è
        if (ZoneAt(player) is { Ready: true } current)
            foreach (Dir d in DirExtensions.All)
            {
                if (!current.Exits[(int)d]) continue;
                var (dx, dz) = d.Offset();
                var cell = (current.X + dx, current.Z + dz);
                if (_discovered.Contains(cell)) continue;
                Vector3 exit = current.ExitPoint(d);
                if (new Vector2(player.X - exit.X, player.Z - exit.Z).Length() < SpawnDistance)
                    Request(cell.Item1, cell.Item2, isNew: true);
            }

        // 3. Isole già scoperte che tornano vicine: si ricaricano prima di entrare in vista
        for (int x = px - 2; x <= px + 2; x++)
            for (int z = pz - 2; z <= pz + 2; z++)
                if (_discovered.Contains((x, z)) && !_zones.ContainsKey((x, z)) && CellDistance(x, z, player) < LoadDistance)
                    Request(x, z, isNew: false);

        // 4. Isole lontane: si liberano (restano "scoperte" e torneranno identiche)
        List<Zone>? far = null;
        foreach (var zone in _zones.Values)
            if (CellDistance(zone.X, zone.Z, player) > UnloadDistance) (far ??= new()).Add(zone);
        if (far != null)
            foreach (var zone in far) Unload(zone);

        return discovered;
    }

    public bool IsWalkable(Vector3 p, float radius)
    {
        foreach (var c in ConnectorsNear(p))
            if (c.Ready && c.Bridge.TryGetHeight(p, 0.15f, out _)) return true;
        var zone = ZoneAt(p);
        return zone is { Ready: true } && zone.IsWalkable(p, radius);
    }

    /// <summary>Altezza della superficie cliccabile (terreno, ponti o pelo dell'acqua); false nel vuoto.</summary>
    public bool TryGetSurface(Vector3 p, out float height)
    {
        foreach (var c in ConnectorsNear(p))
            if (c.Ready && c.Bridge.TryGetHeight(p, 0f, out height))
            {
                height += c.YOffset;
                return true;
            }

        height = 0f;
        var zone = ZoneAt(p);
        if (zone is not { Ready: true }) return false;
        Vector3 local = p - zone.Origin;
        if (MathF.Abs(local.X) > Zone.Half || MathF.Abs(local.Z) > Zone.Half) return false;
        height = MathF.Max(zone.GroundHeight(p), Zone.WaterY + zone.YOffset);
        return true;
    }

    /// <summary>Interseca un raggio con le isole (ray marching + bisezione).</summary>
    public bool Raycast(Vector3 origin, Vector3 dir, out Vector3 hit)
    {
        const float step = 0.2f;
        Vector3 prev = origin;
        for (float t = 0f; t < 160f; t += step)
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

    public bool IsRoad(Vector3 p)
    {
        foreach (var c in ConnectorsNear(p))
            if (c.Bridge.TryGetHeight(p, 0f, out _)) return true;
        return ZoneAt(p)?.IsRoad(p) ?? false;
    }

    public float GroundHeight(Vector3 p)
    {
        foreach (var c in ConnectorsNear(p))
            if (c.Bridge.TryGetHeight(p, 0f, out float h)) return h + c.YOffset;
        return ZoneAt(p)?.GroundHeight(p) ?? 0f;
    }

    public void Dispose()
    {
        foreach (var z in _zones.Values) z.Dispose();
        foreach (var c in Connectors) c.Dispose();
    }
}
