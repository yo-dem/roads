using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>Le carrozzerie dei veicoli.</summary>
public enum BodyStyle
{
    /// <summary>Utilitaria a due volumi: l'auto del giocatore e la più comune nel traffico.</summary>
    Hatch,
    /// <summary>Station wagon: più lunga, col tetto che arriva fino in fondo e le barre portapacchi.</summary>
    Wagon,
    /// <summary>Sportiva: bassa e larga, abitacolo piccolo e arretrato, alettone dietro.</summary>
    Sport,
    /// <summary>Trattore: cofano stretto, cabina chiusa coi vetri, grandi ruote tassellate dietro e piccole davanti.</summary>
    Tractor,
}

/// <summary>
/// Le mesh di un veicolo (carrozzeria di un colore, fari, stop, luci di retromarcia, ruote): una per carrozzeria e
/// colore, condivisa da tutti i veicoli che la usano. Il modello guarda verso +Z locale, a scala 1 (la scala la mette
/// chi lo disegna).
/// </summary>
public sealed class CarModel : IDisposable
{
    // Ruote dell'utilitaria (le usa anche Car per i segni delle gomme)
    public const float WheelRadius = 0.2f;
    public const float WheelX = 0.47f;  // distanza delle ruote dal centro: sporgono un po' dalla scocca
    public const float WheelWidth = 0.22f;

    public BodyStyle Style { get; }
    public Vector3 Paint { get; }
    public readonly Mesh Body, HeadLights, TailLights, ReverseLights;
    private readonly List<Mesh> _wheelMeshes = new();
    /// <summary>Le ruote: mesh, centro (locale) e se sterza.</summary>
    private readonly List<(Mesh Mesh, Vector3 At, float Radius, bool Front)> _wheels = new();

    private static readonly Vector3 Glass = new(0.36f, 0.58f, 0.74f);
    private static readonly Vector3 Trim = new(0.88f, 0.86f, 0.80f);
    private static readonly Vector3 Dark = new(0.20f, 0.18f, 0.18f);
    private static readonly Vector3 Black = new(0.05f, 0.05f, 0.05f);
    private static readonly Vector3 HeadColor = new(1f, 0.95f, 0.7f);
    private static readonly Vector3 TailColor = new(1f, 0.45f, 0.1f);

    public CarModel(GL gl, Vector3 paint, BodyStyle style = BodyStyle.Hatch, int seed = 5)
    {
        Style = style;
        Paint = paint;
        var body = new MeshBuilder { ColorRng = new Random(seed), ColorJitter = 0.02f };
        var head = new MeshBuilder();
        var tail = new MeshBuilder();
        var reverse = new MeshBuilder();
        switch (style)
        {
            case BodyStyle.Wagon: BuildWagon(body, head, tail, reverse, paint); break;
            case BodyStyle.Sport: BuildSport(body, head, tail, reverse, paint); break;
            case BodyStyle.Tractor: BuildTractor(gl, body, head, tail, reverse, paint); break;
            default: BuildHatch(body, head, tail, reverse, paint); break;
        }
        Body = body.Build(gl);
        HeadLights = head.Build(gl);
        TailLights = tail.Build(gl);
        ReverseLights = reverse.Build(gl);
        if (style != BodyStyle.Tractor)
        {
            // Quattro ruote uguali, come l'utilitaria (la sportiva un filo più grandi e larghe)
            float r = style == BodyStyle.Sport ? 0.21f : WheelRadius, w = style == BodyStyle.Sport ? 0.25f : WheelWidth;
            float z = style == BodyStyle.Wagon ? 0.58f : style == BodyStyle.Sport ? 0.55f : 0.5f;
            float x = style == BodyStyle.Sport ? 0.5f : WheelX;
            var wheel = BuildWheel(gl, r, w, hub: 0.09f, lugs: 0);
            foreach (float sx in new[] { -1f, 1f })
                foreach (float sz in new[] { -1f, 1f })
                    _wheels.Add((wheel, new Vector3(sx * x, r, sz * z), r, sz > 0));
        }
    }

    // ------------------------------------------------------------------ carrozzerie

    private static void Lights(MeshBuilder head, MeshBuilder tail, MeshBuilder reverse, float y, float zFront, float zBack,
                               float headX, float tailX)
    {
        foreach (float sx in new[] { -1f, 1f })
        {
            head.AddBox(new Vector3(sx * headX, y, zFront), new Vector3(0.16f, 0.1f, 0.03f), HeadColor);
            tail.AddBox(new Vector3(sx * tailX, y, zBack), new Vector3(0.14f, 0.08f, 0.03f), TailColor);
            reverse.AddBox(new Vector3(sx * 0.15f, y, zBack - 0.005f), new Vector3(0.07f, 0.06f, 0.03f), new Vector3(1f, 1f, 0.95f));
        }
    }

    private static void Common(MeshBuilder mb, float bumperY, float front, float back, float width, float y, float tailX,
                               float exhaustY)
    {
        // Paraurti, griglia, cornici nere attorno agli stop (non si confondono col colore della scocca), scarico
        mb.AddBox(new Vector3(0, bumperY, front), new Vector3(width, 0.12f, 0.08f), Trim);
        mb.AddBox(new Vector3(0, bumperY, back), new Vector3(width, 0.12f, 0.08f), Trim);
        mb.AddBox(new Vector3(0, y - 0.05f, front - 0.04f), new Vector3(0.3f, 0.09f, 0.03f), Dark);
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * tailX, y, back + 0.04f), new Vector3(0.18f, 0.12f, 0.025f), Black);
        mb.Transform = Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0.28f, exhaustY, back - 0.09f);
        mb.AddPrism(Vector3.Zero, 0.045f, 0.045f, 0.12f, 6, Dark);
        mb.Transform = Matrix4x4.Identity;
    }

    private static void BuildHatch(MeshBuilder mb, MeshBuilder head, MeshBuilder tail, MeshBuilder reverse, Vector3 paint)
    {
        // Scocca e abitacolo come tronchi di piramide: sagoma tondeggiante ma sfaccettata
        Frustum(mb, 0.17f, 0.52f, new(0.47f, 0.80f), new(0.45f, 0.76f), 0f, paint, null);
        Frustum(mb, 0.52f, 0.86f, new(0.41f, 0.46f), new(0.33f, 0.32f), -0.1f, paint, Glass);
        mb.AddBox(new Vector3(0, 0.875f, -0.1f), new Vector3(0.5f, 0.03f, 0.5f), Trim); // tettuccio
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.465f, 0.44f, 0.05f), new Vector3(0.03f, 0.03f, 0.12f), Trim); // maniglie
        Common(mb, 0.22f, 0.83f, -0.83f, 0.92f, 0.41f, 0.31f, 0.2f);
        Lights(head, tail, reverse, 0.41f, 0.795f, -0.795f, 0.29f, 0.31f);
    }

    private static void BuildWagon(MeshBuilder mb, MeshBuilder head, MeshBuilder tail, MeshBuilder reverse, Vector3 paint)
    {
        // Più lunga; l'abitacolo arriva fino alla coda (portellone quasi verticale)
        Frustum(mb, 0.17f, 0.52f, new(0.47f, 0.96f), new(0.45f, 0.93f), 0f, paint, null);
        Frustum(mb, 0.52f, 0.86f, new(0.42f, 0.66f), new(0.36f, 0.58f), -0.25f, paint, Glass);
        // Barre portapacchi
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.3f, 0.885f, -0.25f), new Vector3(0.04f, 0.04f, 1.05f), Dark);
        foreach (float sz in new[] { -0.6f, 0.1f })
            mb.AddBox(new Vector3(0, 0.9f, sz), new Vector3(0.64f, 0.025f, 0.05f), Dark);
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.465f, 0.44f, 0.12f), new Vector3(0.03f, 0.03f, 0.12f), Trim);
        Common(mb, 0.22f, 0.99f, -0.99f, 0.92f, 0.41f, 0.31f, 0.2f);
        Lights(head, tail, reverse, 0.41f, 0.955f, -0.955f, 0.29f, 0.31f);
    }

    private static void BuildSport(MeshBuilder mb, MeshBuilder head, MeshBuilder tail, MeshBuilder reverse, Vector3 paint)
    {
        // Bassa e larga, muso lungo, abitacolo piccolo e arretrato, alettone
        Frustum(mb, 0.15f, 0.42f, new(0.51f, 0.9f), new(0.48f, 0.84f), 0f, paint, null);
        Frustum(mb, 0.42f, 0.64f, new(0.42f, 0.4f), new(0.31f, 0.2f), -0.18f, paint, Glass);
        // Cofano sagomato (presa d'aria) e strisce sul cofano
        mb.AddBox(new Vector3(0, 0.425f, 0.5f), new Vector3(0.3f, 0.02f, 0.3f), Black);
        // Alettone: due supporti e l'ala
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.3f, 0.49f, -0.76f), new Vector3(0.04f, 0.14f, 0.06f), Dark);
        mb.AddBox(new Vector3(0, 0.57f, -0.79f), new Vector3(0.9f, 0.03f, 0.16f), paint);
        Common(mb, 0.2f, 0.93f, -0.93f, 0.98f, 0.33f, 0.34f, 0.18f);
        Lights(head, tail, reverse, 0.33f, 0.895f, -0.895f, 0.32f, 0.34f);
    }

    private void BuildTractor(GL gl, MeshBuilder mb, MeshBuilder head, MeshBuilder tail, MeshBuilder reverse, Vector3 paint)
    {
        var steel = new Vector3(0.32f, 0.32f, 0.34f);
        var rim = new Vector3(0.95f, 0.78f, 0.2f); // cerchi gialli
        // Telaio basso e cofano stretto davanti, con la griglia
        mb.AddBox(new Vector3(0, 0.3f, 0.1f), new Vector3(0.36f, 0.2f, 1.5f), steel);
        mb.AddBox(new Vector3(0, 0.56f, 0.42f), new Vector3(0.42f, 0.36f, 0.78f), paint);
        mb.AddBox(new Vector3(0, 0.56f, 0.815f), new Vector3(0.36f, 0.3f, 0.03f), Dark);
        // Scarico verticale sul cofano
        mb.AddPrism(new Vector3(0.12f, 0.74f, 0.6f), 0.04f, 0.04f, 0.42f, 6, Dark);
        // Parafanghi sopra le ruote grandi
        foreach (float sx in new[] { -1f, 1f })
            mb.AddBox(new Vector3(sx * 0.5f, 0.86f, -0.4f), new Vector3(0.3f, 0.05f, 0.72f), paint);
        // Cabina aperta: pedana, sedile, quattro montanti e tetto
        mb.AddBox(new Vector3(0, 0.46f, -0.4f), new Vector3(0.7f, 0.06f, 0.7f), steel);
        mb.AddBox(new Vector3(0, 0.6f, -0.55f), new Vector3(0.3f, 0.08f, 0.26f), Dark);
        mb.AddBox(new Vector3(0, 0.75f, -0.66f), new Vector3(0.3f, 0.3f, 0.05f), Dark);
        mb.AddBox(new Vector3(0, 0.72f, -0.12f), new Vector3(0.06f, 0.26f, 0.06f), Dark); // piantone dello sterzo
        mb.AddBox(new Vector3(0, 0.86f, -0.16f), new Vector3(0.2f, 0.02f, 0.2f), Dark);  // volante
        // Cabina chiusa: montanti, vetri sui quattro lati e il tetto; dentro, la sagoma di chi guida
        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -0.72f, -0.1f })
                mb.AddBox(new Vector3(sx * 0.33f, 0.98f, sz), new Vector3(0.05f, 0.7f, 0.05f), Dark);
        var tint = new Vector3(0.3f, 0.42f, 0.5f);
        mb.AddBox(new Vector3(0, 1.0f, -0.1f), new Vector3(0.62f, 0.62f, 0.02f), tint);   // parabrezza
        mb.AddBox(new Vector3(0, 1.0f, -0.72f), new Vector3(0.62f, 0.62f, 0.02f), tint);  // lunotto
        foreach (float sx in new[] { -1f, 1f })
        {
            mb.AddBox(new Vector3(sx * 0.335f, 1.04f, -0.41f), new Vector3(0.02f, 0.54f, 0.57f), tint); // finestrini
            mb.AddBox(new Vector3(sx * 0.335f, 0.7f, -0.41f), new Vector3(0.03f, 0.12f, 0.6f), paint);  // fascia sotto
        }
        mb.AddBox(new Vector3(0, 1.34f, -0.41f), new Vector3(0.8f, 0.06f, 0.8f), paint);
        mb.AddBox(new Vector3(0, 1.38f, -0.41f), new Vector3(0.5f, 0.03f, 0.5f), Trim);      // tetto bianco
        var shirt = new Vector3(0.3f, 0.35f, 0.55f);
        mb.AddBox(new Vector3(0, 0.88f, -0.5f), new Vector3(0.22f, 0.3f, 0.14f), shirt);     // busto
        mb.AddBox(new Vector3(0, 1.1f, -0.48f), new Vector3(0.13f, 0.13f, 0.13f), new Vector3(0.85f, 0.65f, 0.5f)); // testa
        mb.AddBox(new Vector3(0, 1.18f, -0.49f), new Vector3(0.15f, 0.04f, 0.15f), new Vector3(0.7f, 0.15f, 0.12f)); // berretto
        // Gancio di traino
        mb.AddBox(new Vector3(0, 0.3f, -0.8f), new Vector3(0.14f, 0.06f, 0.2f), steel);
        foreach (float sx in new[] { -1f, 1f })
        {
            head.AddBox(new Vector3(sx * 0.14f, 0.66f, 0.83f), new Vector3(0.1f, 0.08f, 0.03f), HeadColor);
            tail.AddBox(new Vector3(sx * 0.5f, 0.8f, -0.77f), new Vector3(0.1f, 0.06f, 0.03f), TailColor);
        }
        reverse.AddBox(new Vector3(0, 0.36f, -0.9f), new Vector3(0.06f, 0.05f, 0.02f), new Vector3(1f, 1f, 0.95f));

        // Ruote: grandi dietro, tassellate; piccole davanti
        var big = BuildWheel(gl, 0.42f, 0.28f, hub: 0.2f, lugs: 14, rim);
        var small = BuildWheel(gl, 0.21f, 0.16f, hub: 0.1f, lugs: 8, rim);
        foreach (float sx in new[] { -1f, 1f })
        {
            _wheels.Add((big, new Vector3(sx * 0.5f, 0.42f, -0.4f), 0.42f, false));
            _wheels.Add((small, new Vector3(sx * 0.3f, 0.21f, 0.62f), 0.21f, true));
        }
    }

    /// <summary>
    /// Ruota centrata nell'origine con asse lungo X; con <paramref name="lugs"/> > 0 il battistrada ha i tasselli
    /// (blocchetti in rilievo, a spina di pesce alternati sui due lati).
    /// </summary>
    private Mesh BuildWheel(GL gl, float radius, float width, float hub, int lugs, Vector3? rimColor = null)
    {
        var mb = new MeshBuilder { Transform = Matrix4x4.CreateRotationZ(MathF.PI / 2f) };
        mb.AddPrism(new Vector3(0, -width * 0.5f, 0), radius, radius, width, lugs > 0 ? lugs : 10, Dark);
        mb.AddPrism(new Vector3(0, -width * 0.5f - 0.01f, 0), hub, hub, width + 0.02f, 6, rimColor ?? Trim);
        for (int i = 0; i < lugs; i++)
        {
            float a = i * MathF.Tau / lugs;
            for (int side = -1; side <= 1; side += 2)
            {
                // Tassello: sporge dal battistrada, metà larghezza, sfasato tra i due lati
                float aa = a + side * MathF.PI / lugs * 0.5f;
                var rot = Matrix4x4.CreateRotationY(-aa);
                // In coordinate della ruota prima della rotazione Z: l'asse è Y, il battistrada nel piano XZ
                var center = Vector3.Transform(new Vector3(radius + 0.025f, side * width * 0.25f, 0f), rot);
                mb.Transform = Matrix4x4.CreateRotationY(-aa) * Matrix4x4.CreateTranslation(center) * Matrix4x4.CreateRotationZ(MathF.PI / 2f);
                mb.AddBox(Vector3.Zero, new Vector3(0.06f, width * 0.45f, radius * 0.32f), Dark * 0.8f);
            }
        }
        var mesh = mb.Build(gl);
        _wheelMeshes.Add(mesh);
        return mesh;
    }

    // ------------------------------------------------------------------ disegno

    /// <summary>
    /// Aggiunge il veicolo da disegnare. <paramref name="root"/> = posa a terra (scala compresa), <paramref name="body"/> =
    /// posa della scocca (con rollio e beccheggio), <paramref name="spin"/> = rotazione di una ruota di raggio
    /// <see cref="WheelRadius"/> (le altre girano in proporzione), <paramref name="steer"/> = angolo delle ruote
    /// anteriori, <paramref name="brake"/> = intensità degli stop (0..2).
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

        foreach (var (mesh, at, radius, front) in _wheels)
        {
            float turn = spin * WheelRadius / radius; // a pari strada una ruota grande gira più piano
            items.Add(new DrawItem(mesh, Matrix4x4.CreateRotationX(turn) * Matrix4x4.CreateRotationY(front ? steer : 0f)
                                         * Matrix4x4.CreateTranslation(at) * root));
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
        foreach (var m in _wheelMeshes) m.Dispose();
    }
}
