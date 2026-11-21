using System.Numerics;

namespace Roads.World;

/// <summary>Carattere di una cella della mappa (96 x 96): mare o uno dei biomi.</summary>
public enum District { Sea, Meadow, Blossom, Autumn, Snowy }

/// <summary>Valori del territorio in un punto, sfumati tra le celle vicine.</summary>
public readonly record struct MapSample(float Meadow, float Blossom, float Autumn, float Snowy, float Land, float Urban);

/// <summary>
/// La mappa disegnata a mano (file di testo in Assets/Maps): quali celle sono terra o mare e di che bioma, dove
/// stanno la città, i paesi e i punti di riferimento, dove scorrono i fiumi. Le strade NON si disegnano: le traccia
/// il gioco (<see cref="RoadBuilder"/>) partendo da dove stanno città e paesi. Il formato è descritto in testa al
/// file. Fuori dalla mappa c'è il mare. Gli indici (i, j) delle celle sono relativi alla partenza (cella 0, 0).
/// </summary>
public sealed class GameMap
{
    /// <summary>La mappa in uso: va caricata una volta all'avvio, prima di generare il territorio.</summary>
    public static GameMap Current { get; private set; } = null!;

    public string Name { get; private set; } = "Isola";
    public int MinI { get; private set; }
    public int MinJ { get; private set; }
    public int MaxI => MinI + _w - 1;
    public int MaxJ => MinJ + _h - 1;

    private int _w, _h;
    private District[,] _cells = new District[0, 0];
    private readonly List<int> _rivers = new();
    private readonly List<LandmarkSite> _landmarks = new();
    public IReadOnlyList<LandmarkSite> Landmarks => _landmarks;
    public IReadOnlyList<int> RiverColumns => _rivers;

    // ------------------------------------------------------------------ interrogazioni

    private bool Inside(int i, int j) => i >= MinI && j >= MinJ && i <= MaxI && j <= MaxJ;

    public District At(int i, int j) => Inside(i, j) ? _cells[i - MinI, j - MinJ] : District.Sea;

    public bool IsLand(int i, int j) => At(i, j) != District.Sea;

    public bool HasRiver(int column) => _rivers.Contains(column);

    public static string DistrictName(District d) => d switch
    {
        District.Meadow => "Prato",
        District.Blossom => "Bosco fiorito",
        District.Autumn => "Bosco d'autunno",
        District.Snowy => "Valle innevata",
        _ => "Mare",
    };

    /// <summary>
    /// Pesi di biomi e terra nel punto (più la densità urbana dei centri abitati). Ogni cella è "pura" vicino al suo
    /// centro e sfuma nelle vicine a metà strada; le coordinate sono un po' deformate dal rumore, così i confini (e la
    /// costa) non sono dritti.
    /// </summary>
    public MapSample Sample(float x, float z)
    {
        const float C = Terrain.ChunkSize, Warp = 16f;
        float px = x + Warp * (Terrain.ValueNoise(x / 41f, z / 41f, 31) * 2f - 1f);
        float pz = z + Warp * (Terrain.ValueNoise(x / 41f + 7.7f, z / 41f - 3.1f, 32) * 2f - 1f);
        float u = px / C, v = pz / C;
        int i0 = (int)MathF.Floor(u), j0 = (int)MathF.Floor(v);
        float fx = Noise.Smooth(0.22f, 0.78f, u - i0), fz = Noise.Smooth(0.22f, 0.78f, v - j0);

        float meadow = 0f, blossom = 0f, autumn = 0f, snowy = 0f, land = 0f;
        for (int k = 0; k < 4; k++)
        {
            int di = k & 1, dj = k >> 1;
            float w = (di == 1 ? fx : 1f - fx) * (dj == 1 ? fz : 1f - fz);
            if (w <= 0f) continue;
            District d = At(i0 + di, j0 + dj);
            if (d != District.Sea) land += w;
            switch (d)
            {
                case District.Blossom: blossom += w; break;
                case District.Autumn: autumn += w; break;
                case District.Snowy: snowy += w; break;
                default: meadow += w; break; // prato (e il mare, che non si vede)
            }
        }
        return new MapSample(meadow, blossom, autumn, snowy, land, RoadNetwork.Current.Urban(x, z));
    }

    // ------------------------------------------------------------------ caricamento

    private enum SymbolKind { Landmark, City, Town }

    /// <summary>Carica la mappa dal file e la rende quella in uso. Gli errori di formato fermano il gioco con un messaggio chiaro.</summary>
    public static GameMap Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Mappa non trovata: {path}");
        var map = new GameMap();
        var legend = new Dictionary<char, (SymbolKind Symbol, LandmarkKind Kind, string Name)>();
        int startCol = -1, startRow = -1;
        var riverCols = new List<int>();
        var grid = new List<string>();
        bool inGrid = false;

        foreach (var raw in File.ReadAllLines(path))
        {
            string line = raw.TrimEnd();
            if (!inGrid)
            {
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                if (t == "[mappa]") { inGrid = true; continue; }
                int colon = t.IndexOf(':');
                if (t.Length > 2 && !char.IsWhiteSpace(t[0]) && t[1..].TrimStart().StartsWith('='))
                {
                    // Legenda:  F = faro : Faro di Capo Sud     1 = paese : Borgo Est     C = città : Roda
                    string rest = t[(t.IndexOf('=') + 1)..];
                    int sep = rest.IndexOf(':');
                    string kind = (sep >= 0 ? rest[..sep] : rest).Trim().ToLowerInvariant();
                    string name = sep >= 0 ? rest[(sep + 1)..].Trim() : kind;
                    legend[t[0]] = kind switch
                    {
                        "città" or "citta" => (SymbolKind.City, default, name),
                        "paese" or "borgo" => (SymbolKind.Town, default, name),
                        _ => (SymbolKind.Landmark, ParseKind(kind, path), name),
                    };
                }
                else if (colon > 0)
                {
                    string key = t[..colon].Trim().ToLowerInvariant(), value = t[(colon + 1)..].Trim();
                    switch (key)
                    {
                        case "nome": map.Name = value; break;
                        case "partenza":
                            var p = value.Split(',').Select(s => int.Parse(s.Trim())).ToArray();
                            (startCol, startRow) = (p[0], p[1]);
                            break;
                        case "fiumi":
                            riverCols.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s.Trim())));
                            break;
                        default: Console.WriteLine($"Mappa: voce sconosciuta '{key}' ignorata"); break;
                    }
                }
                continue;
            }
            grid.Add(line);
        }

        while (grid.Count > 0 && grid[^1].Trim().Length == 0) grid.RemoveAt(grid.Count - 1);
        if (grid.Count == 0) throw new InvalidDataException($"{path}: manca la griglia dopo [mappa]");
        if (startCol < 0) throw new InvalidDataException($"{path}: manca 'partenza: colonna, riga'");

        map._w = (grid.Max(l => l.Length) + 1) / 2;
        map._h = (grid.Count + 1) / 2;
        map.MinI = -startCol;
        map.MinJ = -startRow;
        map._cells = new District[map._w, map._h];

        // Le celle stanno nelle posizioni pari; un simbolo della legenda può stare ovunque (anche tra le celle)
        var symbols = new List<(char Key, float Col, float Row)>();
        for (int y = 0; y < grid.Count; y++)
            for (int x = 0; x < grid[y].Length; x++)
            {
                char ch = grid[y][x];
                if (legend.ContainsKey(ch))
                {
                    symbols.Add((ch, x / 2f, y / 2f));
                    if (x % 2 == 0 && y % 2 == 0) map._cells[x / 2, y / 2] = District.Meadow; // sopra una cella: prato
                    continue;
                }
                if (x % 2 == 0 && y % 2 == 0) map._cells[x / 2, y / 2] = ParseDistrict(ch, path, x / 2, y / 2);
                else if (ch is not (' ' or '-' or '|' or '.'))
                    Console.WriteLine($"Mappa: simbolo '{ch}' (riga {y + 1}) non è nella legenda, ignorato");
            }

        if (map.At(0, 0) == District.Sea) throw new InvalidDataException($"{path}: la partenza ({startCol},{startRow}) è in mare");
        foreach (int col in riverCols) map._rivers.Add(col - startCol);

        Current = map;
        const float C = Terrain.ChunkSize;
        Vector2 World(float col, float row) => new((col - startCol) * C, (row - startRow) * C);

        // 1. Centri abitati: la loro densità serve già per l'altezza del terreno (le città sono spianate)
        var net = new RoadNetwork();
        RoadNetwork.Current = net;
        foreach (var (key, col, row) in symbols)
        {
            var def = legend[key];
            if (def.Symbol == SymbolKind.Landmark) continue;
            net.Settlements.Add(new Settlement { Name = def.Name, City = def.Symbol == SymbolKind.City, Pos = World(col, row) });
        }
        foreach (var s in net.Settlements) s.Pos = FitSettlement(s);

        // 2. Punti di riferimento: attorno a dove li mette la mappa, su terra asciutta lontana dai fiumi
        foreach (var (key, col, row) in symbols)
        {
            var def = legend[key];
            if (def.Symbol != SymbolKind.Landmark) continue;
            var site = LandmarkSite.Place(key, def.Kind, def.Name, World(col, row), (int)(col * 2), (int)(row * 2));
            if (site != null) map._landmarks.Add(site);
            else Console.WriteLine($"Mappa: nessun posto libero per '{def.Name}', ignorato");
        }

        // 3. Le strade, tracciate dal gioco
        RoadBuilder.Build(net, map);
        Railway.Current = Railway.Build(net, map); // la ferrovia della città, dopo le strade
        return map;
    }

    /// <summary>
    /// Sposta un centro abitato (di poco) dove c'è spazio: terra tutto attorno e niente fiume nel mezzo del paese.
    /// La città invece resta dov'è: un fiume che la attraversa è bello (ci saranno i ponti).
    /// </summary>
    private static Vector2 FitSettlement(Settlement s)
    {
        if (s.City) return s.Pos;
        Vector2 best = s.Pos;
        float bestScore = float.MinValue;
        for (int k = 0; k < 160; k++)
        {
            float a = k * 2.39996f, d = MathF.Sqrt(k) * 6f;
            var p = s.Pos + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d;
            float land = 1f;
            for (int m = 0; m < 12; m++)
            {
                float b = m * MathF.Tau / 12f;
                var q = p + new Vector2(MathF.Cos(b), MathF.Sin(b)) * 58f;
                land = MathF.Min(land, Current.Sample(q.X, q.Y).Land);
            }
            float river = MathF.Min(Terrain.RiverDistance(p.X, p.Y), 70f);
            float score = (land >= 0.97f ? 100f : land * 50f) + river - d * 0.4f;
            if (score > bestScore) { bestScore = score; best = p; }
        }
        return best;
    }

    private static District ParseDistrict(char ch, string path, int c, int r) => ch switch
    {
        '~' or ' ' => District.Sea,
        'p' or 's' or 'c' => District.Meadow, // s/c: i vecchi paese/città, ora si mettono con la legenda
        'f' => District.Blossom,
        'a' => District.Autumn,
        'n' => District.Snowy,
        _ => throw new InvalidDataException($"{path}: carattere '{ch}' non valido nella cella ({c},{r})"),
    };

    private static LandmarkKind ParseKind(string kind, string path) => kind switch
    {
        "faro" => LandmarkKind.Lighthouse,
        "mulino" => LandmarkKind.Windmill,
        "castello" => LandmarkKind.Castle,
        "torre" or "torre dell'acqua" or "acquedotto" => LandmarkKind.WaterTower,
        "antenna" or "ripetitore" => LandmarkKind.Antenna,
        "campanile" => LandmarkKind.BellTower,
        _ => throw new InvalidDataException($"{path}: tipo sconosciuto '{kind}' nella legenda "
                                            + "(validi: città, paese, faro, mulino, castello, torre, antenna, campanile)"),
    };
}
