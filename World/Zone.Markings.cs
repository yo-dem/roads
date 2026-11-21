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

    /// <summary>Distanza dal capo A (e dal capo B) da cui cominciano i segni: fuori dallo slargo dell'incrocio.</summary>
    private static (float From, float To) PaintRange(RoadEdge e, float margin)
    {
        float from = e.A.TrimFor(e) + margin;
        float to = e.Length - e.B.TrimFor(e) - margin;
        return (from, to);
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
        Vector3 prevR = default, prevL = default;
        bool prevOk = false;
        float lift = PathRaise + PaintLift + height;
        for (int k = 0; k <= n; k++)
        {
            float s = s0 + (s1 - s0) * k / n;
            Vector3 c = EdgeLocal(e, s, offset);
            Vector3 right = EdgeLocal(e, s, offset + width * 0.5f), left = EdgeLocal(e, s, offset - width * 0.5f);
            float y = HeightLocal(c.X, c.Z);
            bool ok = InZone(c, 0f) && y > WaterY + 0.2f;
            right.Y = left.Y = y + lift;
            if (k > 0 && ok && prevOk)
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
            if (height > 0f && ok && (k == 0 || k == n))
            {
                // Testate dello spartitraffico
                var down = new Vector3(0, height + 0.02f, 0);
                var side = sideColor ?? color * 0.8f;
                if (k == 0) mb.AddQuad(left - down, right - down, right, left, side);
                else mb.AddQuad(right - down, left - down, left, right, side);
            }
            prevR = right; prevL = left; prevOk = ok;
        }
    }

    /// <summary>Tratteggio lungo la strada: 3 unità sì e 3 no, allineato alla distanza lungo il tratto.</summary>
    private void Dashes(MeshBuilder mb, RoadEdge e, float from, float to, float offset, float width)
    {
        for (float d = MathF.Floor(from / 6f) * 6f; d < to; d += 6f)
        {
            float a = MathF.Max(d, from), b = MathF.Min(d + 3f, to);
            if (b > a) Stripe(mb, e, a, b, offset, width, Paint);
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
                // (nessun muro invisibile: chi ci finisce contro ci sale, vedi RoadNetwork.MedianLift)
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
                    if (!InZone(p, 0.5f)) continue;
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
        if (!InZone(p, 0.3f) || HeightLocal(p.X, p.Z) < WaterY + 0.2f) return;
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
}
