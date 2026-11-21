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
    private readonly Mesh _loco, _car, _carGlow, _locoGlow, _post, _arm, _lamp;
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
        foreach (var (c, d, len, loco) in Cars())
        {
            if (!visible(c)) continue;
            float yaw = MathF.Atan2(d.X, d.Y);
            float y = Terrain.Height(c.X, c.Y) + Railway.RailTop;
            var m = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(c.X, y, c.Y);
            var area = new Vector4(c.X - len, c.Y - len, c.X + len, c.Y + len);
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

    private static (Mesh, Mesh) BuildLoco(GL gl)
    {
        var mb = new MeshBuilder();
        var glow = new MeshBuilder();
        var red = new Vector3(0.80f, 0.16f, 0.14f);
        var yellow = new Vector3(0.95f, 0.78f, 0.20f);
        Bogies(mb, LocoLength);
        // Cofano davanti, cabina dietro
        mb.AddBox(new Vector3(0, 1.05f, 0.9f), new Vector3(1.55f, 1.25f, 3.9f), red);
        mb.AddBox(new Vector3(0, 1.35f, -1.75f), new Vector3(1.85f, 1.85f, 2.3f), red);
        mb.AddBox(new Vector3(0, 0.52f, 0f), new Vector3(1.9f, 0.18f, LocoLength - 0.2f), yellow);   // fascia
        mb.AddBox(new Vector3(0, 2.33f, -1.75f), new Vector3(1.95f, 0.12f, 2.45f), Dark);            // tetto cabina
        foreach (float x in new[] { -0.93f, 0.93f })
            mb.AddBox(new Vector3(x, 1.7f, -1.75f), new Vector3(0.03f, 0.55f, 1.5f), GlassC);
        mb.AddBox(new Vector3(0, 1.72f, -0.59f), new Vector3(1.4f, 0.5f, 0.03f), GlassC);              // parabrezza
        mb.AddBox(new Vector3(0, 1.75f, 2.2f), new Vector3(0.2f, 0.25f, 0.2f), Dark);                  // fumaiolo
        glow.AddBox(new Vector3(0.5f, 0.95f, 2.87f), new Vector3(0.22f, 0.18f, 0.04f), Lit);           // fari
        glow.AddBox(new Vector3(-0.5f, 0.95f, 2.87f), new Vector3(0.22f, 0.18f, 0.04f), Lit);
        return (mb.Build(gl), glow.Build(gl));
    }

    private static (Mesh, Mesh) BuildCarriage(GL gl)
    {
        var mb = new MeshBuilder();
        var glow = new MeshBuilder();
        var body = new Vector3(0.92f, 0.88f, 0.76f);
        var green = new Vector3(0.18f, 0.46f, 0.32f);
        Bogies(mb, CarLength);
        mb.AddBox(new Vector3(0, 1.45f, 0), new Vector3(1.9f, 1.9f, CarLength - 0.15f), body);
        mb.AddBox(new Vector3(0, 0.75f, 0), new Vector3(1.94f, 0.35f, CarLength - 0.1f), green);
        mb.AddBox(new Vector3(0, 2.45f, 0), new Vector3(1.96f, 0.14f, CarLength + 0.05f), new Vector3(0.45f, 0.47f, 0.5f));
        foreach (float x in new[] { -0.96f, 0.96f })
        {
            for (int w = 0; w < 4; w++)
            {
                float z = -CarLength * 0.5f + 0.9f + w * 1.55f;
                (w % 2 == 0 ? glow : mb).AddBox(new Vector3(x, 1.7f, z), new Vector3(0.03f, 0.55f, 1.0f), w % 2 == 0 ? Lit : GlassC);
            }
            mb.AddBox(new Vector3(x, 1.3f, CarLength * 0.5f - 0.5f), new Vector3(0.03f, 1.3f, 0.55f), green); // porta
        }
        return (mb.Build(gl), glow.Build(gl));
    }

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
        _post.Dispose(); _arm.Dispose(); _lamp.Dispose();
        if (Current == this) Current = null;
    }
}
