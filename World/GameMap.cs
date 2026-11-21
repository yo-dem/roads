using System.Numerics;

namespace Roads.World;

/// <summary>Carattere di una cella della mappa (un incrocio e il territorio attorno, 96 x 96).</summary>
public enum District { Sea, Meadow, Blossom, Autumn, Snowy, Town, City }

/// <summary>Valori del territorio in un punto, sfumati tra le celle vicine.</summary>
public readonly record struct MapSample(float Meadow, float Blossom, float Autumn, float Snowy, float Land, float Urban);

/// <summary>
/// La mappa disegnata a mano (file di testo in Assets/Maps): quali celle sono terra o mare e di che quartiere,
/// quali strade collegano gli incroci, dove scorrono i fiumi e dove stanno i punti di riferimento.
/// Il formato è descritto in testa al file della mappa. Fuori dalla mappa c'è il mare.
/// Gli indici (i, j) sono quelli degli incroci nel mondo: l'incrocio (0, 0) è quello della partenza.
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
    private bool[,] _east = new bool[0, 0], _south = new bool[0, 0];
    private readonly HashSet<int> _rivers = new();
    private readonly List<LandmarkSite> _landmarks = new();
    public IReadOnlyList<LandmarkSite> Landmarks => _landmarks;

    // ------------------------------------------------------------------ interrogazioni

    private bool Inside(int i, int j) => i >= MinI && j >= MinJ && i <= MaxI && j <= MaxJ;

    public District At(int i, int j) => Inside(i, j) ? _cells[i - MinI, j - MinJ] : District.Sea;

    public bool IsLand(int i, int j) => At(i, j) != District.Sea;

    /// <summary>La strada dall'incrocio (i, j) verso est esiste?</summary>
    public bool East(int i, int j) => Inside(i, j) && _east[i - MinI, j - MinJ];

    /// <summary>La strada dall'incrocio (i, j) verso sud esiste?</summary>
    public bool South(int i, int j) => Inside(i, j) && _south[i - MinI, j - MinJ];

    public bool HasRiver(int column) => _rivers.Contains(column);

    /// <summary>Quanto è costruita una cella: 0 campagna, circa 0.5 paese, 1 città.</summary>
    public static float Density(District d) => d switch { District.City => 1f, District.Town => 0.55f, _ => 0f };

    public static string DistrictName(District d) => d switch
    {
        District.Meadow => "Prato",
        District.Blossom => "Bosco fiorito",
        District.Autumn => "Bosco d'autunno",
        District.Snowy => "Valle innevata",
        District.Town => "Paese",
        District.City => "Città",
        _ => "Mare",
    };

    /// <summary>
    /// Pesi di biomi, terra e città nel punto. Ogni cella è "pura" vicino al suo incrocio e sfuma nelle vicine
    /// a metà strada; le coordinate sono un po' deformate dal rumore, così i confini (e la costa) non sono dritti.
    /// </summary>
    public MapSample Sample(float x, float z)
    {
        const float C = Terrain.ChunkSize, Warp = 16f;
        float px = x + Warp * (Terrain.ValueNoise(x / 41f, z / 41f, 31) * 2f - 1f);
        float pz = z + Warp * (Terrain.ValueNoise(x / 41f + 7.7f, z / 41f - 3.1f, 32) * 2f - 1f);
        float u = px / C, v = pz / C;
        int i0 = (int)MathF.Floor(u), j0 = (int)MathF.Floor(v);
        float fx = Noise.Smooth(0.22f, 0.78f, u - i0), fz = Noise.Smooth(0.22f, 0.78f, v - j0);

        float meadow = 0f, blossom = 0f, autumn = 0f, snowy = 0f, land = 0f, urban = 0f;
        for (int k = 0; k < 4; k++)
        {
            int di = k & 1, dj = k >> 1;
            float w = (di == 1 ? fx : 1f - fx) * (dj == 1 ? fz : 1f - fz);
            if (w <= 0f) continue;
            District d = At(i0 + di, j0 + dj);
            if (d != District.Sea) land += w;
            urban += w * Density(d);
            switch (d)
            {
                case District.Blossom: blossom += w; break;
                case District.Autumn: autumn += w; break;
                case District.Snowy: snowy += w; break;
                default: meadow += w; break; // prato, paese, città (e il mare, che non si vede)
            }
        }
        return new MapSample(meadow, blossom, autumn, snowy, land, urban);
    }

    // ------------------------------------------------------------------ caricamento

    /// <summary>Carica la mappa dal file e la rende quella in uso. Gli errori di formato fermano il gioco con un messaggio chiaro.</summary>
    public static GameMap Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Mappa non trovata: {path}");
        var map = new GameMap();
        var legend = new Dictionary<char, (LandmarkKind Kind, string Name)>();
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
                if (t.Length > 2 && char.IsUpper(t[0]) && t[1..].TrimStart().StartsWith('='))
                {
                    // Legenda dei punti di riferimento:  F = faro : Faro di Capo Sud
                    string rest = t[(t.IndexOf('=') + 1)..];
                    int sep = rest.IndexOf(':');
                    string kind = (sep >= 0 ? rest[..sep] : rest).Trim().ToLowerInvariant();
                    string name = sep >= 0 ? rest[(sep + 1)..].Trim() : kind;
                    legend[t[0]] = (ParseKind(kind, path), name);
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

        char Ch(int x, int y) => y < grid.Count && x < grid[y].Length ? grid[y][x] : ' ';

        map._w = (grid.Max(l => l.Length) + 1) / 2;
        map._h = (grid.Count + 1) / 2;
        map.MinI = -startCol;
        map.MinJ = -startRow;
        map._cells = new District[map._w, map._h];
        map._east = new bool[map._w, map._h];
        map._south = new bool[map._w, map._h];

        for (int c = 0; c < map._w; c++)
            for (int r = 0; r < map._h; r++)
                map._cells[c, r] = ParseDistrict(Ch(2 * c, 2 * r), path, c, r);

        for (int c = 0; c < map._w; c++)
            for (int r = 0; r < map._h; r++)
            {
                bool land = map._cells[c, r] != District.Sea;
                if (Ch(2 * c + 1, 2 * r) == '-')
                {
                    if (land && c + 1 < map._w && map._cells[c + 1, r] != District.Sea) map._east[c, r] = true;
                    else Console.WriteLine($"Mappa: strada verso est dalla cella ({c},{r}) finisce in mare, ignorata");
                }
                if (Ch(2 * c, 2 * r + 1) == '|')
                {
                    if (land && r + 1 < map._h && map._cells[c, r + 1] != District.Sea) map._south[c, r] = true;
                    else Console.WriteLine($"Mappa: strada verso sud dalla cella ({c},{r}) finisce in mare, ignorata");
                }
            }

        if (map.At(0, 0) == District.Sea) throw new InvalidDataException($"{path}: la partenza ({startCol},{startRow}) è in mare");
        if (!map.East(0, 0)) Console.WriteLine("Mappa: attenzione, dalla partenza non c'è una strada verso est (l'auto parte verso est)");
        foreach (int col in riverCols) map._rivers.Add(col - startCol);

        Current = map;

        // Punti di riferimento: al centro dell'isolato tra quattro incroci (lontano da strade e fiumi).
        // Le posizioni si calcolano adesso perché dipendono da strade e fiumi della mappa appena impostata.
        for (int c = 0; c < map._w; c++)
            for (int r = 0; r < map._h; r++)
            {
                char key = Ch(2 * c + 1, 2 * r + 1);
                if (key == ' ' || key == '.') continue;
                if (!legend.TryGetValue(key, out var def))
                {
                    Console.WriteLine($"Mappa: il punto di riferimento '{key}' non è nella legenda, ignorato");
                    continue;
                }
                var site = LandmarkSite.Place(key, def.Kind, def.Name, c - startCol, r - startRow);
                if (site != null) map._landmarks.Add(site);
                else Console.WriteLine($"Mappa: nessun posto libero per '{def.Name}', ignorato");
            }
        return map;
    }

    private static District ParseDistrict(char ch, string path, int c, int r) => ch switch
    {
        '~' or ' ' => District.Sea,
        'p' => District.Meadow,
        'f' => District.Blossom,
        'a' => District.Autumn,
        'n' => District.Snowy,
        's' => District.Town,
        'c' => District.City,
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
        _ => throw new InvalidDataException($"{path}: tipo di punto di riferimento sconosciuto '{kind}' "
                                            + "(validi: faro, mulino, castello, torre, antenna, campanile)"),
    };
}
