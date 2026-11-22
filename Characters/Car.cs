using System.Numerics;
using Roads.Rendering;
using Roads.World;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// Automobilina giocattolo low poly. Si guida con WASD o le frecce (e col mouse), Shift per andare piano, spazio per il
/// freno a mano (derapata in curva, inversione a U o derapata libera: vedi StartManoeuvre).
/// Guida "da auto": modello cinematico a bicicletta con raggio di sterzata minimo (non gira sul posto),
/// retromarcia, collisione a due cerchi (muso e coda) con scivolamento sugli ostacoli, luci di stop e di retromarcia.
/// Il modello guarda verso +Z locale.
/// </summary>
public sealed class Car : IDisposable
{
    private const float SlowSpeed = 2.6f;
    private const float FastSpeed = 14f;
    private const float ReverseSpeed = 2.4f;
    private const float Accel = 5f;           // unità/s² (retromarcia)
    private const float Brake = 11f;
    private const float Coast = 3f;            // rallentamento senza gas
    private const float Scale = 1.1f;
    private const float WheelBase = 1.0f * Scale; // distanza tra gli assi (ruote a z = ±0.5)
    private const float MaxSteer = 0.6f;          // rad: raggio minimo ≈ WheelBase / tan(MaxSteer) ≈ 1.6
    // Guida da tastiera: i tasti sono tutto-o-niente, quindi lo sterzo dipende dalla velocità.
    // Piano: sterzo pieno e pronto (manovre strette); veloce: poco angolo e ruote che girano con calma.
    private const float TurnRateSlow = 3.2f;      // rad/s: rotazione massima dell'auto a passo d'uomo
    private const float TurnRateFast = 1.1f;      // rad/s: rotazione massima a piena velocità (circa 63°/s)
    private const float SteerRampSlow = 0.15f;    // s: tempo per arrivare a fine corsa da fermi
    private const float SteerRampFast = 0.45f;    // s: tempo per arrivare a fine corsa a piena velocità
    private const float SteerReturn = 0.15f;      // s: rilasciando, le ruote tornano dritte
    private const float HullOffset = 0.46f;       // centri dei due cerchi di collisione, davanti e dietro
    private const float HullRadius = 0.5f;
    private const float WheelRadius = 0.2f;
    private const float WheelX = 0.47f;           // distanza delle ruote dal centro: sporgono un po' dalla scocca
    private const float WheelWidth = 0.22f;

    // Guida senza aiuti: la direzione di marcia segue il muso (quasi subito). Col freno a mano in velocità si fa una
    // delle tre manovre (vedi StartManoeuvre): derapata in curva, inversione a U o derapata libera.
    private const float Grip = 28f;               // 1/s: rapidità con cui la marcia segue il muso
    private const float DriftMinSpeed = 2.5f;     // sotto questa velocità il freno a mano frena e basta
    private const float HandbrakeDecel = 3.2f;    // unità/s²: rallentamento del freno a mano (senza manovra)
    private const float DecideWindow = 0.15f;     // s: se si sterza subito dopo aver tirato il freno a mano vale lo stesso

    // Derapata in curva: l'auto segue la traiettoria della strada (DriftLine) e si immette nella nuova strada
    // dalla parte dello sterzo, di traverso ma sempre in corsia
    private const float CornerMinSpeed = 5f;         // velocità minima per la derapata in curva
    private const float CornerMargin = 2.5f;         // anticipo minimo oltre lo spazio di frenata
    private const float AssistFullSpeed = 9f;        // da questa velocità in su la derapata è piena (traverso massimo)
    private const float AssistCornerSpeedMin = 5f;   // velocità in curva: almeno questa...
    private const float AssistCornerSpeedMax = 7.5f; // ...e al massimo questa (si frena prima dell'incrocio)
    private const float AssistBrake = 9f;            // unità/s²: frenata prima della curva
    private const float AssistMaxAngle = 0.8f;       // rad: traverso in curva (≈46°)
    private const float AssistLeadIn = 6f;           // la coda inizia a scartare a questa distanza dalla curva
    private const float AssistLeadOut = 7f;          // dopo la curva l'auto si raddrizza in questa distanza
    private const float AssistBodyRate = 14f;        // 1/s: prontezza della carrozzeria nel mettersi di traverso
    private const float DriftBoostMax = 2.2f;        // velocità extra massima in uscita da una derapata lunga

    // Inversione a U (strada senza incroci davanti): l'auto scivola lungo la propria corsia rallentando fino a
    // fermarsi mentre la carrozzeria gira di 180°, poi riparte nell'altro senso
    private const float UTurnMinSpeed = 6f;          // velocità minima per la manovra
    private const float UTurnSlideMin = 4f;          // spazio di scivolata (cresce con la velocità)
    private const float UTurnSlideMax = 8f;
    private const float UTurnSlideShortest = 2.5f;   // in fondo a un vicolo cieco: sotto questo spazio niente inversione
    private const float UTurnExitSpeed = 2.5f;       // a fine giro riparte già con questa velocità nel nuovo senso

    // Derapata libera (tutti gli altri casi): l'auto scivola dritta fino a fermarsi in circa SlideTime secondi e la
    // carrozzeria gira finché si tengono freno a mano e sterzo: SlideTurnRate rad/s, al massimo 180°
    private const float SlideTime = 1.2f;
    private const float SlideTurnRate = MathF.PI / 1.0f;

    // Bloccaggio delle ruote: insistendo sul freno in corsa le ruote si bloccano e l'auto striscia dritta
    private const float BrakeLockTime = 0.3f;        // s di freno tenuto premuto prima del bloccaggio
    private const float BrakeLocked = 8.5f;          // unità/s²: a ruote bloccate si frena un po' meno (si striscia)
    private const float LockedSteer = 0.12f;         // quota di sterzo che resta a ruote bloccate

    public Vector3 Position;
    public float Yaw;

    /// <summary>Velocità con segno (negativa in retromarcia).</summary>
    public float Speed => _speed;
    public Vector3 Velocity => Dir(_travelYaw) * _speed;
    public float MaxSpeed => FastSpeed;
    /// <summary>Fari accesi (tasto F).</summary>
    public bool Headlights;

    /// <summary>Motore acceso: spento quando il personaggio è sceso (niente sbuffi dallo scarico).</summary>
    public bool EngineOn = true;
    private float _idleShake = 1f; // vibrazione del motore al minimo: sfuma a zero quando si spegne

    /// <summary>Posizione di un faro (side = -1 sinistro, +1 destro), sul muso dell'auto.</summary>
    public Vector3 HeadlightPos(float side) => Vector3.Transform(new Vector3(side * 0.29f, 0.41f, 0.86f), RootMatrix);

    /// <summary>Direzione del fascio di un faro: un po' verso il basso e appena divergente verso l'esterno.</summary>
    public Vector3 HeadlightDir(float side) =>
        Vector3.Normalize(Vector3.TransformNormal(Vector3.Normalize(new Vector3(side * 0.07f, -0.13f, 1f)), RootMatrix));

    /// <summary>Quanto si sta dando gas (0..1), per il suono del motore.</summary>
    public float Throttle { get; private set; }

    // ------------------------------------------------------------------ cambio automatico a 4 marce
    // Ogni marcia ha un rapporto (la velocità a cui il motore arriva al limite di giri) e una spinta:
    // la 1ª è corta e potente, la 4ª lunga e "pigra". Dentro ogni marcia la spinta segue una curva di
    // coppia: piena a metà giri, cala verso il limite (l'allungo finisce).
    private static readonly float[] GearTop = { 5.0f, 8.0f, 11.0f, 15.0f };   // velocità al limite di giri (la 4ª è lunga: in crociera non sta al limite)
    private static readonly float[] GearPower = { 3.1f, 2.0f, 1.7f, 1.3f };   // spinta di picco (unità/s²): la 4ª allunga piano fino alla massima
    private const float UpshiftRpm = 0.95f;   // si sale di marcia vicino al limite di giri
    private const float DownshiftRpm = 0.5f;  // si scala quando la marcia inferiore sarebbe a metà giri
    private const float ShiftTime = 0.25f;    // durante la cambiata il motore non spinge
    private const float IdleRpm = 0.22f;

    /// <summary>Marcia inserita: 1..4, oppure -1 in retromarcia.</summary>
    public int Gear { get; private set; } = 1;
    /// <summary>Giri del motore normalizzati (minimo ≈ 0.22, limite = 1), per il suono.</summary>
    public float Rpm { get; private set; } = IdleRpm;
    private float _shiftTimer;

    private float GearRpm(int gear) => MathF.Abs(_speed) / GearTop[gear - 1];

    private static float TorqueCurve(float rpm) => 0.6f + 0.4f * MathF.Sin(MathF.PI * Math.Clamp(rpm, 0f, 1f));

    private void UpdateGearbox(float dt)
    {
        _shiftTimer = MathF.Max(0f, _shiftTimer - dt);
        if (_speed < -0.05f) { Gear = -1; return; }
        if (Gear < 1) Gear = 1;

        if (_shiftTimer <= 0f && Gear < GearTop.Length && GearRpm(Gear) >= UpshiftRpm)
        {
            Gear++;
            _shiftTimer = ShiftTime;
        }
        else if (Gear > 1 && GearRpm(Gear - 1) < DownshiftRpm)
            Gear--;
    }

    private float _speed, _yawRate, _spin, _steer, _time, _accel;
    private float _travelYaw, _smokeTimer, _dirtTimer;
    private float _driftTime, _steerIn, _gasIn, _lineU;
    private DriftLine? _line;
    private Vector3 _lineOffset;
    private float _brakeHold, _steerLatch;

    /// <summary>Ruote bloccate per la frenata insistita: l'auto striscia dritta e quasi non sterza.</summary>
    public bool WheelsLocked { get; private set; }

    // ------------------------------------------------------------------ freno a mano: le tre manovre

    /// <summary>Cosa sta facendo l'auto col freno a mano.</summary>
    private enum Manoeuvre { None, Corner, UTurn, Slide }
    private Manoeuvre _manoeuvre;
    private bool _prevHandbrake;
    private float _decide = -1f;                    // da quanto si è tirato il freno a mano senza aver ancora deciso
    private float _driftPower, _uturnSide, _uturnLength, _uturnDecel, _uturnTime, _uturnSpeed;
    private float _slideSide, _slideAngle, _slideBase, _slideTravel, _slideDecel;

    /// <summary>
    /// La decisione, presa una volta sola quando si tira il freno a mano sterzando (lato <paramref name="side"/>):
    /// <list type="number">
    /// <item>da quel lato c'è una strada in cui svoltare, la velocità è giusta e c'è abbastanza anticipo per frenare
    /// fino alla velocità di curva: derapata che immette nella nuova strada;</item>
    /// <item>davanti la strada continua senza incroci (sul viale: nella propria metà carreggiata): inversione a U
    /// completa nella carreggiata in cui si è;</item>
    /// <item>in tutti gli altri casi: derapata libera, l'auto si mette di traverso e si ferma.</item>
    /// </list>
    /// </summary>
    private void StartManoeuvre(float side)
    {
        Vector3 travel = Dir(_travelYaw);
        _driftTime = 0f;
        if (side != 0f && _speed >= CornerMinSpeed
            && DriftLine.Plan(Position, travel, side) is { } corner && corner.TurnSign == side && InTime(corner))
        {
            _manoeuvre = Manoeuvre.Corner;
            _driftPower = Noise.Smooth(CornerMinSpeed, AssistFullSpeed, _speed); // piano: poco di traverso
            _line = corner;
            _lineU = corner.Project(Position);
            _lineOffset = Flat(Position - corner.At(_lineU)); // lo scarto dalla traiettoria si riassorbe in fretta
            return;
        }
        if (side != 0f && _speed >= UTurnMinSpeed)
        {
            float length = Math.Clamp(_speed * 0.6f, UTurnSlideMin, UTurnSlideMax);
            if (DriftLine.AlongRoad(Position, travel, length) is { } along
                && along.Length - along.Project(Position) is var room && room >= UTurnSlideShortest)
            {
                length = MathF.Min(length, room); // in fondo a un vicolo cieco la scivolata si accorcia
                _manoeuvre = Manoeuvre.UTurn;
                _line = along;
                _lineU = along.Project(Position);
                _lineOffset = Flat(Position - along.At(_lineU));
                _uturnLength = _lineU + length;
                _uturnDecel = _speed * _speed / (2f * length); // si ferma esattamente a fine scivolata
                _uturnTime = 2f * length / _speed;              // durata della scivolata (velocità che cala costante)
                _uturnSpeed = _speed;
                _uturnSide = -side;                             // sterzo a destra = giro in senso orario
                return;
            }
        }
        _manoeuvre = Manoeuvre.Slide;
        _slideSide = side;
        _slideAngle = 0f;
        _slideBase = Yaw;
        _slideTravel = _travelYaw;
        _slideDecel = MathF.Max(_speed / SlideTime, 4f); // si ferma in circa SlideTime secondi
    }

    /// <summary>
    /// C'è il giusto anticipo per la curva? Davanti all'inizio della curva serve lo spazio per frenare fino alla
    /// velocità di curva, più un margine; troppo lontano no (la linea arriva a un incrocio solo entro la sua finestra).
    /// </summary>
    private bool InTime(DriftLine line)
    {
        float lead = line.CornerStart - line.Project(Position);
        float brake = MathF.Max(0f, _speed * _speed - AssistCornerSpeedMax * AssistCornerSpeedMax) / (2f * AssistBrake);
        return lead >= brake + CornerMargin;
    }

    /// <summary>Freno a mano tirato (spazio): in velocità, con lo sterzo, fa partire la derapata.</summary>
    public bool Handbrake;
    /// <summary>Direzione in cui l'auto si muove davvero (diversa dal muso quando derapa).</summary>
    public float TravelYaw => _travelYaw;
    /// <summary>Angolo di derapata: quanto il muso è girato rispetto alla direzione di marcia (rad).</summary>
    public float DriftAngle => Noise.WrapAngle(Yaw - _travelYaw);
    private float _slopePitch, _slopeRoll, _tilt, _roll;
    private float _exhaustTimer, _blockedTimer;
    private float _brakeGlow, _handbrakeGlow;

    /// <summary>Intensità degli stop: 1 in frenata, fino a 2 col freno a mano tirato (sfuma al rilascio).</summary>
    public float BrakeLight => MathF.Max(_brakeGlow, 2f * _handbrakeGlow);
    /// <summary>Centro dei due stop e direzione verso cui fanno luce (all'indietro).</summary>
    public Vector3 TailLightPos => Vector3.Transform(new Vector3(0f, 0.41f, -0.85f), RootMatrix);
    public Vector3 TailLightDir => -Forward;

    /// <summary>Quanto stridono le gomme (0..1): cresce con l'angolo di derapata e con la velocità.</summary>
    public float Skid { get; private set; }

    private readonly Mesh _body, _headLights, _tailLights, _reverseLights, _wheel;

    // ------------------------------------------------------------------ modello

    public Car(GL gl, Vector3 position, float yaw)
    {
        Position = position;
        Yaw = yaw;
        _travelYaw = yaw;

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

        // Ruota centrata nell'origine con asse lungo X: larga, sporge dai fianchi e si vede di lato
        mb = new MeshBuilder { Transform = Matrix4x4.CreateRotationZ(MathF.PI / 2f) };
        mb.AddPrism(new Vector3(0, -WheelWidth * 0.5f, 0), WheelRadius, WheelRadius, WheelWidth, 10, dark);
        mb.AddPrism(new Vector3(0, -WheelWidth * 0.5f - 0.01f, 0), 0.09f, 0.09f, WheelWidth + 0.02f, 6, trim);
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
    /// <summary>
    /// Angolo massimo delle ruote nella guida da tastiera: tale che l'auto ruoti al più di una velocità
    /// angolare che cala con la velocità (2.6 rad/s a passo d'uomo, 0.75 a piena velocità). Da fermi
    /// o quasi resta lo sterzo pieno, quindi le manovre lente sono strette e pronte.
    /// </summary>
    private float ManualSteerLimit
    {
        get
        {
            float v = MathF.Abs(_speed);
            if (v < 0.5f) return MaxSteer;
            float turnRate = Noise.Lerp(TurnRateSlow, TurnRateFast, MathF.Pow(MathF.Min(v / FastSpeed, 1f), 0.7f));
            return MathF.Min(MaxSteer, MathF.Atan(WheelBase * turnRate / v));
        }
    }

    // ------------------------------------------------------------------ spinta degli oggetti
    // Rocce e cespugli non fermano l'auto di colpo: vengono spinti via secondo la loro massa (la roccia pesa in
    // proporzione al volume, il cespuglio poco) e l'auto perde la velocità che cede all'oggetto.
    private const float CarMass = 1.5f;

    private Vector3 PushObstacles(WorldMap world, Particles fx, Vector3 step, float newYaw, float dt)
    {
        float len = step.Length();
        if (len < 1e-5f || dt <= 0f) return step;
        Vector2 velocity = new Vector2(step.X, step.Z) / dt;
        Vector3 next = Position + step, f = Dir(newYaw) * HullOffset;
        float hardest = 0f;
        Vector3 where = default;
        foreach (var c in new[] { next + f, next - f })
        {
            float hit = world.PushMovables(c, HullRadius, ref velocity, CarMass, out var contact);
            if (hit > hardest) { hardest = hit; where = contact; }
        }
        if (hardest > 0f && hardest > 1f) fx.Dust(where, hardest > 4f ? 4 : 2); // sbuffo di polvere nell'urto

        // La velocità ceduta all'oggetto si perde: il passo si accorcia (mai all'indietro)
        Vector2 dir = new Vector2(step.X, step.Z) / len;
        float kept = MathF.Max(0f, Vector2.Dot(velocity, dir)) * dt;
        if (kept < len)
        {
            _speed *= kept / len;
            step *= kept / len;
        }
        return step;
    }

    /// <summary>Sta scivolando di traverso in modo visibile.</summary>
    public bool Sliding => MathF.Abs(DriftAngle) > 0.15f;

    /// <summary>1 appena si esce da una derapata lunga con la spinta, poi sfuma (per effetti).</summary>
    public float BoostFlash { get; private set; }

    private static Vector3 Dir(float yaw) => new(MathF.Sin(yaw), 0, MathF.Cos(yaw));
    private Vector3 Forward => Dir(Yaw);
    private Vector3 Side => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw)); // +X locale

    private static float MoveTowards(float v, float target, float maxDelta) =>
        MathF.Abs(target - v) <= maxDelta ? target : v + MathF.Sign(target - v) * maxDelta;

    // ------------------------------------------------------------------ portiere e ingombro (per chi va a piedi)

    /// <summary>Punto nell'abitacolo, dalla parte della portiera (<paramref name="side"/> +1 = sinistra, -1 = destra).</summary>
    public Vector3 SeatPoint(float side) => Position + Side * (side * 0.22f) + new Vector3(0, 0.12f, 0);

    /// <summary>Punto a terra fuori dalla portiera, dove scende il personaggio.</summary>
    public Vector3 DoorPoint(float side) => Position + Side * (side * 0.9f) + Forward * 0.05f;

    /// <summary>Un cerchio di raggio <paramref name="r"/> nel punto tocca la carrozzeria?</summary>
    public bool Blocks(Vector3 p, float r)
    {
        Vector3 f = Forward * HullOffset;
        float rr = (HullRadius + r) * (HullRadius + r);
        return Flat(p - (Position + f)).LengthSquared() < rr || Flat(p - (Position - f)).LengthSquared() < rr;
    }

    /// <summary>L'auto (due cerchi, muso e coda) sta tutta su terreno libero in questa posa?</summary>
    private static bool Fits(WorldMap world, Vector3 p, float yaw)
    {
        Vector3 f = Dir(yaw) * HullOffset;
        return world.IsWalkable(p + f, HullRadius) && world.IsWalkable(p - f, HullRadius)
            // sulle banchine delle stazioni si va solo a piedi
            && !Railway.Current.OnPlatform(new Vector2(p.X + f.X, p.Z + f.Z), HullRadius)
            && !Railway.Current.OnPlatform(new Vector2(p.X - f.X, p.Z - f.Z), HullRadius);
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

    /// <param name="drive">
    /// Guida "da gioco di auto": X = sterzo (-1 sinistra, +1 destra), Y = gas (+1) / freno e retro (-1).
    /// </param>
    public void Update(float dt, Vector2 drive, bool slow, WorldMap world, Particles fx)
    {
        _time += dt;
        _idleShake = Noise.Lerp(_idleShake, EngineOn ? 1f : 0f, Noise.Damp(6f, dt));
        float maxSpeed = slow ? SlowSpeed : FastSpeed;

        // --- Comandi: sterzo e gas. Niente aiuti: l'auto va dove la si guida.
        float wantSpeed = 0f;
        bool coasting = false;
        // Dopo l'inversione a U lo sterzo tenuto per farla non conta finché non lo si rilascia (altrimenti l'auto,
        // finito il giro, continuerebbe a girare)
        if (_steerLatch != 0f && MathF.Sign(drive.X) != _steerLatch) _steerLatch = 0f;
        if (_steerLatch != 0f) drive.X = 0f;
        _steerIn = Math.Clamp(drive.X, -1f, 1f);
        _gasIn = Math.Clamp(drive.Y, -1f, 1f);
        float wantSteer = -_steerIn * ManualSteerLimit; // sterzo a destra = rotazione oraria = yaw che diminuisce
        if (_gasIn > 0.1f) wantSpeed = maxSpeed;
        else if (_gasIn < -0.1f) wantSpeed = _speed > 0.3f ? 0f : -ReverseSpeed; // prima frena, poi retro
        else coasting = true; // senza gas l'auto rallenta da sola, senza accendere gli stop
        bool handbrake = Handbrake;
        if (handbrake) { wantSpeed = 0f; coasting = true; }

        // --- Ruote che sterzano progressivamente, accelerazione e frenata lineari
        // Le ruote arrivano a fine corsa in un tempo che cresce con la velocità: pronte da fermi, calme in velocità
        bool turningIn = MathF.Abs(wantSteer) > MathF.Abs(_steer) && wantSteer * _steer >= 0f;
        float ramp = Noise.Lerp(SteerRampSlow, SteerRampFast, MathF.Min(MathF.Abs(_speed) / FastSpeed, 1f));
        float steerRate = turningIn ? ManualSteerLimit / ramp : MathF.Max(MathF.Abs(_steer), ManualSteerLimit) / SteerReturn;
        _steer = MoveTowards(_steer, wantSteer, steerRate * dt);
        // Oltre la velocità massima (spinta di uscita dalla derapata) col gas si rallenta piano, senza frenare
        if (_gasIn > 0.1f && wantSpeed > 0f && _speed > wantSpeed) coasting = true;
        bool braking = MathF.Abs(wantSpeed) < MathF.Abs(_speed) - 0.05f || wantSpeed * _speed < 0f;
        float prevSpeed = _speed;
        // In avanti la spinta viene dal cambio: marcia, curva di coppia e pausa durante la cambiata
        float push = Accel;
        if (!braking && !coasting && wantSpeed > 0f && _speed >= -0.05f)
            push = _shiftTimer > 0f ? 0f : GearPower[Gear - 1] * TorqueCurve(GearRpm(Gear));
        // Freno tenuto premuto in corsa (in avanti): dopo un attimo le ruote si bloccano
        bool brakePedal = _gasIn < -0.1f && _speed > 0.3f;
        _brakeHold = brakePedal ? _brakeHold + dt : 0f;
        WheelsLocked = brakePedal && _brakeHold > BrakeLockTime && _speed > 1f;
        if (_manoeuvre == Manoeuvre.None)
        {
            _speed = MoveTowards(_speed, wantSpeed, (coasting ? Coast : braking ? (WheelsLocked ? BrakeLocked : Brake) : push) * dt);
            if (handbrake) _speed = MoveTowards(_speed, 0f, HandbrakeDecel * dt); // il freno a mano frena
        }
        UpdateGearbox(dt);
        _accel = (_speed - prevSpeed) / MathF.Max(dt, 1e-4f);
        // Contro un ostacolo il motore non "tira": il suono resta al minimo, legato alla velocità vera
        bool onGas = !braking && wantSpeed != 0f && _shiftTimer <= 0f && _blockedTimer <= 0f && _manoeuvre == Manoeuvre.None;
        Throttle = Noise.Lerp(Throttle, onGas ? 1f : 0f, Noise.Damp(onGas ? 6f : 18f, dt));
        if (_blockedTimer > 0f) Throttle = 0f;
        // Giri: dalla velocità nella marcia inserita; in partenza col gas il motore sale un po' (convertitore)
        float rpm = Gear < 0 ? MathF.Abs(_speed) / ReverseSpeed * 0.8f : GearRpm(Gear);
        Rpm = MathF.Max(rpm, IdleRpm + (_blockedTimer > 0f ? 0f : 0.2f * Throttle));

        // --- Freno a mano: una sola decisione, presa quando lo si tira (o un attimo dopo, se si sterza appena dopo)
        if (handbrake && !_prevHandbrake && _manoeuvre == Manoeuvre.None && _speed > DriftMinSpeed) _decide = 0f;
        _prevHandbrake = handbrake;
        if (_decide >= 0f)
        {
            _decide += dt;
            float side = MathF.Abs(_steerIn) > 0.3f ? MathF.Sign(_steerIn) : 0f;
            if (!handbrake || _speed <= DriftMinSpeed) _decide = -1f;
            else if (side != 0f || _decide > DecideWindow)
            {
                _decide = -1f;
                StartManoeuvre(side);
            }
        }

        float yawRate, newYaw;
        Vector3? railStep = null;
        if (_manoeuvre is Manoeuvre.Corner or Manoeuvre.UTurn && _line == null) _manoeuvre = Manoeuvre.None;
        if (_manoeuvre == Manoeuvre.UTurn && _line != null)
        {
            var line = _line;
            // Scivolata lungo la corsia con frenata costante: si ferma esattamente a fine tratto
            _speed = _uturnSpeed = MathF.Max(0f, _uturnSpeed - _uturnDecel * dt);
            _lineU = MathF.Min(_lineU + _speed * dt, _uturnLength);
            _lineOffset *= MathF.Exp(-4f * dt);
            railStep = Flat(line.At(_lineU) + _lineOffset - Position);
            float heading = line.Heading(_lineU);
            _travelYaw = heading;

            // Rotazione di 180° distribuita sulla durata della scivolata (parte e finisce morbida)
            _driftTime += dt;
            float progress = Math.Clamp(_driftTime / _uturnTime, 0f, 1f);
            newYaw = Noise.WrapAngle(heading + _uturnSide * MathF.PI * Noise.Smooth(0f, 1f, progress));
            yawRate = Noise.WrapAngle(newYaw - Yaw) / MathF.Max(dt, 1e-4f);
            _steer = MoveTowards(_steer, -_uturnSide * MaxSteer, 5f * dt); // ruote in controsterzo

            if (progress >= 1f || _speed <= 0.05f || _blockedTimer > 0f)
            {
                // Fine giro: muso e marcia nel senso opposto, si riparte subito (se un urto l'ha interrotto, si resta così)
                bool clean = _blockedTimer <= 0f;
                _manoeuvre = Manoeuvre.None;
                _line = null;
                if (clean || progress > 0.85f) newYaw = Noise.WrapAngle(heading + _uturnSide * MathF.PI);
                yawRate = 0f;
                _travelYaw = newYaw;
                _steer = 0f;
                _speed = clean ? UTurnExitSpeed : 0f;
                Gear = 1;
                _steerLatch = -_uturnSide; // il segno dello sterzo che ha avviato il giro
            }
        }
        else if (_manoeuvre == Manoeuvre.Corner && _line != null)
        {
            var line = _line;
            // Prima della curva si frena alla velocità giusta per farla di traverso (fa parte della manovra)
            if (_lineU < line.CornerEnd)
            {
                float cornerSpeed = Math.Clamp(_speed, AssistCornerSpeedMin, AssistCornerSpeedMax);
                if (_lineU > line.CornerStart - _speed * 0.9f && _speed > cornerSpeed)
                    _speed = MoveTowards(_speed, cornerSpeed, AssistBrake * dt);
                // Fino alla curva e in curva la velocità non crolla sotto quella giusta (si attraversa l'incrocio)
                _speed = MathF.Max(_speed, MathF.Min(AssistCornerSpeedMin, _speed + 6f * dt));
            }

            // L'auto avanza sulla traiettoria: marcia = direzione della strada, sempre in corsia
            _lineU += MathF.Abs(_speed) * dt;
            _lineOffset *= MathF.Exp(-4f * dt);
            railStep = Flat(line.At(_lineU) + _lineOffset - Position);
            float heading = line.Heading(_lineU);
            _travelYaw = heading;

            // Carrozzeria di traverso secondo un profilo lungo la traiettoria: dritta in avvicinamento, la coda
            // "scarta" negli ultimi metri, traverso pieno dentro l'incrocio, poi si raddrizza in uscita
            float side = MathF.Sign(Noise.WrapAngle(line.Heading(line.CornerEnd - 0.1f) - line.Heading(line.CornerStart + 0.1f)));
            float into = Noise.Smooth(line.CornerStart - AssistLeadIn, line.CornerStart, _lineU);
            float outOf = 1f - Noise.Smooth(line.CornerEnd, line.CornerEnd + AssistLeadOut, _lineU);
            float angle = side * AssistMaxAngle * Noise.Lerp(0.35f, 1f, _driftPower) * MathF.Min(into, outOf);
            newYaw = Yaw + Noise.WrapAngle(heading + angle - Yaw) * Noise.Damp(AssistBodyRate, dt);
            yawRate = Noise.WrapAngle(newYaw - Yaw) / MathF.Max(dt, 1e-4f);
            float slideNow = Noise.WrapAngle(newYaw - heading);
            _steer = MoveTowards(_steer, Math.Clamp(-slideNow * 0.8f, -MaxSteer, MaxSteer), 5f * dt); // controsterzo

            // Fine: curva superata e auto di nuovo dritta, fine linea o urto
            _driftTime += dt;
            bool done = _lineU > line.CornerEnd + AssistLeadOut && MathF.Abs(slideNow) < 0.12f;
            if (done || _lineU > line.Length - 1f || _blockedTimer > 0f)
            {
                _manoeuvre = Manoeuvre.None;
                _line = null;
                _travelYaw = newYaw;
                // Uscita pulita: spinta in avanti e fiammata dallo scarico (piena solo se si è entrati forte)
                if (done && _driftPower > 0.3f)
                {
                    _speed += MathF.Min(1f + _driftTime * 0.5f, DriftBoostMax) * _driftPower;
                    for (int k = 0; k < 4; k++)
                        fx.Ember(Vector3.Transform(new Vector3(0.28f, 0.2f, -1f), RootMatrix));
                    fx.Dust(Vector3.Transform(new Vector3(0.28f, 0.2f, -1f), RootMatrix), 3);
                    BoostFlash = 1f;
                }
            }
        }
        else if (_manoeuvre == Manoeuvre.Slide)
        {
            // Derapata libera: l'auto scivola dritta dove andava, rallentando fino a fermarsi, e la carrozzeria
            // gira finché si tengono freno a mano e sterzo dallo stesso lato: un tocco la mette di traverso,
            // tenendo a lungo arriva a 180°. Lasciati i tasti l'angolo resta quello raggiunto.
            _driftTime += dt;
            if (_slideSide == 0f && MathF.Abs(_steerIn) > 0.3f) _slideSide = MathF.Sign(_steerIn);
            bool holding = handbrake && _slideSide != 0f && MathF.Abs(_steerIn) > 0.3f && MathF.Sign(_steerIn) == _slideSide;
            if (holding) _slideAngle = MathF.Min(MathF.PI, _slideAngle + SlideTurnRate * dt);
            _speed = MoveTowards(_speed, 0f, _slideDecel * dt);
            _travelYaw = _slideTravel;
            float target = Noise.WrapAngle(_slideBase - _slideSide * _slideAngle);
            newYaw = Noise.WrapAngle(Yaw + Noise.WrapAngle(target - Yaw) * Noise.Damp(10f, dt));
            yawRate = Noise.WrapAngle(newYaw - Yaw) / MathF.Max(dt, 1e-4f);
            _steer = MoveTowards(_steer, _slideSide * MaxSteer * 0.8f, 5f * dt); // ruote in controsterzo
            if (_speed < 0.25f || _blockedTimer > 0f)
            {
                // Ferma: muso e marcia coincidono di nuovo, si riparte da come si è
                _manoeuvre = Manoeuvre.None;
                _speed = 0f;
                _travelYaw = newYaw;
                _steer = 0f;
                if (_slideAngle > MathF.PI * 0.9f) _steerLatch = _slideSide;
            }
        }
        else
        {
            // Guida normale: modello a bicicletta, la rotazione dipende dalla velocità (niente rotazioni sul posto).
            // A ruote bloccate le anteriori non direzionano: lo sterzo quasi non conta.
            yawRate = _speed * MathF.Tan(_steer) / WheelBase * (WheelsLocked ? LockedSteer : 1f);
            newYaw = Yaw + yawRate * dt;
            _travelYaw = Noise.WrapAngle(_travelYaw + Noise.WrapAngle(newYaw - _travelYaw) * (1f - MathF.Exp(-Grip * dt)));
        }
        BoostFlash = MathF.Max(0f, BoostFlash - dt * 2.5f);
        Vector3 step = railStep ?? Dir(_travelYaw) * (_speed * dt);
        step = PushObstacles(world, fx, step, newYaw, dt);
        float moved = step.LengthSquared() > 0f ? Move(world, step, newYaw) : 0f;
        _yawRate = Noise.Lerp(_yawRate, yawRate, Noise.Damp(10f, dt));

        // Urti: la velocità diventa quella davvero percorsa. Contro un albero preso in pieno si azzera; strisciando
        // lungo un ostacolo resta solo la parte che si è mossa. (Prima si scalava solo tenendo il gas: lasciandolo,
        // l'auto restava ferma con la velocità di prima "in memoria" e, appena libera, ripartiva da sola.)
        float expected = step.Length(); // il passo tentato (già ridotto da eventuali oggetti spinti)
        if (expected > 1e-6f && moved < expected * 0.999f)
            _speed *= moved / expected;

        // Bloccata mentre vuole muoversi (motore al minimo, fine delle manovre assistite)
        bool pushing = MathF.Abs(wantSpeed) > 0.1f;
        if (pushing && moved < expected * 0.3f) _blockedTimer += dt;
        else _blockedTimer = MathF.Max(0f, _blockedTimer - dt);

        Position.Y = Noise.Lerp(Position.Y, world.GroundHeight(Position), Noise.Damp(25f, dt));

        // Luci: stop accesi in frenata (e in retro), con un breve strascico per non sfarfallare
        bool stopLights = (braking && !coasting && MathF.Abs(_speed) > 0.3f) || _speed < -0.05f;
        _brakeGlow = stopLights ? 1f : MathF.Max(0f, _brakeGlow - dt * 4f);
        _handbrakeGlow = Handbrake ? 1f : MathF.Max(0f, _handbrakeGlow - dt * 4f);
        float skid = Math.Clamp((MathF.Abs(DriftAngle) - 0.08f) / 0.45f, 0f, 1f) * Math.Clamp((MathF.Abs(_speed) - 0.8f) / 3f, 0f, 1f);
        if (WheelsLocked) skid = MathF.Max(skid, Math.Clamp(_speed / 6f, 0.45f, 1f)); // gomme che strisciano
        Skid = Noise.Lerp(Skid, skid, Noise.Damp(skid > Skid ? 20f : 30f, dt)); // si spegne subito se l'auto si ferma

        // Assetto: segue il terreno (ponti ad arco, pendii) e si inclina in curva e in frenata
        float hF = world.GroundHeight(Position + Forward * 0.6f), hB = world.GroundHeight(Position - Forward * 0.6f);
        float hR = world.GroundHeight(Position + Side * 0.4f), hL = world.GroundHeight(Position - Side * 0.4f);
        _slopePitch = Noise.Lerp(_slopePitch, Math.Clamp(MathF.Atan2(hB - hF, 1.2f), -0.4f, 0.4f), Noise.Damp(12f, dt));
        _slopeRoll = Noise.Lerp(_slopeRoll, Math.Clamp(MathF.Atan2(hR - hL, 0.8f), -0.3f, 0.3f), Noise.Damp(12f, dt));
        _tilt = Noise.Lerp(_tilt, Math.Clamp(_accel * 0.012f, -0.09f, 0.09f), Noise.Damp(8f, dt));
        _roll = Noise.Lerp(_roll, Math.Clamp(_yawRate * _speed * 0.012f, -0.12f, 0.12f), Noise.Damp(8f, dt));

        if (!WheelsLocked) _spin += _speed * dt / (WheelRadius * Scale); // bloccate: le ruote non girano

        // Fumo dalle gomme posteriori mentre derapa
        _smokeTimer -= dt;
        if (Sliding && MathF.Abs(_speed) > 2f && _smokeTimer <= 0f)
        {
            _smokeTimer = 0.045f;
            foreach (float sx in new[] { -0.42f, 0.42f })
                fx.Dust(Vector3.Transform(new Vector3(sx, 0.05f, -0.5f), RootMatrix), 1);
        }
        else if (WheelsLocked && _speed > 1.5f && _smokeTimer <= 0f)
        {
            // Ruote bloccate: fumo da tutte e quattro le gomme
            _smokeTimer = 0.06f;
            foreach (float sx in new[] { -0.42f, 0.42f })
                foreach (float sz in new[] { -0.5f, 0.5f })
                    fx.Dust(Vector3.Transform(new Vector3(sx, 0.05f, sz), RootMatrix), 1);
        }

        // Sullo sterrato le gomme posteriori alzano polvere: più fitta e lunga con la velocità e in derapata
        _dirtTimer -= dt;
        float pace = MathF.Abs(_speed);
        if (pace > 1.2f && _dirtTimer <= 0f && world.SurfaceAt(Position) == RoadKind.Dirt)
        {
            float strength = Math.Clamp(pace / 9f, 0.2f, 1f) * (Sliding ? 1.4f : 1f);
            _dirtTimer = Noise.Lerp(0.16f, 0.06f, Math.Clamp(pace / 9f, 0f, 1f));
            Vector3 back = -Forward * MathF.Sign(_speed);
            foreach (float sx in new[] { -0.42f, 0.42f })
                fx.RoadDust(Vector3.Transform(new Vector3(sx, 0.05f, -0.5f), RootMatrix), back, strength);
        }

        // Sbuffi dallo scarico, più fitti in accelerazione
        _exhaustTimer -= dt;
        if (EngineOn && _exhaustTimer <= 0f)
        {
            _exhaustTimer = _accel > 1f ? 0.07f : MathF.Abs(_speed) > 0.5f ? 0.16f : 0.45f;
            fx.Dust(Vector3.Transform(new Vector3(0.28f, 0.2f, -0.95f), RootMatrix), _accel > 1f ? 2 : 1);
        }
    }

    /// <summary>
    /// Quali gomme lasciano il segno sull'asfalto, solo per i freni premuti dal giocatore: 4 = tutte (frenata
    /// insistita con freccia giù), 2 = le posteriori (freno a mano tirato in velocità), 0 = nessuna.
    /// Le manovre assistite (derapata guidata, inversione a U) non segnano da sole: conta solo il tasto premuto.
    /// </summary>
    public int MarkingWheels =>
        _brakeHold > 0.12f && _speed > 1.5f ? 4
        : Handbrake && MathF.Abs(_speed) > 1.5f ? 2
        : 0;

    /// <summary>Intensità dei segni (0..1): cresce con la pressione del freno e con la velocità.</summary>
    public float MarkStrength => MarkingWheels == 4
        ? Noise.Smooth(0.12f, BrakeLockTime + 0.25f, _brakeHold) * Math.Clamp(_speed / 6f, 0.5f, 1f)
        : MarkingWheels == 2 ? Math.Clamp(MathF.Abs(_speed) / 7f, 0.4f, 1f) : 0f;

    /// <summary>Punto di contatto a terra della ruota (<paramref name="sx"/> ±1 lato, <paramref name="sz"/> +1 davanti, -1 dietro).</summary>
    public Vector3 WheelContact(float sx, float sz) => Vector3.Transform(new Vector3(sx * WheelX, 0f, sz * 0.5f), RootMatrix);

    /// <summary>Direzione del muso (orizzontale).</summary>
    public Vector3 Heading => Forward;

    private Matrix4x4 RootMatrix =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationZ(_slopeRoll) * Matrix4x4.CreateRotationX(_slopePitch)
        * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position);

    public void Collect(List<DrawItem> items)
    {
        Matrix4x4 root = RootMatrix;
        // Motore al minimo: leggera vibrazione; in movimento piccoli sobbalzi
        float bounce = MathF.Sin(_time * 38f) * 0.006f * _idleShake // a motore spento l'auto sta ferma
                       + MathF.Abs(MathF.Sin(_spin * 0.7f)) * 0.012f * MathF.Min(MathF.Abs(_speed) / FastSpeed, 1f);
        Matrix4x4 body = Matrix4x4.CreateRotationZ(_roll) * Matrix4x4.CreateRotationX(-_tilt)
                         * Matrix4x4.CreateTranslation(0, bounce, 0) * root;
        items.Add(new DrawItem(_body, body));
        // Fari spenti: solo il vetro chiaro; accesi: pieni. Coi fari accesi anche le luci posteriori restano accese.
        items.Add(new DrawItem(_headLights, body, Headlights ? 1f : 0.2f, CastShadow: !Headlights));
        // Luci di posizione con i fari accesi; in frenata molto più intense, col freno a mano al massimo
        float tail = Headlights ? 0.8f : 0.2f;
        items.Add(new DrawItem(_tailLights, body, MathF.Max(tail, 1.7f * BrakeLight)));
        if (_speed < -0.05f) items.Add(new DrawItem(_reverseLights, body, 1f, CastShadow: false));

        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
            {
                float steer = sz > 0 ? _steer : 0f;
                items.Add(new DrawItem(_wheel, Matrix4x4.CreateRotationX(_spin) * Matrix4x4.CreateRotationY(steer)
                                               * Matrix4x4.CreateTranslation(sx * WheelX, WheelRadius, sz * 0.5f) * root));
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
