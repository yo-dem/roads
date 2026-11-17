using System.Numerics;

namespace Roads.World;

/// <summary>Telecamera dall'alto a 3/4 (stile Animal Crossing) che segue morbidamente il giocatore.</summary>
public sealed class IsoCamera
{
    public float Yaw = 0.35f;
    public float Pitch = 0.82f;
    public float Distance = 22f;
    public Vector3 Target;

    public Vector3 Position => Target + new Vector3(
        MathF.Sin(Yaw) * MathF.Cos(Pitch),
        MathF.Sin(Pitch),
        MathF.Cos(Yaw) * MathF.Cos(Pitch)) * Distance;

    /// <summary>Direzioni sul piano orizzontale, usate per il movimento relativo alla camera.</summary>
    public Vector3 Forward => new(-MathF.Sin(Yaw), 0, -MathF.Cos(Yaw));
    public Vector3 Right => new(MathF.Cos(Yaw), 0, -MathF.Sin(Yaw));

    public void Snap(Vector3 follow) => Target = follow + new Vector3(0, 0.8f, 0);

    public void Update(float dt, Vector3 follow)
    {
        Target = Vector3.Lerp(Target, follow + new Vector3(0, 0.8f, 0), Noise.Damp(5f, dt));
    }

    public Matrix4x4 View => Matrix4x4.CreateLookAt(Position, Target, Vector3.UnitY);
}
