using System.Numerics;
using Roads.Rendering;
using Roads.World;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// Automobilina giocattolo low poly. Stessi comandi del personaggio (click con ricerca del percorso,
/// WASD, Shift per andare piano) ma con una guida da auto: sterza progressivamente, rallenta in curva,
/// ruote che girano e sterzano, carrozzeria che si inclina su pendenze, curve e frenate, sbuffi dallo scarico.
/// Il modello guarda verso +Z locale.
/// </summary>
public sealed class Car : IDisposable
{
    private const float SlowSpeed = 2.6f;
    private const float FastSpeed = 6.8f;
    private const float TurnRate = 4.2f;      // rad/s
    private const float Radius = 0.55f;
    private const float PlanRadius = Radius + 0.03f;
    private const float WheelRadius = 0.2f;
    private const float Scale = 1.1f;

    public Vector3 Position;
    public float Yaw;

    private float _speed, _yawRate, _spin, _steer, _time;
    private float _slopePitch, _slopeRoll, _tilt, _roll;
    private float _exhaustTimer, _stuckTimer;

    private readonly Mesh _body, _lights, _wheel;

    // ------------------------------------------------------------------ destinazione e percorso

    public Vector3? Target { get; private set; }

    private readonly List<Vector3> _waypoints = new();
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
        _stuckTimer = 0f;
        var path = PathFinder.Find(world, Position, goal, PlanRadius, out Vector3 reached);
        _waypoints.Clear();
        if (path == null || Flat(reached - Position).Length() < 0.25f)
        {
            Target = null;
            return;
        }
        _waypoints.AddRange(path);
        bool exact = Vector2.Distance(new(reached.X, reached.Z), new(goal.X, goal.Z)) < 0.01f;
        Target = reached with { Y = exact ? goal.Y : world.GroundHeight(reached) };
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
        {
            mb.AddBox(new Vector3(sx * 0.29f, 0.41f, 0.795f), new Vector3(0.16f, 0.1f, 0.03f), new Vector3(1f, 0.95f, 0.7f));
            mb.AddBox(new Vector3(sx * 0.31f, 0.41f, -0.795f), new Vector3(0.14f, 0.08f, 0.03f), new Vector3(0.95f, 0.15f, 0.12f));
        }
        _lights = mb.Build(gl);

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
    private Vector3 Forward => new(MathF.Sin(Yaw), 0, MathF.Cos(Yaw));
    private Vector3 Side => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw)); // +X locale

    public void Update(float dt, Vector3 input, bool slow, WorldMap world, Particles fx)
    {
        _time += dt;
        float maxSpeed = slow ? SlowSpeed : FastSpeed;
        bool moving = input.LengthSquared() > 0.01f;

        _planCooldown -= dt;
        if (!moving && _request is { } req && _planCooldown <= 0f)
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

        if (moving)
        {
            ClearTarget(); // la tastiera ha la precedenza sul click
        }
        else if (Target != null && _waypoints.Count > 0)
        {
            while (_waypoints.Count > 1 && Flat(_waypoints[0] - Position).Length() < 0.5f)
                _waypoints.RemoveAt(0);

            var toNext = Flat(_waypoints[0] - Position);
            float dist = toNext.Length();
            float remaining = dist;
            for (int i = 1; i < _waypoints.Count; i++) remaining += Flat(_waypoints[i] - _waypoints[i - 1]).Length();

            if (_waypoints.Count == 1 && dist < 0.25f)
                ClearTarget();
            else
            {
                input = toNext;
                moving = true;
                maxSpeed = MathF.Min(maxSpeed, remaining * 3f + 0.5f); // frena in arrivo
            }
        }

        // Sterzo progressivo; in curva stretta l'auto rallenta quasi fino a girare sul posto
        float targetSpeed = 0f, diff = 0f;
        if (moving)
        {
            Vector3 dir = Vector3.Normalize(input);
            diff = Noise.WrapAngle(MathF.Atan2(dir.X, dir.Z) - Yaw);
            float turn = Math.Clamp(diff, -TurnRate * dt, TurnRate * dt);
            Yaw += turn;
            _yawRate = Noise.Lerp(_yawRate, turn / dt, Noise.Damp(10f, dt));
            targetSpeed = maxSpeed * MathF.Max(0.2f, MathF.Cos(MathF.Min(MathF.Abs(diff), MathF.PI / 2f)));
        }
        else
        {
            _yawRate = Noise.Lerp(_yawRate, 0f, Noise.Damp(10f, dt));
        }
        _steer = Noise.Lerp(_steer, Math.Clamp(diff, -0.5f, 0.5f), Noise.Damp(12f, dt));

        float prevSpeed = _speed;
        _speed = Noise.Lerp(_speed, targetSpeed, Noise.Damp(targetSpeed > _speed ? 3.5f : 7f, dt));
        float accel = (_speed - prevSpeed) / MathF.Max(dt, 1e-4f);

        // Movimento con scivolamento lungo gli ostacoli
        Vector3 before = Position;
        Vector3 step = Forward * (_speed * dt);
        if (step.LengthSquared() > 0f)
        {
            Vector3 p = Position;
            if (world.IsWalkable(p + step, Radius)) p += step;
            else
            {
                var sx = new Vector3(step.X, 0, 0);
                var sz = new Vector3(0, 0, step.Z);
                bool moved = false;
                if (world.IsWalkable(p + sx, Radius)) { p += sx; moved = true; }
                if (world.IsWalkable(p + sz, Radius)) { p += sz; moved = true; }
                if (!moved) _speed *= 0.5f;
            }
            Position = p;
        }
        Position.Y = Noise.Lerp(Position.Y, world.GroundHeight(Position), Noise.Damp(25f, dt));

        if (Target is { } dest)
        {
            float progress = Flat(Position - before).Length();
            _stuckTimer = progress < MathF.Max(targetSpeed, 0.5f) * dt * 0.15f ? _stuckTimer + dt : 0f;
            if (_stuckTimer > 0.6f)
            {
                if (_replans++ < 2) Plan(dest, world);
                else ClearTarget();
            }
        }

        // Assetto: segue il terreno (ponti ad arco, pendii) e si inclina in curva e in frenata
        float hF = world.GroundHeight(Position + Forward * 0.6f), hB = world.GroundHeight(Position - Forward * 0.6f);
        float hR = world.GroundHeight(Position + Side * 0.4f), hL = world.GroundHeight(Position - Side * 0.4f);
        _slopePitch = Noise.Lerp(_slopePitch, Math.Clamp(MathF.Atan2(hB - hF, 1.2f), -0.4f, 0.4f), Noise.Damp(12f, dt));
        _slopeRoll = Noise.Lerp(_slopeRoll, Math.Clamp(MathF.Atan2(hR - hL, 0.8f), -0.3f, 0.3f), Noise.Damp(12f, dt));
        _tilt = Noise.Lerp(_tilt, Math.Clamp(accel * 0.012f, -0.09f, 0.09f), Noise.Damp(8f, dt));
        _roll = Noise.Lerp(_roll, Math.Clamp(_yawRate * _speed * 0.012f, -0.12f, 0.12f), Noise.Damp(8f, dt));

        _spin += _speed * dt / (WheelRadius * Scale);

        // Sbuffi dallo scarico, più fitti in accelerazione
        _exhaustTimer -= dt;
        if (_exhaustTimer <= 0f)
        {
            _exhaustTimer = accel > 1f ? 0.07f : _speed > 0.5f ? 0.16f : 0.45f;
            fx.Dust(Vector3.Transform(new Vector3(0.28f, 0.2f, -0.95f), RootMatrix), accel > 1f ? 2 : 1);
        }
    }

    private Matrix4x4 RootMatrix =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationZ(_slopeRoll) * Matrix4x4.CreateRotationX(_slopePitch)
        * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position);

    public void Collect(List<DrawItem> items)
    {
        Matrix4x4 root = RootMatrix;
        // Motore al minimo: leggera vibrazione; in movimento piccoli sobbalzi
        float bounce = MathF.Sin(_time * 38f) * 0.006f + MathF.Abs(MathF.Sin(_spin * 0.7f)) * 0.012f * MathF.Min(_speed / FastSpeed, 1f);
        Matrix4x4 body = Matrix4x4.CreateRotationZ(_roll) * Matrix4x4.CreateRotationX(-_tilt)
                         * Matrix4x4.CreateTranslation(0, bounce, 0) * root;
        items.Add(new DrawItem(_body, body));
        items.Add(new DrawItem(_lights, body, 0.6f));

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
        _lights.Dispose();
        _wheel.Dispose();
    }
}
