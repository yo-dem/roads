using System.Numerics;
using Roads.World;
using Silk.NET.Maths;

namespace Roads;

/// <summary>
/// Mappe a schermo: la minimappa in alto a sinistra (ruota con la camera) e la mappa grande dell'isola (tasto M,
/// col nord in alto). Mostrano solo ciò che si è già esplorato: terra colorata per quartiere, strade, fiumi e
/// punti di riferimento; sulla mappa grande il resto dell'isola si intravede in grigio.
/// </summary>
public sealed partial class Game
{
    // Dimensioni in punti della finestra (sugli schermi Retina vengono moltiplicate)
    private const float MapRadius = 80f, MapMargin = 18f;
    private const float MiniRange = 230f; // unità di mondo dal centro al bordo della minimappa
    private static readonly Vector2 MapCenter = new(MapMargin + MapRadius, MapMargin + MapRadius);

    private bool _bigMap;

    private static readonly Vector4 SeaColor = new(0.12f, 0.27f, 0.42f, 0.82f);
    private static readonly Vector4 Ink = new(0.05f, 0.07f, 0.11f, 0.9f);
    private static readonly Vector4 White = new(0.93f, 0.95f, 0.98f, 0.95f);
    private static readonly Vector4 NorthRed = new(0.98f, 0.38f, 0.32f, 1f);

    private static Vector4 DistrictColor(District d) => d switch
    {
        District.Meadow => new(0.42f, 0.66f, 0.30f, 0.95f),
        District.Blossom => new(0.66f, 0.56f, 0.80f, 0.95f),
        District.Autumn => new(0.80f, 0.56f, 0.24f, 0.95f),
        District.Snowy => new(0.86f, 0.90f, 0.95f, 0.95f),
        District.Town => new(0.78f, 0.70f, 0.60f, 0.95f),
        District.City => new(0.60f, 0.60f, 0.64f, 0.95f),
        _ => SeaColor,
    };

    private static Vector4 LandmarkColor(LandmarkKind k) => k switch
    {
        LandmarkKind.Lighthouse => new(0.92f, 0.22f, 0.18f, 1f),
        LandmarkKind.Windmill => new(1.00f, 0.84f, 0.30f, 1f),
        LandmarkKind.Castle => new(0.62f, 0.40f, 0.86f, 1f),
        LandmarkKind.WaterTower => new(0.35f, 0.70f, 0.95f, 1f),
        LandmarkKind.Antenna => new(1.00f, 0.55f, 0.20f, 1f),
        _ => new(0.80f, 0.45f, 0.32f, 1f),
    };

    // ------------------------------------------------------------------ minimappa

    /// <summary>
    /// Minimappa in alto a sinistra: ruota con la camera (in alto c'è sempre la direzione in cui si guarda),
    /// con i punti cardinali sul bordo e al centro una freccia con la direzione di chi si comanda.
    /// </summary>
    private void DrawMinimap(Vector2D<int> framebuffer)
    {
        float k = _window.Size.X > 0 ? (float)framebuffer.X / _window.Size.X : 1f; // pixel per punto
        Vector2 c = MapCenter * k;
        float r = MapRadius * k;
        float scale = r / MiniRange;

        // Direzione del mondo → schermo: l'avanti della camera punta in alto
        Vector3 right = _camera.Right, forward = _camera.Forward;
        Vector2 ToMap(Vector3 world) => new(Vector3.Dot(world, right), -Vector3.Dot(world, forward));
        Vector3 me = ActivePos;
        Vector2 ToScreen(Vector2 w) => c + ToMap(new Vector3(w.X - me.X, 0f, w.Y - me.Z)) * scale;

        _hud.Disc(c, r, 40, SeaColor);
        var clip = new List<Vector2>();
        for (int i = 0; i < 40; i++)
        {
            float a = MathF.Tau * i / 40f;
            clip.Add(c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (r - 1.5f * k));
        }
        DrawIsland(clip, ToScreen, k, new Vector2(me.X, me.Z), MiniRange * 1.45f, showUnknown: false);

        _hud.Ring(c, r - 2f * k, r, 40, new Vector4(0.93f, 0.95f, 0.98f, 0.8f));
        // Tacche: lunghe sui punti cardinali, corte su quelli intermedi
        for (int i = 0; i < 8; i++)
        {
            float a = MathF.Tau * i / 8f;
            Vector2 d = ToMap(new Vector3(MathF.Sin(a), 0f, -MathF.Cos(a))); // i = 0 → nord (-Z), poi in senso orario
            float inner = i % 2 == 0 ? r - 9f * k : r - 6f * k;
            _hud.Line(c + d * inner, c + d * (r - 2f * k), 2f * k, i == 0 ? NorthRed : White with { W = 0.7f });
        }
        // Lettere sempre dritte, che girano lungo il bordo
        foreach (var (letter, dir) in new[]
                 { ('N', new Vector3(0, 0, -1)), ('E', new Vector3(1, 0, 0)), ('S', new Vector3(0, 0, 1)), ('O', new Vector3(-1, 0, 0)) })
        {
            Vector2 at = c + ToMap(dir) * (r - 20f * k);
            _hud.Disc(at, 8f * k, 12, Ink with { W = 0.55f });
            _hud.Letter(letter, at, 12f * k, 2.2f * k, letter == 'N' ? NorthRed : White);
        }

        Vector2 h = ToMap(new Vector3(MathF.Sin(ActiveYaw), 0f, MathF.Cos(ActiveYaw)));
        PlayerArrow(c, h, k);
        _hud.Flush(framebuffer.X, framebuffer.Y);
    }

    // ------------------------------------------------------------------ mappa grande

    /// <summary>Mappa di tutta l'isola, col nord in alto: si apre e si chiude con M.</summary>
    private void DrawBigMap(Vector2D<int> fb)
    {
        float k = _window.Size.X > 0 ? (float)fb.X / _window.Size.X : 1f;
        var map = GameMap.Current;
        const float C = Terrain.ChunkSize;
        float x0 = (map.MinI - 0.5f) * C, x1 = (map.MaxI + 0.5f) * C, z0 = (map.MinJ - 0.5f) * C, z1 = (map.MaxJ + 0.5f) * C;
        float side = MathF.Min(fb.X, fb.Y) * 0.88f;
        float scale = side / MathF.Max(x1 - x0, z1 - z0);
        var screenCenter = new Vector2(fb.X, fb.Y) * 0.5f;
        var worldCenter = new Vector2((x0 + x1) * 0.5f, (z0 + z1) * 0.5f);
        Vector2 ToScreen(Vector2 w) => screenCenter + (w - worldCenter) * scale; // x = est, y in giù = sud

        Vector2 half = new Vector2(x1 - x0, z1 - z0) * scale * 0.5f;
        Vector2 a = screenCenter - half, b = screenCenter + half;
        _hud.Quad(new(0, 0), new(fb.X, 0), new(fb.X, fb.Y), new(0, fb.Y), new Vector4(0f, 0f, 0f, 0.45f));
        _hud.Quad(a, new(b.X, a.Y), b, new(a.X, b.Y), SeaColor with { W = 0.95f });
        var clip = new List<Vector2> { a, new(b.X, a.Y), b, new(a.X, b.Y) };
        DrawIsland(clip, ToScreen, k * 1.3f, worldCenter, float.MaxValue, showUnknown: true);

        // Cornice e nord
        foreach (var (p, q) in new[] { (a, new Vector2(b.X, a.Y)), (new Vector2(b.X, a.Y), b), (b, new Vector2(a.X, b.Y)), (new Vector2(a.X, b.Y), a) })
            _hud.Line(p, q, 2.5f * k, White with { W = 0.85f });
        var nAt = new Vector2(screenCenter.X, a.Y + 22f * k);
        _hud.Disc(nAt, 13f * k, 16, Ink with { W = 0.7f });
        _hud.Letter('N', nAt, 16f * k, 2.8f * k, NorthRed);

        Vector3 me = ActivePos;
        PlayerArrow(ToScreen(new Vector2(me.X, me.Z)), new Vector2(MathF.Sin(ActiveYaw), MathF.Cos(ActiveYaw)), k * 1.3f);
        _hud.Flush(fb.X, fb.Y);
    }

    // ------------------------------------------------------------------ contenuto comune

    /// <summary>
    /// Disegna l'isola nella regione convessa <paramref name="clip"/> (pixel): celle di terra, fiumi, strade e
    /// punti di riferimento. Ciò che non si è ancora esplorato non compare, oppure (mappa grande) appare in grigio.
    /// </summary>
    private void DrawIsland(List<Vector2> clip, Func<Vector2, Vector2> toScreen, float k, Vector2 around, float range,
                            bool showUnknown)
    {
        var map = GameMap.Current;
        const float C = Terrain.ChunkSize;
        bool Near(int i, int j) => Vector2.Distance(new Vector2(i * C, j * C), around) < range + C;
        bool Seen(int i, int j) => _world.IsExplored(i, j);

        // Terra: un quadrato per cella, del colore del quartiere
        for (int i = map.MinI; i <= map.MaxI; i++)
            for (int j = map.MinJ; j <= map.MaxJ; j++)
            {
                if (!map.IsLand(i, j) || !Near(i, j)) continue;
                bool seen = Seen(i, j);
                if (!seen && !showUnknown) continue;
                var color = seen ? DistrictColor(map.At(i, j)) : new Vector4(0.34f, 0.37f, 0.40f, 0.75f);
                var quad = new List<Vector2>
                {
                    toScreen(new((i - 0.5f) * C, (j - 0.5f) * C)), toScreen(new((i + 0.5f) * C, (j - 0.5f) * C)),
                    toScreen(new((i + 0.5f) * C, (j + 0.5f) * C)), toScreen(new((i - 0.5f) * C, (j + 0.5f) * C)),
                };
                var poly = ClipPolygon(quad, clip);
                for (int t = 1; t + 1 < poly.Count; t++) _hud.Triangle(poly[0], poly[t], poly[t + 1], color);
            }

        // Fiumi (nord-sud) nelle celle esplorate
        var river = new Vector4(0.30f, 0.62f, 0.95f, 1f);
        for (int i = map.MinI - 1; i <= map.MaxI + 1; i++)
        {
            if (!Terrain.HasRiver(i)) continue;
            for (int j = map.MinJ; j <= map.MaxJ; j++)
            {
                if (!Near(i, j)) continue;
                for (int s = 0; s < 8; s++)
                {
                    float za = (j - 0.5f + s / 8f) * C, zb = za + C / 8f;
                    Vector2 pa = new(Terrain.RiverX(i, za), za), pb = new(Terrain.RiverX(i, zb), zb);
                    int ci = (int)MathF.Round((pa.X + pb.X) * 0.5f / C);
                    if (!map.IsLand(ci, j) || !Seen(ci, j)) continue;
                    Segment(toScreen(pa), toScreen(pb), 2.2f * k, river, clip);
                }
            }
        }

        // Strade: un tratto è sulla mappa se si è visto almeno uno dei suoi due incroci
        for (int i = map.MinI; i <= map.MaxI; i++)
            for (int j = map.MinJ; j <= map.MaxJ; j++)
            {
                if (!Near(i, j)) continue;
                if (map.East(i, j) && (Seen(i, j) || Seen(i + 1, j)))
                    Road(i, j, i + 1, j, x => new Vector2(x, Terrain.RoadZ(j, x)), i * C);
                if (map.South(i, j) && (Seen(i, j) || Seen(i, j + 1)))
                    Road(i, j, i, j + 1, z => new Vector2(Terrain.RoadX(i, z), z), j * C);
            }

        void Road(int ia, int ja, int ib, int jb, Func<float, Vector2> at, float from)
        {
            bool urban = GameMap.Density(map.At(ia, ja)) > 0.5f || GameMap.Density(map.At(ib, jb)) > 0.5f;
            var color = urban ? new Vector4(0.97f, 0.97f, 0.97f, 1f) : new Vector4(0.98f, 0.90f, 0.70f, 1f);
            const int steps = 12;
            for (int s = 0; s < steps; s++)
                Segment(toScreen(at(from + C * s / steps)), toScreen(at(from + C * (s + 1) / steps)), 2.6f * k, color, clip);
        }

        // Punti di riferimento scoperti
        foreach (var l in map.Landmarks)
        {
            int ci = (int)MathF.Round(l.Pos.X / C), cj = (int)MathF.Round(l.Pos.Y / C);
            if (!Seen(ci, cj) && !Seen(ci + (l.Pos.X > ci * C ? 1 : -1), cj)) continue;
            Vector2 p = toScreen(l.Pos);
            if (!Inside(p, clip, 6f * k)) continue;
            _hud.Disc(p, 6.5f * k, 14, Ink);
            _hud.Disc(p, 4.8f * k, 14, LandmarkColor(l.Kind));
        }
    }

    /// <summary>Freccia di chi si comanda: punta nella direzione <paramref name="dir"/> (schermo), con bordo scuro.</summary>
    private void PlayerArrow(Vector2 c, Vector2 dir, float k)
    {
        Vector2 h = dir.LengthSquared() > 1e-6f ? Vector2.Normalize(dir) : new Vector2(0, -1);
        Vector2 side = new(-h.Y, h.X);
        void Arrow(float s, Vector4 color)
        {
            Vector2 tip = c + h * 11f * k * s, back = c - h * 3f * k * s;
            Vector2 left = c - h * 8f * k * s + side * 7f * k * s, right = c - h * 8f * k * s - side * 7f * k * s;
            _hud.Triangle(tip, left, back, color);
            _hud.Triangle(tip, back, right, color);
        }
        Arrow(1.3f, Ink);
        Arrow(1f, new Vector4(1f, 0.84f, 0.38f, 1f));
    }

    // ------------------------------------------------------------------ ritaglio (regioni convesse)

    private static float Orientation(List<Vector2> clip)
    {
        float area = 0f;
        for (int i = 0; i < clip.Count; i++)
        {
            Vector2 p = clip[i], q = clip[(i + 1) % clip.Count];
            area += p.X * q.Y - q.X * p.Y;
        }
        return MathF.Sign(area);
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;

    /// <summary>Il punto è dentro la regione convessa, con un margine (pixel) dal bordo?</summary>
    private static bool Inside(Vector2 p, List<Vector2> clip, float margin)
    {
        float o = Orientation(clip);
        for (int i = 0; i < clip.Count; i++)
        {
            Vector2 a = clip[i], b = clip[(i + 1) % clip.Count];
            Vector2 e = b - a;
            if (o * Cross(e, p - a) / e.Length() < margin) return false;
        }
        return true;
    }

    /// <summary>Ritaglia un poligono convesso con una regione convessa (Sutherland–Hodgman).</summary>
    private static List<Vector2> ClipPolygon(List<Vector2> poly, List<Vector2> clip)
    {
        float o = Orientation(clip);
        var output = poly;
        for (int i = 0; i < clip.Count && output.Count > 0; i++)
        {
            Vector2 a = clip[i], e = clip[(i + 1) % clip.Count] - a;
            var input = output;
            output = new List<Vector2>(input.Count + 2);
            for (int k = 0; k < input.Count; k++)
            {
                Vector2 p = input[k], q = input[(k + 1) % input.Count];
                float dp = o * Cross(e, p - a), dq = o * Cross(e, q - a);
                if (dp >= 0f) output.Add(p);
                if ((dp >= 0f) != (dq >= 0f)) output.Add(p + (q - p) * (dp / (dp - dq)));
            }
        }
        return output;
    }

    /// <summary>Linea ritagliata dentro la regione convessa (Cyrus–Beck).</summary>
    private void Segment(Vector2 p, Vector2 q, float width, Vector4 color, List<Vector2> clip)
    {
        float o = Orientation(clip), t0 = 0f, t1 = 1f;
        Vector2 d = q - p;
        for (int i = 0; i < clip.Count; i++)
        {
            Vector2 a = clip[i], e = clip[(i + 1) % clip.Count] - a;
            float num = o * Cross(e, p - a), den = o * Cross(e, d);
            if (MathF.Abs(den) < 1e-6f)
            {
                if (num < 0f) return;
                continue;
            }
            float t = -num / den;
            if (den > 0f) t0 = MathF.Max(t0, t); else t1 = MathF.Min(t1, t);
            if (t0 > t1) return;
        }
        _hud.Line(p + d * t0, p + d * t1, width, color);
    }
}
