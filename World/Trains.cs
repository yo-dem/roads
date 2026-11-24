using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

/// <summary>
/// Ciò che sulla ferrovia si muove: il treno (locomotiva e due carrozze) che gira sull'anello e si ferma a ogni
/// stazione, e le sbarre dei passaggi a livello, che si abbassano col treno in arrivo mentre le luci rosse lampeggiano.
/// Treno e sbarre abbassate sono ostacoli: l'auto e il personaggio non li attraversano.
/// </summary>
public sealed class Trains : IDisposable
{
    public static Trains? Current { get; private set; }

    private const float MaxSpeed = 7.5f, Accel = 1.4f, Decel = 1.6f, Dwell = 9f;
    private const float LocoLength = 6f, CarLength = 6.6f, Gap = 0.45f;
    private const float HalfWidth = 0.95f;
    private const float WarnAhead = 30f;      // le sbarre si abbassano quando il muso è a questa distanza
    private const float ArmSpeed = 1.3f;      // radianti al secondo

    private readonly Railway _rail;
    private readonly Mesh _loco, _car, _carGlow, _locoGlow, _post, _arm, _lamp, _interior, _interiorGlow;
    private float _head;          // distanza del muso lungo l'anello
    private float _speed;
    private float _dwell;
    private int _nextStop;
    private int _at = -1;          // stazione dove il treno è fermo (-1 in viaggio)

    // ------------------------------------------------------------------ viaggiatori

    /// <summary>La stazione dove il treno è fermo con le porte aperte (null mentre viaggia).</summary>
    public RailStation? StoppedAt => _at >= 0 && _dwell > 0.6f ? _rail.Stations[_at] : null;

    /// <summary>Secondi che mancano alla partenza (0 se viaggia).</summary>
    public float DepartsIn => _at >= 0 ? MathF.Max(_dwell, 0f) : 0f;

    /// <summary>
    /// Chi sta in <paramref name="p"/> può salire? Il treno è fermo in stazione e il punto è accanto a una carrozza
    /// (non alla locomotiva), dalla parte della banchina. Restituisce il punto della porta.
    /// </summary>
    public bool CanBoard(Vector2 p, out Vector3 door)
    {
        door = default;
        if (StoppedAt is not { } st) return false;
        foreach (var (c, d, len, loco) in Cars())
        {
            if (loco) continue;
            Vector2 side = new(-d.Y, d.X), rel = p - c;
            float along = Vector2.Dot(rel, d), across = Vector2.Dot(rel, side) * st.Side;
            if (MathF.Abs(along) > len * 0.5f - 0.3f || across < HalfWidth - 0.1f || across > HalfWidth + 1.8f) continue;
            Vector2 at = c + d * along + side * st.Side * HalfWidth;
            door = new Vector3(at.X, Terrain.Height(at.X, at.Y) + Railway.RailTop + 0.9f, at.Y);
            return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ dentro la carrozza

    /// <summary>Chi viaggia è dentro la prima carrozza: la si disegna dall'interno (senza le pareti esterne).</summary>
    public bool Inside;

    /// <summary>Quota del pavimento della carrozza (locale, sopra il ferro).</summary>
    public const float Floor = 0.62f;

    // L'interno è costruito a misura di chi viaggia rimpicciolito (InsideScale): nella stessa carrozza stanno così
    // posti a coppie coi tavolini, cappelliere, toilette, distributore, banco del caffè, posto bici, come in un treno vero.
    // (Da dentro la cassa esterna non si vede: l'interno può avere le sue proporzioni.)

    /// <summary>Quanto è grande il personaggio dentro la carrozza rispetto a fuori.</summary>
    public const float InsideScale = 0.6f;
    /// <summary>Altezza del soffitto sopra il pavimento (per il personaggio rimpicciolito: circa due volte lui).</summary>
    public const float CeilingHeight = 0.95f;
    public static float Top => Floor + CeilingHeight;
    /// <summary>Fin dove si cammina: di lato (pareti) e in lungo (pareti di fondo).</summary>
    public const float WalkX = 0.86f;
    public static float CabinEnd => CarLength * 0.5f - 0.14f;
    /// <summary>Le porte per scendere stanno nel vestibolo davanti, verso la locomotiva (+Z), sui due fianchi.</summary>
    public static float DoorZ => CarLength * 0.5f - 0.42f;
    /// <summary>Mezza larghezza del corridoio tra i posti.</summary>
    public const float AisleHalf = 0.25f;

    /// <summary>Le file di posti: ognuna è una coppia di sedili affacciati con il tavolino in mezzo, per lato.</summary>
    private static readonly float[] Bays = [-1.9f, -0.95f, 0f, 0.95f];
    private const float BayHalf = 0.47f;

    /// <summary>Un posto: centro del sedile, lato del finestrino (+1 destra della marcia) e verso cui guarda (+1 la marcia).</summary>
    public readonly record struct TrainSeat(float X, float Z, float Side, float Facing);

    public static readonly TrainSeat[] Seats =
        Bays.SelectMany(b => new[] { 1f, -1f }.SelectMany(side => new[] { 0.42f, 0.7f }.SelectMany(x => new[]
        {
            new TrainSeat(side * x, b - 0.3f, side, 1f),   // guarda avanti, schienale verso la coda
            new TrainSeat(side * x, b + 0.3f, side, -1f),  // di fronte, guarda indietro
        }))).ToArray();

    /// <summary>Gli ingombri sul pavimento (locale: x0, z0, x1, z1) dove non si cammina: posti, mobili, bici, pali.</summary>
    public static readonly (float X0, float Z0, float X1, float Z1)[] Furniture = BuildFurniture();

    private static (float, float, float, float)[] BuildFurniture()
    {
        var list = new List<(float, float, float, float)>();
        foreach (float b in Bays)
        {
            list.Add((AisleHalf, b - BayHalf, 0.93f, b + BayHalf));
            list.Add((-0.93f, b - BayHalf, -AisleHalf, b + BayHalf));
        }
        float back = -CarLength * 0.5f;
        list.Add((0.38f, back, 0.93f, back + 0.72f));          // toilette
        list.Add((-0.93f, back, -0.5f, back + 0.5f));           // scaffale dei bagagli
        list.Add((-0.36f, back, -0.2f, back + 0.16f));          // cestino
        list.Add((-0.93f, 1.6f, -0.6f, 1.95f));                 // distributore
        list.Add((-0.93f, 2.05f, -0.6f, 2.5f));                 // banco del caffè
        list.Add((0.62f, 1.58f, 0.93f, 2.42f));                 // posto bici
        foreach (float x in new[] { -0.3f, 0.3f })
            list.Add((x - 0.035f, 2.6f - 0.035f, x + 0.035f, 2.6f + 0.035f)); // corrimano verticali
        return list.ToArray();
    }

    /// <summary>Un cerchio di raggio <paramref name="r"/> (locale) sta sul pavimento libero della carrozza?</summary>
    public static bool FreeInside(Vector2 p, float r)
    {
        if (MathF.Abs(p.X) > WalkX - r + 0.07f || MathF.Abs(p.Y) > CabinEnd - r + 0.1f) return false;
        foreach (var (x0, z0, x1, z1) in Furniture)
        {
            float cx = Math.Clamp(p.X, x0, x1), cz = Math.Clamp(p.Y, z0, z1);
            if ((p.X - cx) * (p.X - cx) + (p.Y - cz) * (p.Y - cz) < r * r) return false;
        }
        return true;
    }

    /// <summary>Trasformazione della prima carrozza (locale → mondo): +Z verso la marcia, y = 0 sul ferro.</summary>
    public Matrix4x4 RideMatrix
    {
        get
        {
            var (c, d, _, _) = Cars().ElementAt(1);
            return Matrix4x4.CreateRotationY(MathF.Atan2(d.X, d.Y))
                   * Matrix4x4.CreateTranslation(c.X, Terrain.Height(c.X, c.Y) + Railway.RailTop, c.Y);
        }
    }

    /// <summary>Lato della banchina rispetto alla carrozza (+1 destra della marcia), se il treno è fermo.</summary>
    public float PlatformSide => StoppedAt?.Side ?? 1f;

    /// <summary>Dove sta chi viaggia: in mezzo alla prima carrozza, e la direzione di marcia (yaw).</summary>
    public (Vector3 Pos, float Yaw) Seat
    {
        get
        {
            var (c, d, _, _) = Cars().ElementAt(1);
            return (new Vector3(c.X, Terrain.Height(c.X, c.Y) + Railway.RailTop + 0.6f, c.Y), MathF.Atan2(d.X, d.Y));
        }
    }

    /// <summary>
    /// Dove si scende, se il treno è fermo in stazione: sulla banchina accanto alla prima carrozza, con lo sguardo
    /// rivolto fuori dai binari.
    /// </summary>
    public bool AlightPoint(out Vector3 spot, out float yaw)
    {
        spot = default;
        yaw = 0f;
        if (StoppedAt is not { } st) return false;
        var (c, d, _, _) = Cars().ElementAt(1);
        Vector2 out2 = new Vector2(-d.Y, d.X) * st.Side;
        Vector2 at = c + out2 * (HalfWidth + 0.9f);
        spot = new Vector3(at.X, Terrain.Height(at.X, at.Y) + Railway.PlatformHeight, at.Y);
        yaw = MathF.Atan2(out2.X, out2.Y);
        return true;
    }
    private readonly float[] _armAngle; // per passaggio a livello: 0 = abbassata, 1 = alzata
    private readonly bool[] _closed;
    private float _time;

    private static readonly float[] Lengths = [LocoLength, CarLength, CarLength];
    private static float TrainLength => LocoLength + 2f * (CarLength + Gap);

    public Trains(GL gl, Railway rail)
    {
        _rail = rail;
        _armAngle = Enumerable.Repeat(1f, rail.Crossings.Count).ToArray();
        _closed = new bool[rail.Crossings.Count];
        (_loco, _locoGlow) = BuildLoco(gl);
        (_car, _carGlow) = BuildCarriage(gl);
        (_interior, _interiorGlow) = BuildInterior(gl);
        _post = BuildPost(gl);
        _arm = BuildArm(gl);
        var lb = new MeshBuilder();
        lb.AddBox(Vector3.Zero, new Vector3(0.13f, 0.13f, 0.05f), new Vector3(1f, 0.15f, 0.1f));
        _lamp = lb.Build(gl);
        // Si parte fermi alla prima stazione
        if (rail.Stations.Count > 0) _head = rail.Wrap(rail.Stations[0].S + TrainLength * 0.5f);
        _dwell = Dwell; // si parte fermi in stazione, con tutto il tempo per salire
        _at = rail.Stations.Count > 0 ? 0 : -1;
        _nextStop = rail.Stations.Count > 1 ? 1 : 0;
        Current = this;
    }

    // ------------------------------------------------------------------ movimento

    public void Update(float dt)
    {
        _time += dt;
        if (!_rail.Exists) return;

        if (_dwell > 0f)
        {
            _dwell -= dt;
            if (_dwell <= 0f) _at = -1; // si riparte
        }
        else
        {
            // Frenata dolce fino alla prossima stazione: il treno si ferma con la sua metà davanti alla banchina
            float allowed = MaxSpeed;
            if (_rail.Stations.Count > 0)
            {
                var st = _rail.Stations[_nextStop];
                float stop = _rail.Wrap(st.S + TrainLength * 0.5f);
                float d = _rail.Ahead(_head, stop);
                if (d < 0.08f && _speed < 0.6f)
                {
                    _speed = 0f;
                    _dwell = Dwell;
                    _at = _nextStop;
                    _nextStop = (_nextStop + 1) % _rail.Stations.Count;
                }
                else allowed = MathF.Min(allowed, MathF.Sqrt(2f * Decel * MathF.Max(d - 0.05f, 0f)) + 0.3f);
            }
            _speed = MathF.Min(_speed + Accel * dt, allowed);
            _head = _rail.Wrap(_head + _speed * dt);
        }

        // Passaggi a livello: chiusi se il treno arriva o li sta ancora occupando
        for (int i = 0; i < _rail.Crossings.Count; i++)
        {
            float s = _rail.Crossings[i].S;
            _closed[i] = _rail.Ahead(_head, s) < WarnAhead || _rail.Ahead(s, _head) < TrainLength + 4f;
            _armAngle[i] = MathF.Max(0f, MathF.Min(1f, _armAngle[i] + (_closed[i] ? -1f : 1f) * ArmSpeed * dt / 1.4f));
        }
    }

    /// <summary>Posa di ogni vagone: centro (sul piano), direzione e lunghezza.</summary>
    private IEnumerable<(Vector2 Center, Vector2 Dir, float Length, bool Loco)> Cars()
    {
        float s = _head;
        for (int i = 0; i < Lengths.Length; i++)
        {
            float len = Lengths[i];
            Vector2 front = _rail.At(s), back = _rail.At(s - len);
            Vector2 dir = front - back;
            dir = dir.LengthSquared() > 1e-6f ? Vector2.Normalize(dir) : Vector2.UnitX;
            yield return ((front + back) * 0.5f, dir, len, i == 0);
            s -= len + Gap;
        }
    }

    // ------------------------------------------------------------------ ostacoli

    /// <summary>Un cerchio nel punto tocca il treno o una sbarra abbassata?</summary>
    public bool Blocks(Vector2 p, float r)
    {
        if (!_rail.Exists) return false;
        foreach (var (c, d, len, _) in Cars())
        {
            Vector2 rel = p - c;
            if (rel.LengthSquared() > (len * 0.5f + 2f) * (len * 0.5f + 2f)) continue;
            float along = Vector2.Dot(rel, d), across = Vector2.Dot(rel, new Vector2(-d.Y, d.X));
            float ex = MathF.Abs(along) - len * 0.5f, ez = MathF.Abs(across) - HalfWidth;
            float ox = MathF.Max(ex, 0f), oz = MathF.Max(ez, 0f);
            if (ox * ox + oz * oz < r * r) return true;
        }
        for (int i = 0; i < _rail.Crossings.Count; i++)
        {
            if (_armAngle[i] > 0.35f) continue; // alzata (o quasi): si passa
            var c = _rail.Crossings[i];
            if (Vector2.DistanceSquared(p, c.Point) > 225f) continue;
            foreach (var (pivot, dir, len) in Arms(c))
            {
                Vector2 rel = p - pivot;
                float t = Math.Clamp(Vector2.Dot(rel, dir), 0f, len);
                if (Vector2.Distance(p, pivot + dir * t) < r + 0.12f) return true;
            }
        }
        return false;
    }

    /// <summary>Se il treno è finito addosso a qualcosa (raggio <paramref name="r"/>), lo spinge fuori di fianco.</summary>
    public bool PushOut(ref Vector3 pos, float r)
    {
        bool moved = false;
        foreach (var (c, d, len, _) in Cars())
        {
            var p = new Vector2(pos.X, pos.Z);
            Vector2 side = new(-d.Y, d.X), rel = p - c;
            float along = Vector2.Dot(rel, d), across = Vector2.Dot(rel, side);
            if (MathF.Abs(along) > len * 0.5f + r || MathF.Abs(across) > HalfWidth + r) continue;
            float sign = across >= 0f ? 1f : -1f;
            Vector2 target = c + d * along + side * sign * (HalfWidth + r + 0.05f);
            pos.X = target.X;
            pos.Z = target.Y;
            moved = true;
        }
        return moved;
    }

    /// <summary>Le due sbarre di un passaggio a livello: perno (sul bordo destro per chi arriva), direzione e lunghezza.</summary>
    private static IEnumerable<(Vector2 Pivot, Vector2 Dir, float Length)> Arms(RailCrossing c)
    {
        foreach (float side in new[] { 1f, -1f })
        {
            // Chi arriva da questo lato viaggia verso il binario: -side * RoadDir
            Vector2 travel = -side * c.RoadDir, right = new(-travel.Y, travel.X);
            Vector2 pivot = c.Point + c.RoadDir * side * c.Setback + right * (c.RoadHalfWidth + 0.45f);
            yield return (pivot, -right, c.RoadHalfWidth * 2f + 0.25f);
        }
    }

    // ------------------------------------------------------------------ disegno

    public void Collect(List<DrawItem> items, float night, Func<Vector2, bool> visible)
    {
        if (!_rail.Exists) return;
        int index = 0;
        foreach (var (c, d, len, loco) in Cars())
        {
            bool inside = Inside && index++ == 1;
            if (!visible(c) && !inside) continue;
            float yaw = MathF.Atan2(d.X, d.Y);
            float y = Terrain.Height(c.X, c.Y) + Railway.RailTop;
            var m = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(c.X, y, c.Y);
            var area = new Vector4(c.X - len, c.Y - len, c.X + len, c.Y + len);
            if (inside)
            {
                // Vista da dentro: interno con i finestrini aperti (le pareti esterne coprirebbero tutto)
                items.Add(new DrawItem(_interior, m));
                items.Add(new DrawItem(_interiorGlow, m, 0.35f + 0.9f * night, CastShadow: false));
                continue;
            }
            items.Add(new DrawItem(loco ? _loco : _car, m, Area: area));
            items.Add(new DrawItem(loco ? _locoGlow : _carGlow, m, 0.2f + 1.0f * night, CastShadow: false, Area: area));
        }

        bool blink = (_time * 2.2f) % 1f < 0.5f;
        for (int i = 0; i < _rail.Crossings.Count; i++)
        {
            var c = _rail.Crossings[i];
            if (!visible(c.Point)) continue;
            int k = 0;
            foreach (var (pivot, dir, len) in Arms(c))
            {
                float y = Terrain.Height(pivot.X, pivot.Y) + Zone.PathRaise + Terrain.WalkRaise;
                // Il lato dipinto del palo (-Z) guarda chi arriva: +Z locale va verso il binario
                Vector2 face = c.RoadDir * (k == 0 ? -1f : 1f);
                var post = Matrix4x4.CreateRotationY(MathF.Atan2(face.X, face.Y)) * Matrix4x4.CreateTranslation(pivot.X, y, pivot.Y);
                items.Add(new DrawItem(_post, post));
                // Luci rosse alternate, accese solo a sbarre in movimento o abbassate
                if (_closed[i] || _armAngle[i] < 0.99f)
                    foreach (float lx in new[] { -0.13f, 0.13f })
                    {
                        bool on = (lx < 0f) == blink;
                        if (!on) continue;
                        items.Add(new DrawItem(_lamp, Matrix4x4.CreateTranslation(lx, 1.12f, -0.05f) * post, 1.6f, CastShadow: false));
                    }
                // Sbarra: ruota attorno al perno, da verticale (alzata) a orizzontale attraverso la strada
                float lift = _armAngle[i] * 1.45f;
                float yawArm = MathF.Atan2(-dir.Y, dir.X);
                var arm = Matrix4x4.CreateScale(len, 1f, 1f) * Matrix4x4.CreateRotationZ(lift) * Matrix4x4.CreateRotationY(yawArm)
                          * Matrix4x4.CreateTranslation(pivot.X, y + 0.85f, pivot.Y);
                items.Add(new DrawItem(_arm, arm));
                k++;
            }
        }
    }

    // ------------------------------------------------------------------ modelli (+Z = avanti, base sul ferro)

    private static readonly Vector3 Dark = new(0.16f, 0.16f, 0.18f);
    private static readonly Vector3 GlassC = new(0.25f, 0.33f, 0.42f);
    private static readonly Vector3 Lit = new(1f, 0.86f, 0.55f);

    private static void Bogies(MeshBuilder mb, float len)
    {
        mb.AddBox(new Vector3(0, 0.32f, 0), new Vector3(1.5f, 0.22f, len - 0.4f), Dark); // telaio
        foreach (float z in new[] { -len * 0.32f, len * 0.32f })
            foreach (float x in new[] { -0.62f, 0.62f })
                foreach (float dz in new[] { -0.45f, 0.45f })
                    mb.AddBox(new Vector3(x, 0.2f, z + dz), new Vector3(0.12f, 0.4f, 0.4f), Dark);
    }

    // Treno regionale moderno: cassa bianca, fascia color petrolio, finestrini a nastro, gonne sui carrelli
    private static readonly Vector3 BodyWhite = new(0.93f, 0.94f, 0.95f);
    private static readonly Vector3 Teal = new(0.06f, 0.50f, 0.56f);
    private static readonly Vector3 Skirt = new(0.30f, 0.32f, 0.35f);
    private static readonly Vector3 RoofGrey = new(0.62f, 0.65f, 0.68f);
    private static readonly Vector3 Ribbon = new(0.14f, 0.18f, 0.24f);

    /// <summary>Triangolo con la faccia rivolta verso <paramref name="outward"/>, qualunque sia l'ordine dei vertici.</summary>
    private static void Tri(MeshBuilder mb, Vector3 a, Vector3 b, Vector3 c, Vector3 outward, Vector3 color)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f) (b, c) = (c, b);
        mb.AddTriangle(a, b, c, color);
    }

    /// <summary>
    /// Solido ottenuto estrudendo un profilo laterale convesso (punti (z, y), in giro) tra x = -w e x = +w:
    /// fianchi pieni e una faccia per ogni lato del profilo, colorata da <paramref name="faceColor"/>.
    /// </summary>
    private static void Extrude(MeshBuilder mb, Vector2[] profile, float w, Vector3 side, Func<int, Vector3> faceColor)
    {
        Vector2 mid = profile.Aggregate(Vector2.Zero, (acc, p) => acc + p) / profile.Length;
        for (int i = 1; i + 1 < profile.Length; i++)
            foreach (float sx in new[] { -1f, 1f })
                Tri(mb, new Vector3(sx * w, profile[0].Y, profile[0].X), new Vector3(sx * w, profile[i].Y, profile[i].X),
                    new Vector3(sx * w, profile[i + 1].Y, profile[i + 1].X), new Vector3(sx, 0, 0), side);
        for (int i = 0; i < profile.Length; i++)
        {
            Vector2 p = profile[i], q = profile[(i + 1) % profile.Length];
            Vector2 m = (p + q) * 0.5f - mid;
            var outward = new Vector3(0, m.Y, m.X);
            var a = new Vector3(-w, p.Y, p.X); var b = new Vector3(w, p.Y, p.X);
            var c = new Vector3(w, q.Y, q.X); var d = new Vector3(-w, q.Y, q.X);
            Tri(mb, a, b, c, outward, faceColor(i));
            Tri(mb, a, c, d, outward, faceColor(i));
        }
    }

    /// <summary>Gonne sotto la cassa (coprono quasi tutto il carrello) e mantice d'intercomunicazione sul davanti.</summary>
    private static void Underframe(MeshBuilder mb, float len, bool bellowsFront)
    {
        Bogies(mb, len);
        mb.AddBox(new Vector3(0, 0.46f, 0), new Vector3(1.9f, 0.26f, len - 0.5f), Skirt);
        if (bellowsFront) mb.AddBox(new Vector3(0, 1.45f, len * 0.5f + 0.15f), new Vector3(1.5f, 1.75f, 0.42f), Dark);
    }

    /// <summary>Pantografo a "Z" sul tetto, con gli isolatori.</summary>
    private static void Pantograph(MeshBuilder mb, float z, float roof)
    {
        foreach (float x in new[] { -0.45f, 0.45f })
            mb.AddPrism(new Vector3(x, roof, z), 0.06f, 0.05f, 0.14f, 6, new Vector3(0.75f, 0.35f, 0.25f)); // isolatori
        mb.AddBox(new Vector3(0, roof + 0.16f, z), new Vector3(1.0f, 0.05f, 0.5f), Dark);
        Matrix4x4 old = mb.Transform;
        mb.Transform = Matrix4x4.CreateRotationX(0.75f) * Matrix4x4.CreateTranslation(0, roof + 0.36f, z + 0.12f) * old;
        mb.AddBox(Vector3.Zero, new Vector3(0.06f, 0.05f, 0.62f), Dark);
        mb.Transform = Matrix4x4.CreateRotationX(-0.75f) * Matrix4x4.CreateTranslation(0, roof + 0.6f, z - 0.05f) * old;
        mb.AddBox(Vector3.Zero, new Vector3(0.06f, 0.05f, 0.5f), Dark);
        mb.Transform = old;
        mb.AddBox(new Vector3(0, roof + 0.78f, z - 0.2f), new Vector3(1.1f, 0.04f, 0.08f), Dark); // strisciante
    }

    /// <summary>
    /// Carrozza di testa di un treno regionale moderno: muso aerodinamico inclinato con un grande parabrezza scuro
    /// avvolgente, fanali sottili, cassa bianca con la fascia color petrolio, finestrini a nastro e pantografo.
    /// </summary>
    private static (Mesh, Mesh) BuildLoco(GL gl)
    {
        var mb = new MeshBuilder();
        var glow = new MeshBuilder();
        const float L = LocoLength, W = 0.95f, Bottom = 0.56f, Roof = 2.42f;
        float back = -L * 0.5f + 0.05f, front = L * 0.5f;
        Underframe(mb, L, bellowsFront: false);
        // Profilo laterale: retro, tetto, muso che scende inclinato, punta arrotondata, fondo
        Vector2[] profile =
        [
            new(back, Bottom), new(front - 0.05f, Bottom), new(front, 0.85f), new(front - 0.12f, 1.2f),
            new(front - 0.75f, 1.95f), new(front - 1.35f, Roof - 0.04f), new(front - 1.7f, Roof), new(back, Roof),
        ];
        // Faccia 0 il fondo, 1-2 la punta (fascia colorata), 3 il parabrezza, 4 la fronte del tetto
        Extrude(mb, profile, W, BodyWhite, i => i switch { 0 => Skirt, 1 or 2 => Teal, 3 => Ribbon, 4 => RoofGrey, 5 or 6 => RoofGrey, _ => BodyWhite });
        // Fascia petrolio sui fianchi, finestrini a nastro, porte
        foreach (float sx in new[] { -1f, 1f })
        {
            float x = sx * (W + 0.012f);
            mb.AddBox(new Vector3(x, 0.98f, -0.4f), new Vector3(0.02f, 0.2f, L - 1.0f), Teal);
            mb.AddBox(new Vector3(x, 1.68f, -0.55f), new Vector3(0.02f, 0.62f, L - 2.4f), Ribbon);
            mb.AddBox(new Vector3(x, 1.75f, front - 1.15f), new Vector3(0.02f, 0.4f, 0.55f), Ribbon); // finestrino del macchinista
            mb.AddBox(new Vector3(sx * (W + 0.02f), 1.3f, -L * 0.5f + 0.7f), new Vector3(0.02f, 1.35f, 0.6f), Teal); // porta
            mb.AddBox(new Vector3(sx * (W + 0.03f), 1.3f, -L * 0.5f + 0.7f), new Vector3(0.01f, 1.25f, 0.04f), Skirt);
        }
        // Tetto: tettuccio dei condizionatori e pantografo
        mb.AddBox(new Vector3(0, Roof + 0.08f, -1.2f), new Vector3(1.3f, 0.16f, 1.6f), RoofGrey);
        Pantograph(mb, 0.1f, Roof);
        // Fanali sottili ai lati della punta, sotto il parabrezza (accesi: mesh luminosa)
        foreach (float sx in new[] { -1f, 1f })
            glow.AddBox(new Vector3(sx * 0.58f, 1.05f, front - 0.04f), new Vector3(0.36f, 0.09f, 0.05f), Lit);
        glow.AddBox(new Vector3(0, 2.18f, front - 1.05f), new Vector3(0.3f, 0.07f, 0.05f), Lit); // fanale alto
        return (mb.Build(gl), glow.Build(gl));
    }

    private static (Mesh, Mesh) BuildCarriage(GL gl)
    {
        var mb = new MeshBuilder();
        var glow = new MeshBuilder();
        const float L = CarLength, W = 0.95f, Bottom = 0.56f, Roof = 2.42f;
        Underframe(mb, L, bellowsFront: true);
        // Cassa con gli spigoli del tetto smussati
        float h = L * 0.5f - 0.07f;
        Vector2[] profile = [new(-h, Bottom), new(h, Bottom), new(h, Roof), new(-h, Roof)];
        Extrude(mb, profile, W, BodyWhite, i => i == 0 ? Skirt : i == 2 ? RoofGrey : BodyWhite);
        foreach (float sx in new[] { -1f, 1f })
        {
            Matrix4x4 old = mb.Transform;
            mb.Transform = Matrix4x4.CreateRotationZ(sx * 0.6f) * Matrix4x4.CreateTranslation(sx * (W - 0.1f), Roof - 0.02f, 0) * old;
            mb.AddBox(Vector3.Zero, new Vector3(0.32f, 0.1f, L - 0.14f), RoofGrey);
            mb.Transform = old;
        }
        mb.AddBox(new Vector3(0, Roof + 0.08f, 0), new Vector3(1.4f, 0.14f, L - 0.6f), RoofGrey);
        foreach (float x in new[] { -1f, 1f })
        {
            float xs = x * (W + 0.012f);
            mb.AddBox(new Vector3(xs, 0.98f, 0), new Vector3(0.02f, 0.2f, L - 0.3f), Teal);                     // fascia
            mb.AddBox(new Vector3(xs, 1.66f, -0.3f), new Vector3(0.02f, 0.72f, L - 1.4f), Ribbon);              // nastro
            for (int w = 0; w < 4; w++)
            {
                float z = -CarLength * 0.5f + 0.9f + w * 1.55f;
                if (z > CarLength * 0.5f - 1.0f) continue;
                (w % 2 == 0 ? glow : mb).AddBox(new Vector3(x * (W + 0.022f), 1.66f, z), new Vector3(0.02f, 0.62f, 1.1f),
                                                w % 2 == 0 ? Lit : GlassC);
            }
            // Porta scorrevole color petrolio con la sua finestrella
            mb.AddBox(new Vector3(x * (W + 0.02f), 1.3f, CarLength * 0.5f - 0.5f), new Vector3(0.02f, 1.35f, 0.62f), Teal);
            mb.AddBox(new Vector3(x * (W + 0.03f), 1.62f, CarLength * 0.5f - 0.5f), new Vector3(0.01f, 0.45f, 0.42f), GlassC);
        }
        return (mb.Build(gl), glow.Build(gl));
    }

    /// <summary>
    /// L'interno della carrozza, a misura del personaggio rimpicciolito: pavimento col tappeto nel corridoio, soffitto
    /// con le plafoniere, pareti coi finestrini APERTI (si vede fuori) sopra le file di posti, cappelliere con le
    /// valigie; in coda la toilette, lo scaffale dei bagagli e il cestino; davanti il distributore, il banco del caffè,
    /// il posto bici, i corrimano, le porte sui fianchi e il tabellone. Sotto, carrello e telaio come fuori.
    /// </summary>
    private static (Mesh, Mesh) BuildInterior(GL gl)
    {
        var mb = new MeshBuilder();
        var glow = new MeshBuilder();
        var rng = new Random(17);
        var wall = new Vector3(0.90f, 0.87f, 0.78f);
        var floor = new Vector3(0.40f, 0.38f, 0.36f);
        var carpet = new Vector3(0.22f, 0.30f, 0.42f);
        var seatC = new Vector3(0.20f, 0.36f, 0.62f);
        var head = new Vector3(0.92f, 0.92f, 0.9f);
        var green = new Vector3(0.18f, 0.46f, 0.32f);
        var metal = new Vector3(0.62f, 0.64f, 0.68f);
        var wood = new Vector3(0.55f, 0.38f, 0.24f);
        var yellow = new Vector3(0.95f, 0.78f, 0.15f);
        const float L = CarLength, X = 0.93f, T = 0.04f;
        float top = Top, back = -L * 0.5f, front = L * 0.5f;
        const float Sill = 0.3f, Lintel = 0.72f; // finestrini, sopra il pavimento
        Bogies(mb, L);

        // Pavimento, tappeto del corridoio, striscia gialla davanti alle porte
        mb.AddBox(new Vector3(0, Floor - 0.06f, 0), new Vector3(X * 2f, 0.12f, L - 0.1f), floor);
        mb.AddBox(new Vector3(0, Floor + 0.002f, -0.47f), new Vector3(AisleHalf * 2f, 0.004f, 3.9f), carpet);
        mb.AddBox(new Vector3(0, Floor + 0.003f, DoorZ - 0.2f), new Vector3(X * 2f - 0.1f, 0.004f, 0.06f), yellow);
        // Soffitto e tetto sopra (da fuori dei finestrini si vede il tetto, non il vuoto)
        mb.AddBox(new Vector3(0, top + 0.02f, 0), new Vector3(X * 2f + T, 0.04f, L), wall * 0.96f);
        mb.AddBox(new Vector3(0, top + 0.08f, 0), new Vector3(X * 2f + 0.1f, 0.06f, L + 0.05f), new Vector3(0.45f, 0.47f, 0.5f));
        // Pareti di fondo: in coda la porta d'intercomunicazione (chiusa, con l'oblò), davanti il tabellone
        foreach (float z in new[] { back + T * 0.5f, front - T * 0.5f })
        {
            foreach (float sx in new[] { -1f, 1f })
                mb.AddBox(new Vector3(sx * (X + 0.22f) * 0.5f, Floor + CeilingHeight * 0.5f, z), new Vector3(X - 0.22f, CeilingHeight, T), wall);
            mb.AddBox(new Vector3(0, Floor + 0.82f, z), new Vector3(0.44f, 0.26f, T), wall);
            float dz = z < 0f ? 0.012f : -0.012f;
            mb.AddBox(new Vector3(0, Floor + 0.35f, z + dz), new Vector3(0.42f, 0.7f, T), green);
            mb.AddBox(new Vector3(0, Floor + 0.5f, z + dz * 2f), new Vector3(0.16f, 0.16f, T * 0.5f), Glass());
        }
        // Tabellone sopra la porta davanti (si accende) e mappa della linea in coda
        mb.AddBox(new Vector3(0, Floor + 0.82f, front - 0.06f), new Vector3(0.5f, 0.12f, 0.05f), Dark);
        glow.AddBox(new Vector3(0, Floor + 0.82f, front - 0.09f), new Vector3(0.44f, 0.07f, 0.01f), new Vector3(1f, 0.55f, 0.1f));
        mb.AddBox(new Vector3(-0.3f, Floor + 0.6f, back + 0.52f), new Vector3(0.36f, 0.22f, 0.01f), new Vector3(0.95f, 0.95f, 0.9f));
        mb.AddBox(new Vector3(-0.3f, Floor + 0.6f, back + 0.525f), new Vector3(0.3f, 0.012f, 0.01f), Teal);

        // Pareti laterali: zoccolo e fascia alta pieni; in mezzo i finestrini sopra ogni fila di posti, pieno altrove
        foreach (float sd in new[] { -1f, 1f })
        {
            float x = sd * X;
            mb.AddBox(new Vector3(x, Floor + Sill * 0.5f, 0), new Vector3(T, Sill, L), wall);
            mb.AddBox(new Vector3(x - sd * 0.01f, Floor + 0.1f, 0), new Vector3(T, 0.2f, L), green);
            mb.AddBox(new Vector3(x, (Floor + Lintel + top) * 0.5f, 0), new Vector3(T, top - Floor - Lintel, L), wall);
            float z0 = back;
            foreach (float b in Bays.Append(front + 0.3f))
            {
                float a = z0, e = b - 0.3f;
                // Le porte del vestibolo (verso cui si scende) al posto del muro, davanti
                if (e > a + 0.01f)
                    mb.AddBox(new Vector3(x, Floor + (Sill + Lintel) * 0.5f, (a + e) * 0.5f), new Vector3(T, Lintel - Sill, e - a), wall);
                if (b < front)
                {
                    mb.AddBox(new Vector3(x, Floor + Sill, b), new Vector3(T * 3f, 0.025f, 0.6f), Dark);
                    mb.AddBox(new Vector3(x, Floor + Lintel, b), new Vector3(T * 3f, 0.025f, 0.6f), Dark);
                }
                z0 = b + 0.3f;
            }
            // Porta scorrevole del vestibolo, con la sua finestrella e il pulsante
            mb.AddBox(new Vector3(x - sd * 0.015f, Floor + 0.4f, DoorZ), new Vector3(T, 0.8f, 0.42f), Teal);
            mb.AddBox(new Vector3(x - sd * 0.03f, Floor + 0.55f, DoorZ), new Vector3(T * 0.5f, 0.24f, 0.26f), Glass());
            glow.AddBox(new Vector3(x - sd * 0.035f, Floor + 0.36f, DoorZ + 0.26f), new Vector3(0.01f, 0.04f, 0.04f), new Vector3(0.3f, 1f, 0.4f));
            // Cappelliera lungo i finestrini, con le valigie sopra
            mb.AddBox(new Vector3(sd * 0.82f, Floor + 0.8f, -0.47f), new Vector3(0.22f, 0.02f, 3.8f), metal);
            for (float z = -2.25f; z < 1.3f; z += 0.32f + rng.NextSingle() * 0.25f)
            {
                var bag = new Vector3(0.2f + rng.NextSingle() * 0.6f, 0.2f + rng.NextSingle() * 0.4f, 0.25f + rng.NextSingle() * 0.4f);
                float w = 0.1f + rng.NextSingle() * 0.08f, h = 0.05f + rng.NextSingle() * 0.05f;
                mb.AddBox(new Vector3(sd * 0.83f, Floor + 0.81f + h * 0.5f, z), new Vector3(0.14f, h, w), bag);
            }
        }

        // Posti: cuscino, base, schienale con il poggiatesta bianco, bracciolo sul corridoio; tavolino in mezzo alla coppia
        foreach (var st in Seats)
        {
            float sx = st.X, sz = st.Z, f = st.Facing;
            mb.AddBox(new Vector3(sx, Floor + 0.17f, sz), new Vector3(0.26f, 0.06f, 0.22f), seatC);
            mb.AddBox(new Vector3(sx, Floor + 0.07f, sz), new Vector3(0.2f, 0.14f, 0.16f), Dark);
            mb.AddBox(new Vector3(sx, Floor + 0.36f, sz - f * 0.13f), new Vector3(0.26f, 0.36f, 0.05f), seatC * 0.9f);
            mb.AddBox(new Vector3(sx, Floor + 0.5f, sz - f * 0.105f), new Vector3(0.2f, 0.07f, 0.012f), head);
            if (MathF.Abs(sx) < 0.5f)
                mb.AddBox(new Vector3(sx - st.Side * 0.14f, Floor + 0.25f, sz), new Vector3(0.025f, 0.05f, 0.2f), Dark);
        }
        foreach (float b in Bays)
            foreach (float sd in new[] { -1f, 1f })
            {
                mb.AddBox(new Vector3(sd * 0.6f, Floor + 0.29f, b), new Vector3(0.56f, 0.025f, 0.16f), wood);
                mb.AddBox(new Vector3(sd * 0.86f, Floor + 0.14f, b), new Vector3(0.04f, 0.28f, 0.04f), Dark);
                // Qualcosa sui tavolini: una tazza, un libro
                if (rng.NextSingle() < 0.6f)
                    mb.AddPrism(new Vector3(sd * (0.45f + rng.NextSingle() * 0.3f), Floor + 0.303f, b + 0.03f), 0.018f, 0.016f, 0.04f, 6, head);
                if (rng.NextSingle() < 0.5f)
                    mb.AddBox(new Vector3(sd * (0.5f + rng.NextSingle() * 0.3f), Floor + 0.31f, b - 0.03f), new Vector3(0.08f, 0.015f, 0.06f),
                              new Vector3(0.6f + rng.NextSingle() * 0.4f, 0.3f, 0.2f + rng.NextSingle() * 0.5f));
            }

        // Plafoniere: una per fila e due nei vestiboli
        foreach (float z in Bays.Append(back + 0.45f).Append(2.1f))
            glow.AddBox(new Vector3(0, top - 0.005f, z), new Vector3(0.3f, 0.01f, 0.4f), Lit);

        // --- In coda: toilette (cabina con la porta sul corridoio e l'insegna), scaffale dei bagagli, cestino
        mb.AddBox(new Vector3(0.655f, Floor + CeilingHeight * 0.5f, back + 0.36f), new Vector3(0.55f, CeilingHeight, 0.72f), wall * 0.92f);
        mb.AddBox(new Vector3(0.375f, Floor + 0.38f, back + 0.36f), new Vector3(0.012f, 0.74f, 0.36f), new Vector3(0.75f, 0.72f, 0.68f));
        mb.AddBox(new Vector3(0.37f, Floor + 0.38f, back + 0.5f), new Vector3(0.012f, 0.03f, 0.03f), Dark); // maniglia
        glow.AddBox(new Vector3(0.368f, Floor + 0.8f, back + 0.36f), new Vector3(0.005f, 0.07f, 0.12f), new Vector3(0.25f, 0.55f, 1f)); // WC
        for (int level = 0; level < 3; level++)
        {
            float y = Floor + 0.02f + level * 0.28f;
            mb.AddBox(new Vector3(-0.715f, y, back + 0.25f), new Vector3(0.43f, 0.02f, 0.48f), metal);
            for (int k = 0; k < 2; k++)
            {
                var bag = new Vector3(0.2f + rng.NextSingle() * 0.7f, 0.15f + rng.NextSingle() * 0.4f, 0.2f + rng.NextSingle() * 0.5f);
                float hgt = 0.12f + rng.NextSingle() * 0.1f;
                mb.AddBox(new Vector3(-0.82f + k * 0.2f, y + 0.01f + hgt * 0.5f, back + 0.25f), new Vector3(0.17f, hgt, 0.3f), bag);
            }
        }
        foreach (float sx in new[] { -0.93f, -0.5f })
            mb.AddBox(new Vector3(sx, Floor + 0.42f, back + 0.25f), new Vector3(0.025f, 0.84f, 0.48f), metal);
        mb.AddPrism(new Vector3(-0.28f, Floor, back + 0.08f), 0.07f, 0.075f, 0.24f, 8, metal * 0.8f);

        // --- Davanti: distributore (vetrina accesa con le file di prodotti), banco del caffè, posto bici, corrimano
        mb.AddBox(new Vector3(-0.765f, Floor + 0.33f, 1.775f), new Vector3(0.33f, 0.66f, 0.35f), new Vector3(0.7f, 0.12f, 0.12f));
        glow.AddBox(new Vector3(-0.596f, Floor + 0.42f, 1.775f), new Vector3(0.008f, 0.36f, 0.26f), new Vector3(0.75f, 0.85f, 0.95f));
        for (int r = 0; r < 4; r++)
            for (int c = 0; c < 3; c++)
                mb.AddBox(new Vector3(-0.59f, Floor + 0.28f + r * 0.085f, 1.69f + c * 0.085f), new Vector3(0.01f, 0.05f, 0.05f),
                          new Vector3(0.3f + rng.NextSingle() * 0.7f, 0.3f + rng.NextSingle() * 0.6f, 0.2f + rng.NextSingle() * 0.6f));
        mb.AddBox(new Vector3(-0.765f, Floor + 0.15f, 2.275f), new Vector3(0.33f, 0.3f, 0.45f), wood);
        mb.AddBox(new Vector3(-0.765f, Floor + 0.31f, 2.275f), new Vector3(0.36f, 0.02f, 0.48f), metal);
        mb.AddBox(new Vector3(-0.82f, Floor + 0.4f, 2.2f), new Vector3(0.12f, 0.16f, 0.12f), Dark); // macchina del caffè
        glow.AddBox(new Vector3(-0.755f, Floor + 0.45f, 2.2f), new Vector3(0.004f, 0.02f, 0.02f), new Vector3(1f, 0.2f, 0.1f));
        foreach (float cz in new[] { 2.38f, 2.44f })
            mb.AddPrism(new Vector3(-0.68f, Floor + 0.32f, cz), 0.018f, 0.016f, 0.035f, 6, head);
        // Bici appoggiata alla parete, lungo la carrozza
        var bike = new Vector3(0.85f, 0.2f, 0.18f);
        foreach (float wz in new[] { 1.75f, 2.25f })
        {
            mb.Transform = Matrix4x4.CreateRotationZ(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0.8f, Floor + 0.14f, wz);
            mb.AddPrism(new Vector3(0, -0.012f, 0), 0.14f, 0.14f, 0.024f, 12, Dark);
            mb.AddPrism(new Vector3(0, -0.014f, 0), 0.04f, 0.04f, 0.028f, 6, metal);
            mb.Transform = Matrix4x4.Identity;
        }
        mb.AddBox(new Vector3(0.8f, Floor + 0.22f, 2.0f), new Vector3(0.02f, 0.02f, 0.4f), bike);
        mb.Transform = Matrix4x4.CreateRotationX(0.6f) * Matrix4x4.CreateTranslation(0.8f, Floor + 0.22f, 1.88f);
        mb.AddBox(Vector3.Zero, new Vector3(0.02f, 0.22f, 0.02f), bike);
        mb.Transform = Matrix4x4.CreateRotationX(-0.5f) * Matrix4x4.CreateTranslation(0.8f, Floor + 0.26f, 2.14f);
        mb.AddBox(Vector3.Zero, new Vector3(0.02f, 0.2f, 0.02f), bike);
        mb.Transform = Matrix4x4.Identity;
        mb.AddBox(new Vector3(0.8f, Floor + 0.36f, 2.2f), new Vector3(0.12f, 0.015f, 0.015f), Dark);  // manubrio
        mb.AddBox(new Vector3(0.8f, Floor + 0.33f, 1.86f), new Vector3(0.05f, 0.015f, 0.08f), Dark);  // sella
        mb.AddBox(new Vector3(0.9f, Floor + 0.4f, 2.0f), new Vector3(0.01f, 0.2f, 0.2f), new Vector3(0.25f, 0.45f, 0.85f)); // cartello bici
        foreach (float x in new[] { -0.3f, 0.3f })
            mb.AddPrism(new Vector3(x, Floor, 2.6f), 0.022f, 0.022f, CeilingHeight, 8, yellow);

        return (mb.Build(gl), glow.Build(gl));
    }

    private static Vector3 Glass() => GlassC;

    /// <summary>Palo del passaggio a livello: croce di Sant'Andrea, due luci, base della sbarra. Guarda verso -Z.</summary>
    private static Mesh BuildPost(GL gl)
    {
        var mb = new MeshBuilder();
        var white = new Vector3(0.95f, 0.95f, 0.95f);
        var red = new Vector3(0.82f, 0.12f, 0.12f);
        mb.AddPrism(Vector3.Zero, 0.04f, 0.035f, 1.55f, 6, new Vector3(0.85f, 0.85f, 0.85f));
        mb.AddBox(new Vector3(0, 1.12f, 0f), new Vector3(0.45f, 0.2f, 0.04f), Dark);                // supporto luci
        foreach (float sign in new[] { 1f, -1f })
        {
            Matrix4x4 old = mb.Transform;
            mb.Transform = Matrix4x4.CreateRotationZ(sign * 0.6f) * Matrix4x4.CreateTranslation(0, 1.42f, -0.05f) * old;
            mb.AddBox(Vector3.Zero, new Vector3(0.6f, 0.08f, 0.02f), white);
            mb.AddBox(new Vector3(0, 0, -0.012f), new Vector3(0.58f, 0.03f, 0.01f), red);
            mb.Transform = old;
        }
        mb.AddBox(new Vector3(0, 0.85f, 0), new Vector3(0.16f, 0.16f, 0.16f), Dark);              // perno
        return mb.Build(gl);
    }

    /// <summary>Sbarra a strisce bianche e rosse, lunga 1 lungo +X (si scala alla larghezza della strada).</summary>
    private static Mesh BuildArm(GL gl)
    {
        var mb = new MeshBuilder();
        const int stripes = 7;
        for (int i = 0; i < stripes; i++)
            mb.AddBox(new Vector3((i + 0.5f) / stripes, 0, 0), new Vector3(1f / stripes, 0.07f, 0.07f),
                      i % 2 == 0 ? new Vector3(0.85f, 0.12f, 0.12f) : new Vector3(0.96f, 0.96f, 0.96f));
        return mb.Build(gl);
    }

    public void Dispose()
    {
        _loco.Dispose(); _locoGlow.Dispose(); _car.Dispose(); _carGlow.Dispose();
        _post.Dispose(); _arm.Dispose(); _lamp.Dispose(); _interior.Dispose(); _interiorGlow.Dispose();
        if (Current == this) Current = null;
    }
}
