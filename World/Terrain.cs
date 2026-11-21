using System.Numerics;

namespace Roads.World;

/// <summary>
/// Il territorio come funzioni globali del mondo: altezza, strade, fiumi e biomi dipendono solo dalle
/// coordinate, quindi due pezzi (chunk) vicini calcolano esattamente gli stessi valori sul bordo comune
/// e il terreno è un'unica superficie continua. Tutto è puro e senza stato: sicuro da qualunque thread.
/// </summary>
public static class Terrain
{
    public const float ChunkSize = Zone.Half * 2f;

    // ------------------------------------------------------------------ rumore

    private static float Quintic(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    /// <summary>Rumore a valori su reticolo, interpolato in modo morbido (0..1).</summary>
    public static float ValueNoise(float x, float z, int seed)
    {
        int ix = (int)MathF.Floor(x), iz = (int)MathF.Floor(z);
        float fx = Quintic(x - ix), fz = Quintic(z - iz);
        float a = Noise.Hash01(ix, iz, seed), b = Noise.Hash01(ix + 1, iz, seed);
        float c = Noise.Hash01(ix, iz + 1, seed), d = Noise.Hash01(ix + 1, iz + 1, seed);
        return Noise.Lerp(Noise.Lerp(a, b, fx), Noise.Lerp(c, d, fx), fz);
    }

    // ------------------------------------------------------------------ strade

    // La rete stradale segue una griglia di passo ChunkSize: incroci al centro di ogni pezzo, tratti
    // est-ovest e nord-sud tra incroci vicini (se aperti, come prima tra le isole). Ogni tratto serpeggia
    // con un'ondulazione che si annulla agli incroci, così le strade si incontrano sempre.

    private const float RoadWobble = 6f;

    private static float Wobble(float t, int a, int b, int salt)
    {
        float env = MathF.Sin(MathF.PI * t); // 0 agli incroci
        float sign = Noise.Hash01(a, b, salt) < 0.5f ? -1f : 1f;
        float amp = RoadWobble * (0.4f + 0.6f * Noise.Hash01(a, b, salt + 1));
        return env * (sign * amp + 1.4f * MathF.Sin(MathF.Tau * 2f * t + Noise.Hash01(a, b, salt + 2) * 6f));
    }

    /// <summary>
    /// Quanto serpeggia il tratto tra due incroci: in campagna tutto, in paese meno, in città le strade sono dritte.
    /// Dipende solo dai due incroci, quindi è uguale per tutti i pezzi che il tratto attraversa.
    /// </summary>
    private static float Winding(int i0, int j0, int i1, int j1)
    {
        var map = GameMap.Current;
        return 1f - MathF.Max(GameMap.Density(map.At(i0, j0)), GameMap.Density(map.At(i1, j1)));
    }

    /// <summary>Quota z della strada est-ovest senza tener conto dei ponti (solo l'ondulazione).</summary>
    private static float RawRoadZ(int j, float x)
    {
        int i = (int)MathF.Floor(x / ChunkSize);
        float t = x / ChunkSize - i;
        return j * ChunkSize + Wobble(t, i, j, 501) * Winding(i, j, i + 1, j);
    }

    // Vicino a un ponte la strada smette di serpeggiare: dritta e allineata al ponte (est-ovest)
    private const float BridgeStraight = 7f;   // tratto perfettamente dritto per lato, dal centro del ponte
    private const float BridgeBlend = 13f;     // da qui in poi torna a serpeggiare

    /// <summary>Punto x in cui il fiume della colonna attraversa la strada della riga (dove sta il ponte).</summary>
    public static float BridgeX(int column, int row)
    {
        float x = RiverX(column, row * ChunkSize);
        for (int k = 0; k < 3; k++)
            x = RiverX(column, RawRoadZ(row, x));
        return x;
    }

    /// <summary>
    /// Quota z della strada est-ovest della riga <paramref name="j"/> nel punto x: serpeggia tra gli incroci,
    /// ma attorno a ogni attraversamento di fiume si raddrizza, così entra ed esce dal ponte in linea retta.
    /// </summary>
    public static float RoadZ(int j, float x)
    {
        float z = RawRoadZ(j, x);
        int c = (int)MathF.Round(x / ChunkSize);
        for (int k = c - 1; k <= c + 1; k++)
        {
            if (!HasRiver(k)) continue;
            float xc = BridgeX(k, j), d = MathF.Abs(x - xc);
            if (d >= BridgeBlend) continue;
            z = Noise.Lerp(z, RawRoadZ(j, xc), 1f - Noise.Smooth(BridgeStraight, BridgeBlend, d));
        }
        return z;
    }

    /// <summary>Quota x della strada nord-sud della colonna <paramref name="i"/> nel punto z.</summary>
    public static float RoadX(int i, float z)
    {
        int j = (int)MathF.Floor(z / ChunkSize);
        float t = z / ChunkSize - j;
        return i * ChunkSize + Wobble(t, i, j, 701) * Winding(i, j, i, j + 1);
    }

    /// <summary>All'incrocio (i, j) le strade si incontrano (o curvano): lì c'è uno slargo rotondo.</summary>
    public static bool HasHub(int i, int j)
    {
        bool e = WorldMap.EdgeOpen(i, j, Dir.East), w = WorldMap.EdgeOpen(i, j, Dir.West);
        bool n = WorldMap.EdgeOpen(i, j, Dir.North), s = WorldMap.EdgeOpen(i, j, Dir.South);
        int count = (e ? 1 : 0) + (w ? 1 : 0) + (n ? 1 : 0) + (s ? 1 : 0);
        bool straight = count == 2 && ((e && w) || (n && s));
        return count > 0 && !straight;
    }

    /// <summary>Distanza (approssimata) dalla strada più vicina.</summary>
    public static float RoadDistance(float x, float z)
    {
        float best = float.MaxValue;

        // Tratto est-ovest della riga più vicina
        int j = (int)MathF.Round(z / ChunkSize), i = (int)MathF.Floor(x / ChunkSize);
        if (WorldMap.EdgeOpen(i, j, Dir.East))
            best = MathF.Abs(z - RoadZ(j, x));

        // Tratto nord-sud della colonna più vicina
        int ci = (int)MathF.Round(x / ChunkSize), cj = (int)MathF.Floor(z / ChunkSize);
        if (WorldMap.EdgeOpen(ci, cj, Dir.South))
            best = MathF.Min(best, MathF.Abs(x - RoadX(ci, z)));

        // Slargo all'incrocio più vicino
        if (HasHub(ci, j))
        {
            float dx = x - ci * ChunkSize, dz = z - j * ChunkSize;
            best = MathF.Min(best, MathF.Max(0f, MathF.Sqrt(dx * dx + dz * dz) - Zone.HubRadius + Zone.PathHalf));
        }
        return best;
    }

    // ------------------------------------------------------------------ fiumi

    // Fiumi nord-sud: alcune colonne hanno un fiume, spostato dalla strada nord-sud di 20-37 unità,
    // quindi non la incontra mai; attraversa invece le strade est-ovest, dove nasce un ponte.

    public static bool HasRiver(int column) => GameMap.Current.HasRiver(column); // i fiumi li decide la mappa

    public static float RiverX(int column, float z)
    {
        float offset, phase;
        if (column == 0) { offset = 14f; phase = 0.8f; } // il fiume della partenza, come prima
        else
        {
            float sign = Noise.Hash01(column, 1, 303) < 0.5f ? -1f : 1f;
            offset = sign * ChunkSize * (0.21f + 0.17f * Noise.Hash01(column, 2, 303));
            phase = Noise.Hash01(column, 3, 303) * MathF.Tau;
        }
        return column * ChunkSize + offset + 2.2f * MathF.Sin(z * 0.11f + phase) + 0.8f * MathF.Sin(z * 0.3f + phase * 2f);
    }

    public static float RiverDistance(float x, float z)
    {
        float best = float.MaxValue;
        int c = (int)MathF.Round(x / ChunkSize);
        for (int k = c - 1; k <= c + 1; k++)
            if (HasRiver(k)) best = MathF.Min(best, MathF.Abs(x - RiverX(k, z)));
        return best;
    }

    // ------------------------------------------------------------------ altezza

    private const float SeaFloor = -1.7f;

    /// <summary>
    /// Colline dolci (spianate in città), spianate lungo le strade e attorno ai punti di riferimento,
    /// scavate dai fiumi; dove la mappa dice mare scendono sotto il pelo dell'acqua.
    /// </summary>
    public static float Height(float x, float z)
    {
        var s = GameMap.Current.Sample(x, z);
        float h = 0.55f * ValueNoise(x / 38f, z / 38f, 11)
                + 0.25f * ValueNoise(x / 14f, z / 14f, 12)
                + 0.10f * ValueNoise(x / 5f, z / 5f, 13)
                + 0.05f;
        h *= 1f - 0.85f * s.Urban;
        h *= Noise.Smooth(Zone.PathHalf + 0.2f, Zone.PathHalf + 1.8f, RoadDistance(x, z));
        foreach (var l in GameMap.Current.Landmarks)
        {
            float d = Vector2.Distance(new Vector2(x, z), l.Pos);
            if (d < l.Radius + 4f) h = Noise.Lerp(l.BaseY, h, Noise.Smooth(l.Radius, l.Radius + 4f, d));
        }
        h = Noise.Lerp(SeaFloor, h, Noise.Smooth(0.36f, 0.56f, s.Land));
        float river = 1f - Noise.Smooth(Zone.RiverHalf - 0.9f, Zone.RiverHalf + 0.3f, RiverDistance(x, z));
        return Noise.Lerp(h, -0.9f, river);
    }

    // ------------------------------------------------------------------ biomi

    /// <summary>Peso di ciascun bioma nel punto (somma 1), dai quartieri della mappa sfumati tra le celle.</summary>
    public static (float Meadow, float Blossom, float Autumn, float Snowy) BiomeWeights(float x, float z)
    {
        var s = GameMap.Current.Sample(x, z);
        return (s.Meadow, s.Blossom, s.Autumn, s.Snowy);
    }

    /// <summary>Quanto il punto è costruito (0 campagna, 1 centro città), sfumato tra le celle.</summary>
    public static float Urban(float x, float z) => GameMap.Current.Sample(x, z).Urban;

    // ------------------------------------------------------------------ marciapiedi

    public const float WalkWidth = 1.0f;    // larghezza dei marciapiedi, oltre il bordo della carreggiata
    public const float WalkRaise = 0.08f;   // gradino del cordolo sopra la carreggiata
    public const float WalkFrom = 0.35f;    // da questa densità urbana in su ci sono i marciapiedi

    /// <summary>Vicino al centro di uno slargo i marciapiedi si interrompono (lì si incrociano le carreggiate).</summary>
    public static bool NearHub(float x, float z)
    {
        int i = (int)MathF.Round(x / ChunkSize), j = (int)MathF.Round(z / ChunkSize);
        if (!HasHub(i, j)) return false;
        float dx = x - i * ChunkSize, dz = z - j * ChunkSize;
        return dx * dx + dz * dz < (Zone.HubRadius + WalkWidth + 0.4f) * (Zone.HubRadius + WalkWidth + 0.4f);
    }

    /// <summary>Il punto è su un marciapiede (fascia lungo le strade dei centri abitati)?</summary>
    public static bool OnSidewalk(float x, float z)
    {
        float d = RoadDistance(x, z);
        if (d < Zone.PathHalf || d > Zone.PathHalf + WalkWidth) return false;
        return Urban(x, z) >= WalkFrom && !NearHub(x, z);
    }

    public static Biome BiomeAt(float x, float z)
    {
        var (meadow, blossom, autumn, snowy) = BiomeWeights(x, z);
        float best = meadow;
        Biome b = Biome.Meadow;
        if (blossom > best) { best = blossom; b = Biome.Blossom; }
        if (autumn > best) { best = autumn; b = Biome.Autumn; }
        if (snowy > best) b = Biome.Snowy;
        return b;
    }

    public static float Weight(Biome biome, float x, float z)
    {
        var w = BiomeWeights(x, z);
        return biome switch
        {
            Biome.Meadow => w.Meadow,
            Biome.Blossom => w.Blossom,
            Biome.Autumn => w.Autumn,
            _ => w.Snowy,
        };
    }

    public readonly record struct Palette(Vector3 Grass, Vector3 Path, Vector3 Bank);

    private static readonly Palette MeadowPal = new(new(0.34f, 0.62f, 0.15f), new(0.88f, 0.77f, 0.52f), new(0.55f, 0.50f, 0.34f));
    private static readonly Palette BlossomPal = new(new(0.45f, 0.74f, 0.26f), new(0.92f, 0.82f, 0.58f), new(0.55f, 0.50f, 0.36f));
    private static readonly Palette AutumnPal = new(new(0.58f, 0.66f, 0.22f), new(0.90f, 0.78f, 0.52f), new(0.55f, 0.48f, 0.32f));
    private static readonly Palette SnowyPal = new(new(0.90f, 0.93f, 0.97f), new(0.78f, 0.72f, 0.62f), new(0.62f, 0.66f, 0.72f));

    private static readonly Vector3 Asphalt = new(0.36f, 0.36f, 0.39f);
    private static readonly Vector3 Lawn = new(0.40f, 0.62f, 0.24f);
    private static readonly Vector3 Sand = new(0.90f, 0.83f, 0.60f);

    /// <summary>
    /// Colori del terreno nel punto, mescolati secondo i pesi dei biomi: in città le strade diventano asfalto e
    /// l'erba un prato curato; sulla costa c'è la spiaggia.
    /// </summary>
    public static Palette PaletteAt(float x, float z)
    {
        var s = GameMap.Current.Sample(x, z);
        Vector3 Mix(Func<Palette, Vector3> f) =>
            f(MeadowPal) * s.Meadow + f(BlossomPal) * s.Blossom + f(AutumnPal) * s.Autumn + f(SnowyPal) * s.Snowy;
        float town = Noise.Smooth(0.2f, 0.6f, s.Urban), beach = 1f - Noise.Smooth(0.56f, 0.68f, s.Land);
        Vector3 grass = Vector3.Lerp(Vector3.Lerp(Mix(p => p.Grass), Lawn, town * 0.6f), Sand, beach);
        Vector3 path = Vector3.Lerp(Mix(p => p.Path), Asphalt, town);
        Vector3 bank = Vector3.Lerp(Mix(p => p.Bank), Sand, beach);
        return new Palette(grass, path, bank);
    }
}
