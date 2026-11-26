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
/// Precedenze (non ci sono cartelli: regole che si intuiscono guardando): chi è già nell'incrocio va lasciato uscire;
/// senza semaforo ha la precedenza la strada più importante (statale, poi viale, poi via), tra pari chi viene da
/// destra; chi arriva di fronte e gira a sinistra cede a chi va dritto o gira a destra (anche col verde).
/// Nascono fuori dalla vista (nella nebbia) e spariscono quando ci si allontana: attorno al giocatore ce n'è sempre
/// più o meno lo stesso numero. Il moto è cinematico (la posizione segue la corsia) finché l'auto del giocatore non ci
/// sbatte contro: allora si scambiano quantità di moto secondo le masse (una sportiva vola via, un trattore quasi non si
/// sposta), il veicolo colpito scivola e ruota, si ferma e poi rientra piano in corsia.
/// Carrozzerie: utilitarie, station wagon, sportive; sulle statali di campagna anche trattori, lenti.
/// </summary>
public sealed class Traffic : IDisposable
{
    /// <summary>Il traffico in gioco (per gli ostacoli di <see cref="WorldMap.IsWalkable"/>).</summary>
    public static Traffic? Current { get; private set; }

    // Auto attorno al giocatore: tante nel centro città, poche in periferia e in campagna (vedi Wanted e Crowd)
    private const int TargetCity = 14, TargetCountry = 4;
    private const float SpawnMin = 62f, SpawnMax = 108f, Despawn = 125f;
    private const float Scale = 1.1f;           // come l'auto del giocatore
    private const float WheelBase = 1.1f;
    private const float KnockFriction = 5f;     // unità/s²: rallentamento di un veicolo spinto via
    private const float SpinFriction = 4f;      // rad/s²: rallentamento della rotazione dopo l'urto
    private const float Accel = 2.6f, Decel = 6f, HardDecel = 14f;
    private const float Margin = 0.35f;         // spazio lasciato davanti, oltre al contatto
    // Incastri: dopo JamWait s ferme a vicenda una arretra (al massimo BackTime s a BackSpeed), poi aspetta fino a
    // HoldTime s che l'altra passi; se nessuna può arretrare, dopo GhostAfter s una passa attraverso
    private const int NoHit = int.MinValue;
    private const float JamWait = 1.2f, BackSpeed = 1.4f, BackTime = 2.2f, HoldTime = 3.5f, GhostAfter = 9f;
    // Strada sbarrata dal giocatore: dopo RetreatAfter s si torna indietro e si cambia strada
    private const float RetreatAfter = 4f;
    private const float FollowGap = 2.5f;  // tornando indietro in fila: spazio tra un paraurti e l'altro
    // Cambio di corsia e sorpasso: il muso gira al massimo di LaneMaxYaw rispetto alla corsia, e solo avanzando (al
    // massimo LaneCurv radianti per unità di strada): ci si sposta di lato solo così, mai da fermi
    private const float LaneMaxYaw = 0.4f, LaneCurv = 0.5f;
    /// <summary>Strada che serve per spostarsi di lato di <paramref name="shift"/> (la fine è più dolce, quindi un po' di più).</summary>
    private static float ShiftRoom(float shift) => MathF.Abs(shift) / MathF.Sin(LaneMaxYaw) + 5f;

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

    private static readonly Vector3[] TractorPaints =
    [
        new(0.25f, 0.55f, 0.22f),  // verde
        new(0.78f, 0.16f, 0.12f),  // rosso
        new(0.22f, 0.36f, 0.72f),  // blu
        new(0.92f, 0.5f, 0.12f),   // arancio
    ];

    /// <summary>
    /// Ingombro (due cerchi: distanza dal centro e raggio), massa, elasticità negli urti e quanto corre, per carrozzeria.
    /// Il trattore pesa più di cinque volte un'auto e ha le gomme grandi: chi gli va addosso rimbalza indietro, lui si
    /// sposta appena.
    /// </summary>
    private static (float HullOffset, float HullRadius, float Mass, float Bounce, float Pace) Spec(BodyStyle style) => style switch
    {
        BodyStyle.Wagon => (0.56f, 0.5f, 1.8f, 0.45f, 0.95f),
        BodyStyle.Sport => (0.5f, 0.53f, 1.3f, 0.4f, 1.15f),
        BodyStyle.Tractor => (0.32f, 0.66f, 8f, 0.8f, 0f),
        _ => (0.46f, 0.5f, 1.5f, 0.45f, 1f),
    };

    private readonly Dictionary<BodyStyle, CarModel[]> _models = new();
    private WorldMap? _world;
    private readonly List<Car> _owned = new(); // le auto del giocatore: quella guidata e quelle lasciate parcheggiate
    private Vector2 _focus;
    private Vehicle? _skip; // escluso dagli ostacoli mentre si controlla dove può andare lui stesso
    private readonly List<Vehicle> _cars = new();
    private readonly Random _rng = new(1234);
    private readonly List<RoadEdge> _candidates = new();
    private readonly List<Circle> _obstacles = new();
    private Vector2 _candidatesAround = new(float.MaxValue);
    private float _spawnTimer, _time;
    private int _nextId;
    private bool _warm;

    public int Count => _cars.Count;

    /// <summary>Prove automatiche: misura <see cref="WorstOverlap"/> a ogni fotogramma (costa, nel gioco normale no).</summary>
    public bool TrackOverlap { get; init; }

    /// <summary>Verifica: la sovrapposizione più profonda tra due veicoli vista finora.</summary>
    public float WorstOverlap { get; private set; }
    /// <summary>Prove: il fermo più lungo senza una buona ragione (non al rosso, non in coda) da inizio partita.</summary>
    public float LongestBlock { get; private set; }
    /// <summary>Prove: la velocità di lato più alta (rispetto al muso) e la rotazione più rapida da ferme, senza urti in corso.</summary>
    public float WorstSlide { get; private set; }
    public float WorstSpin { get; private set; }
    private int Backs, Ghosts, Retreats;
    private float _elapsed;       // secondi da inizio partita
    // Strade (tratto e verso) sbarrate dal giocatore: quando lo si è visto l'ultima volta. Chi deve ancora scegliere
    // dove girare le evita, per BarredFor secondi dall'ultima volta che qualcuno ci è rimasto bloccato
    private readonly Dictionary<(RoadEdge, int), float> _barred = new();
    private const float BarredFor = 30f;
    private bool Barred(RoadEdge e, int dir) => _barred.TryGetValue((e, dir), out float t) && _elapsed - t < BarredFor;
    public float MinFollowGap { get; private set; } = float.MaxValue; // prove: la distanza più corta tenuta in fila tornando indietro

    /// <summary>Test: se dato, nascono solo veicoli con questa carrozzeria.</summary>
    public BodyStyle? OnlyStyle { get; init; }
    /// <summary>Prove: si ignorano semafori e precedenze (per provocare incastri negli incroci).</summary>
    public bool Reckless { get; init; }

    /// <summary>Passanti che un'auto aspetta da un po' (dove sono, dove è l'auto, verso dove va): si scansano.</summary>
    public IReadOnlyList<(Vector2 At, Vector2 CarPos, Vector2 Heading)> MakeWay => _makeWay;
    private readonly List<(Vector2 At, Vector2 CarPos, Vector2 Heading)> _makeWay = new();

    /// <summary>Test: scrive in console ogni urto con l'auto del giocatore.</summary>
    public bool LogHits { get; init; }

    public Traffic(GL gl)
    {
        foreach (var style in new[] { BodyStyle.Hatch, BodyStyle.Wagon, BodyStyle.Sport })
            _models[style] = Paints.Select((p, i) => new CarModel(gl, p, style, 11 + i)).ToArray();
        _models[BodyStyle.Tractor] = TractorPaints.Select((p, i) => new CarModel(gl, p, BodyStyle.Tractor, 31 + i)).ToArray();
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
        public float HullOffset, HullRadius, Mass, Bounce;
        public float HalfW, HalfL; // il rettangolo vero (urti tra veicoli)
        // Dopo un urto: velocità e rotazione ricevute, e quanto si è spostato dalla traiettoria (poi ci rientra)
        public Vector2 Knock, Offset;
        public float KnockSpin, YawOffset;
        public bool Knocked => Knock.LengthSquared() > 0.09f || MathF.Abs(KnockSpin) > 0.2f;
        public bool OffTrack => Offset.LengthSquared() > 0.0025f || MathF.Abs(YawOffset) > 0.03f;
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
        public bool TurnFromEdge = true; // la curva comincia in fondo al tratto (Edge); no dopo un'uscita in retro
        public float TurnD;
        // Andatura
        public float Speed, Cruise, Want;
        // Aggiramento di un ostacolo fermo: scostamento in più verso sinistra (negativo) e dove vuole arrivare
        public float Shift, ShiftTarget, Blocked;
        public Vector2 BypassAt;
        public float Ghost; // per qualche istante ignora le altre auto (si sblocca da un ingorgo senza uscita)
        // Incastri tra auto (A ferma B e B ferma A, o un giro di più auto): una fa retromarcia e lascia passare
        public int HitId = NoHit;      // l'auto del traffico che la ferma adesso (NoHit se nessuna)
        public int GiveWayTo;          // a chi sta lasciando passare
        public float Backing, Hold;    // retromarcia in corso; poi attesa che l'altra passi
        public float BackFail;         // ha appena ceduto (o non può arretrare): al prossimo incastro tocca a un'altra
        public bool Reversing;
        public bool Replan;            // arretra fuori dalla svolta verso una strada sbarrata, poi sceglie un'altra uscita
        public RoadEdge? Avoided;      // la scelta già rifatta per evitare una strada sbarrata (non la si rifà di continuo)
        public float LaneYaw;          // cambiando corsia: quanto il muso è girato rispetto alla corsia (+ a destra)
        // Strada sbarrata: si torna indietro fino all'incrocio, si esce in retro girando e si prende un'altra strada
        public bool DeadEnd, Retreat;
        public Curve? BackCurve;
        public float BackD, RetreatFail, RetreatTime;
        public bool RetreatMoved; // prove: ha già cominciato ad arretrare (da lì in poi la distanza in fila conta)
        public string Why = ""; // prove: cosa la ferma
        public bool Touching;          // un passo avanti la farebbe entrare in un altro veicolo (TouchId)
        public int TouchId = NoHit;
        // In coda per una buona ragione (rosso, treno, o dietro a chi lo è): chi le sta dietro aspetta e basta
        public bool Queued, AtLight;
        // Precedenze: l'incrocio della curva, che manovra è, se sta dando la precedenza e da quanto
        public RoadNode? TurnNode;
        public TurnKind Kind;
        public bool Yield;
        public float YieldTime;
        // Posa
        public Vector2 Pos, Heading;
        public float Y, Yaw, Pitch, Spin, Steer, Brake;
        public bool Placed, Dead;

        public float Remaining => (EndS(Edge, Dir) - S) * Dir;
    }

    /// <summary>Manovra all'incrocio, in ordine di precedenza tra chi arriva di fronte.</summary>
    private enum TurnKind { Straight, Right, Left, UTurn }

    private readonly record struct Circle(Vector2 C, float R, int Owner, bool Car, bool Moving);

    // ------------------------------------------------------------------ aggiornamento

    /// <summary>
    /// Fa nascere, guidare e sparire le auto. <paramref name="player"/> = auto del giocatore, <paramref name="walker"/>
    /// = il personaggio a piedi (null se è in auto o in treno), <paramref name="focus"/> = dove sta chi si comanda.
    /// </summary>
    public void Update(float dt, float clock, WorldMap world, IReadOnlyList<Car> owned, Vector3? walker, Vector3 focus,
                       IReadOnlyList<Vector3>? pedestrians = null)
    {
        _world = world;
        _owned.Clear();
        _owned.AddRange(owned);
        var player = owned[0];
        _focus = new Vector2(focus.X, focus.Z);
        _time = clock; // lo stesso orologio dei semafori disegnati
        _elapsed += dt;
        var center = new Vector2(focus.X, focus.Z);

        // Sparisce chi è troppo lontano o è finito fuori dal territorio caricato
        _cars.RemoveAll(v => v.Dead || Vector2.Distance(v.Pos, center) > Despawn
                             || world.ZoneAt(new Vector3(v.Pos.X, 0f, v.Pos.Y)) == null);

        if (Vector2.Distance(center, _candidatesAround) > 20f) RefreshCandidates(center);
        if (!_warm)
        {
            // Alla partenza le strade sono già animate: auto anche vicine, prima che si veda qualunque cosa
            _warm = true;
            int want = Wanted(center);
            for (int k = 0; k < want * 10 && _cars.Count < want; k++) TrySpawn(world, center, player, 14f);
        }
        _spawnTimer -= dt;
        if (_spawnTimer <= 0f && _cars.Count < Wanted(center))
        {
            _spawnTimer = 0.3f;
            TrySpawn(world, center, player, SpawnMin);
        }

        _makeWay.Clear();
        // Ostacoli di questo istante
        _obstacles.Clear();
        foreach (var v in _cars)
            foreach (float k in new[] { 1f, 0f, -1f })
                _obstacles.Add(new Circle(v.Pos + v.Heading * ((v.HalfL - v.HalfW) * k), v.HalfW, v.Id, true, v.Speed > 0.3f));
        foreach (var c in owned)
        {
            Vector2 ph = new(c.Heading.X, c.Heading.Z), pp = new(c.Position.X, c.Position.Z);
            bool moving = c.Velocity.LengthSquared() > 0.3f * 0.3f;
            // Tre cerchi lungo l'auto, larghi quanto lei (per la guida del traffico; gli urti veri usano i rettangoli)
            foreach (float k in new[] { 1f, 0f, -1f })
                _obstacles.Add(new Circle(pp + ph * ((c.HalfLength - c.HalfWidth) * k), c.HalfWidth, -1, false, moving));
        }
        if (walker is { } w) _obstacles.Add(new Circle(new Vector2(w.X, w.Z), 0.35f, -2, false, false));
        if (pedestrians != null)
            foreach (var q in pedestrians) _obstacles.Add(new Circle(new Vector2(q.X, q.Z), 0.3f, -3, false, true));

        foreach (var v in _cars)
        {
            var (pos0, yaw0, placed) = (v.Pos, v.Yaw, v.Placed);
            bool pushed = v.Knock != Vector2.Zero || v.KnockSpin != 0f;
            Drive(v, dt, world);
            if (TrackOverlap && placed && !pushed && dt > 0f && v.Ghost <= 0f) MeasureSlide(v, pos0, yaw0, dt);
        }
        // (niente più "separazione" delle auto sovrapposte: le spingeva di lato. Non si sovrappongono: ogni passo che
        // entrerebbe in un altro veicolo non si fa)
        // Verifica (solo nelle prove): la sovrapposizione più profonda tra veicoli da inizio partita (deve restare ~0)
        if (TrackOverlap)
            foreach (var v in _cars)
        {
            float d = DepthAgainst(BoxOf(v), v, null);
            if (d > WorstOverlap) WorstOverlap = d;
        }
    }

    /// <summary>Quante auto girano attorno al punto: dipende da quanto è centrale (cuore della città = il massimo).</summary>
    private static int Wanted(Vector2 at)
    {
        // Media tra il punto e quattro punti attorno, a metà dell'anello in cui nascono le auto
        float u = Terrain.Urban(at.X, at.Y);
        const float ring = (SpawnMin + SpawnMax) * 0.25f;
        u += Terrain.Urban(at.X + ring, at.Y) + Terrain.Urban(at.X - ring, at.Y)
           + Terrain.Urban(at.X, at.Y + ring) + Terrain.Urban(at.X, at.Y - ring);
        return (int)MathF.Round(Noise.Lerp(TargetCountry, TargetCity, Noise.Smooth(0.3f, 0.85f, u / 5f)));
    }

    /// <summary>Probabilità che nasca un'auto in questo punto: piena nel centro città, bassa in periferia e fuori.</summary>
    private static float Crowd(Vector2 p) => 0.15f + 0.85f * Noise.Smooth(0.45f, 0.9f, Terrain.Urban(p.X, p.Y));

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
        if (_rng.NextSingle() > Crowd(p)) return; // le auto si addensano verso il centro
        var p3 = new Vector3(p.X, 0f, p.Y);
        if (world.ZoneAt(p3) == null || !world.IsWalkable(p3, 0.7f)) return; // anche: niente auto addosso a un'altra
        if (Vector2.Distance(p, new Vector2(player.Position.X, player.Position.Z)) < 6f) return;
        foreach (var o in _cars)
            if (Vector2.DistanceSquared(o.Pos, p) < 7f * 7f) return;

        // Carrozzeria: in campagna, sulle statali, ogni tanto un trattore; altrimenti utilitarie, station wagon, sportive
        float pick = _rng.NextSingle();
        var style = e.Kind == RoadKind.Main && Terrain.Urban(p.X, p.Y) < 0.2f && pick < 0.35f ? BodyStyle.Tractor
                  : pick < 0.5f ? BodyStyle.Hatch : pick < 0.8f ? BodyStyle.Wagon : BodyStyle.Sport;
        if (OnlyStyle is { } only) style = only;
        var models = _models[style];
        var spec = Spec(style);
        var v = new Vehicle
        {
            Id = _nextId++,
            Model = models[_rng.Next(models.Length)],
            HullOffset = spec.HullOffset, HullRadius = spec.HullRadius, Mass = spec.Mass, Bounce = spec.Bounce,
            HalfW = VehicleBox.Size(style).W, HalfL = VehicleBox.Size(style).L,
            Edge = e, Dir = dir, Lane = lane, S = s,
        };
        v.Cruise = style == BodyStyle.Tractor ? 3.2f + 0.8f * _rng.NextSingle()
                                              : Limit(e) * spec.Pace * (0.85f + 0.25f * _rng.NextSingle());
        v.Speed = v.Cruise * 0.8f;
        PlanNext(v);
        Pose(v, world, 0f);
        if (DepthAgainst(BoxOf(v), v, null) > 0f) return; // mai addosso a un'auto parcheggiata
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
            if (Barred(o, od)) w *= 0.001f; // strada sbarrata dal giocatore (c'è già una coda che torna indietro): la si evita
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
        v.TurnNode = node;
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
        v.Kind = uTurn ? TurnKind.UTurn : angle < 0.5f ? TurnKind.Straight : cross > 0f ? TurnKind.Right : TurnKind.Left;
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
        float reach = look + v.HullOffset + 3f;
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
            Vector2 front = PathPoint(v, d + v.HalfL - v.HalfW, shift); // dove sarà il cerchio del muso
            for (int k = 0; k < count; k++)
            {
                var o = _obstacles[near[k]];
                float r = o.R + v.HalfW + 0.1f;
                if (Vector2.DistanceSquared(front, o.C) < r * r)
                {
                    // Un'auto che sta dietro di noi (ci tocca solo di lato o in coda) non ferma
                    if (d == 0f && Vector2.Dot(o.C - v.Pos, v.Heading) < 0.2f) continue;
                    hit = o;
                    return d;
                }
            }
            if (trains != null && d % 1f == 0f && trains.Blocks(front, v.HullRadius + 0.3f)) { train = true; return d; }
        }
        return look;
    }

    private void Drive(Vehicle v, float dt, WorldMap world)
    {
        v.Ghost = MathF.Max(0f, v.Ghost - dt);
        v.BackFail = MathF.Max(0f, v.BackFail - dt);
        v.RetreatFail = MathF.Max(0f, v.RetreatFail - dt);
        if (v.Retreat) { RetreatStep(v, dt, world); return; }
        if (v.Backing > 0f) { Reverse(v, dt, world); return; }
        v.Reversing = false;

        // Velocità voluta: crociera, poi più piano verso la curva dell'incrocio, poi lo spazio libero davanti
        float want = MathF.Min(v.Turning ? v.TurnSpeed : Limit(v.Edge) * 1.15f, v.Cruise);
        if (!v.Turning && v.Turn != null)
            want = MathF.Min(want, MathF.Sqrt(v.TurnSpeed * v.TurnSpeed + 2f * Decel * MathF.Max(0f, v.Remaining - 0.5f)));
        if (!v.Turning && v.Next == null) want = MathF.Min(want, MathF.Sqrt(2f * Decel * MathF.Max(0f, v.Remaining)));

        // Semaforo all'incrocio in fondo al tratto: col rosso (o col giallo, se si fa in tempo) ci si ferma alla linea
        v.AtLight = false;
        if (!v.Turning && !Reckless && LightAhead(v) is { } light)
        {
            float stop = v.Remaining - StopLine(v) - v.HalfL;
            bool halt = light == TrafficLights.Light.Red
                        || (light == TrafficLights.Light.Yellow && v.Speed * v.Speed / (2f * Decel) <= stop + 0.5f);
            if (halt && stop > -0.3f) // chi ha già superato la linea prosegue
            {
                want = MathF.Min(want, MathF.Sqrt(2f * Decel * MathF.Max(0f, stop)));
                v.AtLight = true;
            }
        }

        // Precedenze: se qualcuno ha diritto di passare prima (o è già nell'incrocio sulla nostra traiettoria) ci si ferma
        // alla linea; superata la linea si prosegue
        v.Yield = false;
        if (!v.Turning && v.Turn != null && v.Remaining < 16f && !v.AtLight)
        {
            float stop = v.Remaining - StopLine(v) - v.HalfL;
            if (stop > -0.3f && !Reckless && MustYield(v))
            {
                want = MathF.Min(want, MathF.Sqrt(2f * Decel * MathF.Max(0f, stop)));
                v.Yield = true;
            }
        }
        v.YieldTime = v.Yield && v.Speed < 0.3f ? v.YieldTime + dt : 0f;

        float look = Math.Clamp(2f + v.Speed * v.Speed / (2f * Decel) + v.Speed * 0.6f, 4f, 16f);
        float free = FreeDistance(v, look, v.ShiftTarget, out var hit, out bool train);
        want = MathF.Min(want, MathF.Sqrt(2f * Decel * MathF.Max(0f, free - Margin)));
        if (v.Shift != v.ShiftTarget) want = MathF.Min(want, 2.2f); // mentre cambia corsia va piano
        // Ha appena arretrato per lasciar passare: aspetta che l'altra se ne vada (o che passi un po')
        if (v.Hold > 0f)
        {
            v.Hold -= dt;
            var other = Find(v.GiveWayTo);
            if (other == null || Vector2.Distance(other.Pos, v.Pos) > 6.5f) v.Hold = 0f;
            else want = 0f;
        }

        // Bloccata: da un'auto del giocatore o dal personaggio fermi si passa di lato; da un ingorgo tra auto del
        // traffico (succede negli incroci) dopo un po' si passa attraverso
        bool stuck = (free < 1.2f && want < 0.3f) || v.Touching;
        if (v.Touching && !(free < 1.2f && want < 0.3f))
        {
            // Ferma solo dal contatto: chi la blocca è con chi si toccherebbe
            hit = Find(v.TouchId) is { } t ? new Circle(t.Pos, t.HalfW, t.Id, true, t.Speed > 0.3f) : null;
            train = false;
        }
        v.Why = !stuck ? "" : train ? "treno" : hit is { Car: true } hw ? $"auto {hw.Owner}{(v.Touching ? " a contatto" : "")}"
              : hit is { Owner: -1 } ? "giocatore" : hit is { Owner: -2 } ? "personaggio" : hit is { } ? "passante" : "a contatto";
        v.Blocked = stuck ? v.Blocked + dt : 0f;
        if (!v.Queued && v.Blocked > LongestBlock) LongestBlock = v.Blocked;
        // In coda per un motivo valido: fermi al semaforo, davanti al treno, dietro a un'auto che è in coda anche lei
        Vehicle? ahead = stuck && hit is { Car: true } hc ? Find(hc.Owner) : null;
        v.Queued = ((v.AtLight || v.Yield) && v.Speed < 0.5f) || (stuck && train) || (ahead?.Queued ?? false);
        v.HitId = ahead?.Id ?? NoHit;
        // Strada sbarrata: fermi davanti all'auto del giocatore (o a lui) senza poterla aggirare, o in coda dietro a chi lo
        // è. Dopo un po' si torna indietro fino all'incrocio e si prende un'altra strada.
        bool barred = stuck && !train && (hit is { Car: false, Owner: -1 or -2, Moving: false } || (v.Touching && v.TouchId == NoHit && hit == null));
        if (barred && !v.Turning) _barred[(v.Edge, v.Dir)] = _elapsed; // chi arriva dopo sceglie un'altra strada
        v.DeadEnd = barred || (ahead != null && (ahead.DeadEnd || ahead.Retreat));
        // In coda si comincia dall'ultima: si parte solo se dietro non c'è nessuno in coda che possa arretrare prima
        if (v.DeadEnd && v.Blocked > RetreatAfter && v.RetreatFail <= 0f && !v.Turning && !QueuedBehind(v)) StartRetreat(v);
        // Diretta verso una strada sbarrata: chi deve ancora svoltare cambia uscita; chi è già nella svolta e si è
        // fermato arretra fuori dalla curva e poi ne sceglie un'altra (così la coda non si allunga)
        if (v.Next != null && Barred(v.Next, v.NextDir))
        {
            if (!v.Turning && v.Next != v.Avoided) { PlanNext(v); v.Avoided = v.Next; }
            else if (v.Turning && v.TurnFromEdge && v.Blocked > 1.5f && v.Backing <= 0f && !v.Retreat)
            {
                v.Backing = 2f * BackTime;
                v.Replan = true;
                v.GiveWayTo = NoHit;
                v.Blocked = 0f;
                Backs++;
            }
        }
        // Incastrate a vicenda: una arretra e lascia passare (tocca a lei se è la scelta del giro)
        if (ahead != null && !v.Queued && v.Blocked > JamWait && v.Hold <= 0f && Jam(v) is { } turn)
        {
            if (turn == JamTurn.Back)
            {
                v.Backing = BackTime;
                Backs++;
                v.GiveWayTo = ahead.Id;
                v.Blocked = 0f;
            }
            else { v.Ghost = 2.5f; Ghosts++; } // nessuna può arretrare: passa attraverso, una volta sola
        }
        // Un passante fermo davanti da un po' (in piazza, a metà attraversamento): gli si chiede strada e si scansa
        if (stuck && hit is { Owner: -3 } ped && v.Blocked > 1.5f) _makeWay.Add((ped.C, v.Pos, v.Heading));
        if (stuck && hit is { } h && !train)
        {
            // Solo un ingorgo vero (nessuno davanti sta aspettando il verde o il treno) si sblocca passando attraverso
            // Ingorgo vero (nessuno davanti aspetta il verde o il treno): mai passare attraverso; lontano dagli occhi
            // del giocatore, dopo un po', l'auto se ne va (sparisce nella nebbia e ne nasce un'altra)
            if (h.Car && v.Blocked > 10f && !(ahead?.Queued ?? false) && Vector2.Distance(v.Pos, _focus) > 45f) v.Dead = true;
            else if (!h.Car && !h.Moving && v.Blocked > 2f && v.ShiftTarget == 0f && CanBypass(v))
            {
                v.ShiftTarget = -2f * v.Edge.LaneHalf;
                v.BypassAt = h.C;
                v.Blocked = 0f;
            }
            else if (!h.Car && !h.Moving && v.Blocked > 2f && v.ShiftTarget == 0f && MathF.Abs(v.Shift) < 0.01f)
                ChangeLane(v); // sul viale, anche vicino all'incrocio: si passa nell'altra corsia dello stesso senso
        }
        // Finito l'aggiramento (l'ostacolo è alle spalle e la propria corsia è libera) si rientra
        if (v.ShiftTarget != 0f)
        {
            bool passed = Vector2.Dot(v.BypassAt - v.Pos, v.Heading) < -2.2f;
            if (v.Turning || v.Remaining < ShiftRoom(v.Shift)) v.ShiftTarget = 0f; // rientra in tempo per l'incrocio
            else if (passed && FreeDistance(v, 5f, 0f, out _, out _) >= 5f) v.ShiftTarget = 0f;
            else if (v.Blocked > 5f) v.ShiftTarget = 0f; // anche di là non si passa: si torna in corsia ad aspettare
        }

        // Appena urtato si inchioda (la spinta dell'urto lo fa scivolare); poi si rientra in corsia piano, sterzando
        if (v.Knocked) want = 0f;
        else if (v.OffTrack) want = MathF.Min(want, 1.2f);
        Recover(v, dt);

        float rate = want > v.Speed ? Accel : free < v.Speed * 0.6f || v.Knocked ? HardDecel : Decel;
        float before = v.Speed;
        v.Speed = MathF.Max(0f, MoveTowards(v.Speed, want, rate * dt));
        v.Want = want;
        v.Brake = (want < before - 0.2f && v.Speed > 0.2f) || v.Speed < 0.05f ? 1f : MathF.Max(0f, v.Brake - dt * 4f);

        // Mai dentro un altro veicolo, nel giocatore o in un passante: se il passo ci entrerebbe, ci si ferma a contatto
        float dist = v.Speed * dt;
        if (v.OffTrack && !v.Knocked)
        {
            // Fuori traiettoria dopo un urto: ci si rientra guidando (avanti sterzando, o in retro se si è girati di
            // traverso), mai scivolando di lato
            bool moved = dist <= 0f || Realign(v, dist);
            if (!moved) v.Speed = 0f;
            v.Touching = !moved || (dist <= 0f && want > 0.3f && v.Touching);
            Pose(v, world, dt);
            return;
        }
        bool touching = StepBlocked(v, dist);
        if (touching)
        {
            dist = 0f;
            v.Speed = 0f;
        }
        // A contatto con chi le impedisce di avanzare (anche se davanti la strada sembra libera: di traverso dopo un
        // urto): conta come ferma, per sbloccare l'incastro. Da ferma (velocità azzerata) resta com'era.
        v.Touching = touching || (dist <= 0f && want > 0.3f && v.Touching);
        LaneSteer(v, dist);
        Advance(v, dist);
        Pose(v, world, dt);
    }

    private readonly Dictionary<RoadNode, bool> _hasLight = new();

    private bool HasLight(RoadNode node)
    {
        if (!_hasLight.TryGetValue(node, out bool has)) _hasLight[node] = has = TrafficLights.Has(node);
        return has;
    }

    /// <summary>La luce del semaforo che regola il tratto all'incrocio in fondo (null se lì non c'è semaforo).</summary>
    private TrafficLights.Light? LightAhead(Vehicle v)
    {
        var node = v.Dir > 0 ? v.Edge.B : v.Edge.A;
        if (!HasLight(node) || v.Remaining > 40f) return null;
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

    /// <summary>Importanza della strada per le precedenze senza semaforo.</summary>
    private static int Rank(RoadEdge e) => e.Kind == RoadKind.Main ? 3 : e.Layout == RoadLayout.Avenue ? 2 : 1;

    /// <summary>
    /// <paramref name="o"/> passa prima di <paramref name="v"/> all'incrocio <paramref name="node"/>? La regola è
    /// antisimmetrica (tra due auto una sola ha la precedenza), così non si aspettano a vicenda.
    /// </summary>
    private bool HasPriority(Vehicle o, Vehicle v, RoadNode node)
    {
        bool light = HasLight(node);
        if (!light)
        {
            int ro = Rank(o.Edge), rv = Rank(v.Edge);
            if (ro != rv) return ro > rv;
        }
        // Da dove arrivano: la direzione del loro tratto uscendo dal nodo
        Vector2 vf = v.Edge.LeaveDir(node), of = o.Edge.LeaveDir(node);
        float c = -vf.X * of.Y + vf.Y * of.X; // > 0: o arriva dalla destra di v
        if (light || MathF.Abs(c) < 0.35f)
            return o.Kind != v.Kind ? o.Kind < v.Kind : o.Id < v.Id; // di fronte: chi gira a sinistra cede
        return c > 0f;
    }

    /// <summary>Le due traiettorie nell'incrocio passano tanto vicine da urtarsi? (ognuna dal punto in cui è arrivata)</summary>
    private static bool Conflicts(Curve a, float aFrom, Curve b, float bFrom)
    {
        const float step = 0.5f, near = 1.2f;
        for (float da = aFrom; da <= a.Length + 0.01f; da += step)
        {
            Vector2 pa = a.At(da);
            for (float db = bFrom; db <= b.Length + 0.01f; db += step)
                if (Vector2.DistanceSquared(pa, b.At(db)) < near * near) return true;
        }
        return false;
    }

    /// <summary>
    /// Deve fermarsi alla linea per dare la precedenza? Sì se un'auto già nell'incrocio, o una in arrivo che ha la
    /// precedenza, ha una traiettoria che incrocia la nostra. Se tutti si aspettano a vicenda da un po' (ognuno ha
    /// qualcuno alla sua destra), passa chi ha il numero più basso.
    /// </summary>
    private bool MustYield(Vehicle v)
    {
        var node = v.TurnNode!;
        foreach (var o in _cars)
        {
            if (o == v || o.Turn == null || o.TurnNode != node) continue;
            if (o.Turning)
            {
                if (Conflicts(v.Turn!, 0f, o.Turn, o.TurnD)) return true;
                continue;
            }
            if (o.Edge == v.Edge && o.Dir == v.Dir) continue;      // stessa corsia d'arrivo: basta la distanza
            if (o.AtLight || o.Remaining > 14f) continue;           // fermo al rosso o ancora lontano
            if (o.Speed < 0.3f && o.Remaining > StopLine(o) + 3f) continue; // in coda dietro ad altri
            if (!HasPriority(o, v, node)) continue;
            if (o.Yield && o.YieldTime > 2.5f && v.YieldTime > 2.5f && v.Id < o.Id) continue; // stallo: si sblocca
            if (Conflicts(v.Turn!, 0f, o.Turn, 0f)) return true;
        }
        return false;
    }

    /// <summary>
    /// Prove: lo spostamento di questo fotogramma è da auto? Di lato rispetto al muso (a metà del passo) non ci si deve
    /// muovere, e da ferma non si deve girare. Si tiene il peggio visto.
    /// </summary>
    private void MeasureSlide(Vehicle v, Vector2 pos0, float yaw0, float dt)
    {
        Vector2 step = v.Pos - pos0;
        float mid = yaw0 + 0.5f * Noise.WrapAngle(v.Yaw - yaw0);
        Vector2 right = new(MathF.Cos(mid), -MathF.Sin(mid));
        float slide = MathF.Abs(Vector2.Dot(step, right)) / dt;
        if (slide > WorstSlide) { WorstSlide = slide; SlideWho = $"auto {v.Id} a {v.Pos.X:F0},{v.Pos.Y:F0} ({v.Why}{(v.Retreat ? ", torna indietro" : "")}{(v.Backing > 0f ? ", retro" : "")}{(v.OffTrack ? ", fuori traiettoria" : "")}{(v.Turning ? ", curva" : "")})"; }
        if (step.Length() < 1e-4f)
        {
            float spin = MathF.Abs(Noise.WrapAngle(v.Yaw - yaw0)) / dt;
            if (spin > WorstSpin) { WorstSpin = spin; SpinWho = $"auto {v.Id} a {v.Pos.X:F0},{v.Pos.Y:F0}"; }
        }
    }

    private string SlideWho = "", SpinWho = "";

    /// <summary>Prove: l'auto ferma da più tempo senza una buona ragione e cosa la ferma.</summary>
    private string Worst()
    {
        Vehicle? w = null;
        foreach (var v in _cars) if (!v.Queued && (w == null || v.Blocked > w.Blocked)) w = v;
        if (w == null || w.Blocked <= 0f) return "nessuna";
        return $"auto {w.Id} da {w.Blocked:F1} s ({w.Why}{(w.Yield ? ", dà precedenza" : "")}{(w.Hold > 0f ? ", lascia passare" : "")}"
               + $"{(w.Turning ? ", nell'incrocio" : "")}, {w.Edge.Layout} resta {w.Remaining:F1} shift {w.ShiftTarget:F2}, a {w.Pos.X:F0},{w.Pos.Y:F0})" + Chain(w);
    }

    private string Chain(Vehicle w)
    {
        var sb = new System.Text.StringBuilder();
        var cur = Find(w.HitId);
        for (int i = 0; i < 5 && cur != null && cur != w; i++, cur = Find(cur.HitId))
            sb.Append($" -> auto {cur.Id} [ferma {cur.Blocked:F1} s, {cur.Why}, v {cur.Speed:F1}, vuole {cur.Want:F1}, "
                      + $"coda {cur.Queued}, urtata {cur.Knocked}, fuori traiettoria {cur.OffTrack}, retro {cur.Backing:F1}, "
                      + $"attesa {cur.Hold:F1}, ceduto {cur.BackFail:F1}, curva {cur.Turning}, resta {cur.Remaining:F1}, next {cur.Next != null}]");
        if (cur == w) sb.Append(" -> (giro chiuso)");
        return sb.ToString();
    }

    private Vehicle? Find(int id)
    {
        foreach (var o in _cars) if (o.Id == id && !o.Dead) return o;
        return null;
    }

    private enum JamTurn { Back, Ghost }

    /// <summary>
    /// <paramref name="v"/> è in un incastro (seguendo chi ferma chi si torna a lei: due auto muso contro fianco
    /// nell'incrocio, o un giro di più auto) e tocca a lei sbloccarlo? Arretra una sola auto del giro: chi sta
    /// aggirando un ostacolo (è fuori dalla sua corsia), altrimenti il numero più alto, saltando chi ha appena ceduto o
    /// non riesce ad arretrare. Null se non è un incastro o tocca a un'altra.
    /// </summary>
    private JamTurn? Jam(Vehicle v)
    {
        Vehicle? chosen = null;
        float best = float.MinValue;
        bool anyEligible = false;
        var cur = v;
        for (int i = 0; i < 8; i++)
        {
            if (cur.BackFail <= 0f)
            {
                anyEligible = true;
                float score = (cur.ShiftTarget != 0f ? 1e6f : 0f) + cur.Id;
                if (score > best) { best = score; chosen = cur; }
            }
            var next = Find(cur.HitId);
            // Il giro si interrompe: chi sta davanti si muove, aspetta per una buona ragione, o non è ancora fermo da un po'
            if (next == null || next.Queued || next.Backing > 0f || next.Blocked < JamWait * 0.5f) return null;
            if (next == v)
            {
                if (anyEligible) return chosen == v ? JamTurn.Back : null;
                // Nessuno può arretrare: dopo un po' passa attraverso chi ha il numero più alto
                return v.Blocked > GhostAfter && v.Id >= MaxId(v) ? JamTurn.Ghost : null;
            }
            cur = next;
        }
        return null;
    }

    private int MaxId(Vehicle v)
    {
        int max = v.Id;
        var cur = Find(v.HitId);
        for (int i = 0; i < 8 && cur != null && cur != v; i++, cur = Find(cur.HitId)) max = Math.Max(max, cur.Id);
        return max;
    }

    /// <summary>
    /// Retromarcia per sbloccare un incastro: arretra piano lungo la propria traiettoria (dalla curva dell'incrocio fino
    /// al tratto d'arrivo, mai oltre il suo inizio), senza urtare niente, finché l'altra non riparte o finisce il tempo;
    /// poi aspetta che passi.
    /// </summary>
    private void Reverse(Vehicle v, float dt, WorldMap world)
    {
        v.Backing -= dt;
        v.Reversing = true;
        v.Speed = 0f;
        v.Want = 0f;
        v.Brake = 0f;
        v.Blocked = 0f;
        v.HitId = NoHit;
        v.Queued = v.Yield = v.AtLight = false;
        var other = Find(v.GiveWayTo);
        float d = BackSpeed * dt;
        bool moved = v.Backing > 0f && (other != null || v.Replan) && TryRetreat(v, d);
        if (v.Replan && (!v.Turning || !moved))
        {
            // Uscita all'indietro dalla svolta verso la strada sbarrata: di nuovo in fondo al proprio tratto, si sceglie
            // un'altra uscita (o, se non ci si è riusciti, si riprova tra poco)
            v.Replan = false;
            v.Backing = 0f;
            v.Reversing = false;
            if (!v.Turning) { PlanNext(v); v.Avoided = v.Next; }
            Pose(v, world, dt);
            return;
        }
        // L'altra è ripartita: basta così (ma almeno un attimo di retro, per lasciarle spazio vero)
        bool freed = other != null && other.Blocked <= 0f && other.Speed > 0.3f && v.Backing < BackTime - 0.6f;
        if (!moved || freed)
        {
            // Se non è riuscita a muoversi affatto, al prossimo tentativo prova un'altra del giro
            v.BackFail = v.Backing > BackTime - 0.15f ? 8f : 5f;
            v.Backing = 0f;
            v.Reversing = false;
            v.Hold = HoldTime;
        }
        Pose(v, world, dt);
        if (moved) v.Spin -= d / (CarModel.WheelRadius * Scale);
    }

    private void StartRetreat(Vehicle v)
    {
        v.Retreat = true;
        v.RetreatTime = 0f;
        v.RetreatMoved = false;
        v.BackCurve = null;
        v.Backing = v.Hold = 0f;
        v.ShiftTarget = 0f;
        v.Blocked = 0f;
        Retreats++;
        if (TrackOverlap)
        {
            float gap = GapBehind(v, out var b);
            Console.WriteLine($"  {_elapsed:F1} s: auto {v.Id} comincia a tornare indietro (dietro: {(b == null ? "nessuno" : $"auto {b.Id} a {gap:F1}")})");
        }
    }

    /// <summary>
    /// Strada sbarrata: retromarcia lungo il tratto fino all'incrocio da cui si era entrati, poi uscita all'indietro
    /// girando (il muso si volta verso la strada scelta) e di lì avanti nella strada nuova, come la curva di un incrocio.
    /// Ci si ferma (e si riprova) se dietro c'è qualcosa; se all'incrocio non c'è un'altra strada possibile si rinuncia.
    /// </summary>
    private void RetreatStep(Vehicle v, float dt, WorldMap world)
    {
        v.RetreatTime += dt;
        v.Ghost = MathF.Max(0f, v.Ghost - dt);
        v.Reversing = true;
        v.Speed = 0f;
        v.Want = 0f;
        v.Brake = 0f;
        v.Touching = false;
        v.Queued = v.Yield = v.AtLight = false;
        v.DeadEnd = true; // chi è in coda dietro fa lo stesso (e si sposta per primo)
        v.Why = "torna indietro";
        v.BackFail = MathF.Max(v.BackFail, 1f); // negli incastri arretra l'altra: lei sta già facendo manovra
        float d = BackSpeed * dt;
        bool moved = false;

        bool waiting = false; // ferma per scelta: distanza da chi arretra dietro, o il turno all'incrocio
        if (v.BackCurve == null)
        {
            float fromStart = (v.S - StartS(v.Edge, v.Dir)) * v.Dir;
            if (fromStart > 0.6f)
            {
                // In fila: si arretra solo lasciando FollowGap a chi arretra dietro. Se chi sta dietro è fermo per altro,
                // ci si avvicina fino a mezzo metro e poi è un blocco vero (conta come bloccata: vedi sotto)
                float gap = GapBehind(v, out var behind);
                if ((behind?.Retreat ?? false) && v.RetreatMoved) MinFollowGap = MathF.Min(MinFollowGap, gap);
                if (gap < FollowGap && (behind?.Retreat ?? false)) waiting = true;
                else
                {
                    float room = gap < FollowGap ? gap - 0.5f : float.MaxValue;
                    if (room > 0.02f) moved = TryRetreat(v, MathF.Min(MathF.Min(d, room), fromStart - 0.55f));
                    if (moved) v.RetreatMoved = true;
                }
            }
            else if (BackOutBusy(v)) waiting = true; // all'incrocio una alla volta
            else if (!PlanBackOut(v)) GiveUpRetreat(v); // all'incrocio non c'è dove andare: si aspetta e basta
        }
        else
        {
            float to = MathF.Min(v.BackD + d, v.BackCurve.Length);
            Vector2 pos = v.BackCurve.At(to), h = -v.BackCurve.Tangent(to);
            float yaw = Noise.WrapAngle(MathF.Atan2(h.X, h.Y) + v.YawOffset);
            // Ferma da troppo nell'incrocio (chi le sta dietro non si sposta): ultima risorsa, passa attraverso le auto
            // del traffico (mai attraverso il giocatore o i passanti)
            if (v.Blocked > GhostAfter) { v.Ghost = 4f; Ghosts++; v.Blocked = 0f; }
            bool through = v.Ghost > 0f;
            var box = BoxOf(v, pos + v.Offset, yaw);
            bool free = through ? DepthAgainst(box, v, null, traffic: false) <= 0.002f : Free(v, pos + v.Offset, yaw);
            if (free && !PedestrianIn(box))
            {
                moved = true;
                v.BackD = to;
            }
            if (v.BackD >= v.BackCurve.Length - 1e-3f)
            {
                // Girata: avanti nella strada nuova (la curva è già pronta in v.Turn)
                v.BackCurve = null;
                v.Retreat = false;
                v.Reversing = false;
                v.DeadEnd = false;
                v.Turning = true;
                v.TurnFromEdge = false; // la curva parte dall'incrocio, non dal fondo del tratto
                v.TurnD = 0f;
            }
        }
        // Ferma in retro (non per scelta: per qualcosa dietro): conta come bloccata da lei, così un incastro si scioglie
        // (arretra l'altra); bloccata così troppo a lungo, rinuncia e aspetta davanti
        if (moved || waiting || !v.Retreat) { v.Blocked = 0f; v.HitId = NoHit; }
        else
        {
            v.Blocked += dt;
            v.HitId = BlockerOf(BoxOf(v, v.Pos - v.Heading * 0.6f, v.Yaw), v);
            if (v.BackCurve == null && v.Blocked > 20f) GiveUpRetreat(v);
        }
        Pose(v, world, dt);
        if (moved) v.Spin -= d / (CarModel.WheelRadius * Scale);
    }

    /// <summary>
    /// Dietro <paramref name="v"/> c'è un'auto in coda per colpa sua (ferma contro di lei) che può tornare indietro
    /// prima di lei? Allora <paramref name="v"/> aspetta: in coda si comincia dall'ultima.
    /// </summary>
    private bool QueuedBehind(Vehicle v)
    {
        foreach (var o in _cars)
            if (o != v && !o.Dead && !o.Retreat && o.HitId == v.Id && o.DeadEnd && !o.Turning && o.RetreatFail <= 0f)
                return true;
        return false;
    }

    /// <summary>
    /// Spazio libero dietro <paramref name="v"/> (dal suo paraurti posteriore a quello dell'auto del traffico più vicina
    /// dietro di lei, nella sua fila), e chi è.
    /// </summary>
    private float GapBehind(Vehicle v, out Vehicle? who)
    {
        who = null;
        float best = float.MaxValue;
        Vector2 back = -v.Heading, side = RightOf(v.Heading);
        foreach (var o in _cars)
        {
            if (o == v || o.Dead) continue;
            Vector2 rel = o.Pos - v.Pos;
            float along = Vector2.Dot(rel, back);
            if (along <= 0f) continue;
            if (MathF.Abs(Vector2.Dot(rel, side)) > v.HalfW + o.HalfW + 0.3f) continue; // in un'altra corsia
            // Quanto sporge l'altra verso di noi, com'è girata
            float reach = MathF.Abs(Vector2.Dot(o.Heading, back)) * o.HalfL + MathF.Abs(Vector2.Dot(RightOf(o.Heading), back)) * o.HalfW;
            float gap = along - v.HalfL - reach;
            if (gap < best) { best = gap; who = o; }
        }
        return best;
    }

    /// <summary>All'incrocio da cui si esce in retro c'è già un'altra auto che sta uscendo (o ripartendo)? Una alla volta.</summary>
    private bool BackOutBusy(Vehicle v)
    {
        var node = v.Dir > 0 ? v.Edge.A : v.Edge.B;
        foreach (var o in _cars)
        {
            if (o == v || o.Dead || o.TurnNode != node) continue;
            if (o.Retreat && o.BackCurve != null) return true;
            if (o.Turning && !o.TurnFromEdge) return true;
        }
        return false;
    }

    private void GiveUpRetreat(Vehicle v)
    {
        v.Retreat = false;
        v.Reversing = false;
        v.BackCurve = null;
        v.RetreatFail = 10f;
    }

    /// <summary>L'auto del traffico in cui il rettangolo entra di più (NoHit se nessuna).</summary>
    private int BlockerOf(in VehicleBox box, Vehicle self)
    {
        int id = NoHit;
        float most = 0.002f;
        foreach (var o in _cars)
            if (o != self && !o.Dead && VehicleBox.Overlap(box, BoxOf(o), out _, out float d) && d > most) { most = d; id = o.Id; }
        return id;
    }

    private bool PedestrianIn(in VehicleBox box)
    {
        foreach (var o in _obstacles)
            if (o.Owner < -1 && box.CircleDepth(o.C, o.R, out _) > 0.002f) return true;
        return false;
    }

    /// <summary>
    /// All'inizio del tratto, davanti all'incrocio da cui si era entrati: sceglie un'altra strada che parte da lì (di
    /// lato, non quella dritta di fronte né questa) e prepara l'uscita in retro (v.BackCurve: la coda va dalla parte
    /// opposta alla strada scelta, così il muso le si volta contro) e la curva in avanti per entrarci (v.Turn).
    /// Tutto dev'essere su strada. False se non c'è una strada possibile.
    /// </summary>
    private bool PlanBackOut(Vehicle v)
    {
        var e = v.Edge;
        var node = v.Dir > 0 ? e.A : e.B;
        Vector2 d;                                            // muso, verso il tratto (lontano dall'incrocio)
        // Dall'auto com'è davvero (anche spostata o girata dopo un urto, o a metà di un cambio di corsia): niente salti
        Vector2 p0 = v.Pos;
        d = v.Heading;
        var options = new List<(RoadEdge E, int Dir, Vector2 L)>();
        foreach (var o in node.Edges)
        {
            if (o == e) continue;
            int od = o.A == node ? 1 : -1;
            if (!Allows(o, od)) continue;
            Vector2 l = o.LeaveDir(node);
            if (Vector2.Dot(l, d) < -0.5f) continue;         // di fronte: bisognerebbe girarsi del tutto
            options.Add((o, od, l));
        }
        // A caso, ma la prima che si riesce davvero a fare
        for (int i = options.Count - 1; i > 0; i--) { int j = _rng.Next(i + 1); (options[i], options[j]) = (options[j], options[i]); }
        foreach (var (o, od, l) in options)
        {
            var lanes = o.TravelLanes(od);
            float lane = lanes.Length == 1 ? lanes[0] : lanes.Max(); // corsia esterna
            float startS = StartS(o, od);
            Vector2 p3 = LanePoint(o, od, lane, startS), leave = o.Tangent(startS) * od;
            foreach (float r in new[] { 1.6f, 0.9f, 0.3f })
            {
                // Dove finisce la retro: dalla parte opposta alla strada scelta, col muso già verso di lei
                Vector2 q = node.Pos - l * r;
                var back = new Curve(p0, -d, q, -l, MathF.Max(0.8f, Vector2.Distance(p0, q) * 0.5f));
                float gap = Vector2.Distance(q, p3);
                var forward = new Curve(q, l, p3, leave, MathF.Max(0.6f, gap * 0.42f));
                if (!OnRoad(v, back, true) || !OnRoad(v, forward, false)) continue;
                v.BackCurve = back;
                if (TrackOverlap) Console.WriteLine($"  {_elapsed:F1} s: auto {v.Id} esce in retro all'incrocio verso {o.Kind}");
                // La curva parte dalla posa vera: scostamenti e cambi di corsia sono ora parte di lei
                v.Offset = Vector2.Zero;
                v.YawOffset = v.LaneYaw = 0f;
                v.Shift = v.ShiftTarget = 0f;
                v.BackD = 0f;
                v.Next = o;
                v.NextDir = od;
                v.NextLane = lane;
                v.Turn = forward;
                v.TurnNode = node;
                float angle = MathF.Acos(Math.Clamp(Vector2.Dot(l, leave), -1f, 1f));
                v.Kind = TurnKind.Straight;
                v.TurnSpeed = Noise.Lerp(MathF.Min(v.Cruise, Limit(o)) * 0.9f, 3f, Noise.Smooth(0.25f, 1.5f, angle + 0.5f));
                return true;
            }
        }
        return false;
    }

    /// <summary>L'auto resta sulla strada (o nello slargo) per tutta la curva? (all'indietro: col muso al contrario)</summary>
    private bool OnRoad(Vehicle v, Curve c, bool reverse)
    {
        if (_world == null) return true;
        for (float s = 0f; s <= c.Length + 0.01f; s += 0.4f)
        {
            Vector2 p = c.At(MathF.Min(s, c.Length)), h = c.Tangent(MathF.Min(s, c.Length)) * (reverse ? -1f : 1f);
            Vector2 f = h * v.HullOffset;
            if (!_world.IsRoad(new Vector3(p.X + f.X, 0f, p.Y + f.Y)) || !_world.IsRoad(new Vector3(p.X - f.X, 0f, p.Y - f.Y)))
                return false;
        }
        return true;
    }

    /// <summary>Arretra di <paramref name="d"/> lungo la traiettoria, se lì c'è posto (altrimenti non si muove).</summary>
    private bool TryRetreat(Vehicle v, float d)
    {
        if (v.OffTrack) return Realign(v, d, reverse: true); // fuori traiettoria: dritta all'indietro, dove punta
        var (s, turnD, turning, shift) = (v.S, v.TurnD, v.Turning, v.Shift);
        if (v.Turning)
        {
            v.TurnD -= d;
            if (v.TurnD < 0f && !v.TurnFromEdge)
            {
                // La curva partiva dall'incrocio (dopo un'uscita in retro): più indietro di così non si va
                v.TurnD = turnD;
                return false;
            }
            if (v.TurnD < 0f)
            {
                // Uscita all'indietro dalla curva: si torna sul tratto d'arrivo
                float rest = -v.TurnD;
                v.TurnD = 0f;
                v.Turning = false;
                v.S = EndS(v.Edge, v.Dir) - v.Dir * rest;
            }
        }
        else
        {
            v.S -= v.Dir * d;
            v.Shift -= MathF.Sin(v.LaneYaw) * d; // a metà di un cambio di corsia si arretra dove punta il muso
        }

        Vector2 pos, heading;
        if (v.Turning) { pos = v.Turn!.At(v.TurnD); heading = v.Turn.Tangent(v.TurnD); }
        else
        {
            pos = LanePoint(v.Edge, v.Dir, v.Lane + v.Shift, v.S);
            heading = v.Edge.Tangent(v.S) * v.Dir;
            heading = heading * MathF.Cos(v.LaneYaw) + RightOf(heading) * MathF.Sin(v.LaneYaw);
        }
        pos += v.Offset;
        float yaw = Noise.WrapAngle(MathF.Atan2(heading.X, heading.Y) + v.YawOffset);
        bool ok = (v.Turning || (v.S - StartS(v.Edge, v.Dir)) * v.Dir > 0.5f) && Free(v, pos, yaw);
        if (ok)
        {
            // Né sul personaggio né sui passanti
            var box = BoxOf(v, pos, yaw);
            foreach (var o in _obstacles)
                if (o.Owner < -1 && box.CircleDepth(o.C, o.R, out _) > 0.002f) { ok = false; break; }
        }
        if (!ok) (v.S, v.TurnD, v.Turning, v.Shift) = (s, turnD, turning, shift);
        return ok;
    }

    /// <summary>
    /// Sul viale, bloccata da un ostacolo fermo: passa nell'altra corsia dello stesso senso, se lì davanti è libero
    /// (scivolandoci di lato: la posizione non salta) e ripensa dove girare all'incrocio. False se non si può.
    /// </summary>
    private bool ChangeLane(Vehicle v)
    {
        if (v.Turning || v.Edge.Layout != RoadLayout.Avenue) return false;
        var lanes = v.Edge.TravelLanes(v.Dir);
        float? other = null;
        foreach (float l in lanes) if (MathF.Abs(l - v.Lane) > 0.1f) { other = l; break; }
        if (other is not { } lane) return false;
        if (v.Remaining < ShiftRoom(lane - v.Lane - v.Shift)) return false; // non c'è strada per spostarsi prima dell'incrocio
        var (oldLane, oldShift) = (v.Lane, v.Shift);
        v.Lane = lane;
        v.Shift = oldLane + oldShift - lane; // stessa posizione di prima: poi Shift torna a zero, verso la corsia nuova
        var (next, nextDir, nextLane, turn, node, kind, turnSpeed) = (v.Next, v.NextDir, v.NextLane, v.Turn, v.TurnNode, v.Kind, v.TurnSpeed);
        PlanNext(v); // la curva all'incrocio ora parte dalla corsia nuova
        if (FreeDistance(v, 4f, 0f, out _, out _) < 2f)
        {
            (v.Lane, v.Shift) = (oldLane, oldShift);
            (v.Next, v.NextDir, v.NextLane, v.Turn, v.TurnNode, v.Kind, v.TurnSpeed) = (next, nextDir, nextLane, turn, node, kind, turnSpeed);
            return false;
        }
        v.Blocked = 0f;
        return true;
    }

    /// <summary>Si può passare nella corsia a sinistra? Solo sulle strade a doppio senso e sui viali, lontano dagli incroci.</summary>
    private static bool CanBypass(Vehicle v) => // c'è strada per spostarsi di là e tornare prima dell'incrocio
        !v.Turning && v.Remaining > 2f * ShiftRoom(2f * v.Edge.LaneHalf) && v.Edge.Layout is RoadLayout.TwoWay or RoadLayout.Avenue;

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
                v.TurnFromEdge = true;
                v.TurnD = 0f;
                v.Shift = v.ShiftTarget = v.LaneYaw = 0f;
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

    /// <summary>
    /// Dopo un urto: il veicolo scivola con la velocità ricevuta e ruota, l'attrito lo ferma (se va contro un muro si
    /// ferma lì); poi l'autista lo riporta piano sulla sua traiettoria.
    /// </summary>
    private void Recover(Vehicle v, float dt)
    {
        // La parte dello spostamento lungo la marcia diventa strada fatta (o persa): chi è stato spinto avanti resta
        // avanti, senza tornare indietro in retromarcia; resta da recuperare solo lo scostamento di lato
        if (v.Offset != Vector2.Zero) AbsorbAlong(v);
        if (v.Knock == Vector2.Zero && v.KnockSpin == 0f && !v.OffTrack) return;
        float k = v.Knock.Length();
        if (k > 0f)
        {
            Vector2 step = v.Knock * dt;
            if (Free(v, v.Pos + step, v.Yaw)) v.Offset += step;
            else v.Knock = Vector2.Zero;
            v.Knock = k > KnockFriction * dt ? v.Knock * ((k - KnockFriction * dt) / k) : Vector2.Zero;
        }
        if (v.KnockSpin != 0f)
        {
            float turn = v.KnockSpin * dt;
            if (Free(v, v.Pos, v.Yaw + turn)) v.YawOffset = Math.Clamp(v.YawOffset + turn, -MathF.PI, MathF.PI);
            else v.KnockSpin = 0f;
            v.KnockSpin = MoveTowards(v.KnockSpin, 0f, SpinFriction * dt);
        }
        // Il rientro in traiettoria non è qui: lo fa Realign, guidando (mai scivolando di lato)
    }

    /// <summary>
    /// Cambio di corsia avanzando di <paramref name="dist"/>: il muso gira verso la corsia voluta (al massimo LaneMaxYaw,
    /// e di LaneCurv per unità di strada) e l'auto si sposta di lato solo per questo, poi si raddrizza arrivando.
    /// </summary>
    private static void LaneSteer(Vehicle v, float dist)
    {
        if (dist <= 0f) return;
        float side = v.ShiftTarget - v.Shift;
        if (MathF.Abs(side) < 0.005f && MathF.Abs(v.LaneYaw) < 0.01f)
        {
            v.Shift = v.ShiftTarget;
            v.LaneYaw = 0f;
            return;
        }
        float target = Math.Clamp(side * 0.9f, -LaneMaxYaw, LaneMaxYaw);
        v.LaneYaw = MoveTowards(v.LaneYaw, target, LaneCurv * dist);
        v.Shift += MathF.Sin(v.LaneYaw) * dist;
    }

    /// <summary>Direzione della traiettoria nel punto in cui è l'auto (sul tratto o nella curva dell'incrocio).</summary>
    private static Vector2 PathHeading(Vehicle v) => v.Turning ? v.Turn!.Tangent(v.TurnD) : v.Edge.Tangent(v.S) * v.Dir;

    /// <summary>
    /// Rientro in traiettoria dopo un urto, guidando: avanza di <paramref name="dist"/> nella direzione del muso, che
    /// sterza verso la traiettoria (raggio minimo come un'auto); se è girata troppo di traverso prima fa retro
    /// raddrizzandosi. Con <paramref name="reverse"/> arretra dritta (per lasciar passare). False se lì non c'è posto.
    /// </summary>
    private bool Realign(Vehicle v, float dist, bool reverse = false)
    {
        const float curv = 0.6f; // rad per unità di strada: circa il raggio minimo di sterzata
        if (v.LaneYaw != 0f)
        {
            // Urtata a metà di un cambio di corsia: il muso girato diventa parte della rotazione da recuperare
            v.YawOffset += v.LaneYaw;
            v.LaneYaw = 0f;
            v.ShiftTarget = v.Shift;
        }
        Vector2 path = PathHeading(v);
        float pathYaw = MathF.Atan2(path.X, path.Y);
        float lat = Vector2.Dot(v.Offset, RightOf(path));
        bool back = reverse || MathF.Abs(v.YawOffset) > 1.2f;
        float target = reverse ? v.YawOffset : back ? 0f : Math.Clamp(lat * 0.8f, -0.5f, 0.5f);
        float newOff = MoveTowards(v.YawOffset, target, curv * dist);
        float mid = pathYaw + 0.5f * (v.YawOffset + newOff);
        Vector2 step = new Vector2(MathF.Sin(mid), MathF.Cos(mid)) * (back ? -dist : dist);
        Vector2 pos = v.Pos + step;
        float yaw = pathYaw + newOff;
        if (!Free(v, pos, yaw) || PedestrianIn(BoxOf(v, pos, yaw))) return false;
        v.YawOffset = newOff;
        // Lungo la traiettoria si avanza su di lei; il resto (di lato) resta nello scostamento
        float along = Vector2.Dot(step, path);
        v.Offset += step - path * along;
        if (along > 0f) Advance(v, along);
        else { v.Offset += path * along; AbsorbAlong(v); }
        if (v.Offset.LengthSquared() < 0.02f * 0.02f && MathF.Abs(v.YawOffset) < 0.02f)
        {
            v.Offset = Vector2.Zero;
            v.YawOffset = 0f;
        }
        return true;
    }

    /// <summary>Sposta nella traiettoria (S sul tratto, o la distanza nella curva) la parte dello scostamento lungo la marcia.</summary>
    private static void AbsorbAlong(Vehicle v)
    {
        if (v.Turning)
        {
            Vector2 t = v.Turn!.Tangent(v.TurnD);
            float along = Vector2.Dot(v.Offset, t);
            float to = Math.Clamp(v.TurnD + along, 0f, v.Turn.Length);
            v.Offset -= t * (to - v.TurnD);
            v.TurnD = to;
        }
        else
        {
            Vector2 t = v.Edge.Tangent(v.S) * v.Dir;
            float along = Vector2.Dot(v.Offset, t);
            float lo = MathF.Min(StartS(v.Edge, v.Dir), EndS(v.Edge, v.Dir)), hi = MathF.Max(StartS(v.Edge, v.Dir), EndS(v.Edge, v.Dir));
            float to = Math.Clamp(v.S + v.Dir * along, lo, hi);
            v.Offset -= t * ((to - v.S) * v.Dir);
            v.S = to;
        }
    }

    /// <summary>Il rettangolo del veicolo in una posa.</summary>
    private static VehicleBox BoxOf(Vehicle v, Vector2 pos, float yaw) =>
        new(pos, new Vector2(MathF.Sin(yaw), MathF.Cos(yaw)), v.HalfW, v.HalfL);

    private static VehicleBox BoxOf(Vehicle v) => BoxOf(v, v.Pos, v.Yaw);

    /// <summary>
    /// Quanto il rettangolo entra negli altri veicoli (traffico e auto del giocatore), esclusi quelli indicati: la
    /// sovrapposizione più profonda, 0 se non ne tocca nessuno.
    /// </summary>
    private float DepthAgainst(in VehicleBox box, Vehicle? skipV, Car? skipC, bool traffic = true)
    {
        float worst = 0f;
        foreach (var o in _cars)
            if (traffic && o != skipV && !o.Dead && VehicleBox.Overlap(box, BoxOf(o), out _, out float d)) worst = MathF.Max(worst, d);
        foreach (var c in _owned)
            if (c != skipC && VehicleBox.Overlap(box, c.Box, out _, out float d)) worst = MathF.Max(worst, d);
        return worst;
    }

    /// <summary>Il rettangolo dato tocca un veicolo (escluso <paramref name="except"/>)? Per l'auto del giocatore.</summary>
    public bool Overlaps(VehicleBox box, Car? except) => DepthAgainst(box, null, except) > 0.002f;

    /// <summary>Quanto il rettangolo entra negli altri veicoli (per liberarsi senza entrarci di più).</summary>
    public float Depth(VehicleBox box, Car? except) => DepthAgainst(box, null, except);

    /// <summary>
    /// Il veicolo ci sta in quel punto e con quella direzione? Terreno libero e nessun altro veicolo; se adesso è già
    /// sovrapposto a qualcuno (dopo un urto) può muoversi solo allontanandosene.
    /// </summary>
    private bool Free(Vehicle v, Vector2 pos, float yaw)
    {
        if (_world == null) return true;
        Vector2 f = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw)) * v.HullOffset;
        _skip = v;
        bool ground = _world.IsWalkable(new Vector3(pos.X + f.X, 0f, pos.Y + f.Y), v.HullRadius * 0.9f)
                      && _world.IsWalkable(new Vector3(pos.X - f.X, 0f, pos.Y - f.Y), v.HullRadius * 0.9f);
        _skip = null;
        if (!ground) return false;
        float now = DepthAgainst(BoxOf(v), v, null), next = DepthAgainst(BoxOf(v, pos, yaw), v, null);
        return next <= 0.002f || next < now - 1e-4f;
    }

    /// <summary>
    /// Il passo che il veicolo sta per fare lungo la sua traiettoria lo farebbe entrare in un altro veicolo, nel
    /// giocatore, in un passante? Allora non lo fa (si ferma a contatto, mai dentro).
    /// </summary>
    private bool StepBlocked(Vehicle v, float dist)
    {
        if (dist <= 0f) return false;
        // Dove sarà davvero: anche lo spostamento di lato e il muso girato di un cambio di corsia in corso
        float shift = v.Turning ? v.Shift : v.Shift + MathF.Sin(v.LaneYaw) * dist, laneYaw = v.Turning ? 0f : v.LaneYaw;
        Vector2 a = PathPoint(v, dist - 0.3f, shift), b = PathPoint(v, dist + 0.3f, shift);
        Vector2 heading = b - a;
        heading = heading.LengthSquared() > 1e-6f ? Vector2.Normalize(heading) : v.Heading;
        float yaw = Noise.WrapAngle(MathF.Atan2(heading.X, heading.Y) - laneYaw + v.YawOffset); // muso a destra = yaw minore
        Vector2 pos = PathPoint(v, dist, shift) + v.Offset;
        var now = BoxOf(v);
        var next = BoxOf(v, pos, yaw);
        // Chi si sblocca passando attraverso (incastro senza uscita) ignora le altre auto del traffico, non il giocatore
        float before = DepthAgainst(now, v, null, v.Ghost <= 0f), after = DepthAgainst(next, v, null, v.Ghost <= 0f);
        if (after > 0.002f && after >= before - 1e-4f)
        {
            // Contro chi si urterebbe: l'auto del traffico più dentro (NoHit se è un'auto del giocatore)
            v.TouchId = v.Ghost <= 0f ? BlockerOf(next, v) : NoHit;
            return true;
        }
        // Passanti e personaggio: cerchi
        foreach (var o in _obstacles)
        {
            if (o.Owner >= -1) continue; // auto (traffico e giocatore) già contate sopra coi rettangoli
            float d1 = next.CircleDepth(o.C, o.R, out _);
            if (d1 > 0.002f && d1 >= now.CircleDepth(o.C, o.R, out _))
            {
                v.TouchId = NoHit;
                if (o.Owner == -3) _makeWay.Add((o.C, v.Pos, v.Heading)); // passante a contatto: si scansa
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Separa i veicoli rimasti sovrapposti (non dovrebbe succedere, ma un urto contro un muro può incastrarli): ognuno
    /// si sposta lungo la normale di quanto serve, se lì c'è posto.
    /// </summary>
    private void Separate()
    {
        foreach (var v in _cars)
        {
            var box = BoxOf(v);
            foreach (var o in _cars)
            {
                if (o == v || o.Id < v.Id || v.Ghost > 0f || o.Ghost > 0f || !VehicleBox.Overlap(box, BoxOf(o), out var n, out float d)) continue;
                Nudge(o, n * (d * 0.5f + 0.005f));
                Nudge(v, -n * (d * 0.5f + 0.005f));
                box = BoxOf(v);
            }
            foreach (var c in _owned)
                if (VehicleBox.Overlap(c.Box, box, out var n, out float d)) Nudge(v, n * (d + 0.005f));
        }
    }

    private void Nudge(Vehicle v, Vector2 by)
    {
        if (!Free(v, v.Pos + by, v.Yaw)) return;
        v.Offset += by;
        v.Pos += by;
    }

    /// <summary>
    /// L'auto guidata dal giocatore (<paramref name="self"/>, nella posa <paramref name="box"/> in cui sta per andare,
    /// con la sua velocità e massa) urta gli altri veicoli: traffico e auto parcheggiate. Si scambiano quantità di moto
    /// lungo la normale d'urto tra i due rettangoli; il veicolo colpito riceve velocità e rotazione secondo la sua massa
    /// e viene spinto fuori dall'ingombro; <paramref name="velocity"/> torna cambiata (anche all'indietro, contro un
    /// trattore). Restituisce la velocità d'impatto più forte (0 se nessun urto).
    /// </summary>
    public float PushBox(Car self, VehicleBox box, ref Vector2 velocity, float mass, WorldMap world, out Vector3 contact)
    {
        contact = default;
        float worst = 0f;
        foreach (var v in _cars)
        {
            if (!VehicleBox.Overlap(box, BoxOf(v), out var n, out float depth)) continue;
            Vector2 vel = v.Heading * v.Speed + v.Knock;
            float vn = Vector2.Dot(velocity - vel, n);
            Vector2 at = PointOn(BoxOf(v), -n);
            if (vn > 0f)
            {
                float j = (1f + v.Bounce) * vn / (1f / mass + 1f / v.Mass);
                Vector2 impulse = n * (j / v.Mass);
                // La parte all'indietro rispetto alla marcia la frena; il resto la spinge via (di lato, o in avanti
                // se tamponata)
                float along = Vector2.Dot(impulse, v.Heading);
                if (along < 0f)
                {
                    float slow = MathF.Min(-along, v.Speed);
                    v.Speed -= slow;
                    impulse += v.Heading * slow;
                }
                v.Knock += impulse;
                // Colpita lontano dal centro ruota: momento dell'impulso sul momento d'inerzia
                Vector2 arm = at - v.Pos;
                float inertia = v.Mass * (v.HalfW * v.HalfW + v.HalfL * v.HalfL) / 3f;
                float torque = arm.X * (n.Y * j) - arm.Y * (n.X * j);
                v.KnockSpin = Math.Clamp(v.KnockSpin - torque / inertia, -6f, 6f);
                velocity -= n * (j / mass);
                if (LogHits && vn > 0.5f)
                    Console.WriteLine($"Urto: {v.Model.Style} (massa {v.Mass:F1}) a {vn:F1} u/s: spinta {v.Knock.Length():F1} u/s, "
                                      + $"rotazione {v.KnockSpin:F1} rad/s; auto del giocatore ora a {velocity.Length():F1} u/s"
                                      + (Vector2.Dot(velocity, n) < 0f ? " (rimbalza indietro)" : ""));
                if (vn > worst) { worst = vn; contact = new Vector3(at.X, v.Y + 0.3f, at.Y); }
            }
            // Fuori dall'ingombro dell'auto che arriva (se lì c'è posto; altrimenti l'auto non entra: vedi Car.Fits)
            Vector2 push = n * (depth + 0.01f);
            if (FreeExcept(v, v.Pos + push, v.Yaw, self))
            {
                v.Offset += push;
                v.Pos += push;
            }
        }
        // Le auto parcheggiate dal giocatore: stessa fisica, pesano come un'auto
        foreach (var c in _owned)
        {
            if (c == self || !VehicleBox.Overlap(box, c.Box, out var n, out float depth)) continue;
            Vector3 cv = c.Velocity;
            float vn = Vector2.Dot(velocity - new Vector2(cv.X, cv.Z), n);
            const float other = 1.5f, bounce = 0.45f;
            if (vn > 0f)
            {
                float j = (1f + bounce) * vn / (1f / mass + 1f / other);
                c.Shove(n * (j / other));
                velocity -= n * (j / mass);
                if (vn > worst)
                {
                    worst = vn;
                    Vector2 at = PointOn(c.Box, -n);
                    contact = new Vector3(at.X, c.Position.Y + 0.3f, at.Y);
                }
            }
            c.Nudge(world, n * (depth + 0.01f));
        }
        return worst;
    }

    /// <summary>Il punto del rettangolo più in fuori nella direzione data (dove lo si tocca arrivando da lì).</summary>
    private static Vector2 PointOn(in VehicleBox b, Vector2 dir) =>
        b.C + b.R * (b.W * MathF.Sign(Vector2.Dot(b.R, dir))) * 0.6f + b.F * (b.L * MathF.Sign(Vector2.Dot(b.F, dir))) * 0.6f;

    /// <summary>Come <see cref="Free"/>, senza contare l'auto <paramref name="ignore"/> (quella che la sta spingendo).</summary>
    private bool FreeExcept(Vehicle v, Vector2 pos, float yaw, Car ignore)
    {
        if (_world == null) return true;
        Vector2 f = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw)) * v.HullOffset;
        _skip = v;
        bool ground = _world.IsWalkable(new Vector3(pos.X + f.X, 0f, pos.Y + f.Y), v.HullRadius * 0.9f)
                      && _world.IsWalkable(new Vector3(pos.X - f.X, 0f, pos.Y - f.Y), v.HullRadius * 0.9f);
        _skip = null;
        if (!ground) return false;
        float now = DepthAgainst(BoxOf(v), v, ignore), next = DepthAgainst(BoxOf(v, pos, yaw), v, ignore);
        return next <= 0.002f || next < now - 1e-4f;
    }

    private static void Pose(Vehicle v, WorldMap world, float dt)
    {
        Vector2 pos, heading;
        if (v.BackCurve != null)
        {
            // Uscita in retromarcia nell'incrocio: si va lungo la curva, col muso dalla parte opposta
            pos = v.BackCurve.At(v.BackD);
            heading = -v.BackCurve.Tangent(v.BackD);
        }
        else if (v.Turning)
        {
            pos = v.Turn!.At(v.TurnD);
            heading = v.Turn.Tangent(v.TurnD);
        }
        else
        {
            pos = LanePoint(v.Edge, v.Dir, v.Lane + v.Shift, v.S);
            heading = v.Edge.Tangent(v.S) * v.Dir;
            // Cambiando corsia il muso è girato verso dove va (è lui che fa spostare l'auto di lato)
            if (v.LaneYaw != 0f)
                heading = heading * MathF.Cos(v.LaneYaw) + RightOf(heading) * MathF.Sin(v.LaneYaw);
        }
        // Dopo un urto: spostato e ruotato rispetto alla traiettoria
        pos += v.Offset;
        float yaw = Noise.WrapAngle(MathF.Atan2(heading.X, heading.Y) + v.YawOffset);
        heading = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
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

    /// <summary>Un cerchio nel punto tocca un'auto del traffico? (Per il personaggio a piedi, i passanti, i cerchi delle auto.)</summary>
    public bool Blocks(Vector2 p, float r)
    {
        foreach (var v in _cars)
        {
            if (v == _skip || Vector2.DistanceSquared(v.Pos, p) > (r + 2f) * (r + 2f)) continue;
            if (BoxOf(v).CircleDepth(p, r, out _) > 0f) return true;
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
            v.Model.Collect(items, root, body, v.Spin, v.Steer, night > 0.45f, v.Brake, v.Reversing);
        }
    }

    // ------------------------------------------------------------------ furto

    /// <summary>Un veicolo rubato: dove sta, com'è fatto, e da dove salta giù chi lo guidava (il guidatore sta a sinistra).</summary>
    public readonly record struct Stolen(Vector3 Pos, float Yaw, Vector3 Paint, BodyStyle Style, Vector3 Seat, Vector3 Door);

    /// <summary>
    /// Il veicolo che si può rubare stando a piedi in <paramref name="p"/>: abbastanza vicino (a meno di un passo dalla
    /// carrozzeria) e non troppo veloce. Null se non ce n'è.
    /// </summary>
    private Vehicle? Stealable(Vector2 p, float maxSpeed)
    {
        Vehicle? best = null;
        float bestD = float.MaxValue;
        foreach (var v in _cars)
        {
            if (v.Speed > maxSpeed || Vector2.DistanceSquared(v.Pos, p) > 9f) continue;
            float inside = BoxOf(v).CircleDepth(p, 1.0f, out _); // > 0: entro 1 dalla carrozzeria
            if (inside <= 0f) continue;
            float d = Vector2.Distance(v.Pos, p);
            if (d < bestD) { bestD = d; best = v; }
        }
        return best;
    }

    public bool CanSteal(Vector2 p, float maxSpeed) => Stealable(p, maxSpeed) != null;

    /// <summary>Toglie dal traffico il veicolo rubabile più vicino e dice com'era fatto (null se non ce n'è).</summary>
    public Stolen? Steal(Vector2 p, float maxSpeed)
    {
        if (Stealable(p, maxSpeed) is not { } v) return null;
        _cars.Remove(v);
        var pos = new Vector3(v.Pos.X, v.Y, v.Pos.Y);
        Vector3 left = new(MathF.Cos(v.Yaw), 0f, -MathF.Sin(v.Yaw)); // +X del modello: la sinistra del guidatore
        Vector3 seat = pos + left * 0.22f + new Vector3(0f, 0.12f, 0f);
        Vector3 door = pos + left * (v.HalfW + 0.35f);
        if (_world != null && !_world.IsWalkable(door, 0.2f)) door = pos - left * (v.HalfW + 0.35f); // dall'altra parte
        if (_world != null) door.Y = _world.GroundHeight(door);
        return new Stolen(pos, v.Yaw, v.Model.Paint, v.Model.Style, seat, door);
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

    /// <summary>Test: l'auto seguita si ferma e resta ferma (bersaglio per le prove d'urto).</summary>
    /// <summary>
    /// Test: un punto sulla traiettoria dell'auto del traffico più vicina, <paramref name="ahead"/> davanti a lei nella
    /// sua corsia (prima dell'incrocio), con la direzione di marcia: lì si mette l'auto del giocatore a sbarrarle la strada.
    /// </summary>
    public (Vector3 Pos, float Yaw)? BarrierSpot(Vector3 near, float ahead)
    {
        var v = _cars.Where(c => !c.Turning && c.Remaining > ahead + 2f)
                     .MinBy(c => Vector2.DistanceSquared(c.Pos, new Vector2(near.X, near.Z)));
        if (v == null) return null;
        Vector2 p = PathPoint(v, ahead, 0f), h = PathPoint(v, ahead + 0.3f, 0f) - PathPoint(v, ahead - 0.3f, 0f);
        Console.WriteLine($"Test: strada sbarrata all'auto {v.Id} ({v.Edge.Layout}, a {v.Remaining - ahead:F1} dall'incrocio)");
        return (new Vector3(p.X, 0f, p.Y), MathF.Atan2(h.X, h.Y));
    }

    public void FreezeFollowed()
    {
        if (_cars.Find(c => c.Id == _followed) is { } v) v.Cruise = v.Speed = 0f;
    }

    /// <summary>Riepilogo per i test: quante auto, quante ferme, quante in un incrocio.</summary>
    public string Status()
    {
        int stopped = _cars.Count(v => v.Speed < 0.1f), turning = _cars.Count(v => v.Turning);
        int bypass = _cars.Count(v => v.ShiftTarget != 0f), ghost = _cars.Count(v => v.Ghost > 0f);
        int light = _cars.Count(v => v.AtLight && v.Speed < 0.1f), queued = _cars.Count(v => v.Queued);
        int yields = _cars.Count(v => v.Yield);
        return $"traffico: {_cars.Count} auto, {stopped} ferme ({light} al rosso, {yields} danno precedenza, {queued} in coda), "
               + $"{turning} negli incroci, "
               + $"{bypass} aggirano, {_cars.Count(v => v.Backing > 0f)} in retro per sbloccarsi, {ghost} passano attraverso, "
               + $"{Backs} retro, {Retreats} cambi di strada e {Ghosts} attraversamenti finora, ferma da più tempo {Worst()}, record {LongestBlock:F1} s, "
               + $"distanza minima in fila {(MinFollowGap < 1e9f ? MinFollowGap.ToString("F1") : "-")}, "
               + $"scivolata di lato massima {WorstSlide:F2}/s [{SlideWho}], rotazione da ferma {WorstSpin:F2} rad/s [{SpinWho}], "
               + $"sovrapposizione massima {WorstOverlap:F3}"
               + (_cars.Find(v => v.Id == _followed) is { } f
                   ? $"; seguita: {f.Speed:F1} u/s{(f.AtLight ? ", ferma al semaforo" : "")}{(f.Yield ? ", dà precedenza" : "")}{(f.Queued ? ", in coda" : "")}" : "");
    }

    public void Dispose()
    {
        foreach (var set in _models.Values)
            foreach (var m in set) m.Dispose();
        if (Current == this) Current = null;
    }
}
