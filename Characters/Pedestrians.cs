using System.Numerics;
using Roads.Rendering;
using Roads.World;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// I passanti: personaggi (dello stesso genere di quello del giocatore) che passeggiano nei centri abitati. Camminano sui
/// marciapiedi lungo le vie, agli incroci girano l'angolo o attraversano, ogni tanto tornano indietro; nelle piazze
/// girellano da un punto all'altro fermandosi un po', poi riprendono una via. Si muovono come il personaggio del
/// giocatore (stesse collisioni e animazioni), a passo lento. Le auto del traffico si fermano per loro.
/// Ce n'è sempre più o meno lo stesso numero attorno al giocatore: chi resta lontano ricompare altrove, fuori vista.
/// Chi viene investito dall'auto del giocatore vola via (più lontano quanto più l'auto correva), cade a terra sulla
/// schiena, resta lì un po', si rialza e riprende la sua strada dal marciapiede più vicino.
/// </summary>
public sealed class Pedestrians : IDisposable
{
    private const int Target = 14;
    private const float SpawnMin = 34f, SpawnMax = 58f, Despawn = 72f;
    private const float CurbOffset = 0.45f; // distanza dal bordo della carreggiata (i lampioni stanno più in fuori)

    private enum Mode { Sidewalk, Cross, Piazza, Flee, Down }

    private const float HitSpeed = 1.2f;   // sotto questa velocità l'auto la sposta appena, non la investe
    private const float Gravity = 11f;

    private sealed class Walker
    {
        public required Villager Body;
        public bool Active;
        public Mode Mode;
        // Sul marciapiede: tratto, lato (+1 a destra di chi va da A a B), verso di marcia, distanza da A
        public RoadEdge Edge = null!;
        public int Side, Dir;
        public float S;
        // Attraversando o girellando in piazza: dove si va; dopo, su quale marciapiede si prosegue
        public Vector2 Target;
        public RoadNode? Node;
        public RoadEdge? NextEdge;
        public int NextSide, NextDir;
        public float Pause, Stuck, Pace;
        public float? NextS;        // dove si entra nel marciapiede dopo l'attraversamento (null: all'angolo)
        public Vector2 FleeDir;     // scappando: da che parte
        public float FleeTime;
        // Investita: velocità del volo, da quanto è a terra, fase (0 in volo, 1 a terra, 2 si rialza)
        public Vector3 FlyVel;
        public float DownTime, LieFor;
        public int DownPhase;
        public int Wanders;
        // Si scansa: un'auto del traffico l'aspetta da un po' (per quanto ancora e da che parte)
        public float Dodge;
        public Vector2 DodgeDir;
    }

    private readonly List<Walker> _walkers = new();
    private readonly List<Vector3> _positions = new();
    private readonly Random _rng = new(4321);
    private readonly List<RoadEdge> _edges = new();
    private readonly List<RoadNode> _piazzas = new();
    private Vector2 _around = new(float.MaxValue);
    private float _spawnTimer;
    private bool _warm;

    /// <summary>Dove stanno i passanti in giro (per le auto del traffico, che si fermano per loro).</summary>
    public IReadOnlyList<Vector3> Positions => _positions;

    /// <summary>Personaggi dello stesso genere di quello di serie del giocatore, lui escluso (niente gemelli).</summary>
    public static IReadOnlyList<string> Cast()
    {
        string gender = Villager.DefaultCharacter.Split('-')[0]; // "female-e" → "female"
        var same = Villager.Characters.Where(c => c.StartsWith(gender + "-") && c != Villager.DefaultCharacter).ToList();
        return same.Count > 0 ? same : [Villager.DefaultCharacter];
    }

    public Pedestrians(GL gl)
    {
        var cast = Cast();
        for (int i = 0; i < Target; i++)
        {
            var body = new Villager(gl, Vector3.Zero, 0f, cast[i % cast.Count]);
            _walkers.Add(new Walker { Body = body });
        }
    }

    // ------------------------------------------------------------------ marciapiedi

    private static Vector2 RightOf(Vector2 d) => new(-d.Y, d.X);

    /// <summary>Punto del marciapiede sul lato <paramref name="side"/> del tratto, alla distanza <paramref name="s"/> da A.</summary>
    private static Vector2 Walk(RoadEdge e, int side, float s) =>
        e.At(s) + RightOf(e.Tangent(s)) * (side * (e.HalfWidth + CurbOffset));

    private static bool HasSidewalk(Vector2 p) => Terrain.Urban(p.X, p.Y) >= Terrain.WalkFrom + 0.02f;

    /// <summary>Distanza da A dove il marciapiede del tratto arriva all'angolo dell'incrocio <paramref name="n"/>.</summary>
    private static float CornerS(RoadEdge e, RoadNode n)
    {
        float trim = MathF.Min(n.TrimFor(e) + 0.3f, e.Length * 0.5f);
        return n == e.A ? trim : e.Length - trim;
    }

    private void Refresh(Vector2 center)
    {
        _around = center;
        _edges.Clear();
        _piazzas.Clear();
        float far = (SpawnMax + 10f) * (SpawnMax + 10f);
        foreach (var e in RoadNetwork.Current.Edges)
        {
            if (!e.Paved || e.Length < 4f) continue;
            Vector2 mid = e.At(e.Length * 0.5f);
            if (Vector2.DistanceSquared(mid, center) > far || !HasSidewalk(mid)) continue;
            _edges.Add(e);
        }
        foreach (var n in RoadNetwork.Current.Nodes)
            if (n.Piazza && Vector2.DistanceSquared(n.Pos, center) < far && HasSidewalk(n.Pos)) _piazzas.Add(n);
    }

    // ------------------------------------------------------------------ aggiornamento

    /// <param name="makeWay">Dove un'auto del traffico aspetta da un po' un passante e verso dove va: lui si scansa.</param>
    public void Update(float dt, WorldMap world, Particles fx, Vector3 focus, Func<Vector3, float, bool> blocked, Car? driver = null,
                       IReadOnlyList<(Vector2 At, Vector2 CarPos, Vector2 Heading)>? makeWay = null)
    {
        var center = new Vector2(focus.X, focus.Z);
        if (Vector2.Distance(center, _around) > 15f) Refresh(center);

        foreach (var w in _walkers)
        {
            if (!w.Active) continue;
            var p = new Vector2(w.Body.Position.X, w.Body.Position.Z);
            if (Vector2.Distance(p, center) > Despawn || world.ZoneAt(w.Body.Position) == null)
            {
                w.Active = false;
                w.Body.Hide();
            }
        }
        if (!_warm)
        {
            _warm = true;
            for (int k = 0; k < Target * 8; k++) TrySpawn(world, center, 6f);
        }
        _spawnTimer -= dt;
        if (_spawnTimer <= 0f)
        {
            _spawnTimer = 0.4f;
            TrySpawn(world, center, SpawnMin);
        }

        if (driver != null) HitBy(driver, world, fx);
        if (makeWay != null)
            foreach (var m in makeWay) MakeWay(m.At, m.CarPos, m.Heading);

        _positions.Clear();
        foreach (var w in _walkers)
        {
            if (!w.Active) continue;
            if (w.Mode != Mode.Down) Think(w, dt, world);
            _positions.Add(w.Body.Position);
        }
        foreach (var w in _walkers)
        {
            if (!w.Active) continue;
            if (w.Mode == Mode.Down) { Fall(w, dt, world, fx); continue; }
            w.Dodge = MathF.Max(0f, w.Dodge - dt);
            Vector3 input = w.Dodge > 0f ? new Vector3(w.DodgeDir.X, 0f, w.DodgeDir.Y) : w.Pause > 0f ? Vector3.Zero : Steer(w);
            Vector3 before = w.Body.Position;
            w.Body.Update(dt, input, slow: w.Mode != Mode.Flee, world, fx, blocked); // chi scappa corre
            Progress(w, before, input, dt);
        }
    }

    private void TrySpawn(WorldMap world, Vector2 center, float minDistance)
    {
        var w = _walkers.FirstOrDefault(x => !x.Active);
        if (w == null) return;
        Vector2 at;
        float yaw;
        if (_piazzas.Count > 0 && _rng.NextSingle() < 0.3f)
        {
            var n = _piazzas[_rng.Next(_piazzas.Count)];
            at = n.Pos + RandomIn(n.HubRadius * 0.8f);
            yaw = _rng.NextSingle() * MathF.Tau;
            w.Mode = Mode.Piazza;
            w.Node = n;
            w.Wanders = 2 + _rng.Next(4);
            w.Target = at;
        }
        else
        {
            if (_edges.Count == 0) return;
            var e = _edges[_rng.Next(_edges.Count)];
            float a = CornerS(e, e.A) + 0.5f, b = CornerS(e, e.B) - 0.5f;
            if (b <= a) return;
            w.Mode = Mode.Sidewalk;
            w.Edge = e;
            w.Side = _rng.Next(2) * 2 - 1;
            w.Dir = _rng.Next(2) * 2 - 1;
            w.S = a + _rng.NextSingle() * (b - a);
            at = Walk(e, w.Side, w.S);
            Vector2 t = e.Tangent(w.S) * w.Dir;
            yaw = MathF.Atan2(t.X, t.Y);
            if (!HasSidewalk(at)) return;
        }
        float dist = Vector2.Distance(at, center);
        if (dist < minDistance || dist > SpawnMax) return;
        var p3 = new Vector3(at.X, 0f, at.Y);
        if (world.ZoneAt(p3) == null || !world.IsWalkable(p3, Villager.Radius + 0.1f)) return;
        foreach (var o in _walkers)
            if (o.Active && Vector2.DistanceSquared(new Vector2(o.Body.Position.X, o.Body.Position.Z), at) < 4f) return;
        p3.Y = world.GroundHeight(p3);
        w.Body.Appear(p3, yaw);
        w.Active = true;
        w.Pause = 0f;
        w.Stuck = 0f;
        w.Pace = 0.65f + 0.3f * _rng.NextSingle(); // chi passeggia piano, chi va più spedito
    }

    private Vector2 RandomIn(float radius)
    {
        float a = _rng.NextSingle() * MathF.Tau, r = radius * MathF.Sqrt(_rng.NextSingle());
        return new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
    }

    /// <summary>Decide cosa fare: proseguire, girare l'angolo, attraversare, girellare in piazza.</summary>
    private void Think(Walker w, float dt, WorldMap world)
    {
        w.Pause = MathF.Max(0f, w.Pause - dt);
        var p = new Vector2(w.Body.Position.X, w.Body.Position.Z);
        switch (w.Mode)
        {
            case Mode.Sidewalk:
            {
                var node = w.Dir > 0 ? w.Edge.B : w.Edge.A;
                float end = CornerS(w.Edge, node);
                if ((end - w.S) * w.Dir < 0.3f) AtCorner(w, node);
                break;
            }
            case Mode.Cross:
                if (Vector2.Distance(p, w.Target) < 0.35f)
                {
                    if (w.NextEdge != null)
                    {
                        w.Mode = Mode.Sidewalk;
                        w.Edge = w.NextEdge;
                        w.Side = w.NextSide;
                        w.Dir = w.NextDir;
                        w.S = w.NextS ?? CornerS(w.Edge, w.Dir > 0 ? w.Edge.A : w.Edge.B);
                        w.NextS = null;
                    }
                    else
                    {
                        w.Mode = Mode.Piazza;
                        w.Target = p;
                    }
                }
                break;
            case Mode.Flee:
                w.FleeTime -= dt;
                if (w.FleeTime <= 0f) BackToSidewalk(w, p);
                break;
            case Mode.Piazza:
                if (w.Pause > 0f || Vector2.Distance(p, w.Target) > 0.4f) break;
                if (w.Wanders-- <= 0) { LeavePiazza(w); break; }
                // Un altro punto della piazza (libero), poi una sosta
                for (int k = 0; k < 6; k++)
                {
                    Vector2 t = w.Node!.Pos + RandomIn(w.Node.HubRadius * 0.85f);
                    if (!world.IsWalkable(new Vector3(t.X, 0f, t.Y), Villager.Radius + 0.15f)) continue;
                    w.Target = t;
                    break;
                }
                w.Pause = 1f + 3f * _rng.NextSingle();
                break;
        }
    }

    /// <summary>
    /// In fondo al marciapiede: in piazza si girella; a un incrocio si prende un'altra via, di solito girando l'angolo
    /// (il marciapiede più vicino), a volte attraversando; ogni tanto si torna indietro.
    /// </summary>
    private void AtCorner(Walker w, RoadNode node)
    {
        if (node.Piazza && HasSidewalk(node.Pos))
        {
            w.Mode = Mode.Piazza;
            w.Node = node;
            w.Wanders = 1 + _rng.Next(4);
            w.Target = new Vector2(w.Body.Position.X, w.Body.Position.Z);
            return;
        }
        var options = node.Edges.Where(o => o.Paved && o.Length > 3f).ToList();
        if (!node.Sidewalks || options.Count == 0 || _rng.NextSingle() < 0.12f)
        {
            w.Dir = -w.Dir; // si torna indietro
            return;
        }
        var e = options[_rng.Next(options.Count)];
        int dir = e.A == node ? 1 : -1;
        Vector2 here = new(w.Body.Position.X, w.Body.Position.Z);
        float s0 = CornerS(e, node);
        Vector2 a = Walk(e, 1, s0), b = Walk(e, -1, s0);
        bool nearA = Vector2.DistanceSquared(here, a) < Vector2.DistanceSquared(here, b);
        int side = (_rng.NextSingle() < 0.7f) == nearA ? 1 : -1; // di solito l'angolo, a volte si attraversa
        Vector2 target = side > 0 ? a : b;
        if (!HasSidewalk(target)) { w.Dir = -w.Dir; return; }
        w.Mode = Mode.Cross;
        w.Target = target;
        w.NextEdge = e;
        w.NextSide = side;
        w.NextDir = dir;
    }

    private void LeavePiazza(Walker w)
    {
        var node = w.Node!;
        var options = node.Edges.Where(o => o.Paved && o.Length > 3f).ToList();
        if (options.Count == 0) { w.Wanders = 3; return; }
        var e = options[_rng.Next(options.Count)];
        int side = _rng.Next(2) * 2 - 1;
        w.Mode = Mode.Cross;
        w.Target = Walk(e, side, CornerS(e, node));
        w.NextEdge = e;
        w.NextSide = side;
        w.NextDir = e.A == node ? 1 : -1;
    }

    /// <summary>Direzione in cui camminare (lunghezza = andatura: sotto 1 è il passo lento del personaggio).</summary>
    /// <summary>
    /// Un'auto del traffico aspetta da un po' il passante in <paramref name="at"/>: lui si scansa di lato, dalla parte
    /// in cui è già rispetto alla traiettoria dell'auto, e poi riprende la sua strada (in piazza ne sceglie un'altra).
    /// </summary>
    private void MakeWay(Vector2 at, Vector2 carPos, Vector2 heading)
    {
        Walker? best = null;
        float bestD = 0.6f * 0.6f;
        foreach (var w in _walkers)
        {
            if (!w.Active || w.Mode == Mode.Down || w.Dodge > 0f) continue;
            float d = Vector2.DistanceSquared(new Vector2(w.Body.Position.X, w.Body.Position.Z), at);
            if (d < bestD) { bestD = d; best = w; }
        }
        if (best == null) return;
        Vector2 right = new(heading.Y, -heading.X);
        Vector2 p = new(best.Body.Position.X, best.Body.Position.Z);
        float side = Vector2.Dot(p - carPos, right);
        best.DodgeDir = right * (side >= 0f ? 1f : -1f);
        best.Dodge = 1.4f; // al passo svelto: abbastanza per uscire dall'ingombro dell'auto
        best.Pause = 0f;
        best.Stuck = 0f;
        if (best.Mode == Mode.Piazza) best.Target = p + best.DodgeDir * 1.5f;
    }

    private static Vector3 Steer(Walker w)
    {
        var p = new Vector2(w.Body.Position.X, w.Body.Position.Z);
        Vector2 aim;
        if (w.Mode == Mode.Sidewalk)
        {
            var node = w.Dir > 0 ? w.Edge.B : w.Edge.A;
            float end = CornerS(w.Edge, node);
            float s = w.S + w.Dir * 1.2f;
            if ((end - s) * w.Dir < 0f) s = end;
            aim = Walk(w.Edge, w.Side, s);
        }
        else if (w.Mode == Mode.Flee) return new Vector3(w.FleeDir.X, 0f, w.FleeDir.Y);
        else aim = w.Target;
        Vector2 d = aim - p;
        if (d.LengthSquared() < 1e-4f) return Vector3.Zero;
        d = Vector2.Normalize(d) * w.Pace;
        return new Vector3(d.X, 0f, d.Y);
    }

    /// <summary>Quanto si è avanzati lungo il marciapiede; chi resta bloccato a lungo cambia idea.</summary>
    private void Progress(Walker w, Vector3 before, Vector3 input, float dt)
    {
        Vector3 moved = w.Body.Position - before;
        float step = new Vector2(moved.X, moved.Z).Length();
        if (w.Mode == Mode.Sidewalk)
        {
            Vector2 t = w.Edge.Tangent(w.S) * w.Dir;
            w.S += w.Dir * MathF.Max(0f, Vector2.Dot(new Vector2(moved.X, moved.Z), t));
            w.S = Math.Clamp(w.S, 0f, w.Edge.Length);
        }
        bool trying = input.LengthSquared() > 0.01f;
        w.Stuck = trying && step < 0.2f * dt ? w.Stuck + dt : 0f;
        if (w.Stuck < 2.5f) return;
        w.Stuck = 0f;
        switch (w.Mode)
        {
            case Mode.Sidewalk: w.Dir = -w.Dir; break;              // c'è qualcosa in mezzo: si torna indietro
            case Mode.Cross:
                // Torna sul marciapiede da cui veniva; chi non ne aveva uno (era in piazza, o scappava) va al più vicino
                if (w.Edge != null) { w.Mode = Mode.Sidewalk; w.Dir = -w.Dir; }
                else BackToSidewalk(w, new Vector2(w.Body.Position.X, w.Body.Position.Z));
                break;
            case Mode.Piazza: w.Target = new Vector2(w.Body.Position.X, w.Body.Position.Z); break;
            case Mode.Flee: w.FleeDir = new Vector2(-w.FleeDir.Y, w.FleeDir.X); break; // di là non si passa: di lato
        }
    }

    // ------------------------------------------------------------------ investiti

    /// <summary>
    /// L'auto guidata dal giocatore tocca un passante: se va piano lo sposta appena (lui si scansa); se corre lo
    /// investe: vola via nella direzione dell'auto, un po' di lato e in alto, e cade.
    /// </summary>
    private void HitBy(Car car, WorldMap world, Particles fx)
    {
        var box = car.Box;
        Vector2 vel = new(car.Velocity.X, car.Velocity.Z);
        float speed = vel.Length();
        foreach (var w in _walkers)
        {
            if (!w.Active || w.Mode == Mode.Down || w.Body.Busy) continue;
            var p = new Vector2(w.Body.Position.X, w.Body.Position.Z);
            float depth = box.CircleDepth(p, Villager.Radius + 0.05f, out var n);
            if (depth <= 0f) continue;
            if (speed < HitSpeed)
            {
                // Appena sfiorata: un passo indietro fuori dall'ingombro dell'auto
                var to = w.Body.Position + new Vector3(n.X, 0f, n.Y) * (depth + 0.02f);
                if (world.IsWalkable(to, Villager.Radius)) w.Body.Position = to;
                continue;
            }
            // Investita: la spinta dell'auto (le passa un po' più della sua velocità) più un po' di lato, e verso l'alto
            Vector2 fly = vel * 1.1f + n * (0.8f + speed * 0.1f);
            w.Mode = Mode.Down;
            w.DownPhase = 0;
            w.DownTime = 0f;
            w.LieFor = 2.5f + 2f * _rng.NextSingle();
            w.FlyVel = new Vector3(fly.X, 2f + MathF.Min(speed, 12f) * 0.3f, fly.Y);
            w.Body.Yaw = MathF.Atan2(-vel.X, -vel.Y); // guarda l'auto: cade all'indietro, nel verso in cui vola
            w.Body.Tilt = 0f;
            car.Shove(-vel * 0.06f); // l'urto si sente appena
            fx.Dust(w.Body.Position + new Vector3(0f, 0.3f, 0f), 3);
        }
    }

    /// <summary>In volo, a terra, rialzandosi: poi si riprende la strada dal marciapiede più vicino.</summary>
    private void Fall(Walker w, float dt, WorldMap world, Particles fx)
    {
        var b = w.Body;
        w.DownTime += dt;
        Vector3 pos = b.Position;
        float ground = world.GroundHeight(pos);
        switch (w.DownPhase)
        {
            case 0: // in volo: parabola, ruotando all'indietro fino a stendersi
            {
                Vector3 step = new(w.FlyVel.X * dt, 0f, w.FlyVel.Z * dt);
                if (world.IsWalkable(pos + step, Villager.Radius)) pos += step;
                else w.FlyVel = new Vector3(-w.FlyVel.X * 0.3f, w.FlyVel.Y, -w.FlyVel.Z * 0.3f); // contro un muro: rimbalza
                w.FlyVel.Y -= Gravity * dt;
                pos.Y += w.FlyVel.Y * dt;
                b.Tilt = MathF.Max(-MathF.PI * 0.5f, b.Tilt - 7f * dt);
                if (pos.Y <= ground && w.FlyVel.Y < 0f)
                {
                    pos.Y = ground;
                    w.DownPhase = 1;
                    w.DownTime = 0f;
                    b.Tilt = -MathF.PI * 0.5f;
                    fx.Dust(pos, 4);
                }
                break;
            }
            case 1: // a terra: scivola un po' e resta distesa
            {
                Vector3 h = new(w.FlyVel.X, 0f, w.FlyVel.Z);
                float sp = h.Length();
                if (sp > 0.01f)
                {
                    Vector3 step = h * dt;
                    if (world.IsWalkable(pos + step, Villager.Radius)) pos += step;
                    float slow = MathF.Max(0f, sp - 9f * dt) / sp;
                    w.FlyVel = new Vector3(w.FlyVel.X * slow, 0f, w.FlyVel.Z * slow);
                }
                pos.Y = ground;
                if (w.DownTime > w.LieFor) { w.DownPhase = 2; w.DownTime = 0f; }
                break;
            }
            default: // si rialza piano
                pos.Y = ground;
                b.Tilt = MathF.Min(0f, b.Tilt + 2.2f * dt);
                if (b.Tilt >= 0f)
                {
                    b.Tilt = 0f;
                    w.Stuck = 0f;
                    w.Pace = 0.6f; // un po' acciaccata
                    BackToSidewalk(w, new Vector2(pos.X, pos.Z));
                }
                break;
        }
        b.Ride(dt, pos, b.Yaw, 0f);
    }

    // ------------------------------------------------------------------ chi viene buttato fuori dall'auto

    /// <summary>
    /// Chi guidava l'auto rubata salta giù da <paramref name="seat"/> a <paramref name="door"/> e scappa di corsa
    /// allontanandosi dall'auto (<paramref name="car"/>); poi torna a passeggiare dal marciapiede più vicino.
    /// Prende il posto di un passante libero, o di quello più lontano.
    /// </summary>
    public void Eject(Vector3 seat, Vector3 door, Vector3 car, Vector3 focus)
    {
        var center = new Vector2(focus.X, focus.Z);
        var w = _walkers.FirstOrDefault(x => !x.Active)
                ?? _walkers.MaxBy(x => Vector2.DistanceSquared(new Vector2(x.Body.Position.X, x.Body.Position.Z), center))!;
        Vector2 away = new(door.X - car.X, door.Z - car.Z);
        away = away.LengthSquared() > 1e-4f ? Vector2.Normalize(away) : Vector2.UnitX;
        // Scappa di traverso rispetto all'auto, un po' in avanti o indietro: non resta davanti al muso
        float turn = (_rng.NextSingle() - 0.5f) * 1.2f;
        away = new Vector2(away.X * MathF.Cos(turn) - away.Y * MathF.Sin(turn), away.X * MathF.Sin(turn) + away.Y * MathF.Cos(turn));
        w.Body.HopOut(seat, door, MathF.Atan2(away.X, away.Y));
        w.Active = true;
        w.Mode = Mode.Flee;
        w.FleeDir = away;
        w.FleeTime = 3.5f;
        w.Pause = 0f;
        w.Stuck = 0f;
        w.Pace = 1f;
    }

    /// <summary>Finita la fuga: si va al marciapiede più vicino e si riprende a passeggiare.</summary>
    private void BackToSidewalk(Walker w, Vector2 p)
    {
        if (!RoadNetwork.Current.Nearest(p, 14f, out var hit) || !hit.Edge.Paved)
        {
            w.Mode = Mode.Piazza; // lontano da strade asfaltate: gironzola lì
            w.Node = RoadNetwork.Current.Nodes.MinBy(n => Vector2.DistanceSquared(n.Pos, p));
            w.Target = p;
            w.Wanders = 2;
            return;
        }
        var e = hit.Edge;
        Vector2 right = RightOf(e.Tangent(hit.S));
        int side = Vector2.Dot(p - hit.Point, right) >= 0f ? 1 : -1;
        w.Mode = Mode.Cross;
        w.Target = Walk(e, side, hit.S);
        w.NextEdge = e;
        w.NextSide = side;
        w.NextDir = _rng.Next(2) * 2 - 1;
        w.NextS = hit.S;
    }

    // ------------------------------------------------------------------ disegno

    public void Collect(List<DrawItem> items, Func<Vector2, bool> visible)
    {
        foreach (var w in _walkers)
            if (w.Active && visible(new Vector2(w.Body.Position.X, w.Body.Position.Z))) w.Body.Collect(items);
    }

    public int Count => _walkers.Count(w => w.Active);

    /// <summary>Quanti passanti sono a terra (investiti) in questo momento.</summary>
    public int DownCount => _walkers.Count(w => w.Active && w.Mode == Mode.Down);

    /// <summary>Test: il passante in piedi più vicino al punto, che si ferma lì ad aspettare (null se non ce n'è).</summary>
    public Vector3? Nearest(Vector3 p)
    {
        var w = _walkers.Where(x => x.Active && x.Mode != Mode.Down).MinBy(x => Vector3.DistanceSquared(x.Body.Position, p));
        if (w == null) return null;
        w.Pause = 6f;
        return w.Body.Position;
    }

    public void Dispose()
    {
        foreach (var w in _walkers) w.Body.Dispose();
    }
}
