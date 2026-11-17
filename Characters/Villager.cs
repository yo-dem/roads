using System.Numerics;
using Roads.Rendering;
using Roads.World;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// Personaggio chibi low poly (testa grande, corpo piccolo) con animazioni procedurali:
/// camminata/corsa (gambe e braccia che oscillano, saltello, inclinazione), respiro da fermo,
/// battito di ciglia e sbuffi di polvere ai passi. Il modello guarda verso +Z locale.
/// </summary>
public sealed class Villager : IDisposable
{
    private const float WalkSpeed = 3.4f;   // riferimento per l'ampiezza dell'animazione
    private const float SlowSpeed = 2.4f;   // con Shift
    private const float RunSpeed = 6.5f;    // andatura normale
    private const float Radius = 0.4f;
    private const float HipHeight = 0.33f;

    public Vector3 Position;
    public float Yaw;

    private Vector3 _velocity;
    private float _phase, _time, _move, _lastSin;
    private float _blinkTimer = 2.5f;
    private float _stuckTimer;

    private const float PlanRadius = Radius + 0.03f;

    /// <summary>Punto verso cui il personaggio si sta dirigendo (click del mouse).</summary>
    public Vector3? Target { get; private set; }

    private readonly List<Vector3> _waypoints = new();
    private Vector3? _request, _lastPlanned;
    private float _planCooldown;
    private int _replans;

    /// <summary>Richiede di raggiungere un punto: il percorso viene calcolato al prossimo Update.</summary>
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
        if (path == null || new Vector2(reached.X - Position.X, reached.Z - Position.Z).Length() < 0.2f)
        {
            Target = null;
            return;
        }
        _waypoints.AddRange(path);
        bool exact = Vector2.Distance(new(reached.X, reached.Z), new(goal.X, goal.Z)) < 0.01f;
        Target = reached with { Y = exact ? goal.Y : world.GroundHeight(reached) };
    }

    private readonly Mesh _body, _head, _eye, _arm, _leg;

    private static readonly Vector3 Skin = new(1.0f, 0.82f, 0.66f);
    private static readonly Vector3 Shirt = new(0.30f, 0.62f, 0.86f);
    private static readonly Vector3 Pants = new(0.95f, 0.92f, 0.82f);
    private static readonly Vector3 Hair = new(0.45f, 0.26f, 0.14f);
    private static readonly Vector3 Shoes = new(0.85f, 0.35f, 0.25f);

    public Villager(GL gl, Vector3 position, float yaw)
    {
        Position = position;
        Yaw = yaw;

        var mb = new MeshBuilder();
        mb.AddPrism(new Vector3(0, -0.02f, 0), 0.26f, 0.19f, 0.44f, 8, Shirt);
        mb.AddPrism(new Vector3(0, -0.06f, 0), 0.275f, 0.265f, 0.14f, 8, Pants);
        mb.AddPrism(new Vector3(0, 0.38f, 0), 0.12f, 0.1f, 0.06f, 6, Skin); // collo
        _body = mb.Build(gl);

        mb = new MeshBuilder();
        mb.AddIcosphere(new Vector3(0, 0.34f, 0), new Vector3(0.40f, 0.37f, 0.38f), 1, Skin);
        mb.AddIcosphere(new Vector3(0, 0.50f, -0.04f), new Vector3(0.43f, 0.28f, 0.42f), 1, Hair);
        mb.AddIcosphere(new Vector3(0, 0.36f, -0.12f), new Vector3(0.41f, 0.30f, 0.33f), 1, Hair);
        mb.AddIcosphere(new Vector3(0, 0.27f, 0.385f), new Vector3(0.045f), 0, Skin * 0.92f); // naso
        foreach (float sx in new[] { -0.22f, 0.22f })
            mb.AddBox(new Vector3(sx, 0.235f, 0.305f), new Vector3(0.08f, 0.035f, 0.03f), new Vector3(1f, 0.55f, 0.55f)); // guance
        // Fogliolina sulla testa
        var leaf = new Vector3(0.35f, 0.72f, 0.25f);
        mb.AddTriangle(new(0, 0.76f, 0), new(0.13f, 0.88f, 0.04f), new(0.02f, 1.0f, -0.02f), leaf);
        mb.AddTriangle(new(0, 0.76f, 0), new(0.02f, 1.0f, -0.02f), new(-0.1f, 0.9f, 0.0f), leaf * 0.85f);
        _head = mb.Build(gl);

        mb = new MeshBuilder();
        mb.AddBox(Vector3.Zero, new Vector3(0.075f, 0.11f, 0.06f), new Vector3(0.08f, 0.06f, 0.06f));
        _eye = mb.Build(gl);

        mb = new MeshBuilder();
        mb.AddPrism(new Vector3(0, -0.26f, 0), 0.06f, 0.07f, 0.26f, 6, Shirt);
        mb.AddIcosphere(new Vector3(0, -0.3f, 0), new Vector3(0.075f), 0, Skin);
        _arm = mb.Build(gl);

        mb = new MeshBuilder();
        mb.AddPrism(new Vector3(0, -0.24f, 0), 0.08f, 0.085f, 0.24f, 6, Pants);
        mb.AddBox(new Vector3(0, -0.28f, 0.03f), new Vector3(0.16f, 0.09f, 0.24f), Shoes);
        _leg = mb.Build(gl);
    }

    /// <param name="input">Direzione da tastiera nel mondo (XZ); lunghezza 0 = nessun input.</param>
    /// <param name="slow">Shift premuto: passo lento invece della corsa.</param>
    public void Update(float dt, Vector3 input, bool slow, WorldMap world, Particles fx)
    {
        _time += dt;

        float maxSpeed = slow ? SlowSpeed : RunSpeed;
        bool moving = input.LengthSquared() > 0.01f;

        // Nuova destinazione dal mouse (ricalcolo limitato mentre si tiene premuto il tasto)
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
            // Punti intermedi: si passa al successivo appena ci si avvicina
            while (_waypoints.Count > 1 && Flat(_waypoints[0] - Position).Length() < 0.35f)
                _waypoints.RemoveAt(0);

            var toNext = Flat(_waypoints[0] - Position);
            float dist = toNext.Length();
            float remaining = dist;
            for (int i = 1; i < _waypoints.Count; i++) remaining += Flat(_waypoints[i] - _waypoints[i - 1]).Length();

            if (_waypoints.Count == 1 && dist < 0.15f)
            {
                ClearTarget();
            }
            else
            {
                input = toNext;
                moving = true;
                maxSpeed = MathF.Min(maxSpeed, remaining * 5f + 0.6f); // frena in arrivo
            }
        }

        Vector3 dir = moving ? Vector3.Normalize(input) : Vector3.Zero;
        float targetSpeed = moving ? maxSpeed : 0f;
        _velocity = Vector3.Lerp(_velocity, dir * targetSpeed, Noise.Damp(12f, dt));
        Vector3 before = Position;

        if (moving)
        {
            float targetYaw = MathF.Atan2(dir.X, dir.Z);
            Yaw += Noise.WrapAngle(targetYaw - Yaw) * MathF.Min(1f, 14f * dt);
        }

        // Movimento con "scivolamento" lungo gli ostacoli
        Vector3 step = new(_velocity.X * dt, 0, _velocity.Z * dt);
        if (step.LengthSquared() > 0f)
        {
            Vector3 p = Position;
            if (world.IsWalkable(p + step, Radius)) p += step;
            else
            {
                var sx = new Vector3(step.X, 0, 0);
                var sz = new Vector3(0, 0, step.Z);
                if (world.IsWalkable(p + sx, Radius)) p += sx; else _velocity.X = 0;
                if (world.IsWalkable(p + sz, Radius)) p += sz; else _velocity.Z = 0;
            }
            Position = p;
        }
        Position.Y = Noise.Lerp(Position.Y, world.GroundHeight(Position), Noise.Damp(25f, dt));

        // Bloccato nonostante il percorso: ricalcola un paio di volte, poi rinuncia
        if (Target is { } dest)
        {
            float progress = new Vector2(Position.X - before.X, Position.Z - before.Z).Length();
            _stuckTimer = progress < targetSpeed * dt * 0.25f ? _stuckTimer + dt : 0f;
            if (_stuckTimer > 0.35f)
            {
                if (_replans++ < 2) Plan(dest, world);
                else ClearTarget();
            }
        }

        // --- Stato dell'animazione ---
        float speed = new Vector2(_velocity.X, _velocity.Z).Length();
        _move = Noise.Lerp(_move, Math.Clamp(speed / WalkSpeed, 0f, 1.5f), Noise.Damp(10f, dt));
        if (speed > 0.1f) _phase += dt * (3f + speed * 4.5f);
        else _phase = Noise.Lerp(_phase, MathF.Round(_phase / MathF.PI) * MathF.PI, Noise.Damp(10f, dt)); // piedi uniti

        float s = MathF.Sin(_phase);
        if (_move > 0.3f && MathF.Sign(s) != MathF.Sign(_lastSin))
        {
            float side = s > 0 ? -1f : 1f;
            fx.Dust(Vector3.Transform(new Vector3(side * 0.12f, 0, 0), RootMatrix), speed > WalkSpeed + 0.5f ? 4 : 1);
        }
        _lastSin = s;

        _blinkTimer -= dt;
        if (_blinkTimer < 0f) _blinkTimer = 2f + Random.Shared.NextSingle() * 3f;
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    private const float Scale = 1.3f;

    private Matrix4x4 RootMatrix =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position);

    public void Collect(List<DrawItem> items)
    {
        float amt = MathF.Min(_move, 1.25f);
        float idle = 1f - MathF.Min(amt, 1f);
        float s = MathF.Sin(_phase);
        float swing = s * 0.7f * MathF.Min(amt, 1.1f);

        float bob = -MathF.Abs(s) * 0.05f * amt + MathF.Abs(MathF.Cos(_phase)) * 0.03f * amt;
        float lean = 0.10f * amt;
        float roll = s * 0.05f * amt;
        float breathe = 1f + MathF.Sin(_time * 2.4f) * 0.02f * idle;

        Matrix4x4 root = RootMatrix;
        Matrix4x4 pelvis = Matrix4x4.CreateTranslation(0, HipHeight + bob, 0) * root;
        Matrix4x4 hip = Matrix4x4.CreateRotationZ(roll) * Matrix4x4.CreateRotationX(lean) * pelvis;

        items.Add(new DrawItem(_body, Matrix4x4.CreateScale(1f, breathe, 1f) * hip));

        float headPitch = -lean * 0.6f + MathF.Sin(_phase * 2f) * 0.04f * amt + MathF.Sin(_time * 1.1f) * 0.03f * idle;
        float headRoll = -roll * 1.5f + MathF.Sin(_time * 0.7f) * 0.06f * idle;
        Matrix4x4 head = Matrix4x4.CreateRotationX(headPitch) * Matrix4x4.CreateRotationZ(headRoll)
                         * Matrix4x4.CreateTranslation(0, 0.42f * breathe, 0) * hip;
        items.Add(new DrawItem(_head, head));

        float eyeScale = _blinkTimer < 0.12f ? 0.12f : 1f;
        foreach (float ex in new[] { -0.13f, 0.13f })
            items.Add(new DrawItem(_eye, Matrix4x4.CreateScale(1f, eyeScale, 1f)
                                         * Matrix4x4.CreateTranslation(ex, 0.33f, 0.335f) * head));

        foreach (float side in new[] { 1f, -1f })
        {
            // Braccia in opposizione alle gambe dello stesso lato
            float armAngle = -swing * 0.9f * side + 0.05f * idle * MathF.Sin(_time * 2.4f);
            items.Add(new DrawItem(_arm, Matrix4x4.CreateRotationX(armAngle) * Matrix4x4.CreateRotationZ(side * 0.15f)
                                         * Matrix4x4.CreateTranslation(side * 0.27f, 0.36f, 0) * hip));

            float legAngle = swing * side;
            items.Add(new DrawItem(_leg, Matrix4x4.CreateRotationX(legAngle)
                                         * Matrix4x4.CreateTranslation(side * 0.12f, 0, 0) * pelvis));
        }
    }

    public void Dispose()
    {
        _body.Dispose();
        _head.Dispose();
        _eye.Dispose();
        _arm.Dispose();
        _leg.Dispose();
    }
}
