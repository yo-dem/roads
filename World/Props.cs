using System.Numerics;
using Roads.Rendering;

namespace Roads.World;

/// <summary>Oggetti di scena low poly. Tutte le posizioni sono nel sistema della mesh dell'isola.</summary>
public static class Props
{
    private static readonly Vector3 Trunk = new(0.45f, 0.28f, 0.15f);
    private static readonly Vector3 Snow = new(0.95f, 0.97f, 1.0f);

    private static float R(Random rng, float min, float max) => min + rng.NextSingle() * (max - min);

    /// <summary>Pino a tre coni sovrapposti.</summary>
    public static void Pine(MeshBuilder mb, Random rng, Vector3 p, float s, Vector3 green, bool snowy = false)
    {
        mb.AddPrism(p, 0.17f * s, 0.12f * s, 0.7f * s, 6, Trunk);
        float y = 0.5f * s;
        float r = 1.15f * s;
        float rot = rng.NextSingle() * MathF.Tau;
        for (int i = 0; i < 3; i++)
        {
            float h = (1.5f - i * 0.25f) * s;
            mb.AddCone(p + new Vector3(0, y, 0), r, h, 7, green * (1f + i * 0.07f), rot + i * 0.45f);
            if (snowy)
                mb.AddCone(p + new Vector3(0, y + h * 0.58f, 0), r * 0.45f, h * 0.44f, 7, Snow, rot + i * 0.45f);
            y += h * 0.5f;
            r *= 0.74f;
        }
    }

    /// <summary>Albero a cono singolo, alto e chiaro (come quelli dello screenshot).</summary>
    public static void ConeTree(MeshBuilder mb, Random rng, Vector3 p, float s, Vector3 green)
    {
        mb.AddPrism(p, 0.15f * s, 0.11f * s, 0.6f * s, 6, Trunk);
        mb.AddCone(p + new Vector3(0, 0.45f * s, 0), 0.85f * s, 3.4f * s, 6, green, rng.NextSingle() * MathF.Tau);
    }

    /// <summary>Albero dalla chioma tondeggiante (fiorito o autunnale).</summary>
    public static void RoundTree(MeshBuilder mb, Random rng, Vector3 p, float s, Vector3[] palette)
    {
        mb.AddPrism(p, 0.22f * s, 0.13f * s, 1.9f * s, 6, Trunk);

        // Due rami inclinati
        Matrix4x4 old = mb.Transform;
        for (int i = 0; i < 2; i++)
        {
            float yaw = rng.NextSingle() * MathF.Tau;
            mb.Transform = Matrix4x4.CreateRotationZ(i == 0 ? 0.7f : -0.6f) * Matrix4x4.CreateRotationY(yaw)
                           * Matrix4x4.CreateTranslation(p + new Vector3(0, 1.0f * s, 0)) * old;
            mb.AddPrism(Vector3.Zero, 0.1f * s, 0.06f * s, 1.0f * s, 5, Trunk);
        }
        mb.Transform = old;

        float oldJ = mb.ColorJitter;
        mb.ColorJitter = 0.12f;
        mb.AddIcosphere(p + new Vector3(0, 2.7f * s, 0), new Vector3(1.3f * s), 1, palette[0], rng, 0.1f);
        int extra = 3;
        for (int i = 0; i < extra; i++)
        {
            float a = MathF.Tau * i / extra + R(rng, -0.4f, 0.4f);
            var off = new Vector3(MathF.Cos(a) * 1.1f * s, R(rng, 1.9f, 2.6f) * s, MathF.Sin(a) * 1.1f * s);
            mb.AddIcosphere(p + off, new Vector3(R(rng, 0.7f, 0.95f) * s), 1, palette[rng.Next(palette.Length)], rng, 0.12f);
        }
        mb.ColorJitter = oldJ;
    }

    public static void Rock(MeshBuilder mb, Random rng, Vector3 p, float s, Vector3 color)
    {
        Matrix4x4 old = mb.Transform;
        mb.Transform = Matrix4x4.CreateRotationY(rng.NextSingle() * MathF.Tau) * Matrix4x4.CreateTranslation(p) * old;
        var radii = new Vector3(R(rng, 0.85f, 1.15f), R(rng, 0.6f, 0.85f), R(rng, 0.8f, 1.1f)) * s;
        mb.AddIcosphere(new Vector3(0, radii.Y * 0.35f, 0), radii, 0, color * R(rng, 0.9f, 1.1f), rng, 0.2f);
        mb.Transform = old;
    }

    public static void Bush(MeshBuilder mb, Random rng, Vector3 p, float s, Vector3 green)
    {
        int n = rng.Next(2, 4);
        for (int i = 0; i < n; i++)
        {
            var off = new Vector3(R(rng, -0.3f, 0.3f), 0, R(rng, -0.3f, 0.3f)) * s;
            float r = R(rng, 0.38f, 0.5f) * s;
            mb.AddIcosphere(p + off + new Vector3(0, r * 0.7f, 0), new Vector3(r, r * 0.85f, r), 0, green * R(rng, 0.9f, 1.12f), rng, 0.15f);
        }
    }

    public static void Flowers(MeshBuilder mb, Random rng, Vector3 p, Vector3 color)
    {
        var stem = new Vector3(0.3f, 0.55f, 0.2f);
        int n = rng.Next(3, 6);
        for (int i = 0; i < n; i++)
        {
            var q = p + new Vector3(R(rng, -0.35f, 0.35f), 0, R(rng, -0.35f, 0.35f));
            float h = R(rng, 0.18f, 0.3f);
            mb.AddBox(q + new Vector3(0, h * 0.5f, 0), new Vector3(0.03f, h, 0.03f), stem);
            mb.AddIcosphere(q + new Vector3(0, h + 0.04f, 0), new Vector3(0.085f, 0.06f, 0.085f), 0, color);
            mb.AddIcosphere(q + new Vector3(0, h + 0.07f, 0), new Vector3(0.035f), 0, new Vector3(1f, 0.85f, 0.25f));
        }
    }

    public static void Mushroom(MeshBuilder mb, Random rng, Vector3 p, float s)
    {
        mb.AddPrism(p, 0.07f * s, 0.06f * s, 0.2f * s, 6, new Vector3(0.95f, 0.9f, 0.8f));
        mb.AddCone(p + new Vector3(0, 0.17f * s, 0), 0.2f * s, 0.16f * s, 7, new Vector3(0.85f, 0.18f, 0.12f), rng.NextSingle());
    }

    /// <summary>Anello di pietre e legna; le fiamme sono una mesh separata animata.</summary>
    public static void Campfire(MeshBuilder mb, Random rng, Vector3 p)
    {
        var stone = new Vector3(0.62f, 0.62f, 0.66f);
        for (int i = 0; i < 9; i++)
        {
            float a = MathF.Tau * i / 9;
            Rock(mb, rng, p + new Vector3(MathF.Cos(a), 0, MathF.Sin(a)) * 0.62f, 0.2f, stone);
        }

        Matrix4x4 old = mb.Transform;
        for (int i = 0; i < 3; i++)
        {
            mb.Transform = Matrix4x4.CreateRotationZ(0.35f) * Matrix4x4.CreateRotationY(i * MathF.Tau / 3f)
                           * Matrix4x4.CreateTranslation(p + new Vector3(0, 0.12f, 0)) * old;
            mb.AddBox(new Vector3(-0.2f, 0, 0), new Vector3(0.6f, 0.12f, 0.12f), new Vector3(0.42f, 0.25f, 0.12f));
        }
        mb.Transform = old;
        mb.AddDisc(p + new Vector3(0, 0.06f, 0), 0.3f, 7, new Vector3(0.3f, 0.2f, 0.18f));
    }

    public static Mesh BuildFlame(Silk.NET.OpenGL.GL gl)
    {
        var mb = new MeshBuilder();
        mb.AddCone(Vector3.Zero, 0.26f, 0.75f, 6, new Vector3(1f, 0.5f, 0.1f));
        mb.AddCone(new Vector3(0, 0.02f, 0), 0.15f, 0.95f, 5, new Vector3(1f, 0.85f, 0.25f), 0.5f);
        return mb.Build(gl);
    }
}
