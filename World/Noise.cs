namespace Roads.World;

public static class Noise
{
    public static uint Hash(int x, int y, int salt)
    {
        unchecked
        {
            uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)salt * 2246822519u;
            h = (h ^ (h >> 13)) * 1274126177u;
            return h ^ (h >> 16);
        }
    }

    /// <summary>Valore pseudo-casuale deterministico in [0,1).</summary>
    public static float Hash01(int x, int y, int salt) => (Hash(x, y, salt) & 0xFFFFFF) / 16777216f;

    public static float Smooth(float edge0, float edge1, float x)
    {
        float t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>Fattore di interpolazione esponenziale indipendente dal framerate.</summary>
    public static float Damp(float rate, float dt) => 1f - MathF.Exp(-rate * dt);

    public static float WrapAngle(float a)
    {
        while (a > MathF.PI) a -= MathF.Tau;
        while (a < -MathF.PI) a += MathF.Tau;
        return a;
    }
}
