using System.Numerics;
using Roads.Rendering;
using Roads.World;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// Automobilina giocattolo low poly. Si guida con WASD o le frecce (e col mouse), Shift per andare piano, spazio per il
/// freno a mano: il posteriore perde aderenza e lo sterzo fa sbandare l'auto (nessun aiuto); con indietro
/// inversione a U (vedi StartUTurn).
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
    private const float Brake = 16f;
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
    private const float WheelRadius = CarModel.WheelRadius;
    private const float WheelX = CarModel.WheelX;

    // Derapata vera: la direzione di marcia (dove va l'auto) è separata dal muso (dove punta). Con aderenza piena
    // coincidono; col freno a mano il posteriore perde aderenza, il muso ruota più in fretta e la marcia lo insegue in
    // ritardo: l'auto scivola di traverso e la si guida con sterzo (anche in controsterzo) e gas. Lasciato il freno a
    // mano l'aderenza torna piano piano. La posizione dell'auto non viene mai spostata dal gioco.
    private const float GripFull = 28f;           // 1/s: con aderenza piena la marcia segue il muso quasi subito
    private const float GripDrift = 1.3f;         // 1/s: col freno a mano tirato
    private const float GripRecover = 2.2f;       // 1/s: rapidità con cui torna l'aderenza (uscita dalla derapata)
    private const float DriftMinSpeed = 2.5f;     // sotto questa velocità il freno a mano frena e basta
    private const float DriftYawBoost = 0.9f;     // quanto ruota in più il muso senza aderenza dietro
    private const float DriftYawMax = 2.3f;       // rad/s: rotazione massima in derapata (oltre sarebbe un testacoda)
    private const float DriftSteer = 0.42f;       // sterzo massimo in derapata (in velocità di solito è molto meno)
    private const float DriftSteerRate = 2.6f;    // rad/s: in derapata si sterza e controsterza più svelti
    private const float MaxDrift = 1.15f;         // rad: angolo massimo tra muso e marcia (oltre si girerebbe su se stessi)
    private const float HandbrakeDecel = 4.5f;    // unità/s²: il freno a mano è un freno: rallenta forte
    private const float DriftScrub = 3.5f;        // unità/s²: attrito delle gomme di traverso (per angolo pieno)

    // Freno a mano: il posteriore perde aderenza e basta (lo sterzo fa sbandare l'auto, nessun aiuto). Solo spazio +
    // indietro, entro DecideWindow dalla pressione, fa l'inversione a U.
    private const float DecideWindow = 0.5f;      // s

    // Inversione a U (spazio + indietro, sempre): l'auto scivola lungo la propria corsia rallentando fino a
    // fermarsi mentre la carrozzeria gira di 180°, poi riparte nell'altro senso
    private const float UTurnSlideMin = 4f;          // spazio di scivolata (cresce con la velocità)
    private const float UTurnSlideMax = 8f;
    private const float UTurnExitSpeed = 2.5f;       // a fine giro riparte già con questa velocità nel nuovo senso

    // Bloccaggio delle ruote: insistendo sul freno in corsa tutte e quattro le ruote si bloccano e l'auto striscia
    // dritta inchiodando. Fermata col freno ancora premuto resta ferma: per la retro si lascia e si ripreme.
    private const float BrakeLockTime = 0.3f;        // s di freno tenuto premuto prima del bloccaggio
    private const float BrakeLocked = 19f;           // unità/s²: inchiodata, ancora più decisa della frenata
    private const float LockedSteer = 0.12f;         // quota di sterzo che resta a ruote bloccate

    public Vector3 Position;
    public float Yaw;

    /// <summary>Velocità con segno (negativa in retromarcia).</summary>
    public float Speed => _speed;
    public Vector3 Velocity => Dir(_travelYaw) * _speed + _knock;
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
    private bool _brakeToStop; // il freno premuto ora ha fermato l'auto: niente retro finché non lo si lascia

    /// <summary>Ruote bloccate per la frenata insistita: l'auto striscia dritta e quasi non sterza.</summary>
    public bool WheelsLocked { get; private set; }

    // ------------------------------------------------------------------ freno a mano: derapata e inversione a U

    /// <summary>Manovra guidata in corso (la derapata non lo è: la si guida, con al più un aiuto leggero).</summary>
    private enum Manoeuvre { None, UTurn }
    private Manoeuvre _manoeuvre;
    private bool _prevHandbrake;
    private float _decide = -1f;                    // da quanto si è tirato il freno a mano senza aver ancora deciso
    private float _uturnSide, _uturnLength, _uturnDecel, _uturnTime, _uturnSpeed;
    private float _grip = GripFull;                 // aderenza del posteriore (1/s)


    /// <summary>
    /// Spazio + indietro: inversione a U, sempre e ovunque. In strada l'auto scivola lungo la propria corsia (sul viale
    /// nella propria metà; agli incroci dritta), fuori strada dritta; intanto la carrozzeria gira di 180° verso
    /// sinistra, o dal lato dello sterzo se lo si tiene (<paramref name="side"/>), poi riparte nell'altro senso.
    /// </summary>
    private void StartUTurn(float side)
    {
        Vector3 travel = Dir(_travelYaw);
        float length = Math.Clamp(_speed * 0.6f, UTurnSlideMin, UTurnSlideMax);
        var line = DriftLine.AlongRoad(Position, travel, length, anywhere: true) ?? DriftLine.Straight(Position, travel, length);
        float room = line.Length - line.Project(Position);
        length = Math.Clamp(MathF.Min(length, room), 1.5f, UTurnSlideMax); // in fondo a un vicolo la scivolata si accorcia
        _driftTime = 0f;
        _manoeuvre = Manoeuvre.UTurn;
        _line = line;
        _lineU = line.Project(Position);
        _lineOffset = Flat(Position - line.At(_lineU));
        _uturnLength = _lineU + length;
        _uturnDecel = _speed * _speed / (2f * length); // si ferma esattamente a fine scivolata
        _uturnTime = 2f * length / _speed;              // durata della scivolata (velocità che cala costante)
        _uturnSpeed = _speed;
        _uturnSide = side != 0f ? -side : 1f;           // di serie verso sinistra; sterzo a destra = senso orario
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

    private readonly CarModel _model;

    // ------------------------------------------------------------------ modello

    /// <summary>Mette l'auto, ferma, nel punto e nella direzione dati (test).</summary>
    public void Place(Vector3 position, float yaw)
    {
        Position = position;
        Yaw = _travelYaw = yaw;
        _speed = 0f;
        _knock = Vector3.Zero;
    }

    /// <summary>Carrozzeria e colore (l'auto di partenza è l'utilitaria rossa; rubandone una si prende la sua).</summary>
    public BodyStyle Style => _model.Style;
    public Vector3 Paint { get; }
    /// <summary>Mezza larghezza e mezza lunghezza dell'ingombro vero (ruote e paraurti compresi), per gli urti tra veicoli.</summary>
    public float HalfWidth { get; }
    public float HalfLength { get; }
    /// <summary>Velocità massima della carrozzeria (il trattore è lento).</summary>
    private readonly float _topSpeed = FastSpeed;

    /// <summary>L'ingombro dell'auto come rettangolo orientato, nella posa data.</summary>
    public VehicleBox BoxAt(Vector3 p, float yaw) =>
        new(new Vector2(p.X, p.Z), new Vector2(MathF.Sin(yaw), MathF.Cos(yaw)), HalfWidth, HalfLength);
    public VehicleBox Box => BoxAt(Position, Yaw);

    /// <summary>Spinta ricevuta da un urto (velocità sul piano): si somma al rimbalzo e si smorza da sola.</summary>
    public void Shove(Vector2 velocity)
    {
        _knock += new Vector3(velocity.X, 0f, velocity.Y);
        if (_knock.Length() > BounceMax) _knock = Vector3.Normalize(_knock) * BounceMax;
    }

    /// <summary>Spostamento immediato (per uscire dall'ingombro di chi la urta), solo se lì c'è posto.</summary>
    public bool Nudge(WorldMap world, Vector2 by)
    {
        var to = Position + new Vector3(by.X, 0f, by.Y);
        if (!Fits(world, to, Yaw)) return false;
        Position = to;
        return true;
    }

    public Car(GL gl, Vector3 position, float yaw, Vector3? paint = null, BodyStyle style = BodyStyle.Hatch)
    {
        Position = position;
        Yaw = yaw;
        _travelYaw = yaw;

        Paint = paint ?? new Vector3(0.92f, 0.26f, 0.22f);
        _model = new CarModel(gl, Paint, style);
        (HalfWidth, HalfLength) = VehicleBox.Size(style);
        _topSpeed = style == BodyStyle.Tractor ? 4.5f : style == BodyStyle.Sport ? 16f : FastSpeed;
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

    // ------------------------------------------------------------------ rimbalzo negli urti
    // Contro un ostacolo fisso (albero, muro, treno, riva) o molto pesante (una grossa roccia) l'auto rimbalza via
    // con una velocità proporzionale a quella d'impatto (la parte diretta contro l'ostacolo). Il rimbalzo è una
    // velocità a parte (_knock) che si somma alla marcia e si smorza da sola, come gomme che strisciano.
    private const float BounceRestitution = 0.7f;  // quota della velocità d'impatto restituita
    private const float BounceMinImpact = 1.5f;    // sotto questa velocità d'impatto niente rimbalzo (appoggiarsi non fa saltellare)
    private const float BounceMax = 9f;            // tetto della velocità di rimbalzo
    private const float KnockFriction = 4f;        // unità/s²: rallentamento del rimbalzo
    private Vector3 _knock;
    private float _stun; // dopo un urto, per un attimo il gas non spinge (altrimenti rimanderebbe subito l'auto contro il muro)

    /// <summary>Il cerchio della carrozzeria in <paramref name="c"/> sta su terreno libero?</summary>
    private static bool Free(WorldMap world, Vector3 c) =>
        world.IsWalkable(c, HullRadius) && !Railway.Current.OnPlatform(new Vector2(c.X, c.Z), HullRadius);

    /// <summary>
    /// Normale (orizzontale, verso l'esterno) dell'ostacolo che blocca l'auto nella posa tentata: per ogni cerchio
    /// bloccato si cercano tutt'intorno le direzioni libere e se ne fa la media. Qualunque sia l'ostacolo (cerchio,
    /// muro ruotato, treno, acqua) la media punta dalla parte in cui c'è posto. Null se non la si trova.
    /// </summary>
    private static Vector3? ObstacleNormal(WorldMap world, Vector3 pos, float yaw)
    {
        Vector3 f = Dir(yaw) * HullOffset, sum = Vector3.Zero;
        foreach (var c in new[] { pos + f, pos - f })
        {
            if (Free(world, c)) continue;
            foreach (float reach in new[] { 0.25f, 0.5f })
            {
                Vector3 local = Vector3.Zero;
                for (int i = 0; i < 16; i++)
                {
                    float a = i * MathF.Tau / 16f;
                    Vector3 d = new(MathF.Sin(a), 0f, MathF.Cos(a));
                    if (Free(world, c + d * reach)) local += d;
                }
                if (local.LengthSquared() > 1e-4f) { sum += Vector3.Normalize(local); break; }
            }
        }
        return sum.LengthSquared() > 1e-4f ? Vector3.Normalize(sum) : null;
    }

    /// <summary>Rimbalzo contro un ostacolo fisso: <paramref name="velocity"/> è la velocità con cui ci si è andati contro.</summary>
    private bool Bounce(WorldMap world, Particles fx, Vector3 tried, float yaw, Vector3 velocity)
    {
        if (ObstacleNormal(world, tried, yaw) is not { } n) return false;
        float impact = -Vector3.Dot(velocity, n); // quanto si andava dritti contro l'ostacolo
        if (impact < BounceMinImpact) return false;
        // La marcia perde la parte diretta contro l'ostacolo (tutta se lo si prende in pieno, poca di striscio),
        // altrimenti continuerebbe a spingere l'auto contro il muro annullando il rimbalzo
        Vector3 drive = Dir(_travelYaw) * MathF.Sign(_speed);
        float into = MathF.Max(0f, -Vector3.Dot(drive, n));
        _speed *= (1f - into) * (1f - into);
        _stun = Math.Clamp(impact * 0.05f, 0.15f, 0.5f);
        // Del rimbalzo precedente resta solo la parte lungo il muro; quella nuova è proporzionale all'impatto
        _knock = _knock - n * Vector3.Dot(_knock, n) + n * MathF.Min(impact * BounceRestitution, BounceMax);
        fx.Dust(Position - n * (HullOffset + HullRadius) * 0.8f + new Vector3(0f, 0.3f, 0f), impact > 6f ? 5 : 3);
        return true;
    }

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
        // Gli altri veicoli (traffico e auto parcheggiate): si spostano o rimbalzano secondo la loro massa (un trattore
        // quasi non si muove); l'urto si calcola tra i rettangoli veri delle carrozzerie
        if (Traffic.Current is { } traffic)
        {
            float hit = traffic.PushBox(this, BoxAt(next, newYaw), ref velocity, CarMass, world, out var contact);
            if (hit > hardest) { hardest = hit; where = contact; }
            // Come contro un muro: per un attimo il gas non spinge, altrimenti annullerebbe subito il rimbalzo
            if (hit > BounceMinImpact) _stun = MathF.Max(_stun, Math.Clamp(hit * 0.07f, 0.25f, 0.7f));
        }
        if (hardest > 0f && hardest > 1f) fx.Dust(where, hardest > 4f ? 4 : 2); // sbuffo di polvere nell'urto
        foreach (var (at, hitVel, speed) in world.TakeBroken()) fx.Shards(at, hitVel, speed); // palo abbattuto: schegge

        // La velocità ceduta all'oggetto si perde: il passo si accorcia (mai all'indietro)
        Vector2 dir = new Vector2(step.X, step.Z) / len;
        float along = Vector2.Dot(velocity, dir), kept = MathF.Max(0f, along) * dt;
        if (kept < len)
        {
            _speed *= kept / len;
            _knock *= kept / len;
            step *= kept / len;
        }
        // Un oggetto molto più pesante dell'auto la respinge: la velocità che torna indietro diventa rimbalzo
        if (along < 0f && velocity.Length() > BounceMinImpact * BounceRestitution)
        {
            _knock += new Vector3(velocity.X, 0f, velocity.Y);
            if (_knock.Length() > BounceMax) _knock = Vector3.Normalize(_knock) * BounceMax;
        }
        return step;
    }

    /// <summary>Sta scivolando di traverso in modo visibile.</summary>
    public bool Sliding => MathF.Abs(DriftAngle) > 0.15f;

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

    /// <summary>Un cerchio di raggio <paramref name="r"/> nel punto tocca la carrozzeria (il rettangolo vero)?</summary>
    public bool Blocks(Vector3 p, float r) => Box.CircleDepth(new Vector2(p.X, p.Z), r, out _) > 0f;

    /// <summary>L'auto ci sta in questa posa? (terreno libero, nessun altro veicolo)</summary>
    public bool CanStand(WorldMap world, Vector3 p, float yaw) => Fits(world, p, yaw);

    /// <summary>
    /// Guidata da qualcun altro (un autista del traffico che la sposta): la fisica è ferma e la posa arriva da
    /// <see cref="PuppetMove"/>.
    /// </summary>
    public bool Puppet;

    /// <summary>Guidata da fuori: posa, velocità (negativa in retromarcia: luci bianche) e ruote.</summary>
    public void PuppetMove(Vector3 position, float yaw, float speed, float steer, float dt, WorldMap world)
    {
        Position.X = position.X;
        Position.Z = position.Z;
        Position.Y = Noise.Lerp(Position.Y, world.GroundHeight(Position), Noise.Damp(25f, dt));
        float yawRate = dt > 0f ? Noise.WrapAngle(yaw - Yaw) / dt : 0f;
        Yaw = _travelYaw = yaw;
        _accel = dt > 0f ? (speed - _speed) / dt : 0f;
        _speed = speed;
        _steer = steer;
        _knock = Vector3.Zero;
        _yawRate = Noise.Lerp(_yawRate, yawRate, Noise.Damp(10f, dt));
        _spin += speed * dt / (WheelRadius * Scale);
        _brakeGlow = speed < -0.05f || (MathF.Abs(speed) < 0.05f) ? 1f : MathF.Max(0f, _brakeGlow - dt * 4f);
        float hF = world.GroundHeight(Position + Forward * 0.6f), hB = world.GroundHeight(Position - Forward * 0.6f);
        _slopePitch = Noise.Lerp(_slopePitch, Math.Clamp(MathF.Atan2(hB - hF, 1.2f), -0.4f, 0.4f), Noise.Damp(12f, dt));
        float hR = world.GroundHeight(Position + Side * 0.4f), hL = world.GroundHeight(Position - Side * 0.4f);
        _slopeRoll = Noise.Lerp(_slopeRoll, Math.Clamp(MathF.Atan2(hR - hL, 0.8f), -0.3f, 0.3f), Noise.Damp(12f, dt));
    }

    /// <summary>L'auto (due cerchi, muso e coda) sta tutta su terreno libero in questa posa?</summary>
    private bool Fits(WorldMap world, Vector3 p, float yaw)
    {
        Vector3 f = Dir(yaw) * HullOffset;
        return world.IsWalkable(p + f, HullRadius) && world.IsWalkable(p - f, HullRadius)
            // mai dentro un altro veicolo (rettangoli veri: i cerchi lascerebbero sporgere gli angoli)
            && !(Traffic.Current?.Overlaps(BoxAt(p, yaw), this) ?? false)
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
            // Dentro un altro veicolo ci si può solo allontanare, mai entrare di più
            if (Traffic.Current is { } t && t.Depth(BoxAt(Position + step, newYaw), this) > t.Depth(Box, this) + 1e-4f) return 0f;
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
        if (Puppet) return; // la guida qualcun altro (vedi PuppetMove)
        float maxSpeed = slow ? MathF.Min(SlowSpeed, _topSpeed) : _topSpeed;

        // --- Comandi: sterzo e gas. Niente aiuti: l'auto va dove la si guida.
        float wantSpeed = 0f;
        bool coasting = false;
        // Dopo l'inversione a U lo sterzo tenuto per farla non conta finché non lo si rilascia (altrimenti l'auto,
        // finito il giro, continuerebbe a girare)
        if (_steerLatch != 0f && MathF.Sign(drive.X) != _steerLatch) _steerLatch = 0f;
        if (_steerLatch != 0f) drive.X = 0f;
        _steerIn = Math.Clamp(drive.X, -1f, 1f);
        _gasIn = Math.Clamp(drive.Y, -1f, 1f);
        bool handbrake = Handbrake;
        // In derapata si sterza (e controsterza) di più: è così che la si guida
        float limit = ManualSteerLimit;
        if (handbrake || Sliding) limit = MathF.Max(limit, DriftSteer);
        float wantSteer = -_steerIn * limit; // sterzo a destra = rotazione oraria = yaw che diminuisce
        if (_gasIn > 0.1f)
        {
            // Gas dosabile (grilletto del controller; i tasti danno sempre tutto gas): a metà corsa, metà velocità.
            // Oltre quella velocità si rallenta da soli, senza frenare.
            wantSpeed = maxSpeed * Math.Clamp((_gasIn - 0.1f) / 0.8f, 0f, 1f);
            if (_speed > wantSpeed) coasting = true;
        }
        else if (_gasIn < -0.1f)
        {
            // Prima frena; la retro solo con una nuova pressione da fermi (tenendo premuto dopo la frenata si resta fermi)
            if (_speed > 0.3f) _brakeToStop = true;
            wantSpeed = _speed > 0.3f || _brakeToStop ? 0f : -ReverseSpeed;
        }
        else coasting = true; // senza gas l'auto rallenta da sola, senza accendere gli stop
        if (_gasIn >= -0.1f) _brakeToStop = false;
        if (handbrake) { wantSpeed = 0f; coasting = true; }
        if (_stun > 0f) { _stun -= dt; wantSpeed = 0f; coasting = true; } // appena rimbalzati: il motore non spinge

        // --- Ruote che sterzano progressivamente, accelerazione e frenata lineari
        // Le ruote arrivano a fine corsa in un tempo che cresce con la velocità: pronte da fermi, calme in velocità
        bool turningIn = MathF.Abs(wantSteer) > MathF.Abs(_steer) && wantSteer * _steer >= 0f;
        float ramp = Noise.Lerp(SteerRampSlow, SteerRampFast, MathF.Min(MathF.Abs(_speed) / FastSpeed, 1f));
        float steerRate = turningIn ? ManualSteerLimit / ramp : MathF.Max(MathF.Abs(_steer), ManualSteerLimit) / SteerReturn;
        if (handbrake || Sliding) steerRate = MathF.Max(steerRate, DriftSteerRate);
        _steer = MoveTowards(_steer, wantSteer, steerRate * dt);
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
            if (handbrake) _speed = MoveTowards(_speed, 0f, HandbrakeDecel * dt);
            // Di traverso le gomme strisciano e frenano, tanto più quanto è ampio l'angolo
            _speed = MoveTowards(_speed, 0f, MathF.Abs(MathF.Sin(DriftAngle)) * DriftScrub * dt);
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

        // --- Freno a mano: il posteriore perde aderenza subito (vedi la fisica qui sotto). Se entro mezzo secondo si
        // preme anche indietro: inversione a U.
        if (handbrake && !_prevHandbrake && _manoeuvre == Manoeuvre.None && _speed > DriftMinSpeed) _decide = 0f;
        _prevHandbrake = handbrake;
        if (_decide >= 0f)
        {
            _decide += dt;
            float side = MathF.Abs(_steerIn) > 0.3f ? MathF.Sign(_steerIn) : 0f;
            if (!handbrake || _speed <= DriftMinSpeed || _decide > DecideWindow) _decide = -1f;
            else if (_gasIn < -0.3f)
            {
                _decide = -1f;
                StartUTurn(side);
            }
        }

        float yawRate, newYaw;
        Vector3? railStep = null;
        if (_manoeuvre == Manoeuvre.UTurn && _line == null) _manoeuvre = Manoeuvre.None;
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
            _grip = GripFull;

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
        else
        {
            // Guida (e derapata): modello a bicicletta, la rotazione dipende dalla velocità (niente rotazioni sul posto).
            // Aderenza: crolla col freno a mano in velocità, poi torna piano (uscita morbida dalla derapata)
            float gripTarget = handbrake && _speed > DriftMinSpeed ? GripDrift : GripFull;
            if (MathF.Abs(_speed) < 1f) gripTarget = GripFull;
            _grip = Noise.Lerp(_grip, gripTarget, Noise.Damp(gripTarget < _grip ? 12f : GripRecover, dt));
            float slip = Math.Clamp((GripFull - _grip) / (GripFull - GripDrift), 0f, 1f);
            // A ruote bloccate le anteriori non direzionano: lo sterzo quasi non conta
            float gripYawRate = _speed * MathF.Tan(_steer) / WheelBase * (WheelsLocked ? LockedSteer : 1f);
            yawRate = gripYawRate * (1f + DriftYawBoost * slip);
            // Senza aderenza la rotazione in più ha un tetto: si va di traverso senza girarsi su se stessi
            float yawCap = MathF.Max(MathF.Abs(gripYawRate), DriftYawMax);
            yawRate = Math.Clamp(yawRate, -yawCap, yawCap);
            newYaw = Noise.WrapAngle(Yaw + yawRate * dt);
            // La direzione di marcia insegue il muso con la rapidità data dall'aderenza
            _travelYaw = Noise.WrapAngle(_travelYaw + Noise.WrapAngle(newYaw - _travelYaw) * (1f - MathF.Exp(-_grip * dt)));
            // Traverso massimo: oltre, la marcia viene trascinata dal muso
            float drift = Noise.WrapAngle(newYaw - _travelYaw);
            if (MathF.Abs(drift) > MaxDrift) _travelYaw = Noise.WrapAngle(newYaw - MathF.Sign(drift) * MaxDrift);
            yawRate = Noise.WrapAngle(newYaw - Yaw) / MathF.Max(dt, 1e-4f);
        }
        // Il rimbalzo di un urto si somma alla marcia e si smorza da solo
        float knock = _knock.Length();
        _knock = knock > KnockFriction * dt ? _knock * ((knock - KnockFriction * dt) / knock) : Vector3.Zero;
        Vector3 step = (railStep ?? Dir(_travelYaw) * (_speed * dt)) + _knock * dt;
        step = PushObstacles(world, fx, step, newYaw, dt);
        Vector3 from = Position;
        float moved = step.LengthSquared() > 0f ? Move(world, step, newYaw) : 0f;
        _yawRate = Noise.Lerp(_yawRate, yawRate, Noise.Damp(10f, dt));

        // Urti: la velocità diventa quella davvero percorsa. Contro un albero preso in pieno si azzera; strisciando
        // lungo un ostacolo resta solo la parte che si è mossa. (Prima si scalava solo tenendo il gas: lasciandolo,
        // l'auto restava ferma con la velocità di prima "in memoria" e, appena libera, ripartiva da sola.)
        float expected = step.Length(); // il passo tentato (già ridotto da eventuali oggetti spinti)
        if (expected > 1e-6f && moved < expected * 0.999f)
        {
            _speed *= moved / expected;
            _knock *= moved / expected;
            // Urto forte contro un ostacolo fisso: l'auto rimbalza via (e un'inversione a U in corso finisce lì)
            if (dt > 0f && Bounce(world, fx, from + step, newYaw, step / dt) && _manoeuvre == Manoeuvre.UTurn)
            {
                _manoeuvre = Manoeuvre.None;
                _line = null;
                _speed = 0f;
                _travelYaw = Yaw;
            }
        }

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
        _model.Collect(items, root, body, _spin, _steer, Headlights, BrakeLight, _speed < -0.05f);
    }

    public void Dispose() => _model.Dispose();
}
