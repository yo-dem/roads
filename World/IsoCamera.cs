using System.Numerics;

namespace Roads.World;

/// <summary>
/// Telecamera dall'alto a 3/4 (stile Animal Crossing) che segue morbidamente il giocatore.
/// La visuale standard è quella "di guida": dietro l'auto, bassa e vicina, come in un gioco di guida.
/// La visuale lontana è dall'alto; il passaggio tra le due è graduale.
/// </summary>
public sealed class IsoCamera
{
    // Vista dall'alto: valori scelti dal giocatore (Q/R, tasto destro, rotella)
    public float Yaw = 0.35f;
    public float Pitch = 0.82f;
    public float Distance = 22f;
    public Vector3 Target;

    // Vista di inseguimento
    private const float ChasePitch = 0.22f;   // poco inclinata: si vede una bella fascia di cielo
    private const float ChaseDistance = 7.5f;
    private const float ChaseLift = 0.6f;     // il punto guardato sta un po' sopra l'auto
    private float _chaseYaw;

    // Inseguimento a piedi: alle spalle del personaggio come dietro a una "mini auto", più vicina e un po' più alta
    private const float WalkPitch = 0.3f;
    private const float WalkDistance = 4.3f;
    private const float WalkLift = 0.5f;

    /// <summary>Quanto la vista di inseguimento è quella a piedi (0 = dietro l'auto, 1 = dietro al personaggio).</summary>
    public float Walk { get; private set; }
    private bool _wasChase;

    /// <summary>Quanto si è nella vista di inseguimento (0 = dall'alto, 1 = dietro l'auto).</summary>
    public float Chase { get; private set; }

    public float EffectiveYaw => Yaw + Noise.WrapAngle(_chaseYaw - Yaw) * Chase;
    public float EffectivePitch => Noise.Lerp(Pitch, Noise.Lerp(ChasePitch, MathF.Max(_walkPitch, 0f), Walk), Chase);

    // A piedi la visuale si gira col mouse attorno al personaggio
    private float _walkPitch = WalkPitch;

    // Abbassando la visuale a piedi oltre la rasoterra la camera scende quasi a terra, si accosta al personaggio e
    // guarda in su, verso il cielo
    private const float MinWalkPitch = -0.95f;
    private const float LookUpDrop = 0.38f;    // di quanto scende sotto il punto guardato (che sta a WalkLift dai piedi)
    private const float LookUpCloser = 0.45f;  // quanto si accorcia la distanza, guardando del tutto in su
    private const float LookUpGain = 0.85f;     // angolo verso l'alto per radiante di mouse oltre la rasoterra

    /// <summary>Quanto si guarda in su a piedi (radianti oltre la rasoterra; 0 = visuale normale).</summary>
    public float LookUp => Chase * Walk * MathF.Max(0f, -_walkPitch);

    /// <summary>La camera non scende sotto questa quota (il terreno sotto di lei, la decide il gioco).</summary>
    public float Floor = float.MinValue;

    /// <summary>
    /// Ruota la visuale a piedi attorno al personaggio: <paramref name="dYaw"/> in orizzontale, <paramref name="dPitch"/>
    /// in verticale (da quasi rasoterra, per guardare il paesaggio, a quasi dall'alto).
    /// </summary>
    public void Orbit(float dYaw, float dPitch)
    {
        _chaseYaw += dYaw;
        _walkPitch = Math.Clamp(_walkPitch + dPitch, MinWalkPitch, 1.25f);
    }
    public float EffectiveDistance => _eye is { } eye
        ? Vector3.Distance(eye, Target)
        : Noise.Lerp(Distance, Noise.Lerp(ChaseDistance, WalkDistance, Walk), Chase)
          * (1f - LookUpCloser * Noise.Smooth(0f, -MinWalkPitch, LookUp));

    // Visuale fissa (dentro il vagone): occhio e punto guardato li decide il gioco, fotogramma per fotogramma
    private Vector3? _eye;

    /// <summary>Visuale fissa: la camera sta in <paramref name="eye"/> e guarda <paramref name="look"/>.</summary>
    public void Fix(Vector3 eye, Vector3 look)
    {
        _eye = eye;
        Target = look;
    }

    /// <summary>Si torna alla visuale normale: riparte dalla direzione in cui si guardava, senza scatti.</summary>
    public void Unfix()
    {
        if (_eye is not { } eye) return;
        Vector3 d = eye - Target;
        _chaseYaw = Yaw = MathF.Atan2(d.X, d.Z);
        _eye = null;
    }

    public bool Fixed => _eye != null;

    public Vector3 Position
    {
        get
        {
            if (_eye is { } eye) return eye;
            var p = ClipPoint ?? DesiredPosition;
            return p with { Y = MathF.Max(p.Y, Floor) };
        }
    }

    /// <summary>Dove starebbe la camera senza muri.</summary>
    public Vector3 DesiredPosition => _eye ?? Target + new Vector3(
        MathF.Sin(EffectiveYaw) * MathF.Cos(EffectivePitch),
        MathF.Sin(EffectivePitch),
        MathF.Cos(EffectiveYaw) * MathF.Cos(EffectivePitch)) * EffectiveDistance
        - new Vector3(0f, LookUpDrop * Noise.Smooth(0f, 0.3f, LookUp), 0f);

    /// <summary>
    /// Il punto guardato: di solito il giocatore; guardando in su si alza sopra di lui, tanto più quanto più si abbassa
    /// la visuale col mouse, così il cielo riempie lo schermo.
    /// </summary>
    public Vector3 LookAt
    {
        get
        {
            float up = LookUp;
            if (_eye != null || up <= 0f) return Target;
            var p = Position;
            float flat = MathF.Max(0.5f, new Vector2(Target.X - p.X, Target.Z - p.Z).Length());
            return Target + new Vector3(0f, flat * MathF.Tan(MathF.Min(up * LookUpGain, 0.95f)), 0f);
        }
    }

    /// <summary>
    /// Se un muro sta tra il giocatore e la camera, il gioco la avvicina qui (sulla linea giocatore → camera); null se la
    /// visuale è libera.
    /// </summary>
    public Vector3? ClipPoint;

    /// <summary>Direzioni sul piano orizzontale, usate per il movimento relativo alla camera.</summary>
    public Vector3 Forward
    {
        get
        {
            if (_eye is { } eye)
            {
                var d = new Vector3(Target.X - eye.X, 0f, Target.Z - eye.Z);
                if (d.LengthSquared() > 1e-6f) return Vector3.Normalize(d);
            }
            return new(-MathF.Sin(EffectiveYaw), 0, -MathF.Cos(EffectiveYaw));
        }
    }

    public Vector3 Right
    {
        get
        {
            var f = Forward;
            return new(-f.Z, 0, f.X);
        }
    }

    /// <summary>Posiziona la camera subito, senza transizione (all'avvio).</summary>
    public void Snap(Vector3 follow, bool chase, float heading)
    {
        Target = follow + new Vector3(0, chase ? ChaseLift : 0.8f, 0);
        _chaseYaw = heading + MathF.PI;
        Chase = chase ? 1f : 0f;
        _wasChase = chase;
    }

    /// <param name="target">Punto da seguire.</param>
    /// <param name="chase">Vero per la visuale dietro l'auto, falso per quella lontana.</param>
    /// <param name="heading">Direzione dell'auto (yaw), usata per mettersi alle sue spalle.</param>
    /// <param name="walk">Vero per la visuale vicina a piedi (ha senso solo quando <paramref name="chase"/> è falso).</param>
    /// <param name="follow">A piedi: quanto la camera torna alle spalle del personaggio (0 = resta dov'è, 1 = piano ma deciso).</param>
    public void Update(float dt, Vector3 target, bool chase, float heading, bool walk = false, float follow = 0f)
    {
        Walk = Noise.Lerp(Walk, walk ? 1f : 0f, Noise.Damp(3f, dt));
        // Entrando nell'inseguimento si parte dall'angolo attuale, così non ci sono scatti
        if (Chase <= 0.001f) _chaseYaw = Yaw;
        // Uscendo, la vista dall'alto eredita la direzione di guida: la camera si alza senza girare
        if (!chase && _wasChase)
        {
            Yaw = EffectiveYaw;
            _chaseYaw = Yaw;
        }
        _wasChase = chase;
        Chase = Noise.Lerp(Chase, chase ? 1f : 0f, Noise.Damp(chase ? 3f : 2.5f, dt));
        if (Chase < 0.001f) Chase = 0f;

        // Alle spalle dell'auto: la camera sta dalla parte opposta al muso
        float behind = heading + MathF.PI;
        // In auto sempre alle spalle; a piedi la gira il mouse e torna dietro al personaggio solo quanto dice follow
        if (!walk) _chaseYaw += Noise.WrapAngle(behind - _chaseYaw) * Noise.Damp(4f, dt);
        else if (follow > 0f) _chaseYaw += Noise.WrapAngle(behind - _chaseYaw) * Noise.Damp(1.6f * follow, dt);

        Vector3 aim = target + new Vector3(0, Noise.Lerp(0.8f, Noise.Lerp(ChaseLift, WalkLift, Walk), Chase), 0);
        Target = Vector3.Lerp(Target, aim, Noise.Damp(Noise.Lerp(5f, 9f, Chase), dt));
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, LookAt, Vector3.UnitY);
}
