using System.Numerics;
using Roads.World;
using Silk.NET.Maths;

namespace Roads;

/// <summary>
/// Orientamento a schermo. In alto a sinistra la minimappa (ruota con la camera, mostra ciò che si è esplorato):
/// punti cardinali, la freccia di chi si comanda e l'indicatore del punto da raggiungere. Con M la mappa vera dell'isola si srotola
/// sulla destra, semitrasparente: biomi, boschi, città e paesi, strade (statali, vie, sterrati), fiumi e punti di
/// riferimento. Un clic sulla mappa sceglie il punto da seguire (un clic sul punto lo toglie).
/// </summary>
public sealed partial class Game
{
    // Dimensioni in punti della finestra (sugli schermi Retina vengono moltiplicate)
    private const float CompassRadius = 80f, CompassMargin = 18f;
    private const float MiniRange = 230f;   // unità di mondo dal centro al bordo della minimappa
    private const float WaypointReached = 8f;
    private static readonly Vector2 CompassCenter = new(CompassMargin + CompassRadius, CompassMargin + CompassRadius);

    private bool _bigMap;
    private float _mapReveal;       // 0 = arrotolata, 1 = srotolata del tutto
    private Vector2? _waypoint;     // punto scelto sulla mappa (mondo x, z)

    private static readonly Vector4 Ink = new(0.05f, 0.07f, 0.11f, 0.9f);
    private static readonly Vector4 White = new(0.93f, 0.95f, 0.98f, 0.95f);
    private static readonly Vector4 NorthRed = new(0.98f, 0.38f, 0.32f, 1f);
    private static readonly Vector4 PinColor = new(1f, 0.32f, 0.52f, 1f);
    private static readonly Vector4 CarColor = new(0.92f, 0.20f, 0.16f, 1f);
    private static readonly Vector4 SeaColor = new(0.12f, 0.27f, 0.42f, 0.55f);

    private static Vector4 LandmarkColor(LandmarkKind k) => k switch
    {
        LandmarkKind.Lighthouse => new(0.92f, 0.22f, 0.18f, 1f),
        LandmarkKind.Windmill => new(1.00f, 0.84f, 0.30f, 1f),
        LandmarkKind.Castle => new(0.62f, 0.40f, 0.86f, 1f),
        LandmarkKind.WaterTower => new(0.35f, 0.70f, 0.95f, 1f),
        LandmarkKind.Antenna => new(1.00f, 0.55f, 0.20f, 1f),
        _ => new(0.80f, 0.45f, 0.32f, 1f),
    };

    private float PixelScale => _window.Size.X > 0 ? (float)_window.FramebufferSize.X / _window.Size.X : 1f;

    /// <summary>Apre/chiude la mappa e la fa srotolare; toglie il punto scelto quando lo si raggiunge.</summary>
    private void UpdateMap(float dt)
    {
        _mapReveal = MathF.Max(0f, MathF.Min(1f, _mapReveal + (_bigMap ? dt : -dt) / 0.35f));
        if (_waypoint is { } w && Vector2.Distance(w, new Vector2(ActivePos.X, ActivePos.Z)) < WaypointReached)
        {
            _waypoint = null;
            Console.WriteLine("Punto raggiunto");
        }
    }

    // ------------------------------------------------------------------ minimappa

    /// <summary>
    /// Minimappa in alto a sinistra, come prima: ruota con la camera (in alto c'è sempre la direzione in cui si
    /// guarda) e mostra il territorio già esplorato attorno, con strade, fiumi, cascine e punti di riferimento; sul
    /// bordo i punti cardinali, al centro la freccia di chi si comanda. Il punto scelto sulla mappa grande compare
    /// come freccia rosa: al suo posto se è vicino, sul bordo (nella sua direzione) se è lontano.
    /// </summary>
    private void DrawCompass(Vector2D<int> framebuffer)
    {
        float k = PixelScale;
        Vector2 c = CompassCenter * k;
        float r = CompassRadius * k;

        // Direzione del mondo → schermo: l'avanti della camera punta in alto
        Vector3 right = _camera.Right, forward = _camera.Forward;
        Vector2 ToMap(Vector3 world) => new(Vector3.Dot(world, right), -Vector3.Dot(world, forward));

        _hud.Disc(c, r, 40, SeaColor with { W = 0.85f });
        DrawMiniIsland(c, r, k, ToMap);
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

        // Punto scelto sulla mappa
        if (_waypoint is { } w)
        {
            Vector3 me = ActivePos;
            var to = new Vector3(w.X - me.X, 0f, w.Y - me.Z);
            float dist = to.Length();
            if (dist > 1e-3f)
            {
                Vector2 d = Vector2.Normalize(ToMap(to / dist));
                float rad = MathF.Min(dist * r / MiniRange, r - 12f * k);
                Vector2 at = c + d * rad, side = new(-d.Y, d.X);
                float pulse = 1f + 0.12f * MathF.Sin(_time * 5f);
                // Freccia che punta verso il punto (fuori dalla bussola quando è lontano)
                Vector2 tip = at + d * 7f * k * pulse, back = at - d * 5f * k;
                _hud.Triangle(tip + d * 1.5f * k, back + side * 7f * k, back - side * 7f * k, Ink);
                _hud.Triangle(tip, back + side * 5.5f * k * pulse, back - side * 5.5f * k * pulse, PinColor);
            }
        }

        // A piedi (o in treno): un pallino rosso dove si è lasciata l'auto, sul bordo nella sua direzione se è lontana
        if (_onFoot)
        {
            Vector3 me = ActivePos;
            var to = new Vector3(_player.Position.X - me.X, 0f, _player.Position.Z - me.Z);
            float dist = to.Length();
            Vector2 d = dist > 1e-3f ? Vector2.Normalize(ToMap(to / dist)) : Vector2.Zero;
            float rad = MathF.Min(dist * r / MiniRange, r - 10f * k);
            Vector2 at = c + d * rad;
            _hud.Disc(at, 6f * k, 16, Ink);
            _hud.Disc(at, 4.4f * k, 16, CarColor);
            _hud.Disc(at, 1.6f * k, 10, White);
        }

        Vector2 h = ToMap(new Vector3(MathF.Sin(ActiveYaw), 0f, MathF.Cos(ActiveYaw)));
        PlayerArrow(c, h, k * 0.9f);
        _hud.Flush(framebuffer.X, framebuffer.Y);
    }

    /// <summary>
    /// Il contenuto della minimappa, ritagliato nel cerchio: solo i pezzi di territorio già esplorati (come prima: la
    /// mappa si scopre girando). Stessi colori e stesse strade della mappa grande.
    /// </summary>
    private void DrawMiniIsland(Vector2 c, float r, float k, Func<Vector3, Vector2> toMap)
    {
        const float C = Terrain.ChunkSize;
        Vector3 me = ActivePos;
        var me2 = new Vector2(me.X, me.Z);
        float scale = r / MiniRange, reach = MiniRange * 1.45f;
        Vector2 ToScreen(Vector2 w) => c + toMap(new Vector3(w.X - me.X, 0f, w.Y - me.Z)) * scale;
        bool Seen(Vector2 w) => _world.IsExplored((int)MathF.Round(w.X / C), (int)MathF.Round(w.Y / C));
        bool Near(Vector2 w) => Vector2.DistanceSquared(w, me2) < reach * reach;

        var clip = new List<Vector2>(40);
        for (int i = 0; i < 40; i++)
        {
            float a = MathF.Tau * i / 40f;
            clip.Add(c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * (r - 1.5f * k));
        }

        // Terreno esplorato
        foreach (var (a, b, color) in LandTiles())
        {
            Vector2 mid = (a + b) * 0.5f;
            if (!Near(mid) || !Seen(mid)) continue;
            var quad = new List<Vector2>
            {
                ToScreen(a - new Vector2(0.4f)), ToScreen(new Vector2(b.X + 0.4f, a.Y - 0.4f)),
                ToScreen(b + new Vector2(0.4f)), ToScreen(new Vector2(a.X - 0.4f, b.Y + 0.4f)),
            };
            var poly = ClipPolygon(quad, clip);
            for (int t = 1; t + 1 < poly.Count; t++) _hud.Triangle(poly[0], poly[t], poly[t + 1], color with { W = 0.95f });
        }

        // Fiumi
        var river = new Vector4(0.30f, 0.62f, 0.95f, 1f);
        foreach (int col in GameMap.Current.RiverColumns)
            for (float z = MathF.Floor((me.Z - reach) / 8f) * 8f; z < me.Z + reach; z += 8f)
            {
                Vector2 pa = new(Terrain.RiverX(col, z), z), pb = new(Terrain.RiverX(col, z + 8f), z + 8f);
                if (!Near(pa) || !Seen(pa) || GameMap.Current.Sample(pa.X, pa.Y).Land < 0.55f) continue;
                Segment(ToScreen(pa), ToScreen(pb), 2.4f * k, river, clip);
            }

        // Strade
        foreach (var (kind, layout, pts) in MapRoads())
        {
            float width = layout switch
            {
                RoadLayout.Avenue => 4.2f,
                RoadLayout.OneWay => 1.6f,
                _ => kind switch { RoadKind.Main => 3.4f, RoadKind.Street => 2.3f, _ => 1.8f },
            } * k;
            var color = kind switch
            {
                RoadKind.Main => new Vector4(1f, 0.84f, 0.42f, 1f),
                RoadKind.Street => new Vector4(0.97f, 0.97f, 0.97f, 1f),
                _ => new Vector4(0.98f, 0.90f, 0.70f, 1f),
            };
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                if (!Near(pts[i]) || !(Seen(pts[i]) || Seen(pts[i + 1]))) continue;
                Segment(ToScreen(pts[i]), ToScreen(pts[i + 1]), width, color, clip);
            }
        }

        DrawRailway(ToScreen, k, p => Near(p) && Seen(p) && Inside(ToScreen(p), clip, 5f * k), (a, b, w, col) => Segment(a, b, w, col, clip));

        // Cascine e punti di riferimento scoperti
        foreach (var f in RoadNetwork.Current.Farms)
        {
            if (!Near(f.Yard) || !Seen(f.Yard)) continue;
            Vector2 s = ToScreen(f.Yard);
            if (!Inside(s, clip, 4f * k)) continue;
            _hud.Quad(s + new Vector2(-3f, -3f) * k, s + new Vector2(3f, -3f) * k, s + new Vector2(3f, 3f) * k,
                      s + new Vector2(-3f, 3f) * k, new Vector4(0.70f, 0.22f, 0.16f, 1f));
        }
        foreach (var l in GameMap.Current.Landmarks)
        {
            if (!Seen(l.Pos)) continue;
            Vector2 s = ToScreen(l.Pos);
            if (!Inside(s, clip, 6f * k)) continue;
            _hud.Disc(s, 6.5f * k, 14, Ink);
            _hud.Disc(s, 4.8f * k, 14, LandmarkColor(l.Kind));
        }
    }

    // ------------------------------------------------------------------ mappa grande

    /// <summary>Riquadro della mappa (pixel), centro del mondo mostrato e scala (pixel per unità di mondo).</summary>
    private (Vector2 Min, Vector2 Max, Vector2 WorldCenter, float Scale) MapLayout(Vector2D<int> fb)
    {
        float k = PixelScale, margin = 16f * k;
        var map = GameMap.Current;
        const float C = Terrain.ChunkSize;
        float x0 = (map.MinI - 0.5f) * C, x1 = (map.MaxI + 0.5f) * C, z0 = (map.MinJ - 0.5f) * C, z1 = (map.MaxJ + 0.5f) * C;
        float h = fb.Y - 2f * margin;
        float w = MathF.Min(h * (x1 - x0) / (z1 - z0), fb.X * 0.62f);
        var max = new Vector2(fb.X - margin, margin + h);
        var min = new Vector2(max.X - w, margin);
        float scale = MathF.Min((w - 16f * k) / (x1 - x0), (h - 16f * k) / (z1 - z0));
        return (min, max, new Vector2((x0 + x1) * 0.5f, (z0 + z1) * 0.5f), scale);
    }

    private List<(Vector2 Min, Vector2 Max, Vector4 Color)>? _landTiles;
    private List<(RoadKind Kind, RoadLayout Layout, Vector2[] Points)>? _mapRoads;

    /// <summary>Il territorio della mappa, calcolato una volta: tasselli di 24 unità col colore di bioma, bosco e città.</summary>
    private List<(Vector2 Min, Vector2 Max, Vector4 Color)> LandTiles()
    {
        if (_landTiles != null) return _landTiles;
        _landTiles = new();
        var map = GameMap.Current;
        const float C = Terrain.ChunkSize, T = C / 4f;
        var meadow = new Vector3(0.42f, 0.66f, 0.30f);
        var blossom = new Vector3(0.66f, 0.58f, 0.80f);
        var autumn = new Vector3(0.82f, 0.58f, 0.26f);
        var snowy = new Vector3(0.88f, 0.91f, 0.95f);
        var forest = new Vector3(0.17f, 0.40f, 0.20f);
        var town = new Vector3(0.74f, 0.72f, 0.70f);
        var sand = new Vector3(0.92f, 0.85f, 0.62f);
        for (float x = (map.MinI - 0.5f) * C; x < (map.MaxI + 0.5f) * C; x += T)
            for (float z = (map.MinJ - 0.5f) * C; z < (map.MaxJ + 0.5f) * C; z += T)
            {
                float cx = x + T * 0.5f, cz = z + T * 0.5f;
                var s = map.Sample(cx, cz);
                if (s.Land < 0.5f) continue;
                Vector3 col = meadow * s.Meadow + blossom * s.Blossom + autumn * s.Autumn + snowy * s.Snowy;
                col = Vector3.Lerp(col, forest, 0.6f * Terrain.Forest(cx, cz));
                col = Vector3.Lerp(col, town, Noise.Smooth(0.15f, 0.55f, s.Urban));
                col = Vector3.Lerp(col, sand, 1f - Noise.Smooth(0.5f, 0.66f, s.Land));
                _landTiles.Add((new Vector2(x, z), new Vector2(x + T, z + T), new Vector4(col, 0.86f)));
            }
        return _landTiles;
    }

    /// <summary>Le strade per la mappa (un punto ogni tre), sterrati prima così le statali restano sopra.</summary>
    private List<(RoadKind Kind, RoadLayout Layout, Vector2[] Points)> MapRoads()
    {
        if (_mapRoads != null) return _mapRoads;
        _mapRoads = new();
        foreach (var e in RoadNetwork.Current.Edges.OrderByDescending(e => e.Kind))
        {
            var pts = new List<Vector2>();
            for (int i = 0; i < e.Count; i += 3) pts.Add(e.Points[i]);
            if (pts[^1] != e.Points[^1]) pts.Add(e.Points[^1]);
            if (e.OneWay < 0) pts.Reverse(); // i sensi unici in ordine di marcia (per la freccia)
            _mapRoads.Add((e.Kind, e.Layout, pts.ToArray()));
        }
        return _mapRoads;
    }

    /// <summary>La mappa dell'isola sulla destra, col nord in alto; si srotola da destra verso sinistra.</summary>
    private void DrawBigMap(Vector2D<int> fb)
    {
        float k = PixelScale;
        var (min, max, worldCenter, scale) = MapLayout(fb);
        Vector2 screenCenter = (min + max) * 0.5f;
        Vector2 ToScreen(Vector2 w) => screenCenter + (w - worldCenter) * scale; // x = est, y in giù = sud

        // Srotolamento: si vede la parte a destra del "rullo", che scorre verso sinistra
        float p = 1f - (1f - _mapReveal) * (1f - _mapReveal) * (1f - _mapReveal);
        float edge = max.X - (max.X - min.X) * p;
        var clip = new List<Vector2> { new(edge, min.Y), new(max.X, min.Y), max, new(edge, max.Y) };
        bool full = p >= 0.999f;

        void Quad(Vector2 a, Vector2 b, Vector4 color)
        {
            if (full) { _hud.Quad(a, new(b.X, a.Y), b, new(a.X, b.Y), color); return; }
            if (b.X <= edge) return;
            _hud.Quad(new(MathF.Max(a.X, edge), a.Y), new(b.X, a.Y), b, new(MathF.Max(a.X, edge), b.Y), color);
        }
        void Seg(Vector2 a, Vector2 b, float width, Vector4 color)
        {
            if (full) _hud.Line(a, b, width, color);
            else Segment(a, b, width, color, clip);
        }

        Quad(min, max, SeaColor);
        foreach (var (a, b, color) in LandTiles()) Quad(ToScreen(a), ToScreen(b) + Vector2.One * 0.6f, color);

        // Fiumi
        var river = new Vector4(0.30f, 0.62f, 0.95f, 0.95f);
        var map = GameMap.Current;
        const float C = Terrain.ChunkSize;
        foreach (int col in map.RiverColumns)
            for (float z = (map.MinJ - 0.5f) * C; z < (map.MaxJ + 0.5f) * C; z += 8f)
            {
                Vector2 pa = new(Terrain.RiverX(col, z), z), pb = new(Terrain.RiverX(col, z + 8f), z + 8f);
                if (map.Sample(pa.X, pa.Y).Land < 0.55f) continue;
                Seg(ToScreen(pa), ToScreen(pb), 2.4f * k, river);
            }

        // Strade: sterrati sottili color terra, vie bianche, statali più larghe e gialle con il bordo scuro
        foreach (var (kind, layout, pts) in MapRoads())
        {
            float width = layout switch
            {
                RoadLayout.Avenue => 3.6f,
                RoadLayout.OneWay => 1.3f,
                _ => kind switch { RoadKind.Main => 3.4f, RoadKind.Street => 1.9f, _ => 1.5f },
            } * k;
            var color = kind switch
            {
                RoadKind.Main => new Vector4(1f, 0.84f, 0.42f, 1f),
                RoadKind.Street => new Vector4(0.98f, 0.98f, 0.98f, 0.95f),
                _ => new Vector4(0.62f, 0.48f, 0.30f, 0.95f),
            };
            for (int i = 0; i + 1 < pts.Length; i++)
            {
                Vector2 a = ToScreen(pts[i]), b = ToScreen(pts[i + 1]);
                if (kind == RoadKind.Main) Seg(a, b, width + 1.6f * k, Ink with { W = 0.6f });
                Seg(a, b, width, color);
            }
            // Sensi unici: una freccia azzurra a metà, nel verso di marcia
            if (layout == RoadLayout.OneWay && pts.Length >= 2)
            {
                int m = Math.Min(pts.Length / 2, pts.Length - 2);
                Vector2 a = ToScreen(pts[m]), b = ToScreen(pts[m + 1]);
                if ((full || a.X > edge + 6f * k) && (b - a).LengthSquared() > 1e-4f)
                {
                    Vector2 d = Vector2.Normalize(b - a), s = new(-d.Y, d.X), at = (a + b) * 0.5f;
                    _hud.Triangle(at + d * 5f * k, at - d * 3f * k + s * 4f * k, at - d * 3f * k - s * 4f * k,
                                  new Vector4(0.45f, 0.78f, 1f, 1f));
                }
            }
        }

        DrawRailway(ToScreen, k, p => full || ToScreen(p).X > edge + 6f * k, (a, b, w, c) => Seg(a, b, w, c));

        // Punti di riferimento, cascine
        foreach (var l in map.Landmarks)
        {
            Vector2 s = ToScreen(l.Pos);
            if (!full && s.X < edge + 6f * k) continue;
            _hud.Disc(s, 6.5f * k, 14, Ink);
            _hud.Disc(s, 4.8f * k, 14, LandmarkColor(l.Kind));
        }
        foreach (var f in RoadNetwork.Current.Farms)
        {
            Vector2 s = ToScreen(f.Yard);
            if (!full && s.X < edge + 4f * k) continue;
            _hud.Quad(s + new Vector2(-3f, -3f) * k, s + new Vector2(3f, -3f) * k, s + new Vector2(3f, 3f) * k, s + new Vector2(-3f, 3f) * k,
                      new Vector4(0.70f, 0.22f, 0.16f, 1f));
        }

        // Punto scelto: una puntina
        if (_waypoint is { } wp)
        {
            Vector2 s = ToScreen(wp);
            if (full || s.X > edge + 8f * k)
            {
                _hud.Triangle(s, s + new Vector2(-5f, -11f) * k, s + new Vector2(5f, -11f) * k, Ink);
                _hud.Disc(s + new Vector2(0, -14f) * k, 7.5f * k, 16, Ink);
                _hud.Disc(s + new Vector2(0, -14f) * k, 5.5f * k, 16, PinColor);
            }
        }

        // L'auto parcheggiata, se si è a piedi
        if (_onFoot)
        {
            Vector2 cs = ToScreen(new Vector2(_player.Position.X, _player.Position.Z));
            if (full || cs.X > edge + 8f * k)
            {
                _hud.Disc(cs, 6.5f * k, 16, Ink);
                _hud.Disc(cs, 4.8f * k, 16, CarColor);
                _hud.Disc(cs, 1.7f * k, 10, White);
            }
        }

        // Chi si comanda
        Vector3 me = ActivePos;
        Vector2 meS = ToScreen(new Vector2(me.X, me.Z));
        if (full || meS.X > edge + 10f * k)
            PlayerArrow(meS, new Vector2(MathF.Sin(ActiveYaw), MathF.Cos(ActiveYaw)), k);

        // Cornice, nord e il "rullo" sul bordo che si srotola
        foreach (var (a, b) in new[] { (new Vector2(edge, min.Y), new Vector2(max.X, min.Y)), (new Vector2(max.X, min.Y), max), (max, new Vector2(edge, max.Y)) })
            _hud.Line(a, b, 2f * k, White with { W = 0.7f });
        var nAt = new Vector2(max.X - 22f * k, min.Y + 22f * k);
        if (nAt.X > edge + 14f * k)
        {
            _hud.Disc(nAt, 13f * k, 16, Ink with { W = 0.7f });
            _hud.Letter('N', nAt, 16f * k, 2.8f * k, NorthRed);
        }
        _hud.Quad(new(edge - 5f * k, min.Y - 3f * k), new(edge + 3f * k, min.Y - 3f * k),
                  new(edge + 3f * k, max.Y + 3f * k), new(edge - 5f * k, max.Y + 3f * k), new Vector4(0.86f, 0.80f, 0.66f, 0.95f));
        _hud.Line(new(edge - 1f * k, min.Y - 3f * k), new(edge - 1f * k, max.Y + 3f * k), 1.5f * k, Ink with { W = 0.5f });
        _hud.Flush(fb.X, fb.Y);
    }

    /// <summary>Clic sulla mappa aperta: sceglie il punto da seguire (o lo toglie se si clicca sulla puntina).</summary>
    private void MapClick(Vector2 mouse)
    {
        if (_mapReveal < 0.9f) return;
        float k = PixelScale;
        var fb = _window.FramebufferSize;
        var (min, max, worldCenter, scale) = MapLayout(fb);
        Vector2 px = mouse * k;
        if (px.X < min.X || px.X > max.X || px.Y < min.Y || px.Y > max.Y) return;
        Vector2 screenCenter = (min + max) * 0.5f;
        Vector2 world = worldCenter + (px - screenCenter) / scale;
        if (_waypoint is { } wp && Vector2.Distance(screenCenter + (wp - worldCenter) * scale + new Vector2(0, -10f) * k, px) < 16f * k)
        {
            _waypoint = null;
            return;
        }
        if (GameMap.Current.Sample(world.X, world.Y).Land < 0.5f) return; // in mare no
        _waypoint = world;
        Console.WriteLine($"Punto da raggiungere: {world.X:F0}, {world.Y:F0}");
    }

    /// <summary>Freccia di chi si comanda: punta nella direzione <paramref name="dir"/> (schermo), con bordo scuro.</summary>
    /// <summary>
    /// La ferrovia sulle mappe: linea scura con i tratti bianchi (il simbolo classico) e le stazioni come quadratini.
    /// <paramref name="show"/> dice se un punto del mondo va disegnato, <paramref name="seg"/> disegna un tratto (ritagliato).
    /// </summary>
    private void DrawRailway(Func<Vector2, Vector2> toScreen, float k, Func<Vector2, bool> show, Action<Vector2, Vector2, float, Vector4> seg)
    {
        var rail = Railway.Current;
        if (!rail.Exists) return;
        int n = rail.Points.Count;
        for (int i = 0; i < n; i += 3)
        {
            Vector2 a = rail.Points[i], b = rail.Points[(i + 3) % n];
            if (!show(a)) continue;
            seg(toScreen(a), toScreen(b), 3.2f * k, new Vector4(0.2f, 0.2f, 0.24f, 1f));
            if ((i / 3) % 2 == 0) seg(toScreen(a), toScreen(b), 1.3f * k, new Vector4(0.95f, 0.95f, 0.95f, 1f));
        }
        foreach (var st in rail.Stations)
        {
            Vector2 p = rail.At(st.S);
            if (!show(p)) continue;
            Vector2 s = toScreen(p);
            _hud.Quad(s + new Vector2(-4.5f, -4.5f) * k, s + new Vector2(4.5f, -4.5f) * k, s + new Vector2(4.5f, 4.5f) * k, s + new Vector2(-4.5f, 4.5f) * k, Ink);
            _hud.Quad(s + new Vector2(-3f, -3f) * k, s + new Vector2(3f, -3f) * k, s + new Vector2(3f, 3f) * k, s + new Vector2(-3f, 3f) * k, new Vector4(0.35f, 0.55f, 0.95f, 1f));
        }
    }

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
