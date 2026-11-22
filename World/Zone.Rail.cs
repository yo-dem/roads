using System.Numerics;
using Roads.Rendering;

namespace Roads.World;

/// <summary>
/// La ferrovia nel pezzo di territorio: massicciata di pietrisco, traversine e rotaie (sulle strade solo le rotaie,
/// annegate nell'asfalto) e le stazioni con banchina, pensilina, cartello ed edificio. Ogni pezzetto di binario lo
/// disegna il pezzo in cui cade il suo punto di mezzo: niente doppioni né buchi sui bordi.
/// </summary>
public sealed partial class Zone
{
    private const float RailStep = 0.7f;
    private static readonly Vector3 Gravel = new(0.52f, 0.50f, 0.47f);
    private static readonly Vector3 Sleeper = new(0.32f, 0.24f, 0.18f);
    private static readonly Vector3 Steel = new(0.62f, 0.63f, 0.66f);
    private static readonly Vector3 PlatformStone = new(0.72f, 0.70f, 0.66f);
    private static readonly Vector3 SafetyLine = new(0.95f, 0.82f, 0.20f);

    /// <summary>Punto del binario alla distanza s, scostato verso destra (del verso di marcia), in coordinate locali.</summary>
    private Vector3 RailLocal(Railway rail, float s, float offset)
    {
        Vector2 c = rail.At(s), t = rail.Tangent(s);
        Vector2 p = c + new Vector2(-t.Y, t.X) * offset;
        return new Vector3(p.X - Origin.X, 0f, p.Y - Origin.Z);
    }

    private bool MidInZone(Railway rail, float s0, float s1) => InZone(RailLocal(rail, (s0 + s1) * 0.5f, 0f), 0f);

    /// <summary>Striscia lungo il binario tra s0 e s1, allo scostamento dato, alta <paramref name="top"/> sul terreno, con i fianchi.</summary>
    private void RailStrip(MeshBuilder mb, Railway rail, float s0, float s1, float offset, float width, float top, float sideDrop,
                           Vector3 color, Vector3 side)
    {
        Vector3 a0 = RailLocal(rail, s0, offset - width * 0.5f), a1 = RailLocal(rail, s0, offset + width * 0.5f);
        Vector3 b0 = RailLocal(rail, s1, offset - width * 0.5f), b1 = RailLocal(rail, s1, offset + width * 0.5f);
        float ya = HeightLocal((a0.X + a1.X) * 0.5f, (a0.Z + a1.Z) * 0.5f) + top;
        float yb = HeightLocal((b0.X + b1.X) * 0.5f, (b0.Z + b1.Z) * 0.5f) + top;
        a0.Y = a1.Y = ya;
        b0.Y = b1.Y = yb;
        var down = new Vector3(0, sideDrop, 0);
        mb.AddQuad(a1, b1, b0, a0, color);
        mb.AddQuad(a0 - down, b0 - down, b0, a0, side);
        mb.AddQuad(b1 - down, a1 - down, a1, b1, side);
        mb.AddQuad(b0 - down, a0 - down, a0, b0, side); // l'altra faccia (si vede da entrambi i lati)
        mb.AddQuad(a1 - down, b1 - down, b1, a1, side);
    }

    /// <summary>Il binario nel pezzo.</summary>
    private void BuildRail(MeshBuilder mb)
    {
        var rail = Railway.Current;
        if (!rail.Exists) return;
        // Il binario passa di qui? (controllo veloce al centro e agli angoli allargati)
        bool near = false;
        foreach (var (dx, dz) in new[] { (0f, 0f), (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f), (0f, 1f), (0f, -1f), (1f, 0f), (-1f, 0f) })
            if (rail.Distance(Origin.X + dx * Half * 0.75f, Origin.Z + dz * Half * 0.75f) < Half * 0.6f) near = true;
        if (!near && !rail.Points.Any(p => MathF.Abs(p.X - Origin.X) <= Half && MathF.Abs(p.Y - Origin.Z) <= Half)) return;

        int n = (int)MathF.Ceiling(rail.Length / RailStep);
        float step = rail.Length / n;
        for (int k = 0; k < n; k++)
        {
            float s0 = k * step, s1 = s0 + step;
            if (!MidInZone(rail, s0, s1)) continue;
            Vector3 mid = RailLocal(rail, (s0 + s1) * 0.5f, 0f);
            if (HeightLocal(mid.X, mid.Z) < WaterY + 0.15f) continue;
            bool onRoad = PathDistance(mid.X, mid.Z) < PathHalf + 0.4f;
            if (onRoad)
            {
                // Sulla strada: solo le rotaie, appena sopra l'asfalto
                foreach (float g in new[] { -Railway.Gauge, Railway.Gauge })
                    RailStrip(mb, rail, s0, s1, g, 0.1f, PathRaise + 0.035f, 0.04f, Steel * 1.1f, Steel * 0.7f);
                continue;
            }
            RailStrip(mb, rail, s0, s1, 0f, Railway.BallastHalf * 2f, Railway.BallastTop, Railway.BallastTop + 0.08f, Gravel, Gravel * 0.82f);
            // Traversina (una per passo) e rotaie
            Vector3 sc = RailLocal(rail, s0 + step * 0.5f, 0f);
            Vector2 t = rail.Tangent(s0 + step * 0.5f);
            sc.Y = HeightLocal(sc.X, sc.Z) + Railway.BallastTop + 0.03f;
            Matrix4x4 old = mb.Transform;
            mb.Transform = Matrix4x4.CreateRotationY(MathF.Atan2(t.X, t.Y)) * Matrix4x4.CreateTranslation(sc) * old;
            mb.AddBox(Vector3.Zero, new Vector3(1.55f, 0.07f, 0.24f), Sleeper);
            mb.Transform = old;
            foreach (float g in new[] { -Railway.Gauge, Railway.Gauge })
                RailStrip(mb, rail, s0, s1, g, 0.09f, Railway.RailTop, Railway.RailTop - Railway.BallastTop, Steel * 1.1f, Steel * 0.7f);
        }
    }

    /// <summary>Le stazioni del pezzo: banchina, pensilina con panchina, cartello col fondo blu e un edificio.</summary>
    private void BuildStations(MeshBuilder mb, MeshBuilder glow, Random rng, List<(float X, float Z, float R)> placed)
    {
        var rail = Railway.Current;
        foreach (var st in rail.Stations)
        {
            float off = st.Side * (Railway.BallastHalf + 0.2f + Railway.PlatformWidth * 0.5f);
            float H = st.HalfLength;
            // Banchina (a pezzetti, ognuno nel suo pezzo di territorio)
            for (float s = st.S - H; s < st.S + H - 0.01f; s += 1f)
            {
                float s1 = MathF.Min(s + 1f, st.S + H);
                if (!MidInZone(rail, s, s1)) continue;
                RailStrip(mb, rail, s, s1, off, Railway.PlatformWidth, Railway.PlatformHeight, Railway.PlatformHeight + 0.1f,
                          PlatformStone, PlatformStone * 0.78f);
                float edge = st.Side * (Railway.BallastHalf + 0.2f + 0.22f);
                RailStrip(mb, rail, s, s1, edge, 0.12f, Railway.PlatformHeight + 0.01f, 0.01f, SafetyLine, SafetyLine);
            }
            // (Sulla banchina si sale a piedi, vedi PathLift; le auto no, vedi Railway.OnPlatform in Car)

            // Pensilina con panchina, a metà banchina
            var mid = RailLocal(rail, st.S, off);
            Vector2 t = rail.Tangent(st.S);
            float yaw = MathF.Atan2(t.X, t.Y);
            if (InZone(mid, 0f)) // (anche vicino al bordo del pezzo: lo costruisce il pezzo che ne contiene il centro)
            {
                float y = HeightLocal(mid.X, mid.Z) + Railway.PlatformHeight;
                Matrix4x4 old = mb.Transform;
                mb.Transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(mid.X, y, mid.Z) * old;
                mb.BeginObject();
                foreach (float z in new[] { -2.6f, 2.6f })
                    mb.AddPrism(new Vector3(st.Side * 0.45f, 0, z), 0.05f, 0.05f, 2.0f, 6, Pole);
                mb.AddBox(new Vector3(st.Side * 0.15f, 2.05f, 0), new Vector3(1.6f, 0.1f, 6.2f), new Vector3(0.30f, 0.42f, 0.36f));
                mb.AddBox(new Vector3(st.Side * 0.45f, 0.35f, 0), new Vector3(0.45f, 0.08f, 2.2f), DoorWood);        // panchina
                mb.AddBox(new Vector3(st.Side * 0.65f, 0.6f, 0), new Vector3(0.06f, 0.4f, 2.2f), DoorWood);
                // Cartello della stazione: fondo blu con una riga bianca, su due paletti
                foreach (float z in new[] { 4.4f, 5.6f })
                    mb.AddPrism(new Vector3(st.Side * 0.6f, 0, z), 0.03f, 0.03f, 1.5f, 6, Pole);
                mb.AddBox(new Vector3(st.Side * 0.6f, 1.45f, 5f), new Vector3(0.04f, 0.38f, 1.5f), SignBlue);
                mb.AddBox(new Vector3(st.Side * 0.6f - st.Side * 0.03f, 1.45f, 5f), new Vector3(0.02f, 0.08f, 1.2f), SignWhite);
                mb.EndObject();
                mb.Transform = old;
            }

            // Fabbricato viaggiatori dietro la banchina, rivolto verso i binari (posto scelto da Railway)
            if (st.BuildingPos is { } bw)
            {
                var b = new Vector3(bw.X - Origin.X, 0f, bw.Y - Origin.Z);
                if (!InZone(b, 0f)) continue;
                var foot = Footprint.Box(new Vector2(b.X, b.Z), new Vector2(RailStation.BuildingHalfLength, RailStation.BuildingHalfDepth), st.BuildingYaw);
                float baseY = Corners(foot).Min(p => HeightLocal(p.X, p.Y));
                BuildBuilding(mb, glow, rng, new Vector3(b.X, baseY, b.Z), st.BuildingYaw, 7.2f, 4.4f, 2.7f, false, new Vector3(0.93f, 0.84f, 0.62f));
                AddBoxCollider(foot);
                _buildings.Add(foot);
                placed.Add((b.X, b.Z, foot.Reach));
            }
        }
    }
}
