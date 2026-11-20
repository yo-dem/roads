using System.Globalization;
using System.Numerics;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Roads;

public static class Program
{
    /// <summary>
    /// Argomenti opzionali (per test automatici):
    ///   --shot file.png@secondi   (ripetibile) salva uno screenshot e chiude dopo l'ultimo
    ///   --walk x,z[,finoA]        cammina in direzione mondo (x,z) fino al secondo indicato
    ///   --click px,py@secondi     (ripetibile) simula un click sinistro nel punto dello schermo
    ///   --slow                    passo lento (come tenere premuto Shift)
    ///   --mute                    nessun suono
    ///   --handbrake da,a          tiene tirato il freno a mano tra i due istanti (secondi)
    ///   --start x,z               parte sulla strada est-ovest più vicina al punto (test)
    ///   --char nome               personaggio di partenza (es. female-a, male-c; tasto C in gioco)
    ///   --enter secondi           (ripetibile) preme Invio: scende o sale dall'auto
    ///   --time ore                ora di partenza (es. 12 = mezzogiorno; di serie si parte alle 8:00)
    ///   --lights                  fari accesi da subito (come premere F)
    ///   --zoom distanza           distanza della visuale lontana (8..32, come la rotella)
    ///   --far                     parte con la visuale lontana dall'alto (tasto 2)
    ///   --prespawn R              crea subito le isole entro R celle (test di prestazioni)
    ///   --drive sterzo,gas[,finoA] guida con la visuale da dietro (come i tasti freccia); ripetibile, tratti in ordine
    /// </summary>
    public static void Main(string[] args)
    {
        var debug = ParseArgs(args);

        var options = WindowOptions.Default with
        {
            Size = new Vector2D<int>(1280, 720),
            Title = "Roads",
            VSync = true,
            Samples = 4,
            API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, new APIVersion(3, 3)),
        };

        using var window = Window.Create(options);
        _ = new Game(window, debug);
        window.Run();
    }

    private static DebugOptions ParseArgs(string[] args)
    {
        var d = new DebugOptions();
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--shot" when i + 1 < args.Length:
                    var parts = args[++i].Split('@');
                    d.Shots.Add((parts[0], float.Parse(parts[1], inv)));
                    break;
                case "--walk" when i + 1 < args.Length:
                    var v = args[++i].Split(',').Select(s => float.Parse(s, inv)).ToArray();
                    d.Walk = new Vector3(v[0], 0, v[1]);
                    if (v.Length > 2) d.WalkUntil = v[2];
                    break;
                case "--click" when i + 1 < args.Length:
                    var c = args[++i].Split('@');
                    var xy = c[0].Split(',').Select(s => float.Parse(s, inv)).ToArray();
                    d.Clicks.Add((new Vector2(xy[0], xy[1]), float.Parse(c[1], inv)));
                    break;
                case "--slow":
                    d.Slow = true;
                    break;
                case "--drive" when i + 1 < args.Length:
                    var dv = args[++i].Split(',').Select(s => float.Parse(s, inv)).ToArray();
                    d.Drives.Add((new Vector2(dv[0], dv[1]), dv.Length > 2 ? dv[2] : float.MaxValue));
                    break;
                case "--handbrake" when i + 1 < args.Length:
                    var hb = args[++i].Split(',').Select(s => float.Parse(s, inv)).ToArray();
                    (d.HandbrakeFrom, d.HandbrakeUntil) = (hb[0], hb[1]);
                    break;
                case "--prespawn" when i + 1 < args.Length:
                    d.PreSpawn = int.Parse(args[++i], inv);
                    break;
                case "--far":
                    d.Far = true;
                    break;
                case "--time" when i + 1 < args.Length:
                    d.Hour = float.Parse(args[++i], inv);
                    break;
                case "--zoom" when i + 1 < args.Length:
                    d.Zoom = float.Parse(args[++i], inv);
                    break;
                case "--lights":
                    d.Lights = true;
                    break;
                case "--enter" when i + 1 < args.Length:
                    d.Toggles.Add(float.Parse(args[++i], inv));
                    break;
                case "--char" when i + 1 < args.Length:
                    d.Character = args[++i];
                    break;
                case "--start" when i + 1 < args.Length:
                    var st = args[++i].Split(',').Select(s => float.Parse(s, inv)).ToArray();
                    d.Start = new Vector2(st[0], st[1]);
                    break;
                case "--mute":
                    d.Mute = true;
                    break;
            }
        }
        d.Shots.Sort((a, b) => a.Time.CompareTo(b.Time));
        d.Clicks.Sort((a, b) => a.Time.CompareTo(b.Time));
        return d;
    }
}
