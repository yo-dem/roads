using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// Le mesh dell'automobilina (scocca di un colore, fari, stop, luci di retromarcia, ruota): una per colore, condivisa da
/// tutte le auto che la usano. Il modello guarda verso +Z locale, a scala 1 (la scala la mette chi lo disegna).
/// </summary>
public sealed class CarModel : IDisposable
{
    public const float WheelRadius = 0.2f;
    public const float WheelX = 0.47f;  // distanza delle ruote dal centro: sporgono un po' dalla scocca
    public const float WheelWidth = 0.22f;

    public readonly Mesh Body, HeadLights, TailLights, ReverseLights, Wheel;

    public CarModel(GL gl, Vector3 paint, int seed = 5)
    {
        var glass = new Vector3(0.36f, 0.58f, 0.74f);
        var trim = new Vector3(0.88f, 0.86f, 0.80f);
        var dark = new Vector3(0.20f, 0.18f, 0.18f);

        var mb = new MeshBuilder { ColorRng = new Random(seed), ColorJitter = 0.02f };
        // Scocca e abitacolo come tronchi di piramide: sagoma tondeggiante ma sfaccettata
        Frustum(mb, 0.17f, 0.52f, new(0.47f, 0.80f), new(0.45f, 0.76f), 0f, paint, null);
        Frustum(mb, 0.52f, 0.86f, new(0.41f, 0.46f), new(0.33f, 0.32f), -0.1f, paint, glass);
        // Paraurti, griglia, maniglie, tettuccio
        mb.AddBox(new Vector3(0, 0.22f, 0.83f), new Vector3(0.92f, 0.12f, 0.08f), trim);
        mb.AddBox(new Vector3(0, 0.22f, -0.83f), new Vector3(0.92f, 0.12f, 0.08f), trim);
        mb.AddBox(new Vector3(0, 0.36f, 0.79f), new Vector3(0.3f, 0.09f, 0.03f), dark);
        mb.AddBox(new Vector3(0, 0.875f, -0.1f), new Vector3(0.5f, 0.03f, 0.5f), trim);
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.465f, 0.44f, 0.05f), new Vector3(0.03f, 0.03f, 0.12f), trim);
        // Cornice nera attorno agli stop, perché non si confondano col colore della scocca
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.31f, 0.41f, -0.79f), new Vector3(0.18f, 0.12f, 0.025f), new Vector3(0.05f, 0.05f, 0.05f));
        // Tubo di scarico
        mb.Transform = Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0.28f, 0.2f, -0.92f);
        mb.AddPrism(Vector3.Zero, 0.045f, 0.045f, 0.12f, 6, dark);
        mb.Transform = Matrix4x4.Identity;
        Body = mb.Build(gl);

        mb = new MeshBuilder();
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.29f, 0.41f, 0.795f), new Vector3(0.16f, 0.1f, 0.03f), new Vector3(1f, 0.95f, 0.7f));
        HeadLights = mb.Build(gl);

        mb = new MeshBuilder();
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.31f, 0.41f, -0.795f), new Vector3(0.14f, 0.08f, 0.03f), new Vector3(1f, 0.45f, 0.1f));
        TailLights = mb.Build(gl);

        // Luci di retromarcia: piccole, bianche, accanto agli stop (visibili solo in retro)
        mb = new MeshBuilder();
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.15f, 0.41f, -0.8f), new Vector3(0.07f, 0.06f, 0.03f), new Vector3(1f, 1f, 0.95f));
        ReverseLights = mb.Build(gl);

        // Ruota centrata nell'origine con asse lungo X: larga, sporge dai fianchi e si vede di lato
        mb = new MeshBuilder { Transform = Matrix4x4.CreateRotationZ(MathF.PI / 2f) };
        mb.AddPrism(new Vector3(0, -WheelWidth * 0.5f, 0), WheelRadius, WheelRadius, WheelWidth, 10, dark);
        mb.AddPrism(new Vector3(0, -WheelWidth * 0.5f - 0.01f, 0), 0.09f, 0.09f, WheelWidth + 0.02f, 6, trim);
        Wheel = mb.Build(gl);
    }

    /// <summary>
    /// Aggiunge l'auto da disegnare. <paramref name="root"/> = posa a terra (scala compresa), <paramref name="body"/> =
    /// posa della scocca (con rollio e beccheggio), <paramref name="spin"/> = rotazione delle ruote,
    /// <paramref name="steer"/> = angolo delle ruote anteriori, <paramref name="brake"/> = intensità degli stop (0..2).
    /// </summary>
    public void Collect(List<DrawItem> items, Matrix4x4 root, Matrix4x4 body, float spin, float steer,
                        bool headlights, float brake, bool reversing)
    {
        items.Add(new DrawItem(Body, body));
        // Fari spenti: solo il vetro chiaro; accesi: pieni. Coi fari accesi anche le luci posteriori restano accese.
        items.Add(new DrawItem(HeadLights, body, headlights ? 1f : 0.2f, CastShadow: !headlights));
        // Luci di posizione con i fari accesi; in frenata molto più intense, col freno a mano al massimo
        float tail = headlights ? 0.8f : 0.2f;
        items.Add(new DrawItem(TailLights, body, MathF.Max(tail, 1.7f * brake)));
        if (reversing) items.Add(new DrawItem(ReverseLights, body, 1f, CastShadow: false));

        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
            {
                float s = sz > 0 ? steer : 0f;
                items.Add(new DrawItem(Wheel, Matrix4x4.CreateRotationX(spin) * Matrix4x4.CreateRotationY(s)
                                              * Matrix4x4.CreateTranslation(sx * WheelX, WheelRadius, sz * 0.5f) * root));
            }
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

    public void Dispose()
    {
        Body.Dispose();
        HeadLights.Dispose();
        TailLights.Dispose();
        ReverseLights.Dispose();
        Wheel.Dispose();
    }
}
