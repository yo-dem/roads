using System.Numerics;
using Roads.Rendering;
using Roads.World;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// Il traffico: automobiline guidate dal gioco che girano per le strade asfaltate attorno al giocatore. Ognuna sta nella
/// sua corsia (a destra, sui viali in una delle due, sui sensi unici nel verso giusto), a ogni incrocio sceglie dove
/// andare (più spesso dritto, raramente in un vicolo cieco) e attraversa lo slargo su una curva morbida che unisce la
/// corsia da cui arriva a quella in cui entra. Rallenta prima delle curve, tiene la distanza da chi ha davanti (altre
/// auto, il giocatore, il personaggio a piedi, il treno e le sbarre del passaggio a livello) e, se l'auto del giocatore
/// o il personaggio fermi le sbarrano la strada, li aggira passando nell'altra corsia quando è libera. Ai semafori si
/// ferma prima della linea d'arresto col rosso, e col giallo se fa in tempo a fermarsi senza inchiodare.
/// Nascono fuori dalla vista (nella nebbia) e spariscono quando ci si allontana: attorno al giocatore ce n'è sempre
/// più o meno lo stesso numero. Il moto è cinematico: nessuna fisica, la posizione segue la corsia.
/// </summary>
public sealed class Traffic : IDisposable
{
    /// <summary>Il traffico in gioco (per gli ostacoli di <see cref="WorldMap.IsWalkable"/>).</summary>
    public static Traffic? Current { get; private set; }

    private const int Target = 18;              // auto attorno al giocatore
    private const float SpawnMin = 62f, SpawnMax = 108f, Despawn = 125f;
    private const float Scale = 1.1f;           // come l'auto del giocatore
    private const float WheelBase = 1.1f;
    private const float HullOffset = 0.46f, HullRadius = 0.5f; // due cerchi, muso e coda (come Car)
    private const float Accel = 2.6f, Decel = 6f, HardDecel = 14f;
    private const float Margin = 0.35f;         // spazio lasciato davanti, oltre al contatto

    private static readonly Vector3[] Paints =
    [
        new(0.22f, 0.42f, 0.85f),  // blu
        new(0.95f, 0.78f, 0.22f),  // giallo
        new(0.92f, 0.92f, 0.90f),  // bianco
        new(0.30f, 0.66f, 0.38f),  // verde
        new(0.55f, 0.57f, 0.60f),  // grigio
        new(0.96f, 0.55f, 0.20f),  // arancio
        new(0.20f, 0.22f, 0.26f),  // quasi nero
        new(0.55f, 0.80f, 0.85f),  // azzurro
    ];

    private readonly CarModel[] _models;
    private readonly List<Vehicle> _cars = new();
    private readonly Random _rng = new(1234);
    private readonly List<RoadEdge> _candidates = new();
    private readonly List<Circle> _obstacles = new();
    private Vector2 _candidatesAround = new(float.MaxValue);
    private float _spawnTimer, _time;
    private int _nextId;
    private bool _warm;

    public int Count => _cars.Count;

    public Traffic(GL gl)
    {
        _models = Paints.Select((p, i) => new CarModel(gl, p, 11 + i)).ToArray();
        Current = this;
    }

    // ------------------------------------------------------------------ strade percorribili

    /// <summary>Il traffico gira solo sull'asfalto e dove le corsie hanno un verso (niente sterrati a corsia unica).</summary>
    private static bool Drivable(RoadEdge e) => e.Paved && e.Layout != RoadLayout.Single && e.Length > 0.5f;

    /// <summary>Si può percorrere il tratto nel verso <paramref name="dir"/> (+1 da A a B)?</summary>
    private static bool Allows(RoadEdge e, int dir) => Drivable(e) && (e.OneWay == 0 || e.OneWay == dir);

    private static Vector2 RightOf(Vector2 d) => new(-d.Y, d.X);

    private static Vector2 LanePoint(RoadEdge e, int dir, float lane, float s) =>
        e.At(s) + RightOf(e.Tangent(s) * dir) * lane;

    /// <summary>Dove comincia la carreggiata per chi entra nel tratto nel verso <paramref name="dir"/>.</summary>
    private static float StartS(RoadEdge e, int dir) => dir > 0 ? e.A.TrimFor(e) : e.Length - e.B.TrimFor(e);

    /// <summary>Dove finisce la carreggiata (inizia lo slargo dell'incrocio) per chi va nel verso <paramref name="dir"/>.</summary>
    private static float EndS(RoadEdge e, int dir) => dir > 0 ? e.Length - e.B.TrimFor(e) : e.A.TrimFor(e);

    // ------------------------------------------------------------------ curva nell'incrocio

    /// <summary>Curva di Bézier cubica che attraversa lo slargo, misurata per poterla percorrere a velocità costante.</summary>
    private sealed class Curve
    {
        private const int N = 16;
        private readonly Vector2 _p0, _p1, _p2, _p3;
        private readonly float[] _cum = new float[N + 1];
        public float Length => _cum[N];

        public Curve(Vector2 p0, Vector2 d0, Vector2 p3, Vector2 d1, float reach)
        {
            _p0 = p0;
            _p1 = p0 + d0 * reach;
            _p2 = p3 - d1 * reach;
            _p3 = p3;
            Vector2 prev = p0;
            for (int i = 1; i <= N; i++)
            {
                Vector2 q = Eval(i / (float)N);
                _cum[i] = _cum[i - 1] + Vector2.Distance(prev, q);
                prev = q;
            }
            if (_cum[N] < 0.01f) _cum[N] = 0.01f;
        }

        private Vector2 Eval(float t)
        {
            float u = 1f - t;
            return u * u * u * _p0 + 3f * u * u * t * _p1 + 3f * u * t * t * _p2 + t * t * t * _p3;
        }

        private Vector2 Derivative(float t)
        {
            float u = 1f - t;
            return 3f * u * u * (_p1 - _p0) + 6f * u * t * (_p2 - _p1) + 3f * t * t * (_p3 - _p2);
        }

        private float ParamAt(float d)
        {
            d = Math.Clamp(d, 0f, Length);
            int i = 1;
            while (i < N && _cum[i] < d) i++;
            float span = _cum[i] - _cum[i - 1];
            float f = span > 1e-5f ? (d - _cum[i - 1]) / span : 0f;
            return (i - 1 + f) / N;
        }

        public Vector2 At(float d) => Eval(ParamAt(d));

        public Vector2 Tangent(float d)
        {
            Vector2 v = Derivative(ParamAt(d));
            if (v.LengthSquared() > 1e-8f) return Vector2.Normalize(v);
            return Vector2.Normalize(_p3 - _p0 + new Vector2(1e-4f, 0f));
        }
    }

    // ------------------------------------------------------------------ un'auto

    private sealed class Vehicle
    {
        public int Id;
        public CarModel Model = null!;
        // Sul tratto: verso, corsia (scostamento verso la propria destra) e distanza da A
        public RoadEdge Edge = null!;
        public int Dir;
        public float Lane, S;
        // Dove andrà al prossimo incrocio (scelto entrando nel tratto) e la curva per arrivarci
        public RoadEdge? Next;
        public int NextDir;
        public float NextLane, TurnSpeed;
        public Curve? Turn;
        public bool Turning;
        public float TurnD;
        // Andatura
        public float Speed, Cruise, Want;
        // Aggiramento di un ostacolo fermo: scostamento in più verso sinistra (negativo) e dove vuole arrivare
        public float Shift, ShiftTarget, Blocked;
        public Vector2 BypassAt;
        public float Ghost; // per qualche istante ignora le altre auto (si sblocca da un ingorgo senza uscita)
        // In coda per una buona ragione (rosso, treno, o dietro a chi lo è): chi le sta dietro aspetta e basta
        public bool Queued, AtLight;
        // Posa
        public Vector2 Pos, Heading;
        public float Y, Yaw, Pitch, Spin, Steer, Brake;
        public bool Placed, Dead;

        public float Remaining => (EndS(Edge, Dir) - S) * Dir;
    }

    private readonly record struct Circle(Vector2 C, float R, int Owner, bool Car, bool Moving);

    // ------------------------------------------------------------------ aggiornamento

    /// <summary>
    /// Fa nascere, guidare e sparire le auto. <paramref name="player"/> = auto del giocatore, <paramref name="walker"/>
    /// = il personaggio a piedi (null se è in auto o in treno), <paramref name="focus"/> = dove sta chi si comanda.
    /// </summary>
    public void Update(float dt, float clock, WorldMap world, Car player, Vector3? walker, Vector3 focus)
    {
        _time = clock; // lo stesso orologio dei semafori disegnati
        var center = new Vector2(focus.X, focus.Z);

        // Sparisce chi è troppo lontano o è finito fuori dal territorio caricato
        _cars.RemoveAll(v => v.Dead || Vector2.Distance(v.Pos, center) > Despawn
                             || world.ZoneAt(new Vector3(v.Pos.X, 0f, v.Pos.Y)) == null);

        if (Vector2.Distance(center, _candidatesAround) > 20f) RefreshCandidates(center);
        if (!_warm)
        {
            // Alla partenza le strade sono già animate: auto anche vicine, prima che si veda qualunque cosa
            _warm = true;
            for (int k = 0; k < Target * 6 && _cars.Count < Target; k++) TrySpawn(world, center, player, 14f);
        }
        _spawnTimer -= dt;
        if (_spawnTimer <= 0f && _cars.Count < Target)
        {
            _spawnTimer = 0.3f;
            TrySpawn(world, center, player, SpawnMin);
        }

        // Ostacoli di questo istante
        _obstacles.Clear();
        foreach (var v in _cars)
            foreach (float k in new[] { 1f, -1f })
                _obstacles.Add(new Circle(v.Pos + v.Heading * (HullOffset * k), HullRadius, v.Id, true, v.Speed > 0.3f));
        Vector2 ph = new(player.Heading.X, player.Heading.Z);
        Vector2 pp = new(player.Position.X, player.Position.Z);
        bool playerMoving = player.Velocity.LengthSquared() > 0.3f * 0.3f;
        foreach (float k in new[] { 1f, -1f })
            _obstacles.Add(new Circle(pp + ph * (0.46f * k), 0.5f, -1, false, playerMoving));
        if (walker is { } w) _obstacles.Add(new Circle(new Vector2(w.X, w.Z), 0.35f, -2, false, false));

        foreach (var v in _cars) Drive(v, dt, world);
    }

    private void RefreshCandidates(Vector2 center)
    {
        _candidatesAround = center;
        _candidates.Clear();
        float lo = (SpawnMin - 50f) * (SpawnMin - 50f), hi = (SpawnMax + 10f) * (SpawnMax + 10f);
        foreach (var e in RoadNetwork.Current.Edges)
        {
            if (!Drivable(e)) continue;
            for (int i = 0; i < e.Count; i += 3)
            {
                float d = Vector2.DistanceSquared(e.Points[i], center);
                if (d >= lo && d <= hi) { _candidates.Add(e); break; }
            }
        }
    }

    private void TrySpawn(WorldMap world, Vector2 center, Car player, float minDistance)
    {
        if (_candidates.Count == 0) return;
        var e = _candidates[_rng.Next(_candidates.Count)];
        var dirs = new[] { 1, -1 }.Where(d => Allows(e, d)).ToArray();
        if (dirs.Length == 0) return;
        int dir = dirs[_rng.Next(dirs.Length)];
        float a = MathF.Min(StartS(e, dir), EndS(e, dir)) + 1f, b = MathF.Max(StartS(e, dir), EndS(e, dir)) - 1f;
        if (b <= a) return;
        float s = a + _rng.NextSingle() * (b - a);
        var lanes = e.TravelLanes(dir);
        float lane = lanes[_rng.Next(lanes.Length)];
        Vector2 p = LanePoint(e, dir, lane, s);
        float dist = Vector2.Distance(p, center);
        if (dist < minDistance || dist > SpawnMax) return;
        var p3 = new Vector3(p.X, 0f, p.Y);
        if (world.ZoneAt(p3) == null || !world.IsWalkable(p3, HullRadius)) return; // anche: niente auto addosso a un'altra
        if (Vector2.Distance(p, new Vector2(player.Position.X, player.Position.Z)) < 6f) return;
        foreach (var o in _cars)
            if (Vector2.DistanceSquared(o.Pos, p) < 7f * 7f) return;

        var v = new Vehicle
        {
            Id = _nextId++,
            Model = _models[_rng.Next(_models.Length)],
            Edge = e, Dir = dir, Lane = lane, S = s,
        };
        v.Cruise = Limit(e) * (0.85f + 0.25f * _rng.NextSingle());
        v.Speed = v.Cruise * 0.8f;
        PlanNext(v);
        Pose(v, world, 0f);
        _cars.Add(v);
    }

    /// <summary>Velocità di crociera sul tratto (la statale si fa più veloce delle vie di paese).</summary>
    private static float Limit(RoadEdge e) =>
        e.Kind == RoadKind.Main ? 9f : e.Layout == RoadLayout.Avenue ? 8f : e.Layout == RoadLayout.OneWay ? 5.5f : 6.5f;

    /// <summary>Entrando in un tratto si sceglie già dove andare al prossimo incrocio e la curva per farlo.</summary>
    private void PlanNext(Vehicle v)
    {
        var e = v.Edge;
        var node = v.Dir > 0 ? e.B : e.A;
        float end = EndS(e, v.Dir);
        Vector2 arrive = e.Tangent(end) * v.Dir;

        RoadEdge? pick = null;
        int pickDir = 0;
        float total = 0f;
        // Scelta a caso pesata: dritto più spesso, i vicoli ciechi di rado, mai indietro se c'è altro
        var options = new List<(RoadEdge E, int Dir, float W)>();
        foreach (var o in node.Edges)
        {
            if (o == e) continue;
            int od = o.A == node ? 1 : -1;
            if (!Allows(o, od)) continue;
            Vector2 leave = o.LeaveDir(node);
            float w = 1f + 1.5f * MathF.Max(0f, Vector2.Dot(arrive, leave));
            if (o.Other(node).Degree == 1) w *= 0.15f;
            if (Vector2.Dot(arrive, leave) < -0.8f) w *= 0.2f; // quasi un'inversione
            options.Add((o, od, w));
            total += w;
        }
        if (options.Count > 0)
        {
            float r = _rng.NextSingle() * total;
            foreach (var (o, od, w) in options)
            {
                pick = o;
                pickDir = od;
                if ((r -= w) <= 0f) break;
            }
        }
        else if (Allows(e, -v.Dir))
        {
            // Fine strada: si torna indietro girando nello slargo
            pick = e;
            pickDir = -v.Dir;
        }

        v.Next = pick;
        v.Turn = null;
        if (pick == null) return; // senso unico senza uscita: arrivata in fondo sparisce
        v.NextDir = pickDir;
        var lanes = pick.TravelLanes(pickDir);
        // Sul viale: chi gira a destra prende la corsia esterna, chi gira a sinistra quella interna
        float startS = StartS(pick, pickDir);
        Vector2 leaveDir = pick.Tangent(startS) * pickDir;
        float cross = arrive.X * leaveDir.Y - arrive.Y * leaveDir.X; // > 0: a destra (x est, z sud)
        v.NextLane = lanes.Length == 1 ? lanes[0]
                   : MathF.Abs(cross) < 0.3f ? lanes[_rng.Next(lanes.Length)]
                   : cross > 0f ? lanes.Max() : lanes.Min();

        Vector2 p0 = LanePoint(e, v.Dir, v.Lane, end);
        Vector2 p3 = LanePoint(pick, pickDir, v.NextLane, startS);
        float gap = Vector2.Distance(p0, p3);
        bool uTurn = pick == e;
        float reach = uTurn ? MathF.Max(1.8f, gap * 0.9f) : MathF.Max(0.6f, gap * 0.42f);
        v.Turn = new Curve(p0, arrive, p3, leaveDir, reach);
        float angle = MathF.Acos(Math.Clamp(Vector2.Dot(arrive, leaveDir), -1f, 1f));
        v.TurnSpeed = uTurn ? 2f : Noise.Lerp(MathF.Min(v.Cruise, Limit(pick)) * 0.9f, 3f, Noise.Smooth(0.25f, 1.5f, angle));
    }

    /// <summary>Punto del percorso a <paramref name="d"/> davanti alla posizione attuale (sul tratto, nella curva, nel tratto dopo).</summary>
    private static Vector2 PathPoint(Vehicle v, float d, float shift)
    {
        if (!v.Turning)
        {
            float rem = v.Remaining;
            if (d <= rem || v.Turn == null) return LanePoint(v.Edge, v.Dir, v.Lane + shift, v.S + v.Dir * MathF.Min(d, MathF.Max(rem, 0f)));
            d -= MathF.Max(rem, 0f);
        }
        float done = v.Turning ? v.TurnD : 0f;
        if (done + d <= v.Turn!.Length) return v.Turn.At(done + d);
        d -= v.Turn.Length - done;
        var n = v.Next!;
        float s = Math.Clamp(StartS(n, v.NextDir) + v.NextDir * d, 0f, n.Length);
        return LanePoint(n, v.NextDir, v.NextLane, s);
    }

    /// <summary>
    /// Quanto può avanzare il centro dell'auto prima di toccare qualcosa lungo il percorso (entro <paramref name="look"/>).
    /// Restituisce anche cosa la ferma.
    /// </summary>
    private float FreeDistance(Vehicle v, float look, float shift, out Circle? hit, out bool train)
    {
        hit = null;
        train = false;
        const float step = 0.5f;
        float reach = look + HullOffset + 3f;
        // Solo gli ostacoli abbastanza vicini da contare
        Span<int> near = stackalloc int[_obstacles.Count];
        int count = 0;
        for (int i = 0; i < _obstacles.Count; i++)
        {
            var o = _obstacles[i];
            if (o.Owner == v.Id || (o.Car && v.Ghost > 0f)) continue;
            if (Vector2.DistanceSquared(o.C, v.Pos) < reach * reach) near[count++] = i;
        }
        var trains = Trains.Current;
        for (float d = 0f; d <= look; d += step)
        {
            Vector2 front = PathPoint(v, d + HullOffset, shift); // dove sarà il cerchio del muso
            for (int k = 0; k < count; k++)
            {
                var o = _obstacles[near[k]];
                float r = o.R + HullRadius + 0.15f;
                if (Vector2.DistanceSquared(front, o.C) < r * r)
                {
                    // Un'auto che sta dietro di noi (ci tocca solo di lato o in coda) non ferma
                    if (d == 0f && Vector2.Dot(o.C - v.Pos, v.Heading) < 0.2f) continue;
                    hit = o;
                    return d;
                }
            }
            if (trains != null && d % 1f == 0f && trains.Blocks(front, HullRadius + 0.3f)) { train = true; return d; }
        }
        return look;
    }

    private void Drive(Vehicle v, float dt, WorldMap world)
    {
        v.Ghost = MathF.Max(0f, v.Ghost - dt);

        // Velocità voluta: crociera, poi più piano verso la curva dell'incrocio, poi lo spazio libero davanti
        float want = v.Turning ? v.TurnSpeed : MathF.Min(v.Cruise, Limit(v.Edge) * 1.1f);
        if (!v.Turning && v.Turn != null)
            want = MathF.Min(want, MathF.Sqrt(v.TurnSpeed * v.TurnSpeed + 2f * Decel * MathF.Max(0f, v.Remaining - 0.5f)));
        if (!v.Turning && v.Next == null) want = MathF.Min(want, MathF.Sqrt(2f * Decel * MathF.Max(0f, v.Remaining)));

        // Semaforo all'incrocio in fondo al tratto: col rosso (o col giallo, se si fa in tempo) ci si ferma alla linea
        v.AtLight = false;
        if (!v.Turning && LightAhead(v) is { } light)
        {
            float stop = v.Remaining - StopLine(v) - (HullOffset + HullRadius);
            bool halt = light == TrafficLights.Light.Red
                        || (light == TrafficLights.Light.Yellow && v.Speed * v.Speed / (2f * Decel) <= stop + 0.5f);
            if (halt && stop > -0.3f) // chi ha già superato la linea prosegue
            {
                want = MathF.Min(want, MathF.Sqrt(2f * Decel * MathF.Max(0f, stop)));
                v.AtLight = true;
            }
        }

        float look = Math.Clamp(2f + v.Speed * v.Speed / (2f * Decel) + v.Speed * 0.6f, 4f, 16f);
        float free = FreeDistance(v, look, v.ShiftTarget, out var hit, out bool train);
        want = MathF.Min(want, MathF.Sqrt(2f * Decel * MathF.Max(0f, free - Margin)));
        if (v.Shift != v.ShiftTarget) want = MathF.Min(want, 2.2f); // mentre cambia corsia va piano

        // Bloccata: da un'auto del giocatore o dal personaggio fermi si passa di lato; da un ingorgo tra auto del
        // traffico (succede negli incroci) dopo un po' si passa attraverso
        bool stuck = free < 1.2f && want < 0.3f;
        v.Blocked = stuck ? v.Blocked + dt : 0f;
        // In coda per un motivo valido: fermi al semaforo, davanti al treno, dietro a un'auto che è in coda anche lei
        Vehicle? ahead = stuck && hit is { Car: true } hc ? _cars.Find(o => o.Id == hc.Owner) : null;
        v.Queued = (v.AtLight && v.Speed < 0.5f) || (stuck && train) || (ahead?.Queued ?? false);
        if (stuck && hit is { } h && !train)
        {
            // Solo un ingorgo vero (nessuno davanti sta aspettando il verde o il treno) si sblocca passando attraverso
            if (h.Car && v.Blocked > 6f && !(ahead?.Queued ?? false)) v.Ghost = 2.5f;
            else if (!h.Car && !h.Moving && v.Blocked > 2f && v.ShiftTarget == 0f && CanBypass(v))
            {
                v.ShiftTarget = -2f * v.Edge.LaneHalf;
                v.BypassAt = h.C;
                v.Blocked = 0f;
            }
        }
        // Finito l'aggiramento (l'ostacolo è alle spalle e la propria corsia è libera) si rientra
        if (v.ShiftTarget != 0f)
        {
            bool passed = Vector2.Dot(v.BypassAt - v.Pos, v.Heading) < -2.2f;
            if (v.Turning || v.Remaining < 4f) v.ShiftTarget = 0f;
            else if (passed && FreeDistance(v, 5f, 0f, out _, out _) >= 5f) v.ShiftTarget = 0f;
            else if (v.Blocked > 5f) v.ShiftTarget = 0f; // anche di là non si passa: si torna in corsia ad aspettare
        }
        v.Shift = MoveTowards(v.Shift, v.ShiftTarget, (v.Speed * 0.45f + 0.15f) * dt);

        float rate = want > v.Speed ? Accel : free < v.Speed * 0.6f ? HardDecel : Decel;
        float before = v.Speed;
        v.Speed = MathF.Max(0f, MoveTowards(v.Speed, want, rate * dt));
        v.Want = want;
        v.Brake = (want < before - 0.2f && v.Speed > 0.2f) || v.Speed < 0.05f ? 1f : MathF.Max(0f, v.Brake - dt * 4f);

        Advance(v, v.Speed * dt);
        Pose(v, world, dt);
    }

    private readonly Dictionary<RoadNode, bool> _hasLight = new();

    /// <summary>La luce del semaforo che regola il tratto all'incrocio in fondo (null se lì non c'è semaforo).</summary>
    private TrafficLights.Light? LightAhead(Vehicle v)
    {
        var node = v.Dir > 0 ? v.Edge.B : v.Edge.A;
        if (!_hasLight.TryGetValue(node, out bool has)) _hasLight[node] = has = TrafficLights.Has(node);
        if (!has || v.Remaining > 40f) return null;
        return TrafficLights.State(node, TrafficLights.Group(node, v.Edge), _time);
    }

    /// <summary>
    /// Distanza dalla fine della carreggiata a cui il muso si ferma: appena prima della linea d'arresto, che sta dopo le
    /// strisce pedonali (vedi Zone.Markings: strisce da 0.35 a 1.75, linea da 2.1 a 2.4); sui tratti troppo corti per le
    /// strisce, appena prima dell'incrocio.
    /// </summary>
    private static float StopLine(Vehicle v)
    {
        float z1 = (v.Dir > 0 ? v.Edge.B : v.Edge.A).TrimFor(v.Edge) + 0.35f + 1.4f;
        return z1 + 1f > v.Edge.Length * 0.5f ? 0.2f : 2.5f;
    }

    /// <summary>Si può passare nella corsia a sinistra? Solo sulle strade a doppio senso e sui viali, lontano dagli incroci.</summary>
    private static bool CanBypass(Vehicle v) =>
        !v.Turning && v.Remaining > 8f && v.Edge.Layout is RoadLayout.TwoWay or RoadLayout.Avenue;

    private void Advance(Vehicle v, float dist)
    {
        for (int guard = 0; guard < 6 && dist > 0f; guard++)
        {
            if (!v.Turning)
            {
                float rem = v.Remaining;
                if (dist < rem) { v.S += v.Dir * dist; return; }
                dist -= MathF.Max(rem, 0f);
                v.S = EndS(v.Edge, v.Dir);
                if (v.Next == null || v.Turn == null) { v.Dead = true; return; }
                v.Turning = true;
                v.TurnD = 0f;
                v.Shift = v.ShiftTarget = 0f;
            }
            else
            {
                float rest = v.Turn!.Length - v.TurnD;
                if (dist < rest) { v.TurnD += dist; return; }
                dist -= rest;
                v.Edge = v.Next!;
                v.Dir = v.NextDir;
                v.Lane = v.NextLane;
                v.S = StartS(v.Edge, v.Dir);
                v.Turning = false;
                PlanNext(v);
            }
        }
    }

    private static void Pose(Vehicle v, WorldMap world, float dt)
    {
        Vector2 pos, heading;
        if (v.Turning)
        {
            pos = v.Turn!.At(v.TurnD);
            heading = v.Turn.Tangent(v.TurnD);
        }
        else
        {
            pos = LanePoint(v.Edge, v.Dir, v.Lane + v.Shift, v.S);
            heading = v.Edge.Tangent(v.S) * v.Dir;
            // Cambiando corsia il muso punta un po' verso dove va
            float side = v.ShiftTarget - v.Shift;
            if (MathF.Abs(side) > 0.02f && v.Speed > 0.1f)
                heading = Vector2.Normalize(heading + RightOf(heading) * MathF.Sign(side) * 0.35f);
        }
        float yaw = MathF.Atan2(heading.X, heading.Y);
        if (v.Placed && dt > 0f)
        {
            float yawRate = Noise.WrapAngle(yaw - v.Yaw) / dt;
            float steer = v.Speed > 0.2f ? MathF.Atan(-yawRate * WheelBase / v.Speed) : v.Steer;
            v.Steer = Noise.Lerp(v.Steer, Math.Clamp(steer, -0.6f, 0.6f), Noise.Damp(8f, dt));
        }
        v.Pos = pos;
        v.Heading = heading;
        v.Yaw = yaw;
        v.Spin += v.Speed * dt / (CarModel.WheelRadius * Scale);

        var p3 = new Vector3(pos.X, 0f, pos.Y);
        float ground = world.GroundHeight(p3);
        v.Y = v.Placed ? Noise.Lerp(v.Y, ground, Noise.Damp(25f, dt)) : ground;
        Vector3 f = new(heading.X * 0.6f, 0f, heading.Y * 0.6f);
        float pitch = Math.Clamp(MathF.Atan2(world.GroundHeight(p3 - f) - world.GroundHeight(p3 + f), 1.2f), -0.4f, 0.4f);
        v.Pitch = v.Placed ? Noise.Lerp(v.Pitch, pitch, Noise.Damp(12f, dt)) : pitch;
        v.Placed = true;
    }

    private static float MoveTowards(float v, float target, float maxDelta) =>
        MathF.Abs(target - v) <= maxDelta ? target : v + MathF.Sign(target - v) * maxDelta;

    // ------------------------------------------------------------------ ostacolo per gli altri

    /// <summary>Un cerchio nel punto tocca un'auto del traffico? (Per l'auto del giocatore e il personaggio a piedi.)</summary>
    public bool Blocks(Vector2 p, float r)
    {
        foreach (var v in _cars)
        {
            if (Vector2.DistanceSquared(v.Pos, p) > (r + 2f) * (r + 2f)) continue;
            float rr = (HullRadius + r) * (HullRadius + r);
            Vector2 f = v.Heading * HullOffset;
            if (Vector2.DistanceSquared(p, v.Pos + f) < rr || Vector2.DistanceSquared(p, v.Pos - f) < rr) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ disegno

    /// <summary>Aggiunge le auto visibili. <paramref name="night"/> 0..1: col buio accendono i fari.</summary>
    public void Collect(List<DrawItem> items, float night, Func<Vector2, bool> visible)
    {
        foreach (var v in _cars)
        {
            if (!visible(v.Pos)) continue;
            Matrix4x4 root = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationX(v.Pitch)
                             * Matrix4x4.CreateRotationY(v.Yaw) * Matrix4x4.CreateTranslation(v.Pos.X, v.Y, v.Pos.Y);
            float bounce = MathF.Sin(_time * 38f + v.Id) * 0.005f
                           + MathF.Abs(MathF.Sin(v.Spin * 0.7f)) * 0.01f * MathF.Min(v.Speed / 10f, 1f);
            Matrix4x4 body = Matrix4x4.CreateTranslation(0f, bounce, 0f) * root;
            v.Model.Collect(items, root, body, v.Spin, v.Steer, night > 0.45f, v.Brake, false);
        }
    }

    private int _followed = -1;

    /// <summary>Test: posizione e direzione dell'auto seguita dalla camera (la prima volta, la più vicina al punto).</summary>
    public (Vector3 Pos, float Yaw)? Followed(Vector3 near)
    {
        var v = _cars.FirstOrDefault(c => c.Id == _followed)
                ?? _cars.MinBy(c => Vector2.DistanceSquared(c.Pos, new Vector2(near.X, near.Z)));
        if (v == null) return null;
        _followed = v.Id;
        return (new Vector3(v.Pos.X, v.Y, v.Pos.Y), v.Yaw);
    }

    /// <summary>Riepilogo per i test: quante auto, quante ferme, quante in un incrocio.</summary>
    public string Status()
    {
        int stopped = _cars.Count(v => v.Speed < 0.1f), turning = _cars.Count(v => v.Turning);
        int bypass = _cars.Count(v => v.ShiftTarget != 0f), ghost = _cars.Count(v => v.Ghost > 0f);
        int light = _cars.Count(v => v.AtLight && v.Speed < 0.1f), queued = _cars.Count(v => v.Queued);
        return $"traffico: {_cars.Count} auto, {stopped} ferme ({light} al rosso, {queued} in coda), {turning} negli incroci, "
               + $"{bypass} aggirano, {ghost} sbloccate"
               + (_cars.Find(v => v.Id == _followed) is { } f
                   ? $"; seguita: {f.Speed:F1} u/s{(f.AtLight ? ", ferma al semaforo" : "")}{(f.Queued ? ", in coda" : "")}" : "");
    }

    public void Dispose()
    {
        foreach (var m in _models) m.Dispose();
        if (Current == this) Current = null;
    }
}
