using System.Numerics;

namespace Roads.Characters;

/// <summary>
/// L'ingombro di un veicolo sul piano: un rettangolo orientato (centro, direzione del muso, mezza larghezza e mezza
/// lunghezza, ruote e paraurti compresi). Serve per gli urti tra veicoli: i due cerchi che bastano contro alberi e
/// muri lascerebbero sporgere gli angoli, e due auto sembrerebbero entrare l'una nell'altra.
/// </summary>
public readonly record struct VehicleBox(Vector2 C, Vector2 F, float W, float L)
{
    /// <summary>Destra del muso (x est, z sud).</summary>
    public Vector2 R => new(-F.Y, F.X);

    /// <summary>Mezza larghezza e mezza lunghezza di ogni carrozzeria (modello in scala 1.1).</summary>
    public static (float W, float L) Size(BodyStyle style) => style switch
    {
        BodyStyle.Wagon => (0.63f, 1.12f),
        BodyStyle.Sport => (0.68f, 1.06f),
        BodyStyle.Tractor => (0.74f, 0.98f),
        _ => (0.63f, 0.96f),
    };

    private float Extent(Vector2 axis) => W * MathF.Abs(Vector2.Dot(R, axis)) + L * MathF.Abs(Vector2.Dot(F, axis));

    /// <summary>
    /// I due rettangoli si sovrappongono? (separazione degli assi) Se sì, <paramref name="depth"/> è quanto, e
    /// <paramref name="normal"/> la direzione (da <paramref name="a"/> verso <paramref name="b"/>) in cui spostare b
    /// per separarli col minimo movimento.
    /// </summary>
    public static bool Overlap(in VehicleBox a, in VehicleBox b, out Vector2 normal, out float depth)
    {
        normal = Vector2.Zero;
        depth = float.MaxValue;
        Vector2 d = b.C - a.C;
        float reach = a.W + a.L + b.W + b.L;
        if (d.LengthSquared() > reach * reach) { depth = 0f; return false; }
        foreach (var axis in new[] { a.F, a.R, b.F, b.R })
        {
            float along = Vector2.Dot(d, axis);
            float pen = a.Extent(axis) + b.Extent(axis) - MathF.Abs(along);
            if (pen <= 0f) { depth = 0f; return false; }
            if (pen < depth)
            {
                depth = pen;
                normal = along < 0f ? -axis : axis;
            }
        }
        return true;
    }

    /// <summary>Quanto un cerchio entra nel rettangolo (0 se non lo tocca); <paramref name="normal"/> va dal rettangolo al cerchio.</summary>
    public float CircleDepth(Vector2 p, float r, out Vector2 normal)
    {
        Vector2 rel = p - C;
        float x = Vector2.Dot(rel, R), z = Vector2.Dot(rel, F);
        float cx = Math.Clamp(x, -W, W), cz = Math.Clamp(z, -L, L);
        if (cx != x || cz != z)
        {
            // Fuori: distanza dal punto più vicino del bordo
            Vector2 off = R * (x - cx) + F * (z - cz);
            float dist = off.Length();
            normal = dist > 1e-6f ? off / dist : F;
            return MathF.Max(0f, r - dist);
        }
        // Dentro: si esce dal lato più vicino
        float toSide = W - MathF.Abs(x), toEnd = L - MathF.Abs(z);
        normal = toSide < toEnd ? R * MathF.Sign(x == 0f ? 1f : x) : F * MathF.Sign(z == 0f ? 1f : z);
        return r + MathF.Min(toSide, toEnd);
    }
}
