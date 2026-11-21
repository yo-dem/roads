using System.Numerics;
using Roads.Rendering;

namespace Roads.World;

/// <summary>
/// Centri abitati: case e palazzi lungo le strade, lampioni sui marciapiedi. Quanto si costruisce dipende dalla
/// densità urbana della mappa: in paese case basse col tetto a falde, in città palazzi alti a più file.
/// Le costruzioni hanno ingombri rettangolari (muri dritti) per le collisioni.
/// </summary>
public sealed partial class Zone
{
    // ------------------------------------------------------------------ ingombri rettangolari

    private readonly List<Footprint>?[,] _boxBuckets = new List<Footprint>?[BucketCount, BucketCount];
    private readonly List<Footprint> _buildings = new();

    /// <summary>Registra un ingombro (coordinate locali) in tutti i secchielli che può toccare.</summary>
    private void AddBoxCollider(Footprint f)
    {
        float reach = f.Reach + MaxQueryRadius;
        for (int i = BucketIndex(f.Center.X - reach); i <= BucketIndex(f.Center.X + reach); i++)
            for (int j = BucketIndex(f.Center.Y - reach); j <= BucketIndex(f.Center.Y + reach); j++)
                (_boxBuckets[i, j] ??= new()).Add(f);
    }

    private bool HitsBox(float x, float z, float radius)
    {
        var bucket = _boxBuckets[BucketIndex(x), BucketIndex(z)];
        if (bucket == null) return false;
        var p = new Vector2(x, z);
        foreach (var f in bucket)
            if (f.Hits(p, radius)) return true;
        return false;
    }

    // ------------------------------------------------------------------ edifici

    private static readonly Vector3[] HouseWalls =
    [
        new(0.95f, 0.92f, 0.84f), new(0.96f, 0.84f, 0.64f), new(0.90f, 0.66f, 0.54f),
        new(0.82f, 0.88f, 0.92f), new(0.94f, 0.90f, 0.70f), new(0.86f, 0.80f, 0.92f),
    ];
    private static readonly Vector3[] CityWalls =
    [
        new(0.74f, 0.72f, 0.68f), new(0.62f, 0.64f, 0.70f), new(0.78f, 0.58f, 0.46f),
        new(0.86f, 0.82f, 0.72f), new(0.56f, 0.58f, 0.60f), new(0.70f, 0.66f, 0.58f),
    ];
    private static readonly Vector3[] Awnings =
    [
        new(0.86f, 0.24f, 0.20f), new(0.20f, 0.52f, 0.36f), new(0.22f, 0.40f, 0.72f), new(0.92f, 0.70f, 0.18f),
    ];
    private static readonly Vector3 RoofTiles = new(0.66f, 0.28f, 0.20f);
    private static readonly Vector3 Glass = new(0.28f, 0.36f, 0.46f);
    private static readonly Vector3 LitWindow = new(1.00f, 0.84f, 0.52f);
    private static readonly Vector3 DoorWood = new(0.42f, 0.28f, 0.18f);
    private static readonly Vector3 Pole = new(0.26f, 0.27f, 0.30f);
    private static readonly Vector3 Sidewalk = new(0.74f, 0.73f, 0.70f);
    private static readonly Vector3 Curb = new(0.86f, 0.86f, 0.83f);
    private static readonly Vector3 BarnRed = new(0.66f, 0.24f, 0.18f);
    private static readonly Vector3 Hay = new(0.90f, 0.76f, 0.34f);

    /// <summary>Il punto locale sta dentro il pezzo, con un margine?</summary>
    private static bool InZone(Vector3 p, float margin) => MathF.Abs(p.X) <= Half - margin && MathF.Abs(p.Z) <= Half - margin;

    /// <summary>Punto della mezzeria (coordinate locali del pezzo) e direzione del tratto alla distanza s.</summary>
    private (Vector3 Center, Vector3 Dir) RoadPoint(RoadEdge e, float s)
    {
        Vector2 p = e.At(s), t = e.Tangent(s);
        return (new Vector3(p.X - Origin.X, 0, p.Y - Origin.Z), new Vector3(t.X, 0, t.Y));
    }

    /// <summary>Case, palazzi e lampioni lungo le strade asfaltate del pezzo, dove si è in un centro abitato.</summary>
    private void BuildTown(MeshBuilder mb, MeshBuilder glow, Random rng, List<(float X, float Z, float R)> placed)
    {
        float most = 0f;
        for (int a = -1; a <= 1; a++)
            for (int b = -1; b <= 1; b++)
                most = MathF.Max(most, Terrain.Urban(Origin.X + a * Half * 0.9f, Origin.Z + b * Half * 0.9f));
        if (most < 0.15f) return;

        foreach (var e in RoadNetwork.Current.EdgesNear(X, Z))
        {
            if (!e.Paved) continue; // lungo gli sterrati niente case: ci sono le cascine, in fondo
            Lamps(mb, glow, e, placed);
            for (int row = 0; row < 4; row++)
                foreach (float side in new[] { 1f, -1f })
                    BuildingRow(mb, glow, rng, e, side, row, placed);
        }
    }

    /// <summary>
    /// Una fila di edifici affacciati sul tratto da un lato. In paese una fila di case basse coi giardini; in città
    /// palazzi uno accanto all'altro e altre file dietro, fin dentro gli isolati, più alti verso il centro.
    /// </summary>
    private void BuildingRow(MeshBuilder mb, MeshBuilder glow, Random rng, RoadEdge e, float side, int row,
                             List<(float X, float Z, float R)> placed)
    {
        float s = 1.5f + rng.NextSingle() * 3f;
        while (s < e.Length - 2f)
        {
            var (c, dir) = RoadPoint(e, s);
            if (!InZone(c, -8f)) { s += 3f; continue; }
            var perp = new Vector3(-dir.Z, 0, dir.X) * side;

            float u = Terrain.Urban(WX(c.X), WZ(c.Z));
            float chance = row switch
            {
                0 => 0.95f * Noise.Smooth(0.18f, 0.5f, u),
                1 => 0.9f * Noise.Smooth(0.62f, 0.9f, u),
                _ => (row == 2 ? 0.8f : 0.6f) * Noise.Smooth(0.8f, 0.97f, u),
            };
            if (rng.NextSingle() > chance) { s += 2.5f + rng.NextSingle() * 3f; continue; }

            bool city = u > 0.66f;
            float w = city ? 4.5f + rng.NextSingle() * 3.5f : 3.2f + rng.NextSingle() * 2f;     // lungo la strada
            float d = city ? 5f + rng.NextSingle() * 4f : 3.2f + rng.NextSingle() * 1.6f;       // in profondità
            float h = city
                ? 2.8f + rng.NextSingle() * (2.5f + 10f * Noise.Smooth(0.7f, 1f, u))
                : 1.7f + rng.NextSingle() * 1.1f;
            float setback = e.HalfWidth + Terrain.WalkWidth + (city ? 0.3f : 0.8f + rng.NextSingle() * 1.6f) + row * 11f;
            Vector3 center = c + dir * (w * 0.5f) + perp * (setback + d * 0.5f);
            float yaw = MathF.Atan2(-perp.X, -perp.Z); // il lato +Z dell'edificio guarda la strada
            var foot = Footprint.Box(new Vector2(center.X, center.Z), new Vector2(w * 0.5f, d * 0.5f), yaw);

            if (!LotFree(foot, placed)) { s += 1.5f; continue; }

            float baseY = float.MaxValue;
            foreach (var p in Corners(foot)) baseY = MathF.Min(baseY, HeightLocal(p.X, p.Y));
            BuildBuilding(mb, glow, rng, new Vector3(center.X, baseY, center.Z), yaw, w, d, h, city);
            AddBoxCollider(foot);
            _buildings.Add(foot);
            placed.Add((center.X, center.Z, foot.Reach));
            s += w + (city ? 0.2f + rng.NextSingle() * 0.8f : 1.2f + rng.NextSingle() * 3f);
        }
    }

    // ------------------------------------------------------------------ cascine

    /// <summary>
    /// Le cascine del pezzo: in fondo allo sterrato c'è l'aia, con la casa di fronte a chi arriva, il fienile rosso
    /// di fianco e qualche covone. Le cascine stanno ben dentro il pezzo (vedi la scelta dei posti nella rete).
    /// </summary>
    private void BuildFarms(MeshBuilder mb, MeshBuilder glow, Random rng, List<(float X, float Z, float R)> placed)
    {
        foreach (var farm in RoadNetwork.Current.Farms)
        {
            var yard = new Vector3(farm.Yard.X - Origin.X, 0, farm.Yard.Y - Origin.Z);
            if (!InZone(yard, 0f)) continue;
            var toRoad = new Vector3(farm.Approach.X, 0, farm.Approach.Y);
            var across = new Vector3(-toRoad.Z, 0, toRoad.X);
            const float yardR = 3.4f;

            // Casa: dietro l'aia, guarda verso lo sterrato
            Place(yard - toRoad * (yardR + 3.8f), toRoad, 5.2f, 4.2f, 2.4f, null);
            // Fienile: di lato, guarda l'aia
            float side = Noise.Hash01((int)farm.Yard.X, (int)farm.Yard.Y, 9) < 0.5f ? 1f : -1f;
            Place(yard + across * side * (yardR + 4.8f) - toRoad * 1.2f, -across * side, 6.4f, 5.4f, 3.0f, BarnRed);
            // Covoni attorno
            for (int k = 0; k < 3; k++)
            {
                var p = yard - across * side * (yardR + 2.2f + k * 1.6f) - toRoad * (1.5f + rng.NextSingle() * 2f);
                if (!InZone(p, 1f) || PathDistance(p.X, p.Z) < PathHalf + 0.8f) continue;
                p.Y = HeightLocal(p.X, p.Z) - 0.05f;
                mb.BeginObject();
                mb.AddPrism(p, 0.75f, 0.6f, 1.1f, 9, Hay);
                mb.AddPrism(p + new Vector3(0, 1.1f, 0), 0.6f, 0.15f, 0.45f, 9, Hay * 0.92f);
                mb.EndObject();
                AddCollider(p.X, p.Z, 0.7f);
                placed.Add((p.X, p.Z, 0.9f));
            }
        }

        void Place(Vector3 center, Vector3 facing, float w, float d, float h, Vector3? walls)
        {
            float yaw = MathF.Atan2(facing.X, facing.Z);
            var foot = Footprint.Box(new Vector2(center.X, center.Z), new Vector2(w * 0.5f, d * 0.5f), yaw);
            if (!LotFree(foot, placed)) return;
            float baseY = float.MaxValue;
            foreach (var p in Corners(foot)) baseY = MathF.Min(baseY, HeightLocal(p.X, p.Y));
            BuildBuilding(mb, glow, rng, new Vector3(center.X, baseY, center.Z), yaw, w, d, h, false, walls);
            AddBoxCollider(foot);
            _buildings.Add(foot);
            placed.Add((center.X, center.Z, foot.Reach));
        }
    }

    // ------------------------------------------------------------------ lampioni

    /// <summary>Lampioni sul bordo esterno dei marciapiedi, alternati sui due lati; la lampada si accende di notte.</summary>
    private void Lamps(MeshBuilder mb, MeshBuilder glow, RoadEdge e, List<(float X, float Z, float R)> placed)
    {
        const float Spacing = 13f;
        for (int k = 0; k * Spacing + 4f < e.Length - 2f; k++)
        {
            float s = k * Spacing + 4f;
            var (c, dir) = RoadPoint(e, s);
            if (!InZone(c, 0.5f)) continue;
            float wx = WX(c.X), wz = WZ(c.Z);
            if (Terrain.Urban(wx, wz) < Terrain.WalkFrom || Terrain.NearHub(wx, wz)) continue;
            float side = k % 2 == 0 ? 1f : -1f;
            Vector3 perp = new Vector3(-dir.Z, 0, dir.X) * side;
            Vector3 p = c + perp * (e.HalfWidth + Terrain.WalkWidth - 0.25f);
            if (HeightLocal(p.X, p.Z) < WaterY + 0.12f || !InZone(p, 0.5f)) continue;
            if (PathDistance(p.X, p.Z) < PathHalf + 0.2f) continue; // sulla carreggiata di un'altra via
            p.Y = HeightLocal(p.X, p.Z) + PathRaise + Terrain.WalkRaise;

            mb.AddPrism(p, 0.07f, 0.06f, 2.7f, 6, Pole);
            Vector3 arm = p + new Vector3(0, 2.62f, 0) - perp * 0.35f;
            mb.AddBox(arm, new Vector3(0.08f, 0.08f, 0.08f) + Abs(perp) * 0.62f, Pole);
            Vector3 head = p + new Vector3(0, 2.5f, 0) - perp * 0.62f;
            mb.AddBox(head + new Vector3(0, 0.1f, 0), new Vector3(0.34f, 0.08f, 0.34f), Pole);
            glow.AddBox(head, new Vector3(0.24f, 0.12f, 0.24f), LitWindow);
            AddCollider(p.X, p.Z, 0.12f);
            placed.Add((p.X, p.Z, 0.4f));
        }
    }

    private static IEnumerable<Vector2> Corners(Footprint f)
    {
        foreach (float sx in new[] { -1f, 1f })
            foreach (float sz in new[] { -1f, 1f })
                yield return f.Center + new Vector2(sx * f.Half.X * f.Cos + sz * f.Half.Y * f.Sin,
                                                    -sx * f.Half.X * f.Sin + sz * f.Half.Y * f.Cos);
    }

    /// <summary>Il lotto è edificabile? Asciutto, dentro il pezzo, lontano da strade, fiumi, punti di riferimento e altri edifici.</summary>
    private bool LotFree(Footprint foot, List<(float X, float Z, float R)> placed)
    {
        var pts = Corners(foot).ToList();
        pts.Add(foot.Center);
        for (int k = 0; k < 4; k++) pts.Add((pts[k] + pts[(k + 1) % 4]) * 0.5f); // anche il centro dei lati (contro i muri lunghi)
        foreach (var p in pts)
        {
            if (MathF.Abs(p.X) > Half - 0.6f || MathF.Abs(p.Y) > Half - 0.6f) return false;
            if (PathDistance(p.X, p.Y) < PathHalf + Terrain.WalkWidth + 0.1f) return false;
            if (RiverDistance(p.X, p.Y) < RiverHalf + 1.5f) return false;
            if (HeightLocal(p.X, p.Y) < WaterY + 0.12f) return false; // in città il terreno è spianato a quota ~0
        }
        foreach (var l in GameMap.Current.Landmarks)
            if (Vector2.Distance(new Vector2(WX(foot.Center.X), WZ(foot.Center.Y)), l.Pos) < l.Radius + foot.Reach + 1f) return false;
        foreach (var b in _buildings)
        {
            if (Vector2.Distance(b.Center, foot.Center) > b.Reach + foot.Reach + 0.3f) continue;
            foreach (var p in Corners(foot)) if (b.Hits(p, 0.15f)) return false;
            foreach (var p in Corners(b)) if (foot.Hits(p, 0.15f)) return false;
            if (b.Hits(foot.Center, 0.15f) || foot.Hits(b.Center, 0.15f)) return false;
        }
        foreach (var p in placed)
            if (foot.Hits(new Vector2(p.X, p.Z), p.R)) return false;
        return true;
    }

    /// <summary>
    /// Un edificio: muri, finestre a piani (alcune accese di notte), porta sulla strada e tetto (a falde in paese,
    /// piatto con parapetto in città). Coordinate locali del pezzo; il lato +Z dell'edificio guarda la strada.
    /// </summary>
    private static void BuildBuilding(MeshBuilder mb, MeshBuilder glow, Random rng, Vector3 basePos, float yaw,
                                      float w, float d, float h, bool city, Vector3? wallColor = null)
    {
        Matrix4x4 old = mb.Transform, oldGlow = glow.Transform;
        mb.Transform = glow.Transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(basePos);
        mb.BeginObject();
        glow.BeginObject();

        var walls = wallColor ?? (city ? CityWalls : HouseWalls)[rng.Next(6)];
        mb.AddBox(new Vector3(0, h * 0.5f - 0.3f, 0), new Vector3(w, h + 0.6f, d), walls);
        // Zoccolo un po' più scuro
        mb.AddBox(new Vector3(0, 0.12f, 0), new Vector3(w + 0.06f, 0.5f, d + 0.06f), walls * 0.78f);

        // Finestre su tutte le facciate, piano per piano
        float floorH = city ? 1.2f : 1.05f;
        int floors = Math.Max(1, (int)((h - 0.35f) / floorH));
        for (int face = 0; face < 4; face++)
        {
            bool alongX = face % 2 == 0;
            float len = alongX ? w : d, half = (alongX ? d : w) * 0.5f + 0.03f;
            float sign = face < 2 ? 1f : -1f;
            int cols = Math.Max(1, (int)((len - 0.5f) / (city ? 1.0f : 1.3f)));
            for (int f = 0; f < floors; f++)
                for (int k = 0; k < cols; k++)
                {
                    float t = -len * 0.5f + (k + 0.5f) * len / cols;
                    bool front = face == 0;
                    if (front && f == 0 && MathF.Abs(t) < 0.75f) continue; // lì c'è la porta
                    float y = 0.75f + f * floorH;
                    Vector3 c = alongX ? new Vector3(t, y, sign * half) : new Vector3(sign * half, y, t);
                    Vector3 size = alongX ? new Vector3(0.5f, 0.62f, 0.04f) : new Vector3(0.04f, 0.62f, 0.5f);
                    if (rng.NextSingle() < (city ? 0.35f : 0.45f)) glow.AddBox(c, size, LitWindow);
                    else mb.AddBox(c, size, Glass);
                }
        }

        // Porta (e in città, spesso, la tenda di un negozio)
        mb.AddBox(new Vector3(0, 0.45f, d * 0.5f + 0.03f), new Vector3(0.75f, 1.25f, 0.05f), DoorWood);
        if (city && rng.NextSingle() < 0.55f)
            mb.AddBox(new Vector3(0, 1.35f, d * 0.5f + 0.35f), new Vector3(MathF.Min(w * 0.7f, 3.2f), 0.1f, 0.7f), Awnings[rng.Next(4)]);

        if (city)
        {
            // Tetto piatto: parapetto e qualche impianto
            var top = walls * 0.85f;
            mb.AddBox(new Vector3(0, h + 0.15f, d * 0.5f - 0.08f), new Vector3(w, 0.3f, 0.16f), top);
            mb.AddBox(new Vector3(0, h + 0.15f, -d * 0.5f + 0.08f), new Vector3(w, 0.3f, 0.16f), top);
            mb.AddBox(new Vector3(w * 0.5f - 0.08f, h + 0.15f, 0), new Vector3(0.16f, 0.3f, d), top);
            mb.AddBox(new Vector3(-w * 0.5f + 0.08f, h + 0.15f, 0), new Vector3(0.16f, 0.3f, d), top);
            if (rng.NextSingle() < 0.7f)
                mb.AddBox(new Vector3((rng.NextSingle() - 0.5f) * w * 0.4f, h + 0.35f, (rng.NextSingle() - 0.5f) * d * 0.4f),
                          new Vector3(1.0f, 0.7f, 0.8f), new Vector3(0.66f, 0.68f, 0.70f));
        }
        else
        {
            // Tetto a due falde, col colmo lungo il lato più lungo
            Matrix4x4 keep = mb.Transform;
            if (d > w) mb.Transform = Matrix4x4.CreateRotationY(MathF.PI / 2f) * keep;
            float sx = d > w ? d : w, sz = d > w ? w : d;
            Gable(mb, new Vector3(0, h, 0), sx, sz, 0.55f + sz * 0.22f, RoofTiles * (0.9f + rng.NextSingle() * 0.2f));
            mb.Transform = keep;
            if (rng.NextSingle() < 0.6f)                                         // comignolo
                mb.AddBox(new Vector3(w * 0.25f, h + 0.7f, -d * 0.15f), new Vector3(0.3f, 0.8f, 0.3f), new Vector3(0.6f, 0.36f, 0.3f));
        }

        mb.EndObject();
        // Le finestre accese sfumano col palazzo: stesso ingombro, non solo quello delle finestre
        glow.EndObject(new Vector3(-w * 0.5f, -0.6f, -d * 0.5f), new Vector3(w * 0.5f, h + (city ? 0.5f : 1.6f), d * 0.5f));
        mb.Transform = old;
        glow.Transform = oldGlow;
    }

    /// <summary>Tetto a due falde sopra una pianta sx × sz, col colmo lungo x.</summary>
    private static void Gable(MeshBuilder mb, Vector3 baseCenter, float sx, float sz, float h, Vector3 color)
    {
        float hx = sx * 0.5f + 0.15f, hz = sz * 0.5f + 0.25f;
        Vector3 a = baseCenter + new Vector3(-hx, 0, -hz), b = baseCenter + new Vector3(hx, 0, -hz);
        Vector3 c = baseCenter + new Vector3(hx, 0, hz), d = baseCenter + new Vector3(-hx, 0, hz);
        Vector3 r0 = baseCenter + new Vector3(-hx, h, 0), r1 = baseCenter + new Vector3(hx, h, 0);
        mb.AddQuad(a, b, r1, r0, color);
        mb.AddQuad(c, d, r0, r1, color);
        mb.AddTriangle(d, a, r0, color * 0.85f);
        mb.AddTriangle(b, c, r1, color * 0.85f);
    }

    private static Vector3 Abs(Vector3 v) => new(MathF.Abs(v.X), MathF.Abs(v.Y), MathF.Abs(v.Z));
}
