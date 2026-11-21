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
    public List<float> Toggles { get; } = new(); // istanti in cui premere Invio (test)
    public Vector2? Start;         // punto di partenza (test): x e riga della strada est-ovest
    public float? Hour;            // ora di partenza (test)
    public bool Lights;            // fari accesi da subito (test)
    public float? Zoom;            // distanza della visuale lontana (test; la rotella va da 8 a 32)
    public bool Far;               // parte con la visuale lontana
    public int PreSpawn;
    public string? Map;            // file della mappa (di serie Assets/Maps/isola.txt)
    public bool BigMap;            // parte con la mappa grande aperta (test)
    public Vector2? Waypoint;      // punto da raggiungere già scelto (test)
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
    private uint _skyVao;
    private Clouds _clouds = null!;
    private readonly List<DrawItem> _cloudItems = new();
    private ShadowMap _shadow = null!;
    private Mesh _flame = null!;
    private bool _rightHeld;
    private Vector2 _lastMouse;
    private int _toggleIndex;
    private bool _farView;        // visuale scelta: false = dietro l'auto (standard, tasto 1), true = lontana dall'alto (tasto 2)

    private WorldMap _world = null!;
    private Car _player = null!;
    private Villager _kid = null!;   // il personaggino: scende e sale dall'auto con Invio
    private bool _onFoot;

    // Chi si sta comandando in questo momento: l'auto o il personaggino a piedi
    private Vector3 ActivePos => _onFoot ? _kid.Position : _player.Position;
    private float ActiveYaw => _onFoot ? _kid.Yaw : _player.Yaw;
    private Vector3 ActiveVelocity => _onFoot ? _kid.Velocity : _player.Velocity;
    private Particles _particles = null!;
    private Trains? _trains;
    private SkidMarks _skids = null!;
    private EngineSound? _engine;
    private readonly IsoCamera _camera = new();

    private readonly List<DrawItem> _opaque = new();
    private readonly List<DrawItem> _water = new();
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
        _keyboard.KeyDown += (_, key, _) =>
        {
            if (key == Key.Escape) _window.Close();
            // La visuale cambia solo con questi tasti e resta quella scelta
            if (key is Key.Number1 or Key.Keypad1) _farView = false;
            if (key is Key.Number2 or Key.Keypad2) _farView = true;
            if (key == Key.F && !_onFoot) _player.Headlights = !_player.Headlights; // i fari si comandano solo da dentro l'auto
            if (key is Key.Enter or Key.KeypadEnter) ToggleOnFoot();
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
                // Tasto destro premuto: trascinando si ruota la visuale attorno al personaggio
                if (_rightHeld)
                {
                    Vector2 delta = pos - _lastMouse;
                    _camera.Yaw -= delta.X * 0.008f;
                    _camera.Pitch = Math.Clamp(_camera.Pitch + delta.Y * 0.005f, 0.3f, 1.4f);
                }
                _lastMouse = pos;
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

        _lit = new Shader(_gl, Shaders.LitVertex, Shaders.LitFragment);
        _depth = new Shader(_gl, Shaders.DepthVertex, Shaders.DepthFragment);
        _sky = new Shader(_gl, Shaders.SkyVertex, Shaders.SkyFragment);
        _skyVao = _gl.GenVertexArray();
        _clouds = new Clouds(_gl);
        _shadow = new ShadowMap(_gl, 2048);
        _hud = new Hud(_gl);
        _flame = Props.BuildFlame(_gl);
        _day = new DayCycle(_debug.Hour ?? StartHour);

        // La mappa disegnata a mano va caricata prima di generare qualunque pezzo di territorio
        var map = GameMap.Load(_debug.Map ?? Path.Combine(AppContext.BaseDirectory, "Assets", "Maps", "isola.txt"));
        Console.WriteLine($"Mappa: {map.Name} ({map.MaxI - map.MinI + 1} x {map.MaxJ - map.MinJ + 1} celle, "
                          + $"{map.Landmarks.Count} punti di riferimento)");
        _bigMap = _debug.BigMap;
        _waypoint = _debug.Waypoint;

        // Si parte sulla strada più vicina alla cella di partenza (o al punto chiesto dai test), rivolti verso la città
        var (start, startYaw) = StartOnRoad(_debug.Start ?? Vector2.Zero);
        _world = new WorldMap(_gl, start);
        if (_debug.PreSpawn > 0) _world.PreSpawn(_debug.PreSpawn);
        _particles = new Particles(_gl);
        _trains = new Trains(_gl, Railway.Current);
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

    // Lontano abbastanza da vedere i punti di riferimento all'altro capo dell'isola
    private Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, 0.3f, 1200f);

    private void OnUpdate(double deltaTime)
    {
        float dt = (float)Math.Min(deltaTime, 0.05);
        _time += dt;

        // Rotazione della visuale lontana
        if (_keyboard.IsKeyPressed(Key.Q)) _camera.Yaw -= 1.8f * dt;
        if (_keyboard.IsKeyPressed(Key.R)) _camera.Yaw += 1.8f * dt; // (E è il clacson)

        // Tastiera = guida "da gioco di auto" in entrambe le visuali: sterzo e gas relativi all'auto
        // Solo frecce (WASD non guidano più)
        float steerIn = (_keyboard.IsKeyPressed(Key.Right) ? 1f : 0f) - (_keyboard.IsKeyPressed(Key.Left) ? 1f : 0f);
        float throttleIn = (_keyboard.IsKeyPressed(Key.Up) ? 1f : 0f) - (_keyboard.IsKeyPressed(Key.Down) ? 1f : 0f);
        bool slow = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);
        Vector3 input = Vector3.Zero;

        if (_debug.Walk is { } walk && _time > 0.5f && _time < _debug.WalkUntil)
            input = walk;
        if (_time > 0.5f)
            foreach (var (drive, until) in _debug.Drives)
                if (_time < until) { (steerIn, throttleIn) = (drive.X, drive.Y); break; }
        if (_debug.Slow) slow = true;
        while (_toggleIndex < _debug.Toggles.Count && _time >= _debug.Toggles[_toggleIndex])
        {
            ToggleOnFoot();
            _toggleIndex++;
        }

        if (_onFoot)
        {
            // A piedi, con la visuale da dietro, il personaggio si guida come una mini auto: destra/sinistra lo girano,
            // su va avanti, giù arretra senza voltarsi. Con la visuale lontana le frecce restano rispetto alla camera.
            // L'auto resta parcheggiata a motore spento.
            float? face = null;
            if (input == Vector3.Zero)
            {
                if (_farView)
                {
                    input = _camera.Right * steerIn + _camera.Forward * throttleIn;
                    _footYaw = _kid.Yaw;
                }
                else
                {
                    _footYaw -= steerIn * FootTurnRate * dt;
                    input = new Vector3(MathF.Sin(_footYaw), 0f, MathF.Cos(_footYaw)) * throttleIn;
                    if (throttleIn < 0f) slow = true; // all'indietro piano
                    face = _footYaw;
                }
            }
            _player.Handbrake = false;
            _player.Update(dt, Vector2.Zero, false, _world, _particles);
            _kid.Update(dt, input, slow, _world, _particles, _player.Blocks, face);
            if (_kid.Hidden)
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
            _player.Handbrake = _keyboard.IsKeyPressed(Key.Space)
                                || (_time >= _debug.HandbrakeFrom && _time < _debug.HandbrakeUntil);
            _player.Update(dt, new Vector2(steerIn, throttleIn), slow, _world, _particles);
        }
        // Il treno avanza; se finisce addosso all'auto o al personaggio li sposta di lato
        _trains?.Update(dt);
        if (_trains != null)
        {
            _trains.PushOut(ref _player.Position, 1.05f);
            if (_onFoot) _trains.PushOut(ref _kid.Position, Villager.Radius);
        }
        Zone? spawned = _world.Update(dt, ActivePos);
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
        _camera.Update(dt, ActivePos + ActiveVelocity * 0.3f, chase: !_farView, _onFoot ? _kid.Yaw : _player.TravelYaw,
                       walk: _onFoot);
        UpdateMap(dt);
        if (_engine != null) _engine.Horn = !_onFoot && _keyboard.IsKeyPressed(Key.E); // clacson (solo in auto)
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
                            + (_onFoot ? "  |  a piedi" : "") + (_player.Headlights ? "  |  fari accesi" : "") + $"  |  {perf}";
        }
    }

    private void CollectScene(Matrix4x4 viewProj)
    {
        _viewProj = viewProj;
        _opaque.Clear();
        _water.Clear();
        // Finestre, lampioni e lanterne: accesi col buio
        float night = Noise.Smooth(0.3f, 0.75f, 1f - _day.Daylight);

        foreach (var zone in _world.Zones)
        {
            // Si disegna solo ciò che è entro la nebbia E dentro l'inquadratura
            Vector4 area = ZoneArea(zone);
            float lift = zone.YOffset;
            if (!InView(area) || !InFrustum(area, -5f + MathF.Min(lift, 0f), 8f + MathF.Max(lift, 0f))) continue;
            Matrix4x4 m = zone.Model;
            _opaque.Add(new DrawItem(zone.Opaque, m, Area: area));
            _opaque.Add(new DrawItem(zone.Scenery, m, SeeThrough: true, Area: area));
            zone.CollectMovables(_opaque, area); // rocce e cespugli spostati, disegnati a parte
            if (zone.Water != null) _water.Add(new DrawItem(zone.Water, m, Area: area));
            if (zone.Glow != null)
                _opaque.Add(new DrawItem(zone.Glow, m, 0.1f + 0.9f * night, CastShadow: false, SeeThrough: true, Area: area));

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

        _cloudItems.Clear();
        _clouds.Collect(_cloudItems, _camera.Target, _time);

        _player.Collect(_opaque);
        _kid.Collect(_opaque);
        _particles.Collect(_opaque);
    }

    private unsafe void OnRender(double deltaTime)
    {
        _frames++;
        _frameTime += deltaTime;
        _worstFrame = Math.Max(_worstFrame, deltaTime);

        Vector2D<int> size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return;

        CollectScene(_camera.View * Projection((float)size.X / size.Y));

        // --- 1. Shadow map dal punto di vista del sole, centrata sulla camera ---
        // In vista di guida si vede lontano davanti: l'area delle ombre si sposta in avanti
        Vector3 center = _camera.Target + _camera.Forward * (12f * _camera.Chase);
        Vector3 lightDir = _day.LightDir;
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(center - lightDir * 50f, center, Vector3.UnitY);
        Matrix4x4 lightSpace = lightView * Matrix4x4.CreateOrthographic(56f, 56f, 1f, 110f);

        _shadow.Bind();
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(1.5f, 3f);
        _depth.Use();
        _depth.SetMatrix("uLightSpace", lightSpace);
        var shadowCenter = new Vector2(center.X, center.Z);
        foreach (var item in _opaque)
        {
            if (!item.CastShadow) continue;
            // Fuori dal riquadro della shadow map non serve disegnarlo nel pass delle ombre
            if (item.HasArea && RectDistance(shadowCenter, item.Area) > ShadowReach) continue;
            _depth.SetMatrix("uModel", item.Model);
            item.Mesh.Draw();
        }
        _gl.Disable(EnableCap.PolygonOffsetFill);

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

        _lit.Use();
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
        _lit.SetVector3("uCameraPos", _camera.Position);
        _lit.SetVector3("uFocus", ActivePos + new Vector3(0, _onFoot ? 0.35f : 0.45f, 0));

        _farFog = null;
        SetFog(false);
        _lit.SetFloat("uTime", _time);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _shadow.Texture);
        _lit.SetInt("uShadowMap", 0);

        _lit.SetInt("uWater", 0);
        _lit.SetInt("uCloud", 0);
        foreach (var item in _opaque)
        {
            _lit.SetMatrix("uModel", item.Model);
            _lit.SetInt("uSeeThrough", item.SeeThrough ? 1 : 0);
            _lit.SetInt("uSmooth", item.Smooth ? 1 : 0);
            _lit.SetFloat("uEmissive", item.Emissive);
            SetFog(item.Far);
            item.Mesh.Draw();
        }
        _lit.SetInt("uSmooth", 0);
        SetFog(false);

        _lit.SetInt("uSeeThrough", 0);
        _lit.SetInt("uCloud", 1);
        foreach (var item in _cloudItems)
        {
            _lit.SetMatrix("uModel", item.Model);
            _lit.SetFloat("uEmissive", item.Emissive);
            item.Mesh.Draw();
        }
        _lit.SetInt("uCloud", 0);

        // Impronte delle gomme: sopra il terreno, sotto acqua e trasparenze
        _skids.Draw(_camera.View, proj);
        _lit.Use();

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

        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        SetFog(false);

        if (_mapReveal > 0f) DrawBigMap(size);
        DrawCompass(size);
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
    /// Invio: se si guida e l'auto è (quasi) ferma, il personaggino salta fuori dalla portiera del guidatore
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
        // Si risale dalla portiera più vicina, se si è abbastanza vicini all'auto
        float best = float.MaxValue, bestSide = 1f;
        foreach (float side in new[] { 1f, -1f })
        {
            float d = Vector2.Distance(new(_kid.Position.X, _kid.Position.Z), new(_player.DoorPoint(side).X, _player.DoorPoint(side).Z));
            if (d < best) { best = d; bestSide = side; }
        }
        if (best < 1.3f) _kid.HopIn(_player.SeatPoint(bestSide));
    }

    private void DrawSeeThrough()
    {
        foreach (var item in _opaque)
        {
            if (!item.SeeThrough || (item.HasArea && !NearSightLine(item.Area))) continue;
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
        _shotIndex++;
        if (_shotIndex >= _debug.Shots.Count) _window.Close();
    }

    public void Dispose()
    {
        _engine?.Dispose();
        _hud?.Dispose();
        _world?.Dispose();
        _player?.Dispose();
        _kid?.Dispose();
        _particles?.Dispose();
        _trains?.Dispose();
        _flame?.Dispose();
        _shadow?.Dispose();
        _lit?.Dispose();
        _depth?.Dispose();
        _sky?.Dispose();
        _clouds?.Dispose();
        if (_skyVao != 0) _gl?.DeleteVertexArray(_skyVao);
        _input?.Dispose();
        _gl?.Dispose();
    }
}
