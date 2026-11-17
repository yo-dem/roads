using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

/// <summary>Ponte che collega due isole adiacenti.</summary>
public sealed class Connector : IDisposable
{
    public required Zone A { get; init; }
    public required Zone B { get; init; }
    public required Bridge Bridge { get; init; }
    public required Mesh Mesh { get; init; }

    public bool Ready => A.Ready && B.Ready;
    public float YOffset => MathF.Min(A.YOffset, B.YOffset);
    public Matrix4x4 Model => Matrix4x4.CreateTranslation(0, YOffset, 0);

    public void Dispose() => Mesh.Dispose();
}

/// <summary>
/// Griglia infinita di isole. Le isole vengono create quando il giocatore si avvicina
/// a un'uscita; le uscite sono proprietà dei lati tra due celle, quindi sempre coerenti.
/// </summary>
public sealed class WorldMap : IDisposable
{
    public const float Gap = 4f;
    public const float Spacing = Zone.Half * 2f + Gap;
    private const float SpawnDistance = 7f;

    private readonly GL _gl;
    private readonly Dictionary<(int, int), Zone> _zones = new();
    public IEnumerable<Zone> Zones => _zones.Values;
    public List<Connector> Connectors { get; } = new();
    public int Count => _zones.Count;

    public WorldMap(GL gl)
    {
        _gl = gl;
        Spawn(0, 0).Rise = 1f;
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

    private Zone Spawn(int x, int z)
    {
        var exits = DirExtensions.All.Select(d => EdgeOpen(x, z, d)).ToArray();
        var zone = new Zone(_gl, x, z, exits);
        _zones[(x, z)] = zone;

        foreach (Dir d in DirExtensions.All)
        {
            if (!exits[(int)d]) continue;
            var (dx, dz) = d.Offset();
            if (_zones.TryGetValue((x + dx, z + dz), out var other))
                AddConnector(zone, other, d);
        }
        return zone;
    }

    private void AddConnector(Zone a, Zone b, Dir d)
    {
        Vector3 v = d.Vec();
        var bridge = new Bridge
        {
            Center = a.Origin + v * (Zone.Half + Gap * 0.5f),
            Dir = new Vector3(MathF.Abs(v.X), 0, MathF.Abs(v.Z)),
            Span = Gap + 1.2f,
            BaseY = Zone.PathRaise,
            Arch = 0.15f,
        };
        var mb = new MeshBuilder { ColorRng = new Random(a.X * 7 + a.Z * 13), ColorJitter = 0.05f };
        bridge.Build(mb, bridge.Center);
        Connectors.Add(new Connector { A = a, B = b, Bridge = bridge, Mesh = mb.Build(_gl) });
    }

    public Zone? ZoneAt(Vector3 p)
    {
        int x = (int)MathF.Round(p.X / Spacing), z = (int)MathF.Round(p.Z / Spacing);
        return _zones.GetValueOrDefault((x, z));
    }

    /// <summary>Anima le isole e crea quelle nuove vicino alle uscite. Restituisce l'eventuale isola nuova.</summary>
    public Zone? Update(float dt, Vector3 player)
    {
        foreach (var z in _zones.Values) z.Update(dt);

        var current = ZoneAt(player);
        if (current is not { Ready: true }) return null;

        foreach (Dir d in DirExtensions.All)
        {
            if (!current.Exits[(int)d]) continue;
            var (dx, dz) = d.Offset();
            if (_zones.ContainsKey((current.X + dx, current.Z + dz))) continue;
            Vector3 exit = current.ExitPoint(d);
            float dist = new Vector2(player.X - exit.X, player.Z - exit.Z).Length();
            if (dist < SpawnDistance)
                return Spawn(current.X + dx, current.Z + dz);
        }
        return null;
    }

    public bool IsWalkable(Vector3 p, float radius)
    {
        foreach (var c in Connectors)
            if (c.Ready && c.Bridge.TryGetHeight(p, 0.15f, out _)) return true;
        var zone = ZoneAt(p);
        return zone is { Ready: true } && zone.IsWalkable(p, radius);
    }

    /// <summary>Altezza della superficie cliccabile (terreno, ponti o pelo dell'acqua); false nel vuoto.</summary>
    public bool TryGetSurface(Vector3 p, out float height)
    {
        foreach (var c in Connectors)
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

    public float GroundHeight(Vector3 p)
    {
        foreach (var c in Connectors)
            if (c.Bridge.TryGetHeight(p, 0f, out float h)) return h + c.YOffset;
        return ZoneAt(p)?.GroundHeight(p) ?? 0f;
    }

    public void Dispose()
    {
        foreach (var z in _zones.Values) z.Dispose();
        foreach (var c in Connectors) c.Dispose();
    }
}
