using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

public enum LandmarkKind { Lighthouse, Windmill, Castle, WaterTower, Antenna, BellTower }

/// <summary>
/// Ingombro a terra per le collisioni: cerchio, oppure rettangolo ruotato (muri, case). Coordinate sul piano (x, z).
/// La rotazione segue Matrix4x4.CreateRotationY: un punto locale (x, z) va nel mondo in (x·c + z·s, −x·s + z·c).
/// </summary>
public readonly record struct Footprint(Vector2 Center, Vector2 Half, float Cos, float Sin, bool Round)
{
    public static Footprint Circle(Vector2 center, float r) => new(center, new Vector2(r, r), 1f, 0f, true);
    public static Footprint Box(Vector2 center, Vector2 half, float yaw) => new(center, half, MathF.Cos(yaw), MathF.Sin(yaw), false);

    /// <summary>Raggio del cerchio che contiene tutto l'ingombro.</summary>
    public float Reach => Round ? Half.X : Half.Length();

    /// <summary>Un cerchio di raggio <paramref name="r"/> in <paramref name="p"/> tocca l'ingombro?</summary>
    public bool Hits(Vector2 p, float r)
    {
        Vector2 d = p - Center;
        if (Round) return d.LengthSquared() < (Half.X + r) * (Half.X + r);
        float lx = d.X * Cos - d.Y * Sin, lz = d.X * Sin + d.Y * Cos;
        float ex = lx - Math.Clamp(lx, -Half.X, Half.X), ez = lz - Math.Clamp(lz, -Half.Y, Half.Y);
        return ex * ex + ez * ez < r * r;
    }

    /// <summary>Punto locale (rispetto a un centro ruotato di <paramref name="yaw"/>) portato nel mondo.</summary>
    public static Vector2 ToWorld(Vector2 origin, float yaw, Vector2 local)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return origin + new Vector2(local.X * c + local.Y * s, -local.X * s + local.Y * c);
    }
}

/// <summary>Un punto di riferimento della mappa: dove sta, quanto spazio occupa e il suo nome.</summary>
public sealed class LandmarkSite
{
    public char Key { get; private init; }
    public LandmarkKind Kind { get; private init; }
    public string Name { get; private init; } = "";
    /// <summary>Centro della base sul piano (x, z) e quota del terreno spianato sotto.</summary>
    public Vector2 Pos { get; private init; }
    public float BaseY { get; private init; }
    public float Yaw { get; private init; }
    /// <summary>Raggio della spianata attorno: lì niente alberi, case o falò.</summary>
    public float Radius { get; private init; }
    public List<Footprint> Footprints { get; } = new();

    public Vector3 Position => new(Pos.X, BaseY, Pos.Y);

    private static float RadiusOf(LandmarkKind kind) => kind switch
    {
        LandmarkKind.Lighthouse => 5f,
        LandmarkKind.Windmill => 5f,
        LandmarkKind.Castle => 10f,
        LandmarkKind.WaterTower => 4f,
        LandmarkKind.Antenna => 4f,
        _ => 3.5f,
    };

    /// <summary>
    /// Cerca il posto attorno al punto indicato dalla mappa (<paramref name="center"/>): parte da lì e si allarga a spirale
    /// finché non trova terra asciutta, lontana dai fiumi e dalla piazza delle città. Null se non c'è spazio.
    /// </summary>
    public static LandmarkSite? Place(char key, LandmarkKind kind, string name, Vector2 center, int i, int j)
    {
        float r = RadiusOf(kind);
        for (int k = 0; k < 400; k++)
        {
            float a = k * 2.39996f, dist = MathF.Sqrt(k) * 2.2f; // spirale di Fermat: copre l'area in modo uniforme
            var p = center + new Vector2(MathF.Cos(a), MathF.Sin(a)) * dist;
            if (RoadNetwork.Current.Settlements.Any(s => Vector2.Distance(p, s.Pos) < (s.City ? 20f : 12f) + r)) continue;
            if (Terrain.RiverDistance(p.X, p.Y) < r + Zone.RiverHalf + 3f) continue;
            if (GameMap.Current.Sample(p.X, p.Y).Land < 0.9f) continue;
            float yaw = kind == LandmarkKind.Castle
                ? MathF.PI / 2f * (int)(Noise.Hash01(i, j, 811) * 4f)  // il castello resta allineato alla griglia
                : Noise.Hash01(i, j, 811) * MathF.Tau;
            var site = new LandmarkSite
            {
                Key = key, Kind = kind, Name = name, Pos = p, Yaw = yaw, Radius = r,
                BaseY = Terrain.Height(p.X, p.Y), // prima di aggiungerlo: quota naturale del terreno lì
            };
            site.AddFootprints();
            return site;
        }
        return null;
    }

    private void Circle(float x, float z, float r) => Footprints.Add(Footprint.Circle(Footprint.ToWorld(Pos, Yaw, new(x, z)), r));
    private void Box(float x, float z, float hx, float hz) =>
        Footprints.Add(Footprint.Box(Footprint.ToWorld(Pos, Yaw, new(x, z)), new Vector2(hx, hz), Yaw));

    private void AddFootprints()
    {
        switch (Kind)
        {
            case LandmarkKind.Lighthouse:
                Circle(0, 0, 2.0f);
                Box(2.7f, 0, 1.2f, 1.1f); // casa del guardiano
                break;
            case LandmarkKind.Windmill:
                Circle(0, 0, 2.3f);
                break;
            case LandmarkKind.Castle:
                // Mura con una porta a sud (ci si entra in auto), torri agli angoli e mastio
                Box(0, -6f, 6f, 0.45f);
                Box(-6f, 0, 0.45f, 6f);
                Box(6f, 0, 0.45f, 6f);
                Box(-3.9f, 6f, 2.1f, 0.45f);
                Box(3.9f, 6f, 2.1f, 0.45f);
                foreach (float sx in new[] { -6f, 6f })
                    foreach (float sz in new[] { -6f, 6f })
                        Circle(sx, sz, 1.5f);
                Box(0, -1.8f, 1.8f, 1.8f);
                break;
            case LandmarkKind.WaterTower:
                // Solo le quattro zampe: sotto la cisterna si passa
                foreach (float sx in new[] { -1.6f, 1.6f })
                    foreach (float sz in new[] { -1.6f, 1.6f })
                        Circle(sx, sz, 0.3f);
                break;
            case LandmarkKind.Antenna:
                Circle(0, 0, 1.0f);
                Box(2.2f, 0.4f, 1.0f, 0.8f);
                break;
            default:
                Box(0, 0, 1.45f, 1.45f);
                break;
        }
    }

    /// <summary>Un cerchio nel punto tocca il punto di riferimento?</summary>
    public bool Hits(Vector2 p, float r)
    {
        if (Vector2.DistanceSquared(p, Pos) > (Radius + r + 2f) * (Radius + r + 2f)) return false;
        foreach (var f in Footprints)
            if (f.Hits(p, r)) return true;
        return false;
    }
}

/// <summary>
/// Modello 3D di un punto di riferimento. Le parti luminose (lanterna del faro, luce dell'antenna) sono a parte
/// per accendersi di notte; le pale del mulino girano.
/// </summary>
public sealed class LandmarkModel : IDisposable
{
    public LandmarkSite Site { get; }
    private readonly Mesh _body;
    private readonly Mesh? _glow, _rotor;
    private readonly Vector3 _rotorPivot;
    private readonly Matrix4x4 _model;
    private readonly Vector4 _area;

    private static readonly Vector3 Stone = new(0.70f, 0.68f, 0.62f);
    private static readonly Vector3 DarkStone = new(0.52f, 0.50f, 0.47f);
    private static readonly Vector3 White = new(0.95f, 0.94f, 0.90f);
    private static readonly Vector3 Red = new(0.84f, 0.20f, 0.16f);
    private static readonly Vector3 Roof = new(0.62f, 0.24f, 0.18f);
    private static readonly Vector3 Wood = new(0.50f, 0.33f, 0.20f);
    private static readonly Vector3 Dark = new(0.18f, 0.16f, 0.16f);
    private static readonly Vector3 Steel = new(0.48f, 0.54f, 0.60f);
    private static readonly Vector3 Lamp = new(1.00f, 0.88f, 0.55f);

    public LandmarkModel(GL gl, LandmarkSite site)
    {
        Site = site;
        var rng = new Random(site.Key * 7919 + (int)site.Kind);
        var body = new MeshBuilder { ColorRng = rng, ColorJitter = 0.03f, ComputeEdges = true };
        var glow = new MeshBuilder();
        MeshBuilder? rotor = null;

        // Un unico oggetto: diventa trasparente per intero se copre il giocatore
        body.BeginObject();
        switch (site.Kind)
        {
            case LandmarkKind.Lighthouse: Lighthouse(body, glow); break;
            case LandmarkKind.Windmill:
                Windmill(body);
                rotor = new MeshBuilder { ColorRng = rng, ColorJitter = 0.04f };
                Sails(rotor);
                _rotorPivot = new Vector3(0f, 6.3f, 1.75f);
                break;
            case LandmarkKind.Castle: Castle(body); break;
            case LandmarkKind.WaterTower: WaterTower(body); break;
            case LandmarkKind.Antenna: Antenna(body, glow); break;
            default: BellTower(body); break;
        }
        body.EndObject();

        _body = body.Build(gl);
        _glow = glow.IsEmpty ? null : glow.Build(gl);
        _rotor = rotor?.Build(gl);
        _model = Matrix4x4.CreateRotationY(site.Yaw) * Matrix4x4.CreateTranslation(site.Position);
        float reach = site.Radius + 4f;
        _area = new Vector4(site.Pos.X - reach, site.Pos.Y - reach, site.Pos.X + reach, site.Pos.Y + reach);
    }

    /// <param name="night">0 di giorno, 1 a notte fonda: accende lanterne e luci.</param>
    public void Collect(List<DrawItem> items, float time, float night)
    {
        items.Add(new DrawItem(_body, _model, SeeThrough: true, Area: _area));
        if (_glow != null)
            items.Add(new DrawItem(_glow, _model, 0.25f + 1.1f * night, CastShadow: false, Area: _area));
        if (_rotor != null)
            items.Add(new DrawItem(_rotor, Matrix4x4.CreateRotationZ(time * 0.7f) * Matrix4x4.CreateTranslation(_rotorPivot) * _model,
                                   Area: _area));
    }

    // ------------------------------------------------------------------ modelli (coordinate locali, base a y = 0)

    /// <summary>Tetto a due falde su una scatola larga sx, profonda sz, con il colmo lungo x.</summary>
    private static void Gable(MeshBuilder mb, Vector3 baseCenter, float sx, float sz, float h, Vector3 color)
    {
        float hx = sx * 0.5f + 0.15f, hz = sz * 0.5f + 0.2f;
        Vector3 a = baseCenter + new Vector3(-hx, 0, -hz), b = baseCenter + new Vector3(hx, 0, -hz);
        Vector3 c = baseCenter + new Vector3(hx, 0, hz), d = baseCenter + new Vector3(-hx, 0, hz);
        Vector3 r0 = baseCenter + new Vector3(-hx, h, 0), r1 = baseCenter + new Vector3(hx, h, 0);
        mb.AddQuad(a, b, r1, r0, color);
        mb.AddQuad(c, d, r0, r1, color);
        mb.AddTriangle(d, a, r0, color * 0.9f);
        mb.AddTriangle(b, c, r1, color * 0.9f);
    }

    private static void Lighthouse(MeshBuilder mb, MeshBuilder glow)
    {
        mb.AddPrism(Vector3.Zero + new Vector3(0, -0.5f, 0), 2.1f, 2.0f, 1.1f, 8, DarkStone);
        // Torre a fasce bianche e rosse, che si stringe verso l'alto
        const int bands = 6;
        float y = 0.6f, top = 11f;
        for (int k = 0; k < bands; k++)
        {
            float y0 = y + (top - y) * k / bands, y1 = y + (top - y) * (k + 1) / bands;
            float r0 = Noise.Lerp(1.65f, 1.0f, (float)k / bands), r1 = Noise.Lerp(1.65f, 1.0f, (float)(k + 1) / bands);
            mb.AddPrism(new Vector3(0, y0, 0), r0, r1, y1 - y0, 8, k % 2 == 0 ? White : Red);
        }
        mb.AddPrism(new Vector3(0, 11f, 0), 1.55f, 1.55f, 0.25f, 8, Dark);          // ballatoio
        for (int k = 0; k < 8; k++)                                                   // ringhiera
        {
            float a = MathF.Tau * k / 8f;
            mb.AddBox(new Vector3(MathF.Cos(a) * 1.45f, 11.55f, MathF.Sin(a) * 1.45f), new Vector3(0.08f, 0.6f, 0.08f), Dark);
        }
        glow.AddPrism(new Vector3(0, 11.25f, 0), 0.85f, 0.85f, 1.25f, 8, Lamp);       // lanterna
        mb.AddPrism(new Vector3(0, 12.5f, 0), 1.0f, 1.0f, 0.12f, 8, Dark);
        mb.AddCone(new Vector3(0, 12.6f, 0), 1.0f, 1.0f, 8, Red);
        mb.AddBox(new Vector3(0, 0.95f, 1.62f), new Vector3(0.7f, 1.3f, 0.12f), Wood); // porta
        // Casa del guardiano
        mb.AddBox(new Vector3(2.7f, 0.6f, 0), new Vector3(2.4f, 2.0f, 2.2f), White);
        Gable(mb, new Vector3(2.7f, 1.6f, 0), 2.4f, 2.2f, 1.1f, Roof);
    }

    private static void Windmill(MeshBuilder mb)
    {
        mb.AddPrism(new Vector3(0, -0.4f, 0), 2.3f, 1.5f, 6.9f, 8, Stone);
        mb.AddPrism(new Vector3(0, 6.5f, 0), 1.75f, 1.75f, 0.3f, 8, Wood);
        mb.AddCone(new Vector3(0, 6.8f, 0), 1.9f, 1.9f, 8, Wood * 0.85f);
        mb.AddBox(new Vector3(0, 0.9f, 2.15f), new Vector3(0.9f, 1.6f, 0.2f), Wood);           // porta
        mb.AddBox(new Vector3(0, 3.8f, 1.85f), new Vector3(0.6f, 0.7f, 0.12f), Dark);          // finestre
        mb.AddBox(new Vector3(1.6f, 2.6f, 0.9f), new Vector3(0.12f, 0.6f, 0.5f), Dark);
        mb.AddBox(new Vector3(0, 6.3f, 1.55f), new Vector3(0.5f, 0.5f, 0.5f), Wood);           // mozzo
    }

    /// <summary>Quattro pale del mulino attorno all'origine, nel piano XY (girano attorno a Z).</summary>
    private static void Sails(MeshBuilder mb)
    {
        var sail = new Vector3(0.92f, 0.90f, 0.84f);
        mb.AddBox(Vector3.Zero, new Vector3(0.45f, 0.45f, 0.4f), Wood);
        for (int k = 0; k < 4; k++)
        {
            Matrix4x4 old = mb.Transform;
            mb.Transform = Matrix4x4.CreateRotationZ(MathF.PI / 2f * k) * old;
            mb.AddBox(new Vector3(0, 2.3f, 0.05f), new Vector3(0.14f, 4.4f, 0.12f), Wood);     // braccio
            mb.AddBox(new Vector3(0.38f, 2.75f, 0.1f), new Vector3(0.62f, 3.3f, 0.05f), sail);  // vela
            for (int r = 0; r < 4; r++)                                                           // listelli
                mb.AddBox(new Vector3(0.38f, 1.4f + r * 0.95f, 0.14f), new Vector3(0.66f, 0.06f, 0.04f), Wood);
            mb.Transform = old;
        }
    }

    private static void Castle(MeshBuilder mb)
    {
        const float H = 3.4f, T = 0.9f;
        void Wall(float x, float z, float sx, float sz)
        {
            mb.AddBox(new Vector3(x, H * 0.5f - 0.5f, z), new Vector3(sx, H + 1f, sz), Stone);
            // Merli lungo la cima
            bool alongX = sx > sz;
            float len = alongX ? sx : sz;
            int n = (int)(len / 0.9f);
            for (int k = 0; k < n; k += 2)
            {
                float t = -len * 0.5f + (k + 0.5f) * len / n;
                var c = new Vector3(x + (alongX ? t : 0), H + 0.25f, z + (alongX ? 0 : t));
                mb.AddBox(c, alongX ? new Vector3(len / n, 0.5f, sz) : new Vector3(sx, 0.5f, len / n), Stone);
            }
        }
        Wall(0, -6f, 12f, T);
        Wall(-6f, 0, T, 12f);
        Wall(6f, 0, T, 12f);
        Wall(-3.9f, 6f, 4.2f, T);
        Wall(3.9f, 6f, 4.2f, T);
        mb.AddBox(new Vector3(0, H + 0.15f, 6f), new Vector3(3.6f, 0.9f, T), Stone);             // arco sopra la porta
        foreach (float sx in new[] { -6f, 6f })
            foreach (float sz in new[] { -6f, 6f })
            {
                mb.AddPrism(new Vector3(sx, -0.5f, sz), 1.55f, 1.45f, 6.6f, 8, Stone);
                mb.AddCone(new Vector3(sx, 6.1f, sz), 1.8f, 2.2f, 8, Roof);
            }
        // Mastio con merli e bandiera
        mb.AddBox(new Vector3(0, 4.25f, -1.8f), new Vector3(3.6f, 9.5f, 3.6f), DarkStone);
        for (int k = 0; k < 4; k++)
        {
            float a = MathF.PI / 2f * k;
            var d = new Vector3(MathF.Cos(a), 0, MathF.Sin(a));
            var side = new Vector3(-d.Z, 0, d.X);
            foreach (float s in new[] { -1.2f, 0f, 1.2f })
                mb.AddBox(new Vector3(0, 9.3f, -1.8f) + d * 1.6f + side * s, new Vector3(0.5f, 0.6f, 0.5f), DarkStone);
            mb.AddBox(new Vector3(0, 6.5f, -1.8f) + d * 1.81f, new Vector3(0.4f, 0.9f, 0.4f), Dark); // feritoie
        }
        mb.AddBox(new Vector3(0, 10.8f, -1.8f), new Vector3(0.1f, 2.6f, 0.1f), Dark);
        mb.AddBox(new Vector3(0.55f, 11.6f, -1.8f), new Vector3(1.0f, 0.65f, 0.05f), Red);
        mb.AddBox(new Vector3(0, 1.0f, 0.05f), new Vector3(1.2f, 2.0f, 0.1f), Wood);              // portone del mastio
    }

    private static void WaterTower(MeshBuilder mb)
    {
        foreach (float sx in new[] { -1.6f, 1.6f })
            foreach (float sz in new[] { -1.6f, 1.6f })
                mb.AddPrism(new Vector3(sx, -0.4f, sz), 0.22f, 0.18f, 8.8f, 6, Steel);
        foreach (float y in new[] { 3f, 6f })
        {
            mb.AddBox(new Vector3(0, y, -1.6f), new Vector3(3.2f, 0.14f, 0.14f), Steel);
            mb.AddBox(new Vector3(0, y, 1.6f), new Vector3(3.2f, 0.14f, 0.14f), Steel);
            mb.AddBox(new Vector3(-1.6f, y, 0), new Vector3(0.14f, 0.14f, 3.2f), Steel);
            mb.AddBox(new Vector3(1.6f, y, 0), new Vector3(0.14f, 0.14f, 3.2f), Steel);
        }
        var tank = new Vector3(0.62f, 0.72f, 0.80f);
        mb.AddPrism(new Vector3(0, 8.2f, 0), 2.5f, 2.5f, 3.0f, 12, tank);
        mb.AddPrism(new Vector3(0, 9.3f, 0), 2.56f, 2.56f, 0.5f, 12, White);                     // fascia
        mb.AddCone(new Vector3(0, 11.2f, 0), 2.65f, 1.1f, 12, tank * 0.8f);
        mb.AddBox(new Vector3(0, 4.1f, 1.75f), new Vector3(0.5f, 8.2f, 0.06f), Dark);              // scala
    }

    private static void Antenna(MeshBuilder mb, MeshBuilder glow)
    {
        var concrete = new Vector3(0.66f, 0.66f, 0.64f);
        mb.AddBox(new Vector3(0, 0, 0), new Vector3(2.0f, 0.6f, 2.0f), concrete);
        const int segs = 7;
        const float top = 25f;
        for (int k = 0; k < segs; k++)
        {
            float y0 = 0.3f + top * k / segs, y1 = 0.3f + top * (k + 1) / segs;
            float r0 = Noise.Lerp(0.85f, 0.18f, (float)k / segs), r1 = Noise.Lerp(0.85f, 0.18f, (float)(k + 1) / segs);
            mb.AddPrism(new Vector3(0, y0, 0), r0, r1, y1 - y0, 4, k % 2 == 0 ? Red : White, MathF.PI / 4f);
        }
        foreach (float y in new[] { 9f, 17f })                                                    // parabole
            mb.AddBox(new Vector3(0.5f, y, 0), new Vector3(0.2f, 0.8f, 0.8f), White);
        glow.AddBox(new Vector3(0, top + 0.5f, 0), new Vector3(0.35f, 0.35f, 0.35f), new Vector3(1f, 0.15f, 0.1f));
        mb.AddBox(new Vector3(2.2f, 0.5f, 0.4f), new Vector3(2.0f, 1.6f, 1.6f), concrete);       // casotto
        mb.AddBox(new Vector3(2.2f, 0.55f, 1.22f), new Vector3(0.6f, 1.1f, 0.06f), Dark);
    }

    private static void BellTower(MeshBuilder mb)
    {
        var brick = new Vector3(0.74f, 0.44f, 0.33f);
        mb.AddBox(new Vector3(0, 6f, 0), new Vector3(2.8f, 13f, 2.8f), brick);
        foreach (float y in new[] { 4f, 8.6f, 12.4f })                                              // cornici
            mb.AddBox(new Vector3(0, y, 0), new Vector3(3.05f, 0.22f, 3.05f), White);
        for (int k = 0; k < 4; k++)
        {
            Matrix4x4 old = mb.Transform;
            mb.Transform = Matrix4x4.CreateRotationY(MathF.PI / 2f * k) * old;
            foreach (float x in new[] { -0.55f, 0.55f })                                            // cella campanaria
                mb.AddBox(new Vector3(x, 11.1f, 1.36f), new Vector3(0.6f, 1.7f, 0.12f), Dark);
            mb.AddBox(new Vector3(0, 10.0f, 1.42f), new Vector3(1.1f, 1.1f, 0.06f), White);         // orologio
            mb.AddBox(new Vector3(0, 10.15f, 1.46f), new Vector3(0.07f, 0.45f, 0.04f), Dark);
            mb.AddBox(new Vector3(0.14f, 10.0f, 1.46f), new Vector3(0.32f, 0.07f, 0.04f), Dark);
            mb.Transform = old;
        }
        mb.AddCone(new Vector3(0, 12.5f, 0), 2.15f, 3.2f, 4, new Vector3(0.45f, 0.26f, 0.2f), MathF.PI / 4f);
        mb.AddBox(new Vector3(0, 1.0f, 1.42f), new Vector3(0.9f, 1.9f, 0.08f), Wood);
    }

    public void Dispose()
    {
        _body.Dispose();
        _glow?.Dispose();
        _rotor?.Dispose();
    }
}
