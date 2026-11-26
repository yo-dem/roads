using System.Numerics;
using Roads.Audio;
using Roads.Characters;
using Roads.Rendering;
using Roads.World;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using Shader = Roads.Rendering.Shader;

namespace Roads;

/// <summary>Opzioni per test automatici: camminata simulata e screenshot a tempi prefissati.</summary>
public sealed class DebugOptions
{
    public List<(string Path, float Time)> Shots { get; } = new();
    public Vector3? Walk;
    public float WalkUntil = float.MaxValue;
    /// <summary>Guida manuale simulata, a tratti in sequenza: X = sterzo, Y = gas, fino all'istante Until.</summary>
    public List<(Vector2 Drive, float Until)> Drives { get; } = new();
    public float HandbrakeFrom = float.MaxValue, HandbrakeUntil = float.MaxValue; // freno a mano simulato (test)
    public bool Slow;
    public bool Mute;
    public string? Character; // personaggio di partenza (test)
    public List<float> Toggles { get; } = new(); // istanti in cui premere E (test)
    public Vector2? Start;         // punto di partenza (test): x e riga della strada est-ovest
    public float? Hour;            // ora di partenza (test)
    public bool Lights;            // fari accesi da subito (test)
    public float? Zoom;            // distanza della visuale lontana (test; la rotella va da 8 a 32)
    public bool Far;               // parte con la visuale lontana
    public int PreSpawn;
    public string? Map;            // file della mappa (di serie Assets/Maps/isola.txt)
    public bool BigMap;            // parte con la mappa grande aperta (test)
    public Vector2? Waypoint;      // punto da raggiungere già scelto (test)
    public bool VerifyRoads;       // controlla tutte le strade e esce (test)
    public float PilotUntil;       // fino a questo istante lo sterzo tiene l'auto in corsia (test)
    public bool FollowTraffic;     // la camera segue un'auto del traffico (test)
    public bool Reckless;          // il traffico ignora semafori e precedenze: incastri negli incroci (test)
    public bool Profile;
    public int? Msaa;              // campioni di antialiasing (di serie 4)           // misura i tempi di ogni fase del fotogramma
    public bool HitTest;           // l'auto parte puntata contro il passante più vicino (test)
    public bool StealTest;         // scende e ruba un'auto del traffico (test)
    public bool RamTraffic;        // l'auto parte di fianco a un'auto del traffico, puntata contro (test)
    public Roads.Characters.BodyStyle? BodyStyle; // il traffico ha solo questa carrozzeria (test)
    public bool AimPole;           // l'auto parte puntata contro il palo abbattibile più vicino (test)
    public bool Platform;          // appena a piedi, il personaggio va sulla banchina dove è fermo il treno (test)
    public List<float> Jumps { get; } = new();                    // istanti in cui premere spazio a piedi (test)
    public List<(Vector2 Move, float Until)> Moves { get; } = new(); // WASD simulati: X = A/D, Y = S/W (test)
    public List<(float PixelsPerSecond, float Until)> MouseSteer { get; } = new(); // mouse simulato in auto (test)
    public List<(float PixelsPerSecond, float Until)> MouseY { get; } = new();     // mouse simulato in verticale (test)
}

public sealed partial class Game : IDisposable
{
    private const float StartHour = 8f;    // il gioco parte di mattina
    private DayCycle _day = null!;
    private Hud _hud = null!;

    private readonly IWindow _window;
    private readonly DebugOptions _debug;
    private GL _gl = null!;
    private IInputContext _input = null!;
    private IKeyboard _keyboard = null!;
    private Shader _lit = null!, _depth = null!, _sky = null!;
    private Shader _litFull = null!, _litSolid = null!; // con e senza discard (vedi Shaders.LitFragmentSolid); _lit è quello in uso
    private uint _skyVao;
    private Clouds _clouds = null!;
    private readonly List<DrawItem> _cloudItems = new();
    private ShadowMap _shadow = null!;
    // Ombre in cache: la parte fissa (terreno, case, alberi) si ridisegna solo quando serve; a ogni fotogramma la si
    // copia nella mappa usata dallo shader e ci si aggiungono le cose che si muovono
    private ShadowMap _shadowStatic = null!;
    private Vector3? _shadowCenter;
    private Vector3 _shadowLight;
    private Matrix4x4 _shadowSpace;
    private (int, int) _shadowZones;
    private int _shadowRedos; // per il profilo
    private const float ShadowSlack = 4f; // di quanto la camera può spostarsi prima di ridisegnare la parte fissa
    private Mesh _flame = null!;
    private bool _rightHeld;
    private Vector2 _lastMouse;
    private int _toggleIndex;
    private bool _farView;        // visuale scelta: false = dietro l'auto (standard, tasto 1), true = lontana dall'alto (tasto 2)

    private WorldMap _world = null!;
    private Car _player = null!;
    // Le auto lasciate dal giocatore (la sua di partenza dopo averne rubata un'altra, e così via): restano dove sono,
    // si possono urtare e riprendere con E. Le più lontane, oltre un certo numero, vengono rimosse.
    private readonly List<Car> _parked = new();
    private const int MaxParked = 5;
    private const float StealSpeed = 2.5f; // si ruba solo un'auto che va piano (o è ferma)
    private readonly List<Car> _owned = new();
    private int _stealStep; // test del furto
    private Villager _kid = null!;   // il personaggino: scende e sale dall'auto con E
    private bool _onFoot;

    // Chi si sta comandando in questo momento: l'auto o il personaggino a piedi
    private Vector3 ActivePos => _onFoot ? _kid.Position : _player.Position;
    private float ActiveYaw => _onFoot ? _kid.Yaw : _player.Yaw;
    private Vector3 ActiveVelocity => _onFoot ? _kid.Velocity : _player.Velocity;
    private Particles _particles = null!;
    private Trains? _trains;
    private Traffic? _traffic;   // le auto guidate dal gioco
    private Pedestrians? _peds;  // i passanti nei centri abitati
    private Mesh[] _signalLit = [];   // lente accesa del semaforo: rossa, gialla, verde (vedi TrafficLights.Light)
    private SkidMarks _skids = null!;
    private EngineSound? _engine;
    private readonly IsoCamera _camera = new();

    private readonly List<DrawItem> _opaque = new();
    private readonly List<DrawItem> _water = new();
    private readonly List<DrawItem> _ghost = new(); // il personaggio quando è trasparente (guardando il cielo da sotto)
    private float _kidAlpha = 1f;
    private readonly Random _rng = new();
    private float _time;
    private double _titleTimer;
    private int _frames;                 // per gli FPS mostrati nel titolo
    private double _frameTime, _worstFrame;
    private int _shotIndex;

    public Game(IWindow window, DebugOptions debug)
    {
        _window = window;
        _debug = debug;
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Closing += Dispose; // il contesto GL è ancora valido qui
    }

    private void OnLoad()
    {
        _gl = _window.CreateOpenGL();
        _input = _window.CreateInput();
        _keyboard = _input.Keyboards[0];
        SetupGamepads();
        _keyboard.KeyDown += (_, key, _) =>
        {
            if (key == Key.Escape) _window.Close();
            // La visuale cambia solo con questi tasti e resta quella scelta
            // Tasti: E sali/scendi, F fari, H clacson (vedi sotto), V visuale, Shift corsa, Ctrl passo lento, spazio salto
            if (key is Key.Number1 or Key.Keypad1) _farView = false;
            if (key is Key.Number2 or Key.Keypad2) _farView = true;
            if (key == Key.V) _farView = !_farView;
            if (key == Key.F && !_onFoot) _player.Headlights = !_player.Headlights; // i fari si comandano solo da dentro l'auto
            if (key is Key.E or Key.Enter or Key.KeypadEnter) ToggleOnFoot(); // E (o Invio): sali o scendi dall'auto
            if (key == Key.Space && _onFoot) _jumpQueued = true; // a piedi lo spazio fa saltare (in auto è il freno a mano)
            if (key == Key.M) _bigMap = !_bigMap; // mappa dell'isola a tutto schermo
            if (key == Key.C) // cambia personaggio (con Shift: il precedente)
            {
                _kid.CycleCharacter(_keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight) ? -1 : 1);
                Console.WriteLine($"Personaggio: {_kid.Character}");
            }
        };
        // Il mouse serve solo alla visuale: rotella = distanza, tasto destro trascinato = rotazione
        foreach (var mouse in _input.Mice)
        {
            mouse.Scroll += (_, wheel) => _camera.Distance = Math.Clamp(_camera.Distance - wheel.Y * 1.2f, 8f, 32f);
            mouse.MouseMove += (_, pos) =>
            {
                Vector2 delta = pos - _lastMouse;
                _lastMouse = pos;
                if (_mouseSkip) { _mouseSkip = false; return; } // appena catturato/liberato il puntatore salta
                if (_mouseCaptured)
                {
                    // A piedi (e in treno) il mouse gira la visuale, senza tenere premuto niente
                    LookAround(delta);
                    return;
                }
                // Visuale lontana: tasto destro premuto e trascinando si ruota la visuale
                if (_rightHeld)
                {
                    _camera.Yaw -= delta.X * 0.008f;
                    _camera.Pitch = Math.Clamp(_camera.Pitch + delta.Y * 0.005f, 0.3f, 1.4f);
                }
            };
            mouse.MouseDown += (m, button) =>
            {
                if (button == MouseButton.Left && _bigMap) MapClick(m.Position); // sulla mappa: punto da seguire
                if (button != MouseButton.Right) return;
                _rightHeld = true;
                _lastMouse = m.Position;
            };
            mouse.MouseUp += (_, button) =>
            {
                if (button == MouseButton.Right) _rightHeld = false;
            };
        }

        _gl.Enable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Multisample);

        _litFull = new Shader(_gl, Shaders.LitVertex, Shaders.LitFragment);
        _litSolid = new Shader(_gl, Shaders.LitVertex, Shaders.LitFragmentSolid);
        _lit = _litFull;
        _depth = new Shader(_gl, Shaders.DepthVertex, Shaders.DepthFragment);
        _sky = new Shader(_gl, Shaders.SkyVertex, Shaders.SkyFragment);
        _skyVao = _gl.GenVertexArray();
        _clouds = new Clouds(_gl);
        _shadow = new ShadowMap(_gl, 2048);
        _shadowStatic = new ShadowMap(_gl, 2048);
        _hud = new Hud(_gl);
        _flame = Props.BuildFlame(_gl);
        _day = new DayCycle(_debug.Hour ?? StartHour);

        // La mappa disegnata a mano va caricata prima di generare qualunque pezzo di territorio
        var map = GameMap.Load(_debug.Map ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Maps", "isola.txt"));
        Console.WriteLine($"Mappa: {map.Name} ({map.MaxI - map.MinI + 1} x {map.MaxJ - map.MinJ + 1} celle, "
                          + $"{map.Landmarks.Count} punti di riferimento)");
        _bigMap = _debug.BigMap;
        _waypoint = _debug.Waypoint;
        if (_debug.VerifyRoads)
        {
            // Si generano (in parallelo, senza scheda grafica) tutti i pezzi attraversati da strade
            var cells = RoadNetwork.Current.Edges.SelectMany(e => e.Points)
                .Select(p => ((int)MathF.Round(p.X / Terrain.ChunkSize), (int)MathF.Round(p.Y / Terrain.ChunkSize)))
                .Distinct().ToList();
            var zones = cells.AsParallel().Select(c => new Zone(c.Item1, c.Item2)).ToList();
            var holes = zones.SelectMany(z => z.RoadHoles()).ToList();
            Console.WriteLine($"Verifica strade: {cells.Count} pezzi, {holes.Count} punti di strada mancanti");
            // Cuciture: per ogni tratto, i pezzi che lo disegnano devono coprirlo tutto, da un capo all'altro
            int seams = 0;
            foreach (var e in RoadNetwork.Current.Edges)
            {
                var spans = zones.SelectMany(z => z.RoadRuns(e)).Where(r => r.Count >= 2).Select(r => (A: r[0], B: r[^1]))
                                 .OrderBy(r => r.A).ToList();
                float reached = 0f;
                foreach (var (a, b) in spans)
                {
                    if (a > reached + 0.05f && seams++ < 30)
                        Console.WriteLine($"  cucitura scoperta nel tratto {e.Id} ({e.Kind}) da s={reached:F2} a {a:F2}, a {e.At(reached)}");
                    reached = MathF.Max(reached, b);
                }
                if (reached < e.Length - 0.05f && seams++ < 30)
                    Console.WriteLine($"  cucitura scoperta in fondo al tratto {e.Id} ({e.Kind}) da s={reached:F2} a {e.Length:F2}, a {e.At(reached)}");
            }
            Console.WriteLine($"Verifica cuciture: {seams} tratti di strada non disegnati da nessun pezzo");
            // Incroci e giunzioni: la carreggiata prolungata fino al centro dev'essere tutta coperta (niente buchi a V)
            int gapNodes = 0;
            foreach (var n in RoadNetwork.Current.Nodes)
            {
                var gaps = Zone.JunctionGaps(n).ToList();
                if (gaps.Count == 0) continue;
                if (gapNodes++ < 30)
                    Console.WriteLine($"  incrocio scoperto a {n.Pos.X:F1}, {n.Pos.Y:F1} ({n.Degree} strade, {gaps.Count} punti, "
                                      + $"es. {gaps[0].X:F1}, {gaps[0].Y:F1})");
            }
            Console.WriteLine($"Verifica incroci: {gapNodes} incroci con buchi nella carreggiata");
            VerifyAvenueUTurns();
            var defects = RoadNetwork.Current.FindDefects();
            Console.WriteLine($"Verifica geometria: {defects.Count} difetti");
            foreach (var d in defects.Take(30)) Console.WriteLine("  " + d);
            foreach (var g in holes.GroupBy(h => (h.Edge.Id, (int)(h.Point.X / 8f), (int)(h.Point.Y / 8f))).Take(40))
            {
                var h = g.First();
                Console.WriteLine($"  buco a {h.Point.X:F1}, {h.Point.Y:F1} ({h.Edge.Kind}, tratto {h.Edge.Id}, {g.Count()} punti, "
                                  + $"fiume a {Terrain.RiverDistance(h.Point.X, h.Point.Y):F1}, quota {Terrain.Height(h.Point.X, h.Point.Y):F2})");
            }
            _window.Close();
            return;
        }

        // Si parte sulla strada più vicina alla cella di partenza (o al punto chiesto dai test), rivolti verso la città
        var (start, startYaw) = StartOnRoad(_debug.Start ?? Vector2.Zero);
        _world = new WorldMap(_gl, start);
        if (_debug.PreSpawn > 0) _world.PreSpawn(_debug.PreSpawn);
        _particles = new Particles(_gl);
        _trains = new Trains(_gl, Railway.Current);
        _traffic = new Traffic(_gl) { OnlyStyle = _debug.BodyStyle, LogHits = _debug.RamTraffic, TrackOverlap = _debug.Shots.Count > 0,
                                     Reckless = _debug.Reckless };
        _peds = new Pedestrians(_gl);
        // Lenti accese dei semafori (una mesh per colore), appena davanti a quelle spente del cassonetto
        Vector3[] litColor = [new(1f, 0.16f, 0.1f), new(1f, 0.72f, 0.08f), new(0.2f, 1f, 0.45f)];
        _signalLit = new Mesh[3];
        for (int k = 0; k < 3; k++) // nell'ordine di TrafficLights.Light: rossa in alto, gialla, verde in basso
        {
            var sb = new MeshBuilder();
            sb.AddBox(new Vector3(0f, Zone.SignalLensY[k], Zone.SignalLensZ - 0.004f), new Vector3(0.15f, 0.15f, 0.02f), litColor[k]);
            _signalLit[k] = sb.Build(_gl);
        }
        _skids = new SkidMarks(_gl);
        start.Y = _world.GroundHeight(start);
        _player = new Car(_gl, start, startYaw);
        _kid = new Villager(_gl, start, 0f, _debug.Character ?? Villager.DefaultCharacter); // seduto in auto finché non si scende
        _farView = _debug.Far;
        if (_debug.Zoom is { } zoom) _camera.Distance = Math.Clamp(zoom, 8f, 32f);
        _player.Headlights = _debug.Lights;
        _camera.Snap(_player.Position, chase: !_farView, _player.Yaw);
        if (!_debug.Mute) _engine = new EngineSound();
    }


    private const float FieldOfView = MathF.PI / 180f * 40f;
    // Dentro il vagone la visuale si allarga (come l'occhio in uno spazio stretto); seduti ancora di più
    private const float CabinFov = MathF.PI / 180f * 62f, SeatFov = MathF.PI / 180f * 72f;
    private float _fov = FieldOfView;

    // Lontano abbastanza da vedere i punti di riferimento all'altro capo dell'isola; vicino abbastanza per i vagoni
    private Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(_fov, aspect, _onTrain ? 0.08f : 0.3f, 1200f);

    // ------------------------------------------------------------------ profilo (--profilo)
    private readonly System.Diagnostics.Stopwatch _prof = new();
    private readonly Dictionary<string, double> _profMs = new();
    private int _profFrames;
    private double _profClock;
    private readonly Dictionary<string, long> _profTris = new();
    private readonly Dictionary<string, double> _frameMs = new(), _worstMs = new();
    private double _worstTotal;
    private long _allocAtReport;
    private int _gc0, _gc1, _gc2;
    private long _trisAtMark;
    private void Mark(string phase)
    {
        if (!_debug.Profile) return;
        _profMs[phase] = _profMs.GetValueOrDefault(phase) + _prof.Elapsed.TotalMilliseconds;
        _frameMs[phase] = _frameMs.GetValueOrDefault(phase) + _prof.Elapsed.TotalMilliseconds;
        _profTris[phase] = _profTris.GetValueOrDefault(phase) + (Mesh.Triangles - _trisAtMark);
        _trisAtMark = Mesh.Triangles;
        _prof.Restart();
    }

    private void ProfileReport(double deltaTime)
    {
        if (!_debug.Profile) return;
        double total = _frameMs.Values.Sum();
        if (total > _worstTotal)
        {
            _worstTotal = total;
            _worstMs.Clear();
            foreach (var kv in _frameMs) _worstMs[kv.Key] = kv.Value;
        }
        _frameMs.Clear();
        _profFrames++;
        _profClock += deltaTime;
        if (_profClock < 3.0) return;
        string parts = string.Join("  ", _profMs.Select(kv => $"{kv.Key} {kv.Value / _profFrames:F2}"));
        Console.WriteLine($"Profilo: {_profFrames / _profClock:F0} FPS, ms per fotogramma: {parts}; oggetti {_opaque.Count}, "
                          + $"ombre fisse ridisegnate {_shadowRedos} volte su {_profFrames}, "
                          + $"{Mesh.Triangles / _profFrames / 1000} mila triangoli e {Mesh.DrawCalls / _profFrames} disegni per fotogramma");
        Console.WriteLine("  triangoli (mila) per fase: " + string.Join("  ", _profTris.Where(kv => kv.Value > 0)
                              .Select(kv => $"{kv.Key} {kv.Value / _profFrames / 1000}")));
        _profTris.Clear();
        Console.WriteLine($"  fotogramma peggiore {_worstTotal:F1} ms: " + string.Join("  ", _worstMs.Where(kv => kv.Value > 0.5)
                              .Select(kv => $"{kv.Key} {kv.Value:F1}")));
        _worstTotal = 0;
        long alloc = GC.GetTotalAllocatedBytes();
        Console.WriteLine($"  memoria allocata {(alloc - _allocAtReport) / _profClock / 1e6:F1} MB/s, raccolte gen0/1/2: "
                          + $"{GC.CollectionCount(0) - _gc0}/{GC.CollectionCount(1) - _gc1}/{GC.CollectionCount(2) - _gc2}");
        _allocAtReport = alloc;
        (_gc0, _gc1, _gc2) = (GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2));
        Mesh.Triangles = 0;
        _trisAtMark = 0;
        Mesh.DrawCalls = 0;
        _shadowRedos = 0;
        _profMs.Clear();
        _profFrames = 0;
        _profClock = 0;
    }

    private void OnUpdate(double deltaTime)
    {
        _prof.Restart();
        float dt = (float)Math.Min(deltaTime, 0.05);
        _time += dt;

        // Rotazione della visuale lontana
        if (_keyboard.IsKeyPressed(Key.Q)) _camera.Yaw -= 1.8f * dt;
        if (_keyboard.IsKeyPressed(Key.R)) _camera.Yaw += 1.8f * dt; // (E sale e scende dall'auto)

        // Tastiera = guida "da gioco di auto" in entrambe le visuali: sterzo e gas relativi all'auto
        // Guida con le frecce o con WASD (W gas, S freno/retro, A/D sterzo): i due insiemi si possono usare insieme
        bool K(Key a, Key b) => _keyboard.IsKeyPressed(a) || _keyboard.IsKeyPressed(b);
        float steerIn = (K(Key.Right, Key.D) ? 1f : 0f) - (K(Key.Left, Key.A) ? 1f : 0f);
        float throttleIn = (K(Key.Up, Key.W) ? 1f : 0f) - (K(Key.Down, Key.S) ? 1f : 0f);
        bool slow = _keyboard.IsKeyPressed(Key.ControlLeft) || _keyboard.IsKeyPressed(Key.ControlRight); // passo lento
        bool sprint = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight); // a piedi: corsa
        // A piedi: WASD o frecce (e spazio per saltare)
        float moveX = (K(Key.D, Key.Right) ? 1f : 0f) - (K(Key.A, Key.Left) ? 1f : 0f);
        float moveY = (K(Key.W, Key.Up) ? 1f : 0f) - (K(Key.S, Key.Down) ? 1f : 0f);
        // Controller (si somma alla tastiera): vedi Game.Gamepad.cs
        var pad = ReadPad();
        steerIn = Math.Clamp(steerIn + pad.SteerX, -1f, 1f);
        throttleIn = Math.Clamp(throttleIn + pad.Gas - pad.Brake, -1f, 1f);
        slow |= pad.Slow && _onFoot; // (in auto la levetta poco inclinata sterza piano, non rallenta)
        sprint |= pad.Sprint;
        moveX = Math.Clamp(moveX + pad.MoveX, -1f, 1f);
        moveY = Math.Clamp(moveY + pad.MoveY, -1f, 1f);
        PadLook(pad.Look, dt);
        if (pad.ZoomIn || pad.ZoomOut)
            _camera.Distance = Math.Clamp(_camera.Distance + (pad.ZoomOut ? 12f : -12f) * dt, 8f, 32f);
        bool jumpPressed = _jumpQueued || (_debug.Jumps.Count > 0 && _time >= _debug.Jumps[0] && _debug.Jumps.Remove(_debug.Jumps[0]));
        _jumpQueued = false;
        if (_debug.Moves.Count > 0 && _time > 0.5f)
            foreach (var (move, until) in _debug.Moves)
                if (_time < until) { (moveX, moveY) = (move.X, move.Y); break; }
        UpdateMouseCapture();
        // In auto, con la visuale da vicino, anche il mouse sterza (si somma ai tasti)
        steerIn = Math.Clamp(steerIn + MouseSteering(dt), -1f, 1f);
        if (_debug.MouseSteer.Count > 0 && _time > 0.5f)
            foreach (var (px, until) in _debug.MouseSteer)
                if (_time < until) { LookAround(new Vector2(px * dt, 0f)); break; }
        if (_debug.MouseY.Count > 0 && _time > 0.5f)
            foreach (var (py, until) in _debug.MouseY)
                if (_time < until) { LookAround(new Vector2(0f, py * dt)); break; }
        Vector3 input = Vector3.Zero;

        if (_debug.Walk is { } walk && _time > 0.5f && _time < _debug.WalkUntil)
            input = walk;
        if (_time > 0.5f)
            foreach (var (drive, until) in _debug.Drives)
                if (_time < until) { (steerIn, throttleIn) = (drive.X, drive.Y); break; }
        if (_time < _debug.PilotUntil && !_onFoot) steerIn = PilotSteer(); // test: si resta in corsia come un giocatore
        if (_debug.Slow) slow = true;
        while (_toggleIndex < _debug.Toggles.Count && _time >= _debug.Toggles[_toggleIndex])
        {
            ToggleOnFoot();
            _toggleIndex++;
        }

        if (_debug.AimPole && _time > 0.3f && !_onFoot)
        {
            // Test: l'auto sulla strada a 10 unità dal palo più vicino, puntata contro di lui
            _debug.AimPole = false;
            Vector3? best = null;
            foreach (var zone in _world.Zones)
                foreach (var pole in zone.PoleBases())
                    if (best == null || Vector3.DistanceSquared(pole, _player.Position) < Vector3.DistanceSquared(best.Value, _player.Position))
                        best = pole;
            if (best is { } target && RoadNetwork.Current.Nearest(new Vector2(target.X, target.Z), 8f, out var hit))
            {
                Vector2 t = hit.Edge.Tangent(hit.S), from = hit.Point - t * 10f;
                Vector2 aim = Vector2.Normalize(new Vector2(target.X, target.Z) - from);
                _player.Place(new Vector3(from.X, _player.Position.Y, from.Y), MathF.Atan2(aim.X, aim.Y));
                Console.WriteLine($"Test: auto puntata contro il palo in {target.X:F1}, {target.Z:F1}");
            }
        }

        if (_debug.HitTest && _time > 0.4f && !_onFoot && _peds?.Nearest(_player.Position) is { } walker)
        {
            // Test: l'auto a 9 unità dal passante, sulla strada se possibile, puntata contro di lui
            _debug.HitTest = false;
            Vector3 origin = walker;
            for (int i = 0; i < 16; i++)
            {
                float a = i * MathF.Tau / 16f;
                Vector3 p = walker + new Vector3(MathF.Sin(a), 0f, MathF.Cos(a)) * 9f;
                if (_world.IsRoad(p) && _world.IsWalkable(p, 0.7f)) { origin = p; break; }
            }
            _player.Place(origin with { Y = _world.GroundHeight(origin) }, MathF.Atan2(walker.X - origin.X, walker.Z - origin.Z));
            Console.WriteLine($"Test: auto puntata contro un passante in {walker.X:F1}, {walker.Z:F1}");
        }

        if (_debug.StealTest && _traffic != null)
        {
            // Test del furto: l'auto del traffico più vicina si ferma, si scende, si va alla sua portiera e si preme E
            if (_stealStep == 0 && _time > 0.4f && !_onFoot && _traffic.Followed(_player.Position) is not null)
            {
                _traffic.FreezeFollowed();
                ToggleOnFoot();
                _stealStep = 1;
            }
            else if (_stealStep == 1 && _time > 1.3f && _traffic.Followed(_player.Position) is { } target)
            {
                Vector3 left = new(MathF.Cos(target.Yaw), 0f, -MathF.Sin(target.Yaw));
                foreach (float k in new[] { 1f, -1f })
                {
                    Vector3 spot = target.Pos + left * (1.25f * k);
                    if (!_world.IsWalkable(spot, Villager.Radius)) continue;
                    _kid.Position = spot with { Y = _world.GroundHeight(spot) };
                    break;
                }
                _stealStep = 2;
            }
            else if (_stealStep == 2 && _time > 1.8f)
            {
                Console.WriteLine($"Test: furto {(_traffic.CanSteal(new Vector2(_kid.Position.X, _kid.Position.Z), StealSpeed) ? "possibile" : "NON possibile")}");
                ToggleOnFoot();
                _stealStep = 3;
            }
        }

        if (_debug.RamTraffic && _time > 0.3f && !_onFoot && _traffic?.Followed(_player.Position) is { } victim)
        {
            // Test: l'auto a 7 unità di fianco a quella del traffico, puntata contro di lei (anticipandone la marcia)
            _debug.RamTraffic = false;
            _traffic.FreezeFollowed();
            Vector3 side = new(MathF.Cos(victim.Yaw), 0f, -MathF.Sin(victim.Yaw));
            Vector3 aim = victim.Pos + new Vector3(MathF.Sin(victim.Yaw), 0f, MathF.Cos(victim.Yaw)) * 3f;
            // Di fianco, dal lato dove c'è strada libera; se non c'è, dietro (tamponamento)
            Vector3 origin = victim.Pos - new Vector3(MathF.Sin(victim.Yaw), 0f, MathF.Cos(victim.Yaw)) * 8f;
            foreach (float k in new[] { 1f, -1f })
            {
                Vector3 p = aim + side * (7f * k);
                if (_world.IsRoad(p) && _world.IsWalkable(p, 0.6f)) { origin = p; break; }
            }
            _player.Place(origin with { Y = _world.GroundHeight(origin) }, MathF.Atan2(aim.X - origin.X, aim.Z - origin.Z));
            Console.WriteLine($"Test: auto puntata contro un'auto del traffico in {victim.Pos.X:F1}, {victim.Pos.Z:F1}");
        }

        if (_onFoot)
        {
            // L'auto resta parcheggiata a motore spento.
            if (_debug.Platform && !_kid.Busy && !_onTrain && !_boarding && _trains != null
                && _trains.AlightPoint(out var platform, out float platformYaw))
            {
                // Test: subito sulla banchina, girati verso il treno
                _kid.Position = platform;
                _kid.Yaw = _footYaw = platformYaw + MathF.PI;
                _debug.Platform = false;
            }
            // A piedi come in GTA 5: WASD o frecce (levetta sinistra) rispetto alla visuale, e il personaggio si volta
            // sempre da solo dove va: con indietro si gira e corre verso la camera, non indietreggia mai. Shift (✕) corre,
            // Ctrl (levetta appena inclinata) va piano. Il mouse (levetta destra) gira la visuale attorno a lui.
            if (_onTrain) input = Vector3.Zero; // a bordo cammina RideTrain
            else if (input == Vector3.Zero)
                input = _camera.Right * moveX + _camera.Forward * moveY;
            if (!_onTrain && jumpPressed) _kid.Jump();
            _player.Handbrake = false;
            _player.Update(dt, Vector2.Zero, false, _world, _particles);
            if (!_onTrain) _kid.Update(dt, input, slow, _world, _particles, BlockedByOwnCars, sprint: sprint && !slow);
            if (_kid.Hidden && _boarding)
            {
                // Il saltello sulla carrozza è finito: si è dentro, davanti alla porta, rivolti verso i sedili
                _boarding = false;
                _onTrain = true;
                _seat = -1;
                _cabin = new Vector2(0f, Trains.DoorZ - 0.15f);
                _cabinYaw = _cabinLook = MathF.PI;
                _kid.SizeScale = Trains.InsideScale; // dentro si è più piccoli: la carrozza diventa grande
                if (_trains != null) _trains.Inside = true;
                Console.WriteLine("In treno");
            }
            else if (_kid.Hidden && !_onTrain)
            {
                // Il saltello per rientrare è finito: si torna a guidare
                _onFoot = false;
                _player.EngineOn = true;
            }
        }
        else
        {
            // Guida con le frecce; rilasciati i tasti l'auto prosegue per inerzia, senza frenare da sola.
            // Freno a mano (spazio): tirato in velocità con lo sterzo fa entrare in curva in derapata
            _player.Handbrake = _keyboard.IsKeyPressed(Key.Space) || pad.Handbrake
                                || (_time >= _debug.HandbrakeFrom && _time < _debug.HandbrakeUntil);
            _player.Update(dt, new Vector2(steerIn, throttleIn), slow, _world, _particles);
        }
        // Il treno avanza; se finisce addosso all'auto o al personaggio li sposta di lato
        _trains?.Update(dt);
        if (_trains != null)
        {
            _trains.PushOut(ref _player.Position, 1.05f);
            if (_onTrain) RideTrain(dt, moveX, moveY, sprint, slow, jumpPressed);
            else if (_onFoot && !_boarding) _trains.PushOut(ref _kid.Position, Villager.Radius);
        }
        Mark("guida");
        // Il traffico gira attorno a chi si comanda; il personaggio conta come ostacolo solo quando è a terra
        // Le auto parcheggiate restano lì (spinte da un urto scivolano e si fermano)
        foreach (var car in _parked) car.Update(dt, Vector2.Zero, false, _world, _particles);
        _owned.Clear();
        _owned.Add(_player);
        _owned.AddRange(_parked);
        // I passanti non attraversano le auto del giocatore; le auto del traffico si fermano per loro
        _peds?.Update(dt, _world, _particles, ActivePos, BlockedByOwnCars, driver: _onFoot ? null : _player, makeWay: _traffic?.MakeWay);
        _traffic?.Update(dt, _time, _world, _owned, _onFoot && !_onTrain && !_kid.Hidden ? _kid.Position : null, ActivePos,
                         _peds?.Positions);
        Mark("traffico+passanti");
        Zone? spawned = _world.Update(dt, ActivePos);
        Mark("territorio");
        if (spawned != null)
            Console.WriteLine($"Nuovo territorio ({spawned.X},{spawned.Z}): {spawned.Name}");

        // Scintille dai falò (solo quelli che si possono vedere)
        foreach (var zone in _world.Zones)
        {
            if (!InView(ZoneArea(zone))) continue;
            foreach (var c in zone.Campfires)
                if (_rng.NextSingle() < dt * 7f)
                    _particles.Ember(zone.Origin + c + new Vector3(0, zone.YOffset, 0));
        }

        _particles.Update(dt);
        _skids.Update(dt, _player);
        // La camera anticipa un po' nella direzione di marcia, di più quando si va veloci
        // In derapata la camera da dietro segue la direzione di marcia, così si vede l'auto di traverso
        // A piedi la visuale da dietro segue il personaggio come se fosse una mini auto
        // In treno la camera sta dietro la carrozza, larga come dietro l'auto (walk: false)
        _fov = Noise.Lerp(_fov, !_onTrain ? FieldOfView : _seat >= 0 ? SeatFov : CabinFov, Noise.Damp(6f, dt));
        // In treno la visuale è dentro la carrozza (la decide RideTrain); altrimenti quella normale
        if (!_onTrain)
        {
            _camera.Unfix();
            // Come in GTA la camera, se il mouse è fermo, torna piano alle spalle di chi corre in avanti o di lato; quando
            // invece indietreggia verso la camera non gira: arretra con lui, che resta di spalle
            _mouseLookIdle += dt;
            Vector3 kv = _kid.Velocity with { Y = 0f };
            float follow = 0f, heading = _onFoot ? _kid.Yaw : _player.TravelYaw;
            if (_onFoot && _mouseLookIdle > 0.6f && kv.LengthSquared() > 0.5f)
                follow = Noise.Smooth(-0.2f, 0.5f, Vector3.Dot(Vector3.Normalize(kv), _camera.Forward));
            Vector3 camAt = ActivePos + ActiveVelocity * 0.3f;
            if (_debug.FollowTraffic && _traffic?.Followed(ActivePos) is { } car) (camAt, heading) = car;
            _camera.Update(dt, camAt, chase: !_farView, heading,
                           walk: _onFoot, follow: follow);
            AvoidWalls(dt);
            // A piedi la camera può scendere quasi a terra (guardando in su): mai sotto il terreno, e la visuale passa
            // sopra prati e scarpate tra lei e il personaggio
            if (!_onFoot) _camera.Floor = _camFloor = float.MinValue;
            else
            {
                float floor = TerrainFloor();
                _camFloor = floor >= _camFloor ? floor : Noise.Lerp(_camFloor, floor, Noise.Damp(4f, dt));
                _camera.Floor = _camFloor;
            }
            // Guardando il cielo da sotto il personaggio sta davanti alla camera: diventa quasi trasparente
            _kidAlpha = 1f - 0.8f * Noise.Smooth(0.06f, 0.4f, _camera.LookUp);
        }
        UpdateMap(dt);
        if (_engine != null) _engine.Horn = !_onFoot && (_keyboard.IsKeyPressed(Key.H) || pad.Horn); // clacson: H (solo in auto)
        _engine?.Update(dt, _player.Rpm, _player.Throttle, running: _player.EngineOn, skid: _player.Skid);

        _day.Update(dt);

        _titleTimer += deltaTime;
        if (_titleTimer > 0.25)
        {
            _titleTimer = 0;
            var here = _world.ZoneAt(ActivePos);
            // Prestazioni sempre visibili: FPS medi e fotogramma più lento dall'ultimo aggiornamento
            string perf = _frameTime > 0 ? $"{_frames / _frameTime:F0} FPS (max {_worstFrame * 1000:F0} ms)" : "";
            _frames = 0;
            _frameTime = _worstFrame = 0;
            // Vicino a un punto di riferimento se ne legge il nome: così si impara la mappa
            string near = "";
            foreach (var l in GameMap.Current.Landmarks)
                if (Vector2.Distance(l.Pos, new Vector2(ActivePos.X, ActivePos.Z)) < 45f) near = $"  ({l.Name})";
            string place = RoadNetwork.Current.SettlementAt(ActivePos.X, ActivePos.Z)?.Name
                           ?? (here != null ? GameMap.DistrictName(GameMap.Current.At(here.X, here.Z)) : "...");
            _window.Title = $"Roads  |  {_day.Clock}  |  {place}{near}  |  territorio esplorato: {_world.Count}"
                            + (_onTrain ? TrainStatus() : _onFoot ? "  |  a piedi" + BoardHint() : "")
                            + (_player.Headlights ? "  |  fari accesi" : "") + $"  |  {perf}";
        }
    }

    /// <summary>Gli uniform del fotogramma per lo shader in uso (<see cref="_lit"/>): camera, luci, cielo, lampioni.</summary>
    private void SetupLit(Matrix4x4 proj, Matrix4x4 lightSpace, Vector3 lightDir)
    {
        _lit.Use();
        _lit.SetFloat("uAlpha", 1f);
        _lit.SetMatrix("uProj", proj);
        _lit.SetMatrix("uView", _camera.View);
        _lit.SetMatrix("uLightSpace", lightSpace);
        _lit.SetVector3("uLightDir", lightDir);
        _lit.SetVector3("uLightColor", _day.LightColor);
        _lit.SetVector3("uAmbientLow", _day.AmbientLow);
        _lit.SetVector3("uAmbientHigh", _day.AmbientHigh);
        SetSkyColors(_lit);
        _lit.SetInt("uHeadOn", _player.Headlights ? 1 : 0);
        _lit.SetVector3("uHeadPos[0]", _player.HeadlightPos(-1f));
        _lit.SetVector3("uHeadPos[1]", _player.HeadlightPos(1f));
        _lit.SetVector3("uHeadDir[0]", _player.HeadlightDir(-1f));
        _lit.SetVector3("uHeadDir[1]", _player.HeadlightDir(1f));
        // Fari e bagliore degli stop: pieni di notte; di giorno il sole li copre (niente terreno bruciato sul bianco)
        float darkness = 1f - _day.Daylight;
        _lit.SetFloat("uHeadGlow", 0.2f + 0.8f * darkness * darkness);
        _lit.SetVector3("uTailPos", _player.TailLightPos);
        _lit.SetVector3("uTailDir", _player.TailLightDir);
        _lit.SetFloat("uTailGlow", darkness * ((_player.Headlights ? 0.25f : 0f) + 0.9f * _player.BrakeLight));
        SetLamps();
        _lit.SetVector3("uCameraPos", _camera.Position);
        _lit.SetVector3("uFocus", ActivePos + new Vector3(0, _onFoot ? 0.35f : 0.45f, 0));
        _lit.SetInt("uOnFoot", _onFoot ? 1 : 0);
        _lit.SetInt("uCamInside", _cameraInside && !_onTrain ? 1 : 0);

        _farFog = null;
        SetFog(false);
        _lit.SetFloat("uTime", _time);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _shadow.Texture);
        _lit.SetInt("uShadowMap", 0);

        _lit.SetInt("uWater", 0);
        _lit.SetInt("uCloud", 0);
    }

    private const int MaxLamps = 24; // come uLampPos nello shader
    private readonly float[] _lampXyz = new float[MaxLamps * 3];
    private readonly List<(float D, Vector3 P)> _lampNear = new();

    /// <summary>Di notte i lampioni più vicini alla visuale fanno luce vera sulla strada (pozze di luce calda).</summary>
    private void SetLamps()
    {
        float glow = Noise.Smooth(0.3f, 0.75f, 1f - _day.Daylight);
        _lampNear.Clear();
        if (glow > 0.01f)
        {
            // Centro un po' avanti alla camera: lì c'è ciò che si vede
            Vector3 c = _camera.Target + _camera.Forward * 10f;
            foreach (var zone in _world.Zones)
                foreach (var p in zone.LampLights)
                {
                    float d = Vector2.DistanceSquared(new(p.X, p.Z), new(c.X, c.Z));
                    if (d < 45f * 45f) _lampNear.Add((d, p));
                }
            _lampNear.Sort((a, b) => a.D.CompareTo(b.D));
        }
        _lampGlow = glow;
        _lampArea = new Vector4(float.NaN);
        LampsFor(default);
        _lit.SetFloat("uLampGlow", glow);
    }

    private float _lampGlow;
    private Vector4 _lampArea = new(float.NaN);

    /// <summary>
    /// Di notte ogni pezzo di territorio riceve solo i lampioni che possono illuminarlo (entro la portata della loro
    /// pozza di luce): lo shader ne scorre pochi invece di tutti quelli vicini alla camera. Gli oggetti senza ingombro
    /// (auto, personaggi) ricevono i più vicini alla camera.
    /// </summary>
    private void LampsFor(in DrawItem item)
    {
        if (_lampGlow <= 0.01f) return;
        Vector4 area = item.HasArea ? item.Area : default;
        if (area == _lampArea) return;
        _lampArea = area;
        int n = 0;
        foreach (var (_, p) in _lampNear)
        {
            if (item.HasArea && RectDistance(new Vector2(p.X, p.Z), area) > 8.6f) continue;
            _lampXyz[n * 3] = p.X;
            _lampXyz[n * 3 + 1] = p.Y;
            _lampXyz[n * 3 + 2] = p.Z;
            if (++n == MaxLamps) break;
        }
        _lit.SetInt("uLampCount", n);
        if (n > 0) _lit.SetVector3Array("uLampPos[0]", _lampXyz.AsSpan(0, n * 3));
    }

    private void CollectScene(Matrix4x4 viewProj)
    {
        _fading.Clear();
        _viewProj = viewProj;
        _opaque.Clear();
        _water.Clear();
        _ghost.Clear();
        // Finestre, lampioni e lanterne: accesi col buio
        float night = Noise.Smooth(0.3f, 0.75f, 1f - _day.Daylight);

        foreach (var zone in _world.Zones)
        {
            // Si disegna solo ciò che è entro la nebbia E dentro l'inquadratura
            Vector4 area = ZoneArea(zone);
            float lift = zone.YOffset;
            if (!InView(area) || !InFrustum(area, -5f + MathF.Min(lift, 0f), 8f + MathF.Max(lift, 0f))) continue;
            Matrix4x4 m = zone.Model;
            _opaque.Add(new DrawItem(zone.Opaque, m, Area: area, Static: true));
            _opaque.Add(new DrawItem(zone.Scenery, m, SeeThrough: true, Area: area, Static: true));
            if (NearSightLine(area) && AnyFading(zone))
            {
                _fading.Add(zone.Scenery);
                if (zone.Glow != null) _fading.Add(zone.Glow);
            }
            zone.CollectMovables(_opaque, area); // rocce e cespugli spostati, disegnati a parte
            if (zone.Water != null) _water.Add(new DrawItem(zone.Water, m, Area: area));
            if (zone.Glow != null)
                _opaque.Add(new DrawItem(zone.Glow, m, 0.1f + 0.9f * night, CastShadow: false, SeeThrough: true, Area: area));
            // Semafori: per ogni testa la lente accesa in questo istante
            foreach (var h in zone.TrafficHeads)
            {
                var light = TrafficLights.State(h.Node, h.Group, _time);
                _opaque.Add(new DrawItem(_signalLit[(int)light],
                                         Matrix4x4.CreateRotationY(h.Yaw) * Matrix4x4.CreateTranslation(h.World), 1f, CastShadow: false));
            }

            for (int i = 0; i < zone.Campfires.Count; i++)
            {
                float t = _time * 1f + i;
                var flicker = Matrix4x4.CreateScale(1f + 0.12f * MathF.Sin(t * 11f), 0.9f + 0.2f * MathF.Sin(t * 17f + 1f), 1f + 0.12f * MathF.Cos(t * 13f));
                _opaque.Add(new DrawItem(_flame, flicker * Matrix4x4.CreateRotationY(t * 0.8f)
                                                 * Matrix4x4.CreateTranslation(zone.Campfires[i] + new Vector3(0, 0.1f, 0)) * m,
                                         0.9f, CastShadow: false));
            }
        }

        // Punti di riferimento: si disegnano sempre, ma la nebbia li copre come tutto il resto
        foreach (var l in _world.Landmarks) l.Collect(_opaque, _time, night);
        // Treno e passaggi a livello (solo quelli entro la vista)
        var camFlat = new Vector2(_camera.Position.X, _camera.Position.Z);
        _trains?.Collect(_opaque, night, p => Vector2.Distance(p, camFlat) < ViewRange + 12f);
        _traffic?.Collect(_opaque, night, p => Vector2.Distance(p, camFlat) < ViewRange + 4f);
        _peds?.Collect(_opaque, p => Vector2.Distance(p, camFlat) < ViewRange);

        _cloudItems.Clear();
        _clouds.Collect(_cloudItems, _camera.Target, _time);

        _player.Collect(_opaque);
        foreach (var car in _parked)
            if (Vector2.Distance(new Vector2(car.Position.X, car.Position.Z), camFlat) < ViewRange + 4f) car.Collect(_opaque);
        _kid.Collect(_kidAlpha < 0.99f ? _ghost : _opaque);
        _particles.Collect(_opaque);
    }

    private unsafe void OnRender(double deltaTime)
    {
        Mark("resto aggiornamento");
        _frames++;
        _frameTime += deltaTime;
        _worstFrame = Math.Max(_worstFrame, deltaTime);

        Vector2D<int> size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return;

        CollectScene(_camera.View * Projection((float)size.X / size.Y));
        Mark("raccolta scena");

        // --- 1. Shadow map dal punto di vista del sole, centrata sulla camera ---
        // In vista di guida si vede lontano davanti: l'area delle ombre si sposta in avanti
        Vector3 center = _camera.Target + _camera.Forward * (12f * _camera.Chase);
        Vector3 lightDir = _day.LightDir;
        // La parte fissa si ridisegna se la camera si è spostata oltre il margine, se il sole (o la luna) si è mosso o
        // se il territorio caricato è cambiato; altrimenti si riusa
        var zones = (_world.LoadedCount, _world.Count);
        bool redo = _shadowCenter is not { } old || Vector3.Distance(old, center) > ShadowSlack
                    || Vector3.Distance(_shadowLight, lightDir) > 0.004f || zones != _shadowZones;
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(1.5f, 3f);
        _depth.Use();
        if (redo)
        {
            _shadowRedos++;
            _shadowCenter = center;
            _shadowLight = lightDir;
            _shadowZones = zones;
            Matrix4x4 view = Matrix4x4.CreateLookAt(center - lightDir * 50f, center, Vector3.UnitY);
            // Un po' più grande del necessario: la camera può spostarsi di ShadowSlack senza uscire dal riquadro
            _shadowSpace = view * Matrix4x4.CreateOrthographic(56f + 2f * ShadowSlack, 56f + 2f * ShadowSlack, 1f, 110f);
            _shadowStatic.Bind();
            _gl.Clear(ClearBufferMask.DepthBufferBit);
            _depth.SetMatrix("uLightSpace", _shadowSpace);
            var staticCenter = new Vector2(center.X, center.Z);
            foreach (var item in _opaque)
            {
                if (!item.CastShadow || !item.Static) continue;
                // Fuori dal riquadro della shadow map non serve disegnarlo nel pass delle ombre
                if (item.HasArea && RectDistance(staticCenter, item.Area) > ShadowReach + ShadowSlack) continue;
                _depth.SetMatrix("uModel", item.Model);
                item.Mesh.Draw();
            }
        }
        Matrix4x4 lightSpace = _shadowSpace;
        _shadow.CopyFrom(_shadowStatic);
        _depth.SetMatrix("uLightSpace", lightSpace);
        var shadowCenter = new Vector2(_shadowCenter!.Value.X, _shadowCenter.Value.Z);
        foreach (var item in _opaque)
        {
            if (!item.CastShadow || item.Static) continue;
            if (item.HasArea && RectDistance(shadowCenter, item.Area) > ShadowReach + ShadowSlack) continue;
            _depth.SetMatrix("uModel", item.Model);
            item.Mesh.Draw();
        }
        foreach (var item in _ghost) // anche trasparente, il personaggio fa ombra
        {
            _depth.SetMatrix("uModel", item.Model);
            item.Mesh.Draw();
        }
        _gl.Disable(EnableCap.PolygonOffsetFill);
        Mark("ombre (cpu)");
        if (_debug.Profile) { _gl.Finish(); Mark("ombre (gpu)"); }

        // --- 2. Scena principale ---
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        Vector3 horizon = _day.SkyHorizon;
        _gl.ClearColor(horizon.X, horizon.Y, horizon.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var proj = Projection((float)size.X / size.Y);

        // Cielo a gradiente dietro a tutto
        if (Matrix4x4.Invert(_camera.View * proj, out Matrix4x4 invViewProj))
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _sky.Use();
            _sky.SetMatrix("uInvViewProj", invViewProj);
            SetSkyColors(_sky);
            _sky.SetFloat("uStars", _day.Stars);
            _sky.SetFloat("uTime", _time);
            _gl.BindVertexArray(_skyVao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.DepthTest);
        }

        foreach (var shader in new[] { _litSolid, _litFull })
        {
            _lit = shader;
            SetupLit(proj, lightSpace, lightDir);
        }

        // Prima tutto ciò che non sbiadisce mai, con lo shader senza discard; poi gli oggetti che possono sbiadire
        foreach (bool seeThrough in new[] { false, true })
        {
            _lit = seeThrough ? _litFull : _litSolid;
            _lit.Use();
            _lampArea = new Vector4(float.NaN);
            _farFog = null;
            foreach (var item in _opaque)
            {
                if (item.SeeThrough != seeThrough) continue;
                _lit.SetMatrix("uModel", item.Model);
                _lit.SetInt("uSeeThrough", item.SeeThrough ? 1 : 0);
                _lit.SetInt("uSmooth", item.Smooth ? 1 : 0);
                _lit.SetFloat("uEmissive", item.Emissive);
                SetFog(item.Far);
                LampsFor(item);
                item.Mesh.Draw();
            }
        }
        LampsFor(default); // acqua, trasparenze e personaggio: i lampioni più vicini alla camera
        _lit.SetInt("uSmooth", 0);
        SetFog(false);
        Mark("scena: opachi");
        if (_debug.Profile) { _gl.Finish(); Mark("opachi (gpu)"); }

        _lit.SetInt("uSeeThrough", 0);
        _lit.SetInt("uCloud", 1);
        foreach (var item in _cloudItems)
        {
            _lit.SetMatrix("uModel", item.Model);
            _lit.SetFloat("uEmissive", item.Emissive);
            item.Mesh.Draw();
        }
        _lit.SetInt("uCloud", 0);

        Mark("scena: nuvole");
        // Impronte delle gomme: sopra il terreno, sotto acqua e trasparenze
        _skids.Draw(_camera.View, proj);
        _lit.Use();
        Mark("scena: gomme");

        // --- 3. Acqua trasparente ---
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _gl.DepthMask(false);
        _lit.SetInt("uWater", 1);
        _lit.SetFloat("uEmissive", 0f);
        foreach (var item in _water)
        {
            _lit.SetMatrix("uModel", item.Model);
            item.Mesh.Draw();
        }
        _lit.SetInt("uWater", 0);
        Mark("scena: acqua");

        // --- 4. Alberi e rocce che coprono il giocatore: semitrasparenti, con i soli spigoli marcati ---
        // Prima solo la profondità (così di ogni oggetto resta la superficie più vicina, senza
        // facce interne sovrapposte), poi il colore in blending su quella superficie.
        _gl.ColorMask(false, false, false, false);
        _gl.DepthMask(true);
        _lit.SetInt("uSeeThrough", 2);
        DrawSeeThrough();
        _gl.ColorMask(true, true, true, true);
        _gl.DepthMask(false);
        _gl.DepthFunc(DepthFunction.Lequal);
        DrawSeeThrough();
        _gl.DepthFunc(DepthFunction.Less);
        _lit.SetInt("uSeeThrough", 0);
        Mark("scena: trasparenti");

        // --- 5. Il personaggio trasparente: come sopra, prima la profondità e poi il colore della sola superficie davanti ---
        if (_ghost.Count > 0)
        {
            _gl.ColorMask(false, false, false, false);
            _gl.DepthMask(true);
            DrawGhost();
            _gl.ColorMask(true, true, true, true);
            _gl.DepthMask(false);
            _gl.DepthFunc(DepthFunction.Lequal);
            _lit.SetFloat("uAlpha", _kidAlpha);
            DrawGhost();
            _lit.SetFloat("uAlpha", 1f);
            _gl.DepthFunc(DepthFunction.Less);
            _lit.SetInt("uSmooth", 0);
        }

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        SetFog(false);

        if (_mapReveal > 0f) DrawBigMap(size);
        Mark("scena: personaggio e mappa");
        DrawCompass(size);
        Mark("scena: minimappa");
        if (_debug.Profile) { _gl.Finish(); Mark("attesa gpu"); }
        ProfileReport(deltaTime);
        TakeScreenshotIfDue(size);
    }

    private bool? _farFog;

    /// <summary>
    /// Nebbia normale, oppure quella "lunga" dei punti di riferimento: comincia oltre la vista normale e non
    /// li copre mai del tutto, così si vedono stagliati nella foschia da un capo all'altro dell'isola.
    /// </summary>
    private void SetFog(bool far)
    {
        if (_farFog == far) return;
        _farFog = far;
        _lit.SetFloat("uFogNear", far ? ViewRange * 0.7f : _camera.EffectiveDistance + 14f);
        _lit.SetFloat("uFogFar", far ? 900f : ViewRange);
        _lit.SetFloat("uFogMax", far ? 0.8f : 1f);
    }

    /// <summary>Oltre questa distanza dalla camera la nebbia copre tutto (è la stessa uFogFar dello shader).</summary>
    private float ViewRange => _camera.EffectiveDistance + 55f;

    /// <summary>Metà diagonale del riquadro delle ombre (56 x 56) più lo spazio per le ombre lunghe.</summary>
    private const float ShadowReach = 48f;

    private static Vector4 ZoneArea(Zone z) =>
        new(z.Origin.X - Zone.Half, z.Origin.Z - Zone.Half, z.Origin.X + Zone.Half, z.Origin.Z + Zone.Half);

    /// <summary>Distanza sul piano tra un punto e un rettangolo (0 se il punto è dentro).</summary>
    private static float RectDistance(Vector2 p, Vector4 area)
    {
        float dx = MathF.Max(MathF.Max(area.X - p.X, p.X - area.Z), 0f);
        float dz = MathF.Max(MathF.Max(area.Y - p.Y, p.Y - area.W), 0f);
        return MathF.Sqrt(dx * dx + dz * dz);
    }

    private Matrix4x4 _viewProj;

    private const float FootTurnRate = 2.8f; // radianti al secondo: quanto gira il personaggio a piedi con le frecce
    private float _footYaw;

    /// <summary>
    /// Punto di partenza: sulla strada più vicina a <paramref name="near"/> (le asfaltate valgono un po' di più), con
    /// l'auto rivolta lungo la strada verso la città.
    /// </summary>
    private static (Vector3 Pos, float Yaw) StartOnRoad(Vector2 near)
    {
        var net = RoadNetwork.Current;
        RoadEdge? best = null;
        float bestScore = float.MaxValue, bestS = 0f;
        foreach (var e in net.Edges)
            for (int i = 0; i < e.Count; i++)
            {
                float score = Vector2.Distance(e.Points[i], near) + (e.Paved ? 0f : 25f);
                // Non proprio su un incrocio: lì l'auto partirebbe di traverso
                if (i < 4 || i > e.Count - 5) score += 15f;
                if (score < bestScore) { bestScore = score; best = e; bestS = e.CumAt(i); }
            }
        if (best == null) return (new Vector3(near.X, 0f, near.Y), MathF.PI / 2f);
        Vector2 p = best.At(bestS), t = best.Tangent(bestS);
        Vector2 goal = net.Settlements.FirstOrDefault(s => s.City)?.Pos ?? Vector2.Zero;
        float dir = 1f;
        if (Vector2.Dot(t, goal - p) < 0f) { t = -t; dir = -1f; }
        // Nella corsia di destra, non sulla mezzeria
        p += new Vector2(-t.Y, t.X) * best.TravelLanes(dir).Max();
        return (new Vector3(p.X, 0f, p.Y), MathF.Atan2(t.X, t.Y));
    }

    /// <summary>Margine sui lati: un albero appena fuori inquadratura può ancora proiettare l'ombra dentro.</summary>
    private const float FrustumMargin = 8f;

    /// <summary>
    /// Il volume (rettangolo <paramref name="area"/> sul piano, tra le quote indicate) tocca la piramide di vista?
    /// Scartato solo se tutti gli 8 vertici stanno fuori dallo stesso piano della piramide (test conservativo).
    /// </summary>
    private bool InFrustum(Vector4 area, float yMin, float yMax)
    {
        int left = 0, right = 0, down = 0, up = 0, behind = 0, far = 0;
        for (int i = 0; i < 8; i++)
        {
            float x = (i & 1) == 0 ? area.X - FrustumMargin : area.Z + FrustumMargin;
            float z = (i & 2) == 0 ? area.Y - FrustumMargin : area.W + FrustumMargin;
            float y = (i & 4) == 0 ? yMin : yMax;
            Vector4 c = Vector4.Transform(new Vector4(x, y, z, 1f), _viewProj);
            if (c.X < -c.W) left++;
            if (c.X > c.W) right++;
            if (c.Y < -c.W) down++;
            if (c.Y > c.W) up++;
            if (c.Z < 0f) behind++;   // System.Numerics: profondità NDC in [0, 1]
            if (c.Z > c.W) far++;
        }
        return left < 8 && right < 8 && down < 8 && up < 8 && behind < 8 && far < 8;
    }

    /// <summary>
    /// Solo vicino al tratto camera → giocatore un albero può coprire l'auto: i due pass della trasparenza
    /// ridisegnano soltanto la vegetazione delle isole che toccano quella striscia.
    /// </summary>
    // Lo scenario dei pezzi in cui qualche oggetto copre davvero il giocatore: solo questi passano dal disegno
    // semitrasparente (due passate in più su tutta la loro vegetazione, che altrimenti si sprecherebbero)
    private readonly HashSet<Mesh> _fading = new();

    /// <summary>
    /// Qualche oggetto dello scenario del pezzo sbiadisce in questo fotogramma? Stesso test dello shader (objectFade),
    /// con gli stessi margini: se qui dice di no, lì nessun vertice avrebbe vFade > 0.
    /// </summary>
    private bool AnyFading(Zone zone)
    {
        Vector3 cam = _camera.Position, focus = ActivePos + new Vector3(0, _onFoot ? 0.35f : 0.45f, 0);
        Vector2 a = new(cam.X, cam.Z), ab = new Vector2(focus.X, focus.Z) - a;
        float abLen2 = MathF.Max(ab.LengthSquared(), 1e-4f), abLen = MathF.Sqrt(abLen2);
        Vector3 o = zone.Origin;
        foreach (var (x, z, rs, y0, y1) in zone.SceneryObjects)
        {
            var b = new Vector2(x + o.X, z + o.Z);
            float r = MathF.Abs(rs), top = y1 - y0 + (y0 + o.Y + zone.YOffset);
            float toCam = Vector2.Distance(b, a);
            if (rs < 0f && _onFoot)
            {
                if (_cameraInside && !_onTrain && toCam < r * 1.5f) return true;
                continue;
            }
            float tRaw = Vector2.Dot(b - a, ab) / abLen2, t = Math.Clamp(tRaw, 0f, 1f);
            float dist = Vector2.Distance(a + ab * t, b);
            float rayY = cam.Y + (focus.Y - cam.Y) * t;
            if (dist < r + 1.4f && rayY < top + 0.8f && (tRaw - 1f) * abLen < 0.4f) return true;
            if (toCam < r + 1.8f && cam.Y < top + 0.6f) return true;
        }
        return false;
    }

    private bool NearSightLine(Vector4 area)
    {
        Vector3 cam = _camera.Position, focus = ActivePos;
        const float reach = 8f;
        return area.X <= MathF.Max(cam.X, focus.X) + reach && area.Z >= MathF.Min(cam.X, focus.X) - reach
            && area.Y <= MathF.Max(cam.Z, focus.Z) + reach && area.W >= MathF.Min(cam.Z, focus.Z) - reach;
    }

    private bool InView(Vector4 area)
    {
        Vector3 cam = _camera.Position;
        return RectDistance(new Vector2(cam.X, cam.Z), area) < ViewRange;
    }

    private void SetSkyColors(Shader shader)
    {
        shader.SetVector3("uSkyZenith", _day.SkyZenith);
        shader.SetVector3("uSkyHorizon", _day.SkyHorizon);
        shader.SetVector3("uSkyBelow", _day.SkyBelow);
    }

    // ------------------------------------------------------------------ scendere e salire dall'auto

    /// <summary>
    /// E (o Invio, △ sul controller): se si guida e l'auto è (quasi) ferma, il personaggino salta fuori dalla portiera del guidatore
    /// (o da quella opposta se è bloccata) e il motore si spegne; se si è a piedi vicino all'auto, ci risale.
    /// </summary>
    private void ToggleOnFoot()
    {
        if (!_onFoot)
        {
            if (MathF.Abs(_player.Speed) > 0.6f) return;
            foreach (float side in new[] { 1f, -1f }) // +1 = sinistra (guidatore), -1 = destra
            {
                Vector3 spot = _player.DoorPoint(side);
                if (!_world.IsWalkable(spot, Villager.Radius) || _player.Blocks(spot, Villager.Radius)) continue;
                spot.Y = _world.GroundHeight(spot);
                Vector3 outward = Vector3.Normalize(new Vector3(spot.X - _player.Position.X, 0, spot.Z - _player.Position.Z));
                _kid.HopOut(_player.SeatPoint(side), spot, MathF.Atan2(outward.X, outward.Z));
                _footYaw = _player.Yaw; // appena a terra si gira nel verso dell'auto: la camera sta alle sue spalle, non dietro l'auto
                _onFoot = true;
                _player.EngineOn = false;
                return;
            }
            return; // non c'è spazio per scendere da nessuna delle due parti
        }

        if (_kid.Busy) return;

        // In treno: seduti ci si alza; vicino a un sedile ci si siede; alla porta, a treno fermo, si scende
        if (_onTrain)
        {
            if (_trains == null) return;
            if (_seat >= 0)
            {
                var s = Trains.Seats[_seat];
                // Ci si alza nel corridoio accanto al posto, rivolti verso la marcia
                _cabin = new Vector2(s.Side * 0.1f, s.Z);
                _cabinYaw = _cabinLook = s.Facing > 0f ? 0f : MathF.PI;
                _seat = -1;
                return;
            }
            int near = NearSeat();
            if (near >= 0)
            {
                _seat = near;
                _kid.StandStill();
                _lookPan = _lookTilt = 0f;
                return;
            }
            if (_cabin.Y > Trains.DoorZ - 0.35f && _trains.AlightPoint(out var spot, out float yaw))
            {
                var m = _trains.RideMatrix;
                _kid.SizeScale = 1f;
                _kid.HopOut(Vector3.Transform(new Vector3(_cabin.X, Trains.Floor, _cabin.Y), m), spot, yaw);
                _footYaw = yaw;
                _onTrain = false;
                _trains.Inside = false;
            }
            return;
        }

        // Si risale dalla portiera più vicina, se si è abbastanza vicini a una delle proprie auto (anche una lasciata
        // parcheggiata: diventa quella che si guida)
        float best = float.MaxValue, bestSide = 1f;
        Car? bestCar = null;
        foreach (var car in _parked.Prepend(_player))
            foreach (float side in new[] { 1f, -1f })
            {
                float d = Vector2.Distance(new(_kid.Position.X, _kid.Position.Z), new(car.DoorPoint(side).X, car.DoorPoint(side).Z));
                if (d < best) { best = d; bestSide = side; bestCar = car; }
            }
        if (best < 1.3f && bestCar != null)
        {
            if (bestCar != _player)
            {
                _parked.Remove(bestCar);
                _parked.Add(_player);
                _player = bestCar;
            }
            _kid.HopIn(_player.SeatPoint(bestSide));
            return;
        }

        // Un'auto del traffico vicina e lenta: si apre la portiera, chi guida viene buttato fuori e si sale al suo posto
        if (_traffic?.Steal(new Vector2(_kid.Position.X, _kid.Position.Z), StealSpeed) is { } stolen)
        {
            var car = new Car(_gl, stolen.Pos, stolen.Yaw, stolen.Paint, stolen.Style) { EngineOn = false };
            _parked.Add(_player);
            _player.EngineOn = false;
            _player.Headlights = false;
            _player = car;
            TrimParked();
            _peds?.Eject(stolen.Seat, stolen.Door, stolen.Pos, ActivePos);
            _kid.HopIn(car.SeatPoint(1f));
            Console.WriteLine($"Auto rubata: {stolen.Style}");
            return;
        }

        // Altrimenti, se si è sulla banchina accanto a una carrozza del treno fermo, si sale in treno
        if (_trains != null && _trains.CanBoard(new Vector2(_kid.Position.X, _kid.Position.Z), out var door))
        {
            _kid.HopIn(door);
            _boarding = true;
        }
    }

    /// <summary>
    /// Verifica (test): su ogni viale, da ogni corsia e in entrambi i versi, l'inversione a U guidata resta nella propria
    /// metà carreggiata, e dopo essersi girati (anche con una derapata libera vicino agli incroci) l'aiuto in corsia
    /// tiene l'auto dalla stessa parte dello spartitraffico.
    /// </summary>
    private static void VerifyAvenueUTurns()
    {
        var net = RoadNetwork.Current;
        int tried = 0, turns = 0, crossTurn = 0, intoHub = 0;
        // Da che parte dello spartitraffico sta un punto, rispetto al viale e al suo verso A→B (0 se non è su un viale)
        float SideOf(Vector2 p, RoadEdge e)
        {
            if (net.HubAt(p, 0f) != null) return 0f; // dentro un incrocio o la piazza non c'è spartitraffico
            if (!net.Nearest(p, e.HalfWidth + 0.5f, out var h) || h.Edge.Layout != RoadLayout.Avenue) return 0f;
            Vector2 t = h.Edge.Tangent(h.S);
            // Lo stesso viale può proseguire in un altro tratto con verso opposto: si confronta col verso di e
            if (Vector2.Dot(t, e.Tangent(e.Length * 0.5f)) < 0f) t = -t;
            return MathF.Sign(Vector2.Dot(p - h.Point, new Vector2(-t.Y, t.X)));
        }
        foreach (var e in net.Edges.Where(e => e.Layout == RoadLayout.Avenue))
            for (float s = 1f; s < e.Length - 1f; s += 1f)
                foreach (float dir in new[] { 1f, -1f })
                    foreach (float lane in e.TravelLanes(dir))
                    {
                        Vector2 t = e.Tangent(s) * dir, right = new(-t.Y, t.X);
                        Vector2 p = e.At(s) + right * lane;
                        float side = MathF.Sign(lane * dir); // nel verso A→B
                        tried++;
                        var travel = new Vector3(t.X, 0f, t.Y);
                        if (Characters.DriftLine.AlongRoad(new Vector3(p.X, 0f, p.Y), travel, 6f) is { } line)
                        {
                            turns++;
                            bool crossed = false, inHub = false;
                            for (float u = 0f; u <= line.Length; u += 0.25f)
                            {
                                var q = line.At(u);
                                float sd = SideOf(new Vector2(q.X, q.Z), e);
                                if (sd != 0f && sd != side) crossed = true;
                                // Dentro un incrocio (o una piazza): oltre l'imbocco della strada su cui si è; il viale può
                                // proseguire in un altro tratto attraverso un semplice raccordo, e lì non è un incrocio
                                if (!net.NearestAligned(new Vector2(q.X, q.Z), Vector2.Zero, e.HalfWidth + 0.5f, out var hq)) inHub = true;
                                else
                                {
                                    var g = hq.Edge;
                                    bool junA = g.A.Degree >= 3 || g.A.Piazza, junB = g.B.Degree >= 3 || g.B.Piazza;
                                    if ((junA && hq.S < g.A.TrimFor(g)) || (junB && hq.S > g.Length - g.B.TrimFor(g))) inHub = true;
                                }
                            }
                            if (crossed && crossTurn++ < 5)
                                Console.WriteLine($"  inversione oltre lo spartitraffico: viale {e.Id} a {p.X:F1}, {p.Y:F1}");
                            if (inHub && intoHub++ < 5)
                                Console.WriteLine($"  inversione che entra nell'incrocio: viale {e.Id} a {p.X:F1}, {p.Y:F1}");
                        }
                    }
        Console.WriteLine($"Verifica inversioni sui viali: {tried} prove, {turns} inversioni guidate, "
                          + $"{crossTurn} oltre lo spartitraffico, {intoHub} dentro un incrocio");
    }

    /// <summary>
    /// Solo per i test: lo sterzo che terrebbe l'auto in corsia (inseguimento di un punto della corsia poco più avanti),
    /// come farebbe un giocatore prima di tirare il freno a mano.
    /// </summary>
    private float PilotSteer()
    {
        var travel = _player.Velocity.LengthSquared() > 0.01f ? _player.Velocity : new Vector3(MathF.Sin(_player.Yaw), 0f, MathF.Cos(_player.Yaw));
        float look = MathF.Max(4f, MathF.Abs(_player.Speed) * 0.6f);
        if (Characters.DriftLine.AlongRoad(_player.Position, travel, look, anywhere: true) is not { } line) return 0f;
        var aim = line.At(line.Length);
        float diff = Noise.WrapAngle(MathF.Atan2(aim.X - _player.Position.X, aim.Z - _player.Position.Z) - _player.Yaw);
        return Math.Clamp(-diff * 3f, -1f, 1f); // sterzo a destra = yaw che diminuisce
    }

    // ------------------------------------------------------------------ mouse e visuale a piedi

    private bool _mouseCaptured, _mouseSkip, _jumpQueued;
    private const float MouseYaw = 0.0045f, MousePitch = 0.0032f;

    // Sterzo col mouse (in auto, visuale da vicino): ~600 pixel di movimento per sterzare al massimo; fermando il mouse
    // il "volante" torna da solo al centro, piano (in circa un secondo e mezzo da tutto sterzato)
    private const float MouseSteer = 1f / 600f, MouseSteerReturn = 0.7f, MouseSteerHold = 0.12f;
    private float _mouseSteer, _mouseSteerIdle;

    /// <summary>La sterzata data col mouse in questo istante (si somma ai tasti), con il ritorno al centro.</summary>
    private float MouseSteering(float dt)
    {
        if (_onFoot || (!_mouseCaptured && _debug.MouseSteer.Count == 0)) { _mouseSteer = 0f; return 0f; }
        _mouseSteerIdle += dt;
        if (_mouseSteerIdle > MouseSteerHold)
            _mouseSteer = MathF.Sign(_mouseSteer) * MathF.Max(0f, MathF.Abs(_mouseSteer) - MouseSteerReturn * dt);
        return _mouseSteer;
    }

    /// <summary>
    /// Con la visuale da vicino il puntatore è catturato: a piedi e in treno il mouse gira la visuale, in auto sterza.
    /// Si libera con la mappa aperta (per cliccarci), nella visuale lontana e se la finestra non è attiva.
    /// </summary>
    private void UpdateMouseCapture()
    {
        // (nelle prove automatiche con gli screenshot il puntatore resta libero)
        bool want = !_farView && !_bigMap && _window.IsVisible && _debug.Shots.Count == 0;
        if (want == _mouseCaptured) return;
        _mouseCaptured = want;
        _mouseSkip = true;
        foreach (var mouse in _input.Mice)
            mouse.Cursor.CursorMode = !want ? CursorMode.Normal
                : mouse.Cursor.IsSupported(CursorMode.Raw) ? CursorMode.Raw : CursorMode.Disabled;
    }

    /// <summary>Il mouse ha mosso la visuale: attorno al personaggio, nella carrozza o seduti al finestrino.</summary>
    private void LookAround(Vector2 delta)
    {
        if (!_onFoot)
        {
            // In auto il mouse è un volante poco sensibile: tanto movimento, poca sterzata
            _mouseSteer = Math.Clamp(_mouseSteer + delta.X * MouseSteer, -1f, 1f);
            if (MathF.Abs(delta.X) > 0.5f) _mouseSteerIdle = 0f;
            return;
        }
        if (_onTrain && _seat >= 0)
        {
            _lookPan = Math.Clamp(_lookPan - delta.X * MouseYaw, -1.4f, 1.4f);
            _lookTilt = Math.Clamp(_lookTilt - delta.Y * MousePitch, -0.6f, 0.5f);
        }
        else if (_onTrain) _cabinLook -= delta.X * MouseYaw;
        else
        {
            // A piedi il mouse gira la visuale attorno al personaggio
            _camera.Orbit(-delta.X * MouseYaw, delta.Y * MousePitch);
            if (delta.LengthSquared() > 1f) _mouseLookIdle = 0f;
        }
    }

    private float _mouseLookIdle = 10f; // s dall'ultimo movimento del mouse a piedi

    /// <summary>
    /// La camera non entra in case e punti di riferimento. Lungo la linea dalla testa del giocatore (non dal punto guardato,
    /// che sta un po' avanti nella direzione di corsa e, correndo contro un muro, ci finirebbe dentro) alla camera si cerca
    /// il primo punto dentro un edificio, e la camera si ferma prima. Ma mai più vicino di una distanza minima, per non
    /// finire dentro il giocatore: se il muro è più vicino di così, la camera resta dentro l'edificio e lo shader rende
    /// trasparente quell'edificio (vedi _cameraInside). Si avvicina subito, torna indietro con calma.
    /// </summary>
    private void AvoidWalls(float dt)
    {
        _cameraInside = false;
        if (_farView || _camera.Fixed) { _camera.ClipPoint = null; _clipDist = float.MaxValue; return; }
        Vector3 origin = new(ActivePos.X, _camera.Target.Y, ActivePos.Z);
        Vector3 desired = _camera.DesiredPosition;
        float len = Vector3.Distance(origin, desired);
        if (len < 0.01f) { _camera.ClipPoint = null; return; }
        Vector3 dir = (desired - origin) / len;
        float minDist = MathF.Min(len, _onFoot ? 1.4f : 3f); // mai dentro il giocatore (o l'auto)
        float want = FreeAlong(origin, dir, len) is { } free ? MathF.Max(free, minDist) : len;
        _clipDist = want < MathF.Min(_clipDist, len) ? want : MathF.Min(want, MathF.Min(_clipDist, len) + 5f * dt);
        if (_clipDist >= len - 0.01f)
        {
            _clipDist = float.MaxValue;
            _camera.ClipPoint = null;
        }
        else _camera.ClipPoint = origin + dir * _clipDist;
        _cameraInside = _world.InsideSolid(_camera.Position, 0.15f);
    }

    private float _clipDist = float.MaxValue;
    private float _camFloor = float.MinValue;

    /// <summary>
    /// A piedi, quota minima della camera: sopra il terreno sotto di lei e abbastanza alta perché la linea di vista verso
    /// il personaggio passi sopra prati e scarpate in mezzo (con la visuale rasoterra, o guardando in su, la camera sta
    /// quasi a terra e una gobba del prato coprirebbe tutto).
    /// </summary>
    private float TerrainFloor()
    {
        Vector3 cam = _camera.ClipPoint ?? _camera.DesiredPosition, from = new(ActivePos.X, _camera.Target.Y, ActivePos.Z);
        float floor = _world.GroundOrObstacle(cam) + 0.2f;
        for (float t = 0.3f; t < 0.99f; t += 0.07f)
        {
            var p = Vector3.Lerp(from, cam, t);
            float need = _world.GroundOrObstacle(p) + 0.15f;
            if (need > p.Y) floor = MathF.Max(floor, from.Y + (need - from.Y) / t);
        }
        return floor;
    }
    private bool _cameraInside; // la camera è dentro (o addosso a) un edificio: a piedi solo allora le case sbiadiscono

    /// <summary>Fin dove si può andare da <paramref name="from"/> lungo <paramref name="dir"/> senza entrare in un edificio.</summary>
    private float? FreeAlong(Vector3 from, Vector3 dir, float dist)
    {
        const float Margin = 0.3f; // la camera resta un po' fuori dal muro (il piano vicino non lo taglia)
        for (float s = 0.3f; s <= dist; s += 0.12f)
        {
            var p = from + dir * s;
            // A piedi anche rocce e cespugli (non diventano trasparenti): con la camera bassa la si porta davanti a loro
            if (_world.InsideSolid(p, Margin) || (_onFoot && _world.ObstacleTop(p) is { } top && p.Y < top + 0.15f))
                return MathF.Max(0f, s - 0.12f);
        }
        return null;
    }


    // ------------------------------------------------------------------ a bordo del treno

    private int _seat = -1;          // posto occupato (-1 in piedi)
    private Vector2 _cabin;          // posizione nella carrozza (locale: x di lato, z verso la marcia)
    private float _cabinYaw;         // direzione nella carrozza (0 = verso la marcia)
    private float _lookPan;          // seduti: quanto si gira lo sguardo dal finestrino (radianti)

    /// <summary>
    /// Il posto più vicino a dove si è e si guarda (nel corridoio accanto alla fila, entro un passo e mezzo), -1 se nessuno.
    /// </summary>
    private int NearSeat()
    {
        int best = -1;
        float bestD = 0.62f;
        Vector2 aim = _cabin + new Vector2(MathF.Sin(_cabinYaw), MathF.Cos(_cabinYaw)) * 0.2f;
        for (int i = 0; i < Trains.Seats.Length; i++)
        {
            var s = Trains.Seats[i];
            float d = Vector2.Distance(aim, new Vector2(s.X, s.Z));
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private float _cabinLook = MathF.PI; // a bordo, in piedi: dove si guarda nella carrozza (0 = verso la marcia)
    private float _lookTilt;             // seduti: sguardo più su o più giù


    /// <summary>
    /// A bordo, rimpiccioliti: in piedi si cammina su tutto il pavimento libero con WASD rispetto a dove si guarda
    /// (il mouse gira lo sguardo, Shift corre), scivolando lungo i mobili; la camera segue da dietro e resta dentro
    /// la carrozza. Seduti la visuale è dagli occhi verso il finestrino e il mouse gira lo sguardo. Tutto in coordinate
    /// della carrozza, che poi si portano nel mondo: il treno si muove, noi con lui.
    /// </summary>
    private void RideTrain(float dt, float moveX, float moveY, bool run, bool slow, bool jump)
    {
        if (_trains == null) return;
        var m = _trains.RideMatrix;
        Vector3 W(float x, float y, float z) => Vector3.Transform(new Vector3(x, y, z), m);
        float carYaw = MathF.Atan2(m.M31, m.M33); // direzione della marcia (asse +Z locale nel mondo)
        const float k = Trains.InsideScale;

        if (_seat >= 0)
        {
            var s = Trains.Seats[_seat];
            // Occhi del personaggio seduto; lo sguardo parte dal finestrino e il mouse lo gira (avanti, indietro, su, giù)
            float eyeY = Trains.Floor + 0.44f;
            Vector3 eye = W(s.X - s.Side * 0.02f, eyeY, s.Z + s.Facing * 0.02f);
            Vector3 look = W(s.X + s.Side * MathF.Cos(_lookPan) * 3f, eyeY + MathF.Tan(_lookTilt) * 3f,
                             s.Z + MathF.Sin(_lookPan) * s.Side * 3f);
            _kid.Hide();
            _kid.Position = W(s.X, Trains.Floor + 0.2f, s.Z); // per mappe e caricamento del territorio
            _kid.Yaw = carYaw;
            _camera.Fix(eye, look);
            return;
        }

        // In piedi: come in strada (WASD rispetto allo sguardo, Shift corre, Ctrl piano, spazio salta), in proporzione
        // alla grandezza; il personaggio si volta da solo dove cammina
        var look2 = new Vector2(MathF.Sin(_cabinLook), MathF.Cos(_cabinLook));
        var right2 = new Vector2(-look2.Y, look2.X);
        Vector2 move = look2 * moveY + right2 * moveX;
        const float r = Villager.Radius * k;
        _kid.SizeScale = k;
        if (jump) _kid.Jump();
        _kid.WalkInside(dt, move, slow, run && !slow, p => Trains.FreeInside(p, r), ref _cabin, ref _cabinYaw);
        _kid.Position = W(_cabin.X, Trains.Floor + _kid.Air, _cabin.Y);
        _kid.Yaw = carYaw + _cabinYaw;

        // Camera dietro e sopra il personaggio rispetto allo sguardo, sotto il soffitto e dentro le pareti; più vicina
        // se dietro c'è una parete di fondo
        float dist = 0.95f;
        Vector2 back = _cabin - look2 * dist;
        back.X = Math.Clamp(back.X, -Trains.WalkX + 0.02f, Trains.WalkX - 0.02f);
        back.Y = Math.Clamp(back.Y, -Trains.CabinEnd, Trains.CabinEnd);
        Vector3 eyeAt = W(back.X, Trains.Floor + 0.66f, back.Y);
        Vector3 target = W(_cabin.X + look2.X * 0.45f, Trains.Floor + 0.26f, _cabin.Y + look2.Y * 0.45f);
        _camera.Fix(eyeAt, target);
    }

    /// <summary>Nel titolo, in treno: cosa si può fare, dove si è fermi e quando si riparte.</summary>
    private string TrainStatus()
    {
        string where = _trains?.StoppedAt is { } st ? $"fermi a {st.Name} (si riparte tra {_trains.DepartsIn:F0} s)" : "in viaggio";
        string hint = _seat >= 0 ? "seduti: mouse per guardare, E per alzarsi"
            : NearSeat() >= 0 ? "E per sedersi"
            : _cabin.Y > Trains.DoorZ - 0.35f && _trains?.StoppedAt != null ? "E per scendere"
            : "WASD o frecce per camminare, Shift per correre, mouse per guardare; le porte nel vestibolo davanti";
        return $"  |  in treno, {where}  |  {hint}";
    }

    /// <summary>Nel titolo, a piedi accanto al treno fermo: si può salire.</summary>
    private string BoardHint()
    {
        var at = new Vector2(_kid.Position.X, _kid.Position.Z);
        if (_trains != null && _trains.CanBoard(at, out _)) return "  (E per salire sul treno)";
        if (_traffic != null && _traffic.CanSteal(at, StealSpeed)) return "  (E per rubare l'auto)";
        return "";
    }

    /// <summary>Un cerchio nel punto tocca una delle auto del giocatore (quella guidata o una parcheggiata)?</summary>
    private bool BlockedByOwnCars(Vector3 p, float r)
    {
        if (_player.Blocks(p, r)) return true;
        foreach (var car in _parked)
            if (car.Blocks(p, r)) return true;
        return false;
    }

    /// <summary>Troppe auto lasciate in giro: la più lontana viene rimossa (portata via dal carro attrezzi).</summary>
    private void TrimParked()
    {
        while (_parked.Count > MaxParked)
        {
            var far = _parked.MaxBy(c => Vector3.DistanceSquared(c.Position, ActivePos))!;
            _parked.Remove(far);
            far.Dispose();
        }
    }

    private bool _onTrain;   // il personaggio viaggia in treno
    private bool _boarding;  // sta saltando sulla carrozza

    private void DrawGhost()
    {
        foreach (var item in _ghost)
        {
            _lit.SetMatrix("uModel", item.Model);
            _lit.SetInt("uSmooth", item.Smooth ? 1 : 0);
            _lit.SetFloat("uEmissive", item.Emissive);
            item.Mesh.Draw();
        }
    }

    private void DrawSeeThrough()
    {
        foreach (var item in _opaque)
        {
            if (!item.SeeThrough || (item.HasArea && !_fading.Contains(item.Mesh))) continue;
            _lit.SetMatrix("uModel", item.Model);
            _lit.SetFloat("uEmissive", item.Emissive);
            SetFog(item.Far);
            item.Mesh.Draw();
        }
        SetFog(false);
    }

    private unsafe void TakeScreenshotIfDue(Vector2D<int> size)
    {
        if (_shotIndex >= _debug.Shots.Count || _time < _debug.Shots[_shotIndex].Time) return;

        int w = size.X, h = size.Y;
        var pixels = new byte[w * h * 4];
        fixed (byte* p = pixels)
            _gl.ReadPixels(0, 0, (uint)w, (uint)h, PixelFormat.Rgba, PixelType.UnsignedByte, p);

        // OpenGL legge dal basso verso l'alto: capovolgo le righe
        var flipped = new byte[pixels.Length];
        int stride = w * 4;
        for (int y = 0; y < h; y++)
            System.Buffer.BlockCopy(pixels, (h - 1 - y) * stride, flipped, y * stride, stride);
        for (int i = 3; i < flipped.Length; i += 4) flipped[i] = 255;

        PngWriter.Save(_debug.Shots[_shotIndex].Path, w, h, flipped);
        Console.WriteLine($"Screenshot salvato: {_debug.Shots[_shotIndex].Path}  ({_window.Title})");
        if (_traffic != null) Console.WriteLine("  " + _traffic.Status() + $"; passanti: {_peds?.Count ?? 0} ({_peds?.DownCount ?? 0} a terra)");
        _shotIndex++;
        if (_shotIndex >= _debug.Shots.Count) _window.Close();
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _miniLayer?.Dispose();
        _hud?.Dispose();
        _world?.Dispose();
        _player?.Dispose();
        foreach (var car in _parked) car.Dispose();
        _kid?.Dispose();
        _particles?.Dispose();
        _trains?.Dispose();
        _traffic?.Dispose();
        _peds?.Dispose();
        foreach (var m in _signalLit) m.Dispose();
        _flame?.Dispose();
        _shadow?.Dispose();
        _shadowStatic?.Dispose();
        _litFull?.Dispose();
        _litSolid?.Dispose();
        _depth?.Dispose();
        _sky?.Dispose();
        _clouds?.Dispose();
        if (_skyVao != 0) _gl?.DeleteVertexArray(_skyVao);
        _input?.Dispose();
        _gl?.Dispose();
    }
}
