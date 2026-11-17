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
    ///   --drive sterzo,gas[,finoA] guida con la visuale da dietro (come i tasti WASD)
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
                    d.Drive = new Vector2(dv[0], dv[1]);
                    if (dv.Length > 2) d.DriveUntil = dv[2];
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
