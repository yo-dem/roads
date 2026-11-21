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

    // Le strade sono quelle della rete (RoadNetwork): una spezzata per tratto, con slarghi agli incroci.

    /// <summary>
    /// Distanza dalla strada più vicina riportata alla larghezza di riferimento: vale <see cref="Zone.PathHalf"/> sul
    /// bordo della carreggiata qualunque sia la larghezza della strada (meno dentro, di più fuori).
    /// </summary>
    public static float RoadDistance(float x, float z) => RoadNetwork.Current.Distance(x, z);

    // ------------------------------------------------------------------ fiumi

    // Fiumi nord-sud: le colonne indicate dalla mappa hanno un fiume, spostato dal centro della colonna di
    // 20-37 unità e un po' serpeggiante. Le strade lo attraversano solo di traverso, su un ponte.

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
        h *= 1f - 0.85f * Noise.Smooth(0f, 0.5f, s.Urban);
        h *= Noise.Smooth(Zone.PathHalf + 0.2f, Zone.PathHalf + 1.8f, RoadDistance(x, z));
        h *= Noise.Smooth(Railway.BallastHalf + 0.3f, Railway.BallastHalf + 2.5f, Railway.Current.Distance(x, z));
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

    /// <summary>Quanto il punto è costruito (0 campagna, 1 centro città): dai centri abitati della rete.</summary>
    public static float Urban(float x, float z) => RoadNetwork.Current.Urban(x, z);

    /// <summary>Quanto il punto è bosco fitto (macchie e fasce tra i paesi).</summary>
    public static float Forest(float x, float z) => RoadNetwork.Current.Forest(x, z);

    // ------------------------------------------------------------------ marciapiedi

    public const float WalkWidth = 1.0f;    // larghezza dei marciapiedi, oltre il bordo della carreggiata
    public const float WalkRaise = 0.08f;   // gradino del cordolo sopra la carreggiata
    public const float WalkFrom = 0.35f;    // da questa densità urbana in su ci sono i marciapiedi

    /// <summary>Vicino a uno slargo (incrocio, piazza) i marciapiedi si interrompono: lì si incrociano le carreggiate.</summary>
    public static bool NearHub(float x, float z) => RoadNetwork.Current.HubAt(new Vector2(x, z), 0f) != null;

    /// <summary>Il punto è su un marciapiede (fascia lungo le strade asfaltate dei centri abitati)?</summary>
    public static bool OnSidewalk(float x, float z)
    {
        var p = new Vector2(x, z);
        if (!RoadNetwork.Current.Nearest(p, 6f, out var hit) || !hit.Edge.Paved) return false;
        float d = hit.Distance - hit.Edge.HalfWidth;
        if (d < 0f || d > WalkWidth) return false;
        if (RoadDistance(x, z) < Zone.PathHalf) return false; // sulla carreggiata di un'altra strada
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

    public static readonly Vector3 Asphalt = new(0.36f, 0.36f, 0.39f);
    public static readonly Vector3 Paving = new(0.70f, 0.64f, 0.56f); // lastricato delle piazze
    private static readonly Vector3 Lawn = new(0.40f, 0.62f, 0.24f);
    private static readonly Vector3 Concrete = new(0.60f, 0.59f, 0.56f);
    private static readonly Vector3 Sand = new(0.90f, 0.83f, 0.60f);

    /// <summary>
    /// Colori del terreno nel punto, mescolati secondo i pesi dei biomi: in città l'erba è un prato curato, sulla
    /// costa c'è la spiaggia. Path è la terra battuta degli sterrati (le strade asfaltate hanno il loro colore).
    /// </summary>
    public static Palette PaletteAt(float x, float z)
    {
        var s = GameMap.Current.Sample(x, z);
        Vector3 Mix(Func<Palette, Vector3> f) =>
            f(MeadowPal) * s.Meadow + f(BlossomPal) * s.Blossom + f(AutumnPal) * s.Autumn + f(SnowyPal) * s.Snowy;
        float town = Noise.Smooth(0.2f, 0.6f, s.Urban), beach = 1f - Noise.Smooth(0.56f, 0.68f, s.Land);
        // Nel cuore della città niente prati: cortili e slarghi lastricati tra i palazzi
        float core = Noise.Smooth(0.68f, 0.86f, s.Urban);
        Vector3 grass = Vector3.Lerp(Vector3.Lerp(Vector3.Lerp(Mix(p => p.Grass), Lawn, town * 0.6f), Concrete, core), Sand, beach);
        Vector3 bank = Vector3.Lerp(Mix(p => p.Bank), Sand, beach);
        return new Palette(grass, Mix(p => p.Path), bank);
    }
}
