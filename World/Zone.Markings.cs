using System.Numerics;
using Roads.Rendering;

namespace Roads.World;

/// <summary>
/// Segnaletica e arredo delle strade: linee dipinte (mezzerie tratteggiate, linee di bordo delle statali, linee tra
/// le corsie dei viali), frecce dei sensi unici, strisce pedonali e linee d'arresto agli incroci dei centri abitati,
/// lo spartitraffico alberato dei viali e i cartelli "senso unico" / "divieto d'accesso".
/// Tutto si misura sulla distanza lungo il tratto (uguale per ogni pezzo): ogni segno lo disegna il pezzo in cui
/// cade, e i segni lunghi si tagliano sul bordo, così i pezzi combaciano.
/// </summary>
public sealed partial class Zone
{
    private static readonly Vector3 Paint = new(0.94f, 0.93f, 0.88f);
    private static readonly Vector3 MedianGrass = new(0.38f, 0.60f, 0.26f);
    private static readonly Vector3 SignBlue = new(0.12f, 0.32f, 0.70f);
    private static readonly Vector3 SignRed = new(0.82f, 0.12f, 0.12f);
    private static readonly Vector3 SignWhite = new(0.96f, 0.96f, 0.96f);
    private static readonly Vector3[] AvenueLeaves = [new(0.33f, 0.58f, 0.24f), new(0.27f, 0.50f, 0.20f), new(0.40f, 0.64f, 0.26f)];

    private const float PaintLift = 0.014f; // la vernice sta appena sopra l'asfalto

    /// <summary>Punto del mondo, scostato di <paramref name="offset"/> verso destra (di chi va da A a B), in coordinate locali.</summary>
    private Vector3 EdgeLocal(RoadEdge e, float s, float offset)
    {
        Vector2 c = e.At(s), t = e.Tangent(s);
        Vector2 p = c + new Vector2(-t.Y, t.X) * offset;
        return new Vector3(p.X - Origin.X, 0f, p.Y - Origin.Z);
    }

    /// <summary>
    /// Quota (senza il rialzo) della lastra d'asfalto nel punto a distanza <paramref name="s"/> e scostamento
    /// <paramref name="offset"/>: come la costruisce Strip, piana tra un vertice e il successivo (ogni RoadStep lungo il
    /// tratto) e tra i due bordi.
    /// </summary>
    private float RoadSurface(RoadEdge e, float s, float offset)
    {
        float s0 = MathF.Floor(s / RoadStep) * RoadStep, s1 = MathF.Min(s0 + RoadStep, e.Length);
        float t = s1 > s0 ? Math.Clamp((s - s0) / (s1 - s0), 0f, 1f) : 0f;
        float hw = e.HalfWidth;
        float across = Math.Clamp((hw - offset) / (2f * hw), 0f, 1f); // 0 sul bordo sinistro (+offset), 1 sul destro
        float Section(float at)
        {
            Vector3 l = EdgeLocal(e, at, hw), r = EdgeLocal(e, at, -hw);
            return Noise.Lerp(HeightAnywhere(l.X, l.Z), HeightAnywhere(r.X, r.Z), across);
        }
        return Noise.Lerp(Section(s0), Section(s1), t);
    }

    /// <summary>Distanza dal capo A (e dal capo B) da cui cominciano i segni: fuori dallo slargo dell'incrocio.</summary>
    private static (float From, float To) PaintRange(RoadEdge e, float margin)
    {
        float from = PassThrough(e, e.A) ? 0f : e.A.TrimFor(e) + margin;
        float to = PassThrough(e, e.B) ? e.Length : e.Length - e.B.TrimFor(e) - margin;
        return (from, to);
    }

    /// <summary>
    /// Il nodo è solo un punto di passaggio (due tratti uguali che proseguono quasi dritti, senza incrocio)? Lì le linee
    /// dipinte continuano senza interrompersi.
    /// </summary>
    private static bool PassThrough(RoadEdge e, RoadNode n)
    {
        if (n.Degree != 2 || n.Piazza || n.Destination) return false;
        var other = n.Edges[0] == e ? n.Edges[1] : n.Edges[0];
        if (other == e || other.Layout != e.Layout || other.Kind != e.Kind || other.OneWay != 0 || e.OneWay != 0) return false;
        return Vector2.Dot(e.LeaveDir(n), other.LeaveDir(n)) < -0.9f; // quasi opposti: la strada va dritta
    }

    /// <summary>
    /// Una striscia dipinta (o rialzata, con <paramref name="height"/> &gt; 0) che segue la strada tra s0 e s1, allo
    /// scostamento dato e larga <paramref name="width"/>. Si disegnano solo i pezzetti dentro questo pezzo di terreno.
    /// </summary>
    private void Stripe(MeshBuilder mb, RoadEdge e, float s0, float s1, float offset, float width, Vector3 color,
                        float height = 0f, Vector3? sideColor = null)
    {
        if (s1 - s0 < 0.05f) return;
        int n = Math.Max(1, (int)MathF.Ceiling((s1 - s0) / 0.7f));
        Vector3 prevR = default, prevL = default, prevC = default;
        bool prevOk = false;
        float lift = PathRaise + PaintLift + height;
        for (int k = 0; k <= n; k++)
        {
            float s = s0 + (s1 - s0) * k / n;
            Vector3 c = EdgeLocal(e, s, offset);
            Vector3 right = EdgeLocal(e, s, offset + width * 0.5f), left = EdgeLocal(e, s, offset - width * 0.5f);
            float y = HeightAnywhere(c.X, c.Z); // (il pezzetto a cavallo del bordo sporge nel pezzo accanto)
            float top = RoadSurface(e, s, offset); // la lastra d'asfalto lì (senza il rialzo)
            // Asciutto e, per lo spartitraffico, non sui binari (lì si apre); il pezzo di bordo si decide dopo
            bool ok = y > WaterY + 0.2f
                      && (height <= 0f || Railway.Current.Distance(WX(c.X), WZ(c.Z)) > Railway.BallastHalf + 1f);
            // Sopra l'asfalto (che tra i suoi vertici è piatto) e sopra il terreno (che lì sotto può fare una gobba):
            // la vernice non sparisce mai sotto la lastra
            right.Y = left.Y = MathF.Max(y, top) + lift;
            // Ogni pezzetto lo disegna il pezzo di territorio in cui cade il suo punto di mezzo (niente buchi sui bordi)
            if (k > 0 && ok && prevOk && InZone((prevC + c) * 0.5f, 0f))
            {
                mb.AddQuad(prevR, right, left, prevL, color);
                if (height > 0f)
                {
                    var down = new Vector3(0, height + 0.02f, 0);
                    var side = sideColor ?? color * 0.8f;
                    mb.AddQuad(prevR - down, right - down, right, prevR, side);
                    mb.AddQuad(left - down, prevL - down, prevL, left, side);
                }
            }
            if (height > 0f && ok && InZone(c, 0f) && (k == 0 || k == n))
            {
                // Testate dello spartitraffico
                var down = new Vector3(0, height + 0.02f, 0);
                var side = sideColor ?? color * 0.8f;
                if (k == 0) mb.AddQuad(left - down, right - down, right, left, side);
                else mb.AddQuad(right - down, left - down, left, right, side);
            }
            prevR = right; prevL = left; prevC = c; prevOk = ok;
        }
    }

    /// <summary>Tratteggio lungo la strada: 3 unità sì e 3 no, allineato alla distanza lungo il tratto.</summary>
    private void Dashes(MeshBuilder mb, RoadEdge e, float from, float to, float offset, float width)
    {
        for (float d = MathF.Floor(from / 6f) * 6f; d < to; d += 6f)
        {
            float a = MathF.Max(d, from), b = MathF.Min(d + 3f, to);
            if (b - a > 1f) Stripe(mb, e, a, b, offset, width, Paint); // niente frammenti di trattino ai capi
        }
    }

    /// <summary>I segni dipinti di un tratto asfaltato (vedi la descrizione della classe).</summary>
    private void Markings(MeshBuilder mb, RoadEdge e)
    {
        if (!e.Paved) return;
        var (from, to) = PaintRange(e, 0.4f);
        if (to <= from) return;
        float hw = e.HalfWidth;

        switch (e.Layout)
        {
            case RoadLayout.TwoWay:
                Dashes(mb, e, from + 0.6f, to - 0.6f, 0f, 0.12f);
                if (e.Kind == RoadKind.Main)
                    foreach (float side in new[] { 1f, -1f })
                        Stripe(mb, e, from, to, side * (hw - 0.16f), 0.1f, Paint);
                break;
            case RoadLayout.Avenue:
            {
                float divider = RoadEdge.AvenueMedian * 0.5f + RoadEdge.AvenueLane;
                Dashes(mb, e, from + 0.6f, to - 0.6f, divider, 0.1f);
                Dashes(mb, e, from + 0.6f, to - 0.6f, -divider, 0.1f);
                // Spartitraffico: aiuola rialzata col cordolo, interrotta prima degli incroci (lì si gira)
                // (nessun muro invisibile: chi ci finisce contro ci sale, vedi RoadNetwork.MedianLift;
                // sui binari l'aiuola si interrompe, vedi Stripe)
                float m0 = e.A.TrimFor(e) + RoadNetwork.MedianGap, m1 = e.Length - e.B.TrimFor(e) - RoadNetwork.MedianGap;
                Stripe(mb, e, m0, m1, 0f, RoadEdge.AvenueMedian - 0.1f, MedianGrass, RoadNetwork.MedianHeight, Curb);
                break;
            }
            case RoadLayout.OneWay:
                // Frecce grandi nel verso di marcia, una ogni 8 unità (si vedono anche da dietro l'auto)
                for (float d = MathF.Floor(from / 8f) * 8f + 4f; d < to - 2.5f; d += 8f)
                {
                    if (d < from + 3.5f) continue;
                    float dir = e.OneWay >= 0 ? 1f : -1f;
                    float tail = d - dir * 1.5f, neck = d + dir * 0.6f, tip = d + dir * 1.6f;
                    Stripe(mb, e, MathF.Min(tail, neck), MathF.Max(tail, neck), 0f, 0.26f, Paint);
                    Vector3 a = EdgeLocal(e, neck, 0.5f), b = EdgeLocal(e, neck, -0.5f), c = EdgeLocal(e, tip, 0f);
                    if (!InZone(c, 0f)) continue;
                    float y = HeightLocal(c.X, c.Z) + PathRaise + PaintLift;
                    a.Y = b.Y = c.Y = y;
                    mb.AddTriangle(a, c, b, Paint);
                    mb.AddTriangle(b, c, a, Paint);
                }
                break;
        }

        // Strisce pedonali e linea d'arresto agli incroci dei centri abitati
        foreach (var node in new[] { e.A, e.B })
        {
            if (node.Degree < 3 && !node.Piazza) continue;
            Vector2 at = node.Pos;
            if (Terrain.Urban(at.X, at.Y) < Terrain.WalkFrom) continue;
            bool atA = node == e.A;
            float z0 = node.TrimFor(e) + 0.35f, z1 = z0 + 1.4f;
            if (z1 + 1f > e.Length * 0.5f) continue; // tratto troppo corto
            float s0 = atA ? z0 : e.Length - z1, s1 = atA ? z1 : e.Length - z0;
            for (float o = -hw + 0.35f; o < hw - 0.2f; o += 0.62f)
                Stripe(mb, e, s0, s1, o, 0.34f, Paint);
            // Linea d'arresto sulle corsie che entrano nell'incrocio
            int incoming = atA ? -1 : 1;
            var lanes = e.Lanes.Where(l => l.Dir == 0 || l.Dir == incoming).ToArray();
            if (lanes.Length == 0) continue;
            float lo = lanes.Min(l => l.Offset) - e.LaneHalf, hi = lanes.Max(l => l.Offset) + e.LaneHalf;
            lo = MathF.Max(lo, -hw + 0.05f);
            hi = MathF.Min(hi, hw - 0.05f);
            float q0 = atA ? z1 + 0.35f : e.Length - z1 - 0.65f;
            Stripe(mb, e, q0, q0 + 0.3f, (lo + hi) * 0.5f, hi - lo, Paint);
        }
    }

    // ------------------------------------------------------------------ arredo

    /// <summary>Alberi sullo spartitraffico dei viali e cartelli dei sensi unici (oggetti di scena, con collisioni).</summary>
    private void StreetFurniture(MeshBuilder mb, Random rng, List<(float X, float Z, float R)> placed)
    {
        foreach (var e in RoadNetwork.Current.EdgesNear(X, Z))
        {
            if (e.Layout == RoadLayout.Avenue)
            {
                var (from, to) = PaintRange(e, 2.6f);
                for (float s = MathF.Floor(from / 7f) * 7f + 3.5f; s < to; s += 7f)
                {
                    if (s < from) continue;
                    var p = EdgeLocal(e, s, 0f);
                    if (!InZone(p, 0.5f) || Railway.Current.Keepout(WX(p.X), WZ(p.Z))) continue;
                    p.Y = HeightLocal(p.X, p.Z) + PathRaise + 0.12f;
                    mb.BeginObject();
                    Props.RoundTree(mb, rng, p, 0.5f + rng.NextSingle() * 0.12f, AvenueLeaves);
                    mb.EndObject();
                    AddCollider(p.X, p.Z, 0.25f);
                    placed.Add((p.X, p.Z, 0.6f));
                }
            }
            if (e.Layout != RoadLayout.OneWay || e.OneWay == 0) continue;

            float dir = e.OneWay;
            var entry = dir > 0 ? e.A : e.B;
            var exit = dir > 0 ? e.B : e.A;
            float side = e.HalfWidth + Terrain.WalkWidth * 0.55f;
            // "Senso unico" all'inizio, sul marciapiede a destra di chi entra e rivolto verso di lui: freccia bianca
            // in su su fondo blu
            float sIn = e.ToS(entry, entry.TrimFor(e) + 2.2f);
            if (sIn > 0f && sIn < e.Length)
                Sign(mb, placed, EdgeLocal(e, sIn, dir * side), e.Tangent(sIn) * dir, oneWay: true);
            // "Divieto d'accesso" in fondo, per chi arriverebbe contromano: a destra del suo verso di marcia
            float sOut = e.ToS(exit, exit.TrimFor(e) + 2.2f);
            if (sOut > 0f && sOut < e.Length)
                Sign(mb, placed, EdgeLocal(e, sOut, -dir * side), -e.Tangent(sOut) * dir, oneWay: false);
        }
    }

    /// <summary>
    /// Un cartello su palo, rivolto verso chi arriva: <paramref name="travel"/> è il suo verso di marcia. Senso unico:
    /// quadrato blu con la freccia bianca in su; divieto d'accesso: disco rosso con la barra bianca.
    /// </summary>
    private void Sign(MeshBuilder mb, List<(float X, float Z, float R)> placed, Vector3 p, Vector2 travel, bool oneWay)
    {
        if (!InZone(p, 0.3f) || HeightLocal(p.X, p.Z) < WaterY + 0.2f || Railway.Current.Keepout(WX(p.X), WZ(p.Z))) return;
        p.Y = HeightLocal(p.X, p.Z) + PathRaise + Terrain.WalkRaise;
        float yaw = MathF.Atan2(travel.X, travel.Y); // +Z locale del cartello = verso di marcia
        Matrix4x4 old = mb.Transform;
        mb.Transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(p) * old;
        mb.BeginObject();
        // Misure vere in scala con l'auto (larga ~1,3 unità ≈ 1,8 m): palo ~2,3 m, cartello ~60 cm
        mb.AddPrism(Vector3.Zero, 0.03f, 0.027f, 1.65f, 6, Pole);
        const float y = 1.45f;
        // Chi arriva sta verso -Z: le facce dipinte guardano da quella parte
        if (oneWay)
        {
            mb.AddBox(new Vector3(0f, y, -0.035f), new Vector3(0.42f, 0.42f, 0.025f), SignBlue);
            mb.AddBox(new Vector3(0f, y - 0.04f, -0.052f), new Vector3(0.06f, 0.22f, 0.01f), SignWhite);   // asta
            mb.AddBox(new Vector3(0f, y + 0.09f, -0.052f), new Vector3(0.2f, 0.045f, 0.01f), SignWhite);   // punta
            mb.AddBox(new Vector3(0f, y + 0.13f, -0.052f), new Vector3(0.12f, 0.04f, 0.01f), SignWhite);
            mb.AddBox(new Vector3(0f, y + 0.165f, -0.052f), new Vector3(0.05f, 0.03f, 0.01f), SignWhite);
        }
        else
        {
            Matrix4x4 keep = mb.Transform;
            mb.Transform = Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, y, -0.035f) * keep;
            mb.AddPrism(new Vector3(0f, -0.0125f, 0f), 0.22f, 0.22f, 0.025f, 16, SignRed);
            mb.Transform = keep;
            mb.AddBox(new Vector3(0f, y, -0.052f), new Vector3(0.3f, 0.065f, 0.01f), SignWhite);
        }
        mb.EndObject();
        mb.Transform = old;
        AddCollider(p.X, p.Z, 0.06f);
        placed.Add((p.X, p.Z, 0.25f));
    }

    // ------------------------------------------------------------------ cartelli delle stazioni

    /// <summary>
    /// Dove la via della stazione si stacca dalla strada (o dove la stazione si affaccia su una via) c'è un cartello
    /// blu col treno bianco, dipinto sulle due facce: lo vede chi arriva da entrambi i versi della strada.
    /// </summary>
    private void StationSigns(MeshBuilder mb, List<(float X, float Z, float R)> placed)
    {
        var net = RoadNetwork.Current;
        foreach (var st in Railway.Current.Stations)
        {
            if (st.AccessJunction is not { } j || st.Forecourt is not { } f) continue;
            Vector2 world;
            if (Vector2.Distance(j, f) < 0.5f)
            {
                // La stazione si affaccia su una via: cartello sul ciglio, dalla parte della stazione
                if (!net.Nearest(j, 4f, out var hit)) continue;
                world = hit.Point + st.AccessDir * (hit.Edge.HalfWidth + Terrain.WalkWidth * 0.55f);
            }
            else
            {
                // All'imbocco della via della stazione, sul suo ciglio destro, qualche metro dopo l'incrocio
                Vector2 q = j + st.AccessDir * 7f;
                if (!net.Nearest(q, 5f, out var hit)) continue;
                Vector2 t = hit.Edge.Tangent(hit.S);
                Vector2 side = new(-t.Y, t.X);
                if (Vector2.Dot(side, st.AccessRoadDir) < 0f) side = -side; // dalla parte da cui si arriva più spesso
                world = hit.Point + side * (hit.Edge.HalfWidth + Terrain.WalkWidth * 0.55f);
            }
            var p = new Vector3(world.X - Origin.X, 0f, world.Y - Origin.Z);
            if (!InZone(p, 0f) || HeightLocal(p.X, p.Z) < WaterY + 0.2f) continue; // palo sottile: basta che stia nel pezzo
            if (Railway.Current.Distance(world.X, world.Y) < Railway.BallastHalf + 1.5f) continue;
            p.Y = HeightLocal(p.X, p.Z) + PathRaise + Terrain.WalkRaise;
            StationSign(mb, p, st.AccessRoadDir);
            AddCollider(p.X, p.Z, 0.06f);
            placed.Add((p.X, p.Z, 0.3f));
        }
    }

    /// <summary>Cartello "stazione": rettangolo blu con un treno bianco visto di fronte, sulle due facce.</summary>
    private static void StationSign(MeshBuilder mb, Vector3 p, Vector2 facing)
    {
        float yaw = MathF.Atan2(facing.X, facing.Y);
        Matrix4x4 old = mb.Transform;
        mb.Transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(p) * old;
        mb.BeginObject();
        mb.AddPrism(Vector3.Zero, 0.03f, 0.027f, 1.75f, 6, Pole);
        const float y = 1.5f;
        mb.AddBox(new Vector3(0f, y, 0f), new Vector3(0.5f, 0.56f, 0.03f), SignBlue);
        foreach (float z in new[] { -1f, 1f })
        {
            float f = z * 0.02f;
            // Treno di fronte: cassa arrotondata in cima, parabrezza blu, due fari, carrelli e rotaie
            mb.AddBox(new Vector3(0f, y + 0.03f, f), new Vector3(0.26f, 0.26f, 0.012f), SignWhite);
            mb.AddBox(new Vector3(0f, y + 0.175f, f), new Vector3(0.2f, 0.04f, 0.012f), SignWhite);
            mb.AddBox(new Vector3(0f, y + 0.08f, f * 1.4f), new Vector3(0.19f, 0.09f, 0.012f), SignBlue);
            foreach (float sx in new[] { -1f, 1f })
            {
                mb.AddBox(new Vector3(sx * 0.075f, y - 0.05f, f * 1.4f), new Vector3(0.04f, 0.035f, 0.012f), SignBlue);
                mb.AddBox(new Vector3(sx * 0.08f, y - 0.135f, f), new Vector3(0.05f, 0.05f, 0.012f), SignWhite); // ruote
                mb.AddBox(new Vector3(sx * 0.1f, y - 0.2f, f), new Vector3(0.12f, 0.02f, 0.012f), SignWhite);   // binari
            }
        }
        mb.EndObject();
        mb.Transform = old;
    }

    // ------------------------------------------------------------------ semafori

    /// <summary>Altezze (sopra il marciapiede) delle tre lenti del semaforo: rossa in alto, gialla, verde in basso.</summary>
    public static readonly float[] SignalLensY = [2.12f, 1.9f, 1.68f];
    public const float SignalLensZ = -0.115f;

    /// <summary>
    /// I semafori degli incroci che li hanno (vedi <see cref="TrafficLights.Has"/>): per ogni strada che entra
    /// nell'incrocio un palo sul marciapiede alla destra di chi arriva, poco prima delle strisce, rivolto verso di lui.
    /// Qui solo palo, cassonetto e lenti spente; quella accesa la aggiunge il gioco.
    /// </summary>
    private void TrafficSignals(MeshBuilder mb, List<(float X, float Z, float R)> placed)
    {
        var net = RoadNetwork.Current;
        for (int i = X - 1; i <= X + 1; i++)
            for (int j = Z - 1; j <= Z + 1; j++)
                foreach (var n in net.NodesIn(i, j))
                {
                    if (!TrafficLights.Has(n)) continue;
                    foreach (var e in n.Edges)
                    {
                        bool atA = e.A == n;
                        int incoming = atA ? -1 : 1; // verso di marcia (lungo il tratto) di chi arriva al nodo
                        if (!e.Lanes.Any(l => l.Dir == 0 || l.Dir == incoming)) continue; // senso unico in uscita
                        float s = e.ToS(n, n.TrimFor(e) + 0.6f);
                        if (s <= 0f || s >= e.Length) continue;
                        Vector2 travel = e.Tangent(s) * incoming;
                        var p = EdgeLocal(e, s, incoming * (e.HalfWidth + Terrain.WalkWidth * 0.6f));
                        if (!InZone(p, 0f) || HeightLocal(p.X, p.Z) < WaterY + 0.2f) continue;
                        p.Y = HeightLocal(p.X, p.Z) + PathRaise + Terrain.WalkRaise;
                        float yaw = MathF.Atan2(travel.X, travel.Y);
                        SignalPost(mb, p, yaw);
                        TrafficHeads.Add(new TrafficHead(p + Origin, yaw, n, TrafficLights.Group(n, e)));
                        AddCollider(p.X, p.Z, 0.08f);
                        placed.Add((p.X, p.Z, 0.3f));
                    }
                }
    }

    /// <summary>Palo con il cassonetto a tre lenti (spente) e le visiere; le lenti guardano verso -Z (chi arriva).</summary>
    private static void SignalPost(MeshBuilder mb, Vector3 p, float yaw)
    {
        var housing = new Vector3(0.12f, 0.13f, 0.12f);
        Vector3[] off = [new(0.30f, 0.06f, 0.05f), new(0.30f, 0.24f, 0.04f), new(0.05f, 0.24f, 0.08f)];
        Matrix4x4 old = mb.Transform;
        mb.Transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(p) * old;
        mb.BeginObject();
        mb.AddPrism(Vector3.Zero, 0.045f, 0.04f, 2.3f, 8, Pole);
        mb.AddBox(new Vector3(0f, 1.9f, 0f), new Vector3(0.28f, 0.74f, 0.2f), housing);
        mb.AddBox(new Vector3(0f, 1.9f, 0.11f), new Vector3(0.4f, 0.86f, 0.02f), housing * 0.8f); // pannello di contrasto
        for (int k = 0; k < 3; k++)
        {
            float y = SignalLensY[k];
            mb.AddBox(new Vector3(0f, y, SignalLensZ + 0.01f), new Vector3(0.15f, 0.15f, 0.02f), off[k]);
            mb.AddBox(new Vector3(0f, y + 0.09f, SignalLensZ - 0.04f), new Vector3(0.18f, 0.025f, 0.1f), housing); // visiera
        }
        mb.EndObject();
        mb.Transform = old;
    }
}
