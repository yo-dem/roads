using System.Numerics;
using Roads.Rendering;
using Roads.World;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// Automobilina giocattolo low poly. Si comanda col click (ricerca del percorso) o con WASD, Shift per andare piano.
/// Guida "da auto": modello cinematico a bicicletta con raggio di sterzata minimo (non gira sul posto),
/// retromarcia per le manovre, collisione a due cerchi (muso e coda) con scivolamento sugli ostacoli,
/// inseguimento del percorso con punto di mira avanzato (pure pursuit), luci di stop e di retromarcia.
/// Il modello guarda verso +Z locale.
/// </summary>
public sealed class Car : IDisposable
{
    private const float SlowSpeed = 2.6f;
    private const float FastSpeed = 6.8f;
    private const float ReverseSpeed = 2.4f;
    private const float Accel = 5f;           // unità/s²
    private const float Brake = 11f;
    private const float Scale = 1.1f;
    private const float WheelBase = 1.0f * Scale; // distanza tra gli assi (ruote a z = ±0.5)
    private const float MaxSteer = 0.6f;          // rad: raggio minimo ≈ WheelBase / tan(MaxSteer) ≈ 1.6
    private const float SteerRate = 3.5f;         // rad/s: velocità con cui si girano le ruote
    private const float HullOffset = 0.46f;       // centri dei due cerchi di collisione, davanti e dietro
    private const float HullRadius = 0.5f;
    private const float PlanRadius = 0.6f;        // margine per la ricerca del percorso
    private const float WheelRadius = 0.2f;
    private const float ManeuverTime = 0.75f;     // durata di una retromarcia di manovra

    public Vector3 Position;
    public float Yaw;

    /// <summary>Velocità con segno (negativa in retromarcia).</summary>
    public float Speed => _speed;
    public Vector3 Velocity => Forward * _speed;
    public float MaxSpeed => FastSpeed;
    /// <summary>Quanto si sta dando gas (0..1), per il suono del motore.</summary>
    public float Throttle { get; private set; }

    private float _speed, _yawRate, _spin, _steer, _time, _accel;
    private float _slopePitch, _slopeRoll, _tilt, _roll;
    private float _exhaustTimer, _blockedTimer;
    private float _maneuver, _maneuverSteer;   // retromarcia in corso: tempo residuo e sterzo
    private int _maneuverCount, _lastManeuverSign = 1;
    private float _sinceManeuver = 10f;
    private float _brakeGlow;

    private readonly Mesh _body, _headLights, _tailLights, _reverseLights, _wheel;

    // ------------------------------------------------------------------ destinazione e percorso

    public Vector3? Target { get; private set; }

    private readonly List<Vector3> _waypoints = new();
    private Vector3 _segStart;   // inizio del tratto di percorso corrente
    private Vector3? _request, _lastPlanned;
    private float _planCooldown;
    private int _replans;

    public void SetTarget(Vector3 point) => _request = point;

    public void ClearTarget()
    {
        Target = null;
        _request = null;
        _lastPlanned = null;
        _waypoints.Clear();
    }

    private void Plan(Vector3 goal, WorldMap world)
    {
        _blockedTimer = 0f;
        _maneuverCount = 0;
        var path = PathFinder.Find(world, Position, goal, PlanRadius, out Vector3 reached);
        _waypoints.Clear();
        if (path == null || Flat(reached - Position).Length() < 0.25f)
        {
            Target = null;
            return;
        }
        _waypoints.AddRange(path);
        _segStart = Position;
        bool exact = Vector2.Distance(new(reached.X, reached.Z), new(goal.X, goal.Z)) < 0.01f;
        Target = reached with { Y = exact ? goal.Y : world.GroundHeight(reached) };
    }

    /// <summary>
    /// Pure pursuit: proietta l'auto sul tratto corrente e restituisce il punto che sta
    /// <paramref name="lookahead"/> più avanti lungo il percorso. Le curve vengono così arrotondate.
    /// </summary>
    private Vector3 PursuitPoint(float lookahead, out float remaining)
    {
        while (_waypoints.Count > 1)
        {
            Vector3 b = _waypoints[0];
            if (ProjectT(_segStart, b, Position) >= 1f || Flat(b - Position).Length() < 0.6f)
            {
                _segStart = b;
                _waypoints.RemoveAt(0);
            }
            else break;
        }

        Vector3 a0 = _segStart, b0 = _waypoints[0];
        Vector3 proj = a0 + (b0 - a0) * Math.Clamp(ProjectT(a0, b0, Position), 0f, 1f);

        remaining = Flat(b0 - Position).Length();
        for (int i = 1; i < _waypoints.Count; i++) remaining += Flat(_waypoints[i] - _waypoints[i - 1]).Length();

        float left = lookahead;
        Vector3 cur = proj;
        foreach (var next in _waypoints)
        {
            float seg = Flat(next - cur).Length();
            if (seg >= left) return cur + (next - cur) * (left / seg);
            left -= seg;
            cur = next;
        }
        return _waypoints[^1];
    }

    private static float ProjectT(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = Flat(b - a);
        float len2 = ab.LengthSquared();
        return len2 < 1e-6f ? 1f : Vector3.Dot(Flat(p - a), ab) / len2;
    }

    // ------------------------------------------------------------------ modello

    public Car(GL gl, Vector3 position, float yaw)
    {
        Position = position;
        Yaw = yaw;

        var red = new Vector3(0.92f, 0.26f, 0.22f);
        var glass = new Vector3(0.36f, 0.58f, 0.74f);
        var trim = new Vector3(0.88f, 0.86f, 0.80f);
        var dark = new Vector3(0.20f, 0.18f, 0.18f);

        var mb = new MeshBuilder { ColorRng = new Random(5), ColorJitter = 0.02f };
        // Scocca e abitacolo come tronchi di piramide: sagoma tondeggiante ma sfaccettata
        Frustum(mb, 0.17f, 0.52f, new(0.47f, 0.80f), new(0.45f, 0.76f), 0f, red, null);
        Frustum(mb, 0.52f, 0.86f, new(0.41f, 0.46f), new(0.33f, 0.32f), -0.1f, red, glass);
        // Paraurti, griglia, maniglie, tettuccio
        mb.AddBox(new Vector3(0, 0.22f, 0.83f), new Vector3(0.92f, 0.12f, 0.08f), trim);
        mb.AddBox(new Vector3(0, 0.22f, -0.83f), new Vector3(0.92f, 0.12f, 0.08f), trim);
        mb.AddBox(new Vector3(0, 0.36f, 0.79f), new Vector3(0.3f, 0.09f, 0.03f), dark);
        mb.AddBox(new Vector3(0, 0.875f, -0.1f), new Vector3(0.5f, 0.03f, 0.5f), trim);
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.465f, 0.44f, 0.05f), new Vector3(0.03f, 0.03f, 0.12f), trim);
        // Tubo di scarico
        mb.Transform = Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0.28f, 0.2f, -0.92f);
        mb.AddPrism(Vector3.Zero, 0.045f, 0.045f, 0.12f, 6, dark);
        mb.Transform = Matrix4x4.Identity;
        _body = mb.Build(gl);

        mb = new MeshBuilder();
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.29f, 0.41f, 0.795f), new Vector3(0.16f, 0.1f, 0.03f), new Vector3(1f, 0.95f, 0.7f));
        _headLights = mb.Build(gl);

        mb = new MeshBuilder();
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.31f, 0.41f, -0.795f), new Vector3(0.14f, 0.08f, 0.03f), new Vector3(0.95f, 0.15f, 0.12f));
        _tailLights = mb.Build(gl);

        // Luci di retromarcia: piccole, bianche, accanto agli stop (visibili solo in retro)
        mb = new MeshBuilder();
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.15f, 0.41f, -0.8f), new Vector3(0.07f, 0.06f, 0.03f), new Vector3(1f, 1f, 0.95f));
        _reverseLights = mb.Build(gl);

        // Ruota centrata nell'origine con asse lungo X
        mb = new MeshBuilder { Transform = Matrix4x4.CreateRotationZ(MathF.PI / 2f) };
        mb.AddPrism(new Vector3(0, -0.08f, 0), WheelRadius, WheelRadius, 0.16f, 10, dark);
        mb.AddPrism(new Vector3(0, -0.09f, 0), 0.09f, 0.09f, 0.18f, 6, trim);
        _wheel = mb.Build(gl);
    }

    /// <summary>Tronco di piramide a base rettangolare; se <paramref name="glass"/> è dato aggiunge i finestrini.</summary>
    private static void Frustum(MeshBuilder mb, float y0, float y1, Vector2 bot, Vector2 top, float z, Vector3 color, Vector3? glass)
    {
        Vector3 B(float sx, float sz) => new(sx * bot.X, y0, z + sz * bot.Y);
        Vector3 T(float sx, float sz) => new(sx * top.X, y1, z + sz * top.Y);
        var faces = new[]
        {
            (B(-1, 1), B(1, 1), T(1, 1), T(-1, 1)),
            (B(1, -1), B(-1, -1), T(-1, -1), T(1, -1)),
            (B(1, 1), B(1, -1), T(1, -1), T(1, 1)),
            (B(-1, -1), B(-1, 1), T(-1, 1), T(-1, -1)),
        };
        var mid = new Vector3(0, (y0 + y1) * 0.5f, z);
        foreach (var (a, b, c, d) in faces)
        {
            mb.AddQuad(a, b, c, d, color);
            if (glass is not { } g) continue;
            Vector3 center = (a + b + c + d) * 0.25f;
            Vector3 n = center - mid;
            n.Y = 0f;
            Vector3 o = Vector3.Normalize(n) * 0.006f;
            Vector3 In(Vector3 p) => center + (p - center) * new Vector3(0.78f, 0.7f, 0.78f) + o;
            mb.AddQuad(In(a), In(b), In(c), In(d), g);
        }
        mb.AddQuad(T(-1, 1), T(1, 1), T(1, -1), T(-1, -1), color);
    }

    // ------------------------------------------------------------------ guida

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);
    private static Vector3 Dir(float yaw) => new(MathF.Sin(yaw), 0, MathF.Cos(yaw));
    private Vector3 Forward => Dir(Yaw);
    private Vector3 Side => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw)); // +X locale

    private static float MoveTowards(float v, float target, float maxDelta) =>
        MathF.Abs(target - v) <= maxDelta ? target : v + MathF.Sign(target - v) * maxDelta;

    /// <summary>L'auto (due cerchi, muso e coda) sta tutta su terreno libero in questa posa?</summary>
    private static bool Fits(WorldMap world, Vector3 p, float yaw)
    {
        Vector3 f = Dir(yaw) * HullOffset;
        return world.IsWalkable(p + f, HullRadius) && world.IsWalkable(p - f, HullRadius);
    }

    /// <summary>
    /// Sposta l'auto. Se la mossa è bloccata prova senza ruotare, poi devia il passo di angoli crescenti
    /// (proiettandolo) per scivolare lungo l'ostacolo qualunque sia la sua forma. Restituisce lo spostamento fatto.
    /// </summary>
    private float Move(WorldMap world, Vector3 step, float newYaw)
    {
        // Già incastrata (non dovrebbe succedere): si libera con un controllo più permissivo
        if (!Fits(world, Position, Yaw))
        {
            if (!world.IsWalkable(Position + step, 0.3f)) return 0f;
            Position += step;
            Yaw = newYaw;
            return step.Length();
        }

        if (Fits(world, Position + step, newYaw)) { Position += step; Yaw = newYaw; return step.Length(); }
        if (Fits(world, Position + step, Yaw)) { Position += step; return step.Length(); }

        foreach (float a in new[] { 0.35f, 0.7f, 1.05f, 1.4f })
            foreach (float sign in new[] { 1f, -1f })
            {
                float c = MathF.Cos(a * sign), s = MathF.Sin(a * sign);
                var d = new Vector3(step.X * c + step.Z * s, 0, -step.X * s + step.Z * c) * MathF.Cos(a);
                if (Fits(world, Position + d, Yaw)) { Position += d; return d.Length(); }
            }
        return 0f;
    }

    private void StartManeuver(float diff)
    {
        // In retro lo sterzo è invertito: per portare il muso verso la meta si sterza dalla parte opposta
        int sign = MathF.Abs(diff) > 0.15f ? MathF.Sign(diff) : (_lastManeuverSign = -_lastManeuverSign);
        _maneuver = ManeuverTime;
        _maneuverSteer = -sign * MaxSteer;
        _maneuverCount++;
        _sinceManeuver = 0f;
        _blockedTimer = 0f;
    }

    public void Update(float dt, Vector3 input, bool slow, WorldMap world, Particles fx)
    {
        _time += dt;
        _sinceManeuver += dt;
        float maxSpeed = slow ? SlowSpeed : FastSpeed;
        bool keys = input.LengthSquared() > 0.01f;

        _planCooldown -= dt;
        if (!keys && _request is { } req && _planCooldown <= 0f)
        {
            _request = null;
            if (_lastPlanned is not { } lp || Target == null || Vector3.Distance(lp, req) > 0.3f)
            {
                _lastPlanned = req;
                _replans = 0;
                Plan(req, world);
                _planCooldown = 0.12f;
            }
        }
        if (keys) ClearTarget(); // la tastiera ha la precedenza sul click

        // --- Dove vuole andare: direzione dei tasti o punto di mira lungo il percorso
        Vector3? aim = null;
        float remaining = float.MaxValue;
        bool following = false;
        if (keys)
            aim = Position + Vector3.Normalize(Flat(input)) * 3f;
        else if (Target != null && _waypoints.Count > 0)
        {
            float lookahead = Math.Clamp(1.2f + MathF.Abs(_speed) * 0.3f, 1.2f, 3f);
            Vector3 p = PursuitPoint(lookahead, out remaining);
            if (remaining < 0.25f) ClearTarget();
            else { aim = p; following = true; }
        }

        // --- Sterzo e velocità desiderati
        float wantSpeed = 0f, wantSteer = 0f, diff = 0f;
        if (aim is { } goal)
        {
            Vector3 to = Flat(goal - Position);
            float dist = MathF.Max(to.Length(), 0.5f);
            diff = Noise.WrapAngle(MathF.Atan2(to.X, to.Z) - Yaw);
            float arrive = following ? remaining * 2.2f + 0.3f : float.MaxValue; // frena in arrivo

            // Meta alle spalle e vicina: retromarcia dritta verso di lei invece di un giro largo
            bool backUp = following && remaining < 3.5f && MathF.Abs(diff) > 2.0f;
            // Direzione quasi opposta da fermi: inversione a "tre punti" (retro sterzando, poi avanti)
            if (_maneuver <= 0f && _sinceManeuver > 1.2f && !backUp && MathF.Abs(diff) > 2.2f && MathF.Abs(_speed) < 1.5f)
                StartManeuver(diff);

            if (_maneuver > 0f)
            {
                wantSteer = _maneuverSteer;
                wantSpeed = -ReverseSpeed * 0.8f;
            }
            else if (backUp)
            {
                float rearDiff = Noise.WrapAngle(diff - MathF.PI);
                wantSteer = Math.Clamp(-rearDiff * 2f, -MaxSteer, MaxSteer);
                wantSpeed = -MathF.Min(ReverseSpeed, arrive);
            }
            else
            {
                // Pure pursuit: curvatura dell'arco che porta al punto di mira
                float curvature = 2f * MathF.Sin(diff) / dist;
                wantSteer = MathF.Abs(diff) > MathF.PI / 2f
                    ? MathF.Sign(diff) * MaxSteer
                    : Math.Clamp(MathF.Atan(WheelBase * curvature), -MaxSteer, MaxSteer);
                // In curva stretta si rallenta, ma senza fermarsi: l'auto gira solo muovendosi
                wantSpeed = maxSpeed * Noise.Lerp(1f, 0.4f, MathF.Abs(wantSteer) / MaxSteer);
                if (MathF.Abs(diff) > 1.4f) wantSpeed = MathF.Min(wantSpeed, maxSpeed * 0.45f);
                wantSpeed = MathF.Max(MathF.Min(wantSpeed, arrive), MathF.Min(1.2f, arrive));
            }
        }
        else if (_maneuver > 0f)
        {
            _maneuver = 0f;
        }

        // --- Dinamica: ruote che sterzano progressivamente, accelerazione e frenata lineari
        _steer = MoveTowards(_steer, wantSteer, SteerRate * dt);
        bool braking = MathF.Abs(wantSpeed) < MathF.Abs(_speed) - 0.05f || wantSpeed * _speed < 0f;
        float prevSpeed = _speed;
        _speed = MoveTowards(_speed, wantSpeed, (braking ? Brake : Accel) * dt);
        _accel = (_speed - prevSpeed) / MathF.Max(dt, 1e-4f);
        Throttle = Noise.Lerp(Throttle, braking || wantSpeed == 0f ? 0f : 1f, Noise.Damp(6f, dt));

        // Modello a bicicletta: la rotazione dipende dalla velocità (niente rotazioni sul posto)
        float yawRate = _speed * MathF.Tan(_steer) / WheelBase;
        float newYaw = Yaw + yawRate * dt;
        Vector3 step = Dir(newYaw) * (_speed * dt);
        float moved = step.LengthSquared() > 0f ? Move(world, step, newYaw) : 0f;
        _yawRate = Noise.Lerp(_yawRate, yawRate, Noise.Damp(10f, dt));

        // Bloccata contro un ostacolo: si scala la velocità e, se insiste, si fa manovra in retro
        bool pushing = MathF.Abs(wantSpeed) > 0.1f;
        if (pushing && moved < MathF.Abs(_speed) * dt * 0.3f)
        {
            _speed *= 0.5f;
            _blockedTimer += dt;
        }
        else _blockedTimer = MathF.Max(0f, _blockedTimer - dt);

        if (_maneuver > 0f)
        {
            _maneuver -= dt;
            if (_blockedTimer > 0.2f) _maneuver = 0f; // anche dietro è bloccato: si riprova in avanti
        }
        else if (_blockedTimer > 0.35f && aim != null)
        {
            if (following && _maneuverCount >= 3)
            {
                if (_replans++ < 2 && Target is { } dest) Plan(dest, world);
                else ClearTarget();
            }
            else StartManeuver(diff);
        }

        Position.Y = Noise.Lerp(Position.Y, world.GroundHeight(Position), Noise.Damp(25f, dt));

        // Luci: stop accesi in frenata (e in retro), con un breve strascico per non sfarfallare
        bool stopLights = (braking && MathF.Abs(_speed) > 0.3f) || _speed < -0.05f;
        _brakeGlow = stopLights ? 1f : MathF.Max(0f, _brakeGlow - dt * 4f);

        // Assetto: segue il terreno (ponti ad arco, pendii) e si inclina in curva e in frenata
        float hF = world.GroundHeight(Position + Forward * 0.6f), hB = world.GroundHeight(Position - Forward * 0.6f);
        float hR = world.GroundHeight(Position + Side * 0.4f), hL = world.GroundHeight(Position - Side * 0.4f);
        _slopePitch = Noise.Lerp(_slopePitch, Math.Clamp(MathF.Atan2(hB - hF, 1.2f), -0.4f, 0.4f), Noise.Damp(12f, dt));
        _slopeRoll = Noise.Lerp(_slopeRoll, Math.Clamp(MathF.Atan2(hR - hL, 0.8f), -0.3f, 0.3f), Noise.Damp(12f, dt));
        _tilt = Noise.Lerp(_tilt, Math.Clamp(_accel * 0.012f, -0.09f, 0.09f), Noise.Damp(8f, dt));
        _roll = Noise.Lerp(_roll, Math.Clamp(_yawRate * _speed * 0.012f, -0.12f, 0.12f), Noise.Damp(8f, dt));

        _spin += _speed * dt / (WheelRadius * Scale);

        // Sbuffi dallo scarico, più fitti in accelerazione
        _exhaustTimer -= dt;
        if (_exhaustTimer <= 0f)
        {
            _exhaustTimer = _accel > 1f ? 0.07f : MathF.Abs(_speed) > 0.5f ? 0.16f : 0.45f;
            fx.Dust(Vector3.Transform(new Vector3(0.28f, 0.2f, -0.95f), RootMatrix), _accel > 1f ? 2 : 1);
        }
    }

    private Matrix4x4 RootMatrix =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationZ(_slopeRoll) * Matrix4x4.CreateRotationX(_slopePitch)
        * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position);

    public void Collect(List<DrawItem> items)
    {
        Matrix4x4 root = RootMatrix;
        // Motore al minimo: leggera vibrazione; in movimento piccoli sobbalzi
        float bounce = MathF.Sin(_time * 38f) * 0.006f
                       + MathF.Abs(MathF.Sin(_spin * 0.7f)) * 0.012f * MathF.Min(MathF.Abs(_speed) / FastSpeed, 1f);
        Matrix4x4 body = Matrix4x4.CreateRotationZ(_roll) * Matrix4x4.CreateRotationX(-_tilt)
                         * Matrix4x4.CreateTranslation(0, bounce, 0) * root;
        items.Add(new DrawItem(_body, body));
        items.Add(new DrawItem(_headLights, body, 0.6f));
        items.Add(new DrawItem(_tailLights, body, 0.2f + 0.8f * _brakeGlow));
        if (_speed < -0.05f) items.Add(new DrawItem(_reverseLights, body, 1f, CastShadow: false));

        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
            {
                float steer = sz > 0 ? _steer : 0f;
                items.Add(new DrawItem(_wheel, Matrix4x4.CreateRotationX(_spin) * Matrix4x4.CreateRotationY(steer)
                                               * Matrix4x4.CreateTranslation(sx * 0.44f, WheelRadius, sz * 0.5f) * root));
            }
    }

    public void Dispose()
    {
        _body.Dispose();
        _headLights.Dispose();
        _tailLights.Dispose();
        _reverseLights.Dispose();
        _wheel.Dispose();
    }
}
