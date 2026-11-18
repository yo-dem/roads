using System.Numerics;

namespace Roads.World;

/// <summary>
/// Telecamera dall'alto a 3/4 (stile Animal Crossing) che segue morbidamente il giocatore.
/// La visuale standard è quella "di guida": dietro l'auto, bassa e vicina, come in un gioco di guida.
/// La visuale lontana è dall'alto; il passaggio tra le due è graduale.
/// </summary>
public sealed class IsoCamera
{
    // Vista dall'alto: valori scelti dal giocatore (Q/E, tasto destro, rotella)
    public float Yaw = 0.35f;
    public float Pitch = 0.82f;
    public float Distance = 22f;
    public Vector3 Target;

    // Vista di inseguimento
    private const float ChasePitch = 0.22f;   // poco inclinata: si vede una bella fascia di cielo
    private const float ChaseDistance = 7.5f;
    private const float ChaseLift = 0.6f;     // il punto guardato sta un po' sopra l'auto
    private float _chaseYaw;
    private bool _wasChase;

    /// <summary>Quanto si è nella vista di inseguimento (0 = dall'alto, 1 = dietro l'auto).</summary>
    public float Chase { get; private set; }

    public float EffectiveYaw => Yaw + Noise.WrapAngle(_chaseYaw - Yaw) * Chase;
    public float EffectivePitch => Noise.Lerp(Pitch, ChasePitch, Chase);
    public float EffectiveDistance => Noise.Lerp(Distance, ChaseDistance, Chase);

    public Vector3 Position => Target + new Vector3(
        MathF.Sin(EffectiveYaw) * MathF.Cos(EffectivePitch),
        MathF.Sin(EffectivePitch),
        MathF.Cos(EffectiveYaw) * MathF.Cos(EffectivePitch)) * EffectiveDistance;

    /// <summary>Direzioni sul piano orizzontale, usate per il movimento relativo alla camera.</summary>
    public Vector3 Forward => new(-MathF.Sin(EffectiveYaw), 0, -MathF.Cos(EffectiveYaw));
    public Vector3 Right => new(MathF.Cos(EffectiveYaw), 0, -MathF.Sin(EffectiveYaw));

    /// <summary>Posiziona la camera subito, senza transizione (all'avvio).</summary>
    public void Snap(Vector3 follow, bool chase, float heading)
    {
        Target = follow + new Vector3(0, chase ? ChaseLift : 0.8f, 0);
        _chaseYaw = heading + MathF.PI;
        Chase = chase ? 1f : 0f;
        _wasChase = chase;
    }

    /// <param name="follow">Punto da seguire.</param>
    /// <param name="chase">Vero per la visuale dietro l'auto, falso per quella lontana.</param>
    /// <param name="heading">Direzione dell'auto (yaw), usata per mettersi alle sue spalle.</param>
    public void Update(float dt, Vector3 follow, bool chase, float heading)
    {
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
        _chaseYaw += Noise.WrapAngle(behind - _chaseYaw) * Noise.Damp(4f, dt);

        Vector3 aim = follow + new Vector3(0, Noise.Lerp(0.8f, ChaseLift, Chase), 0);
        Target = Vector3.Lerp(Target, aim, Noise.Damp(Noise.Lerp(5f, 9f, Chase), dt));
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);
}
