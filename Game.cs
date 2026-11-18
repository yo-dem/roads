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
    public List<(Vector2 Screen, float Time)> Clicks { get; } = new();
    public Vector3? Walk;
    public float WalkUntil = float.MaxValue;
    /// <summary>Guida manuale simulata, a tratti in sequenza: X = sterzo, Y = gas, fino all'istante Until.</summary>
    public List<(Vector2 Drive, float Until)> Drives { get; } = new();
    public float HandbrakeFrom = float.MaxValue, HandbrakeUntil = float.MaxValue; // freno a mano simulato (test)
    public bool Slow;
    public bool Mute;
    public string? Character; // personaggio di partenza (test)
    public List<float> Toggles { get; } = new(); // istanti in cui premere Invio (test)
    public float? Hour;            // ora di partenza (test)
    public bool Lights;            // fari accesi da subito (test)
    public float? Zoom;            // distanza della visuale lontana (test; la rotella va da 8 a 32)
    public bool Far;               // parte con la visuale lontana
    public int PreSpawn;
}

public sealed class Game : IDisposable
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
    private Mesh _flame = null!, _marker = null!;
    private IMouse? _mouse;
    private bool _mouseHeld, _rightHeld;
    private Vector2 _lastMouse;
    private float _markerAge;
    private int _toggleIndex;
    private bool _farView;        // visuale scelta: false = dietro l'auto (standard, tasto 1), true = lontana dall'alto (tasto 2)
    private bool _driving;        // guida da tastiera in corso (fino al prossimo click di destinazione)
    private int _clickIndex;

    private WorldMap _world = null!;
    private Car _player = null!;
    private Villager _kid = null!;   // il personaggino: scende e sale dall'auto con Invio
    private bool _onFoot;

    // Chi si sta comandando in questo momento: l'auto o il personaggino a piedi
    private Vector3 ActivePos => _onFoot ? _kid.Position : _player.Position;
    private float ActiveYaw => _onFoot ? _kid.Yaw : _player.Yaw;
    private Vector3 ActiveVelocity => _onFoot ? _kid.Velocity : _player.Velocity;
    private Particles _particles = null!;
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
            if (key == Key.C) // cambia personaggio (con Shift: il precedente)
            {
                _kid.CycleCharacter(_keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight) ? -1 : 1);
                Console.WriteLine($"Personaggio: {_kid.Character}");
            }
        };
        foreach (var mouse in _input.Mice)
        {
            _mouse ??= mouse;
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
                if (button == MouseButton.Right)
                {
                    _rightHeld = true;
                    _lastMouse = m.Position;
                    return;
                }
                if (button != MouseButton.Left) return;
                if (OverMinimap(m.Position)) return; // un click sulla minimappa non è una destinazione
                _mouseHeld = true;
                _driving = false; // il click passa al percorso automatico (la visuale non cambia)
                _mouse = m;
                if (Pick(m.Position)) _markerAge = 0f;
            };
            mouse.MouseUp += (_, button) =>
            {
                if (button == MouseButton.Left) _mouseHeld = false;
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
        _marker = BuildMarker(_gl);

        var start = new Vector3(-2.5f, 0f, 0.3f);
        _world = new WorldMap(_gl, start);
        if (_debug.PreSpawn > 0) _world.PreSpawn(_debug.PreSpawn);
        _particles = new Particles(_gl);
        start.Y = _world.GroundHeight(start);
        _player = new Car(_gl, start, MathF.PI / 2f);
        _kid = new Villager(_gl, start, 0f, _debug.Character ?? Villager.DefaultCharacter); // seduto in auto finché non si scende
        _farView = _debug.Far;
        if (_debug.Zoom is { } zoom) _camera.Distance = Math.Clamp(zoom, 8f, 32f);
        _player.Headlights = _debug.Lights;
        _camera.Snap(_player.Position, chase: !_farView, _player.Yaw);
        if (!_debug.Mute) _engine = new EngineSound();
    }

    private bool Down(Key a, Key b) => _keyboard.IsKeyPressed(a) || _keyboard.IsKeyPressed(b);

    private const float FieldOfView = MathF.PI / 180f * 40f;

    private Matrix4x4 Projection(float aspect) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FieldOfView, aspect, 0.3f, 300f);

    /// <summary>Converte un punto dello schermo in un raggio e imposta la destinazione del personaggio.</summary>
    private bool Pick(Vector2 screen)
    {
        Vector2D<int> size = _window.Size;
        if (size.X == 0 || size.Y == 0) return false;

        float x = 2f * screen.X / size.X - 1f;
        float y = 1f - 2f * screen.Y / size.Y;
        Matrix4x4 viewProj = _camera.View * Projection((float)size.X / size.Y);
        if (!Matrix4x4.Invert(viewProj, out Matrix4x4 inv)) return false;

        // System.Numerics usa profondità NDC in [0,1]
        Vector4 near = Vector4.Transform(new Vector4(x, y, 0f, 1f), inv);
        Vector4 far = Vector4.Transform(new Vector4(x, y, 1f, 1f), inv);
        Vector3 origin = new Vector3(near.X, near.Y, near.Z) / near.W;
        Vector3 dir = Vector3.Normalize(new Vector3(far.X, far.Y, far.Z) / far.W - origin);

        if (!_world.Raycast(origin, dir, out Vector3 hit)) return false;
        if (_onFoot) _kid.SetTarget(hit);
        else _player.SetTarget(hit);
        return true;
    }

    private static Mesh BuildMarker(GL gl)
    {
        var mb = new MeshBuilder();
        var color = new Vector3(1f, 0.95f, 0.7f);
        const int segments = 18;
        for (int s = 0; s < segments; s++)
        {
            float a0 = MathF.Tau * s / segments, a1 = MathF.Tau * (s + 1) / segments;
            Vector3 d0 = new(MathF.Cos(a0), 0, MathF.Sin(a0)), d1 = new(MathF.Cos(a1), 0, MathF.Sin(a1));
            mb.AddQuad(d0 * 0.32f, d1 * 0.32f, d1 * 0.45f, d0 * 0.45f, color);
        }
        mb.AddDisc(Vector3.Zero, 0.1f, 8, color);
        return mb.Build(gl);
    }

    private void OnUpdate(double deltaTime)
    {
        float dt = (float)Math.Min(deltaTime, 0.05);
        _time += dt;

        // Rotazione della visuale lontana
        if (_keyboard.IsKeyPressed(Key.Q)) _camera.Yaw -= 1.8f * dt;
        if (_keyboard.IsKeyPressed(Key.E)) _camera.Yaw += 1.8f * dt;

        // Tastiera = guida "da gioco di auto" in entrambe le visuali: sterzo e gas relativi all'auto
        float steerIn = (Down(Key.D, Key.Right) ? 1f : 0f) - (Down(Key.A, Key.Left) ? 1f : 0f);
        float throttleIn = (Down(Key.W, Key.Up) ? 1f : 0f) - (Down(Key.S, Key.Down) ? 1f : 0f);
        bool slow = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);
        Vector3 input = Vector3.Zero;

        // Tenendo premuto il tasto sinistro il personaggio segue il cursore
        if (_mouseHeld && _mouse != null) Pick(_mouse.Position);

        if (_debug.Walk is { } walk && _time > 0.5f && _time < _debug.WalkUntil)
            input = walk;
        if (_time > 0.5f)
            foreach (var (drive, until) in _debug.Drives)
                if (_time < until) { (steerIn, throttleIn) = (drive.X, drive.Y); break; }
        if (_debug.Slow) slow = true;
        while (_clickIndex < _debug.Clicks.Count && _time >= _debug.Clicks[_clickIndex].Time)
        {
            _driving = false;
            if (Pick(_debug.Clicks[_clickIndex].Screen)) _markerAge = 0f;
            _clickIndex++;
        }
        while (_toggleIndex < _debug.Toggles.Count && _time >= _debug.Toggles[_toggleIndex])
        {
            ToggleOnFoot();
            _toggleIndex++;
        }

        if (_onFoot)
        {
            // A piedi: WASD/frecce rispetto alla camera; l'auto resta parcheggiata a motore spento
            if (input == Vector3.Zero) input = _camera.Right * steerIn + _camera.Forward * throttleIn;
            _player.Handbrake = false;
            _player.Update(dt, Vector3.Zero, false, _world, _particles, Vector2.Zero);
            _kid.Update(dt, input, slow, _world, _particles, _player.Blocks);
            if (_kid.Hidden)
            {
                // Il saltello per rientrare è finito: si torna a guidare
                _onFoot = false;
                _player.EngineOn = true;
            }
        }
        else
        {
            // Un tasto di guida prende il controllo (e annulla il percorso); un click lo restituisce al percorso.
            // Rilasciati i tasti l'auto prosegue per inerzia, senza frenare da sola.
            if (steerIn != 0f || throttleIn != 0f) _driving = true;
            Vector2? manual = _driving ? new Vector2(steerIn, throttleIn) : null;
            // Freno a mano (spazio): tirato in velocità con lo sterzo fa entrare in curva in derapata
            _player.Handbrake = _keyboard.IsKeyPressed(Key.Space)
                                || (_time >= _debug.HandbrakeFrom && _time < _debug.HandbrakeUntil);
            if (_player.Handbrake && manual == null && MathF.Abs(_player.Speed) > 0.1f)
            {
                // Tirarlo mentre l'auto va da sola al punto cliccato passa alla guida manuale
                _driving = true;
                manual = new Vector2(steerIn, throttleIn);
            }
            _player.Update(dt, input, slow, _world, _particles, manual);
        }
        _markerAge += dt;
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
        // La camera anticipa un po' nella direzione di marcia, di più quando si va veloci
        // In derapata la camera da dietro segue la direzione di marcia, così si vede l'auto di traverso
        _camera.Update(dt, ActivePos + ActiveVelocity * 0.3f, chase: !_farView && !_onFoot, _player.TravelYaw,
                       walk: !_farView && _onFoot);
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
            _window.Title = $"Roads  |  {_day.Clock}  |  {here?.Name ?? "..."}  |  territorio esplorato: {_world.Count}"
                            + (_onFoot ? "  |  a piedi" : "") + (_player.Headlights ? "  |  fari accesi" : "") + $"  |  {perf}";
        }
    }

    private void CollectScene(Matrix4x4 viewProj)
    {
        _viewProj = viewProj;
        _opaque.Clear();
        _water.Clear();

        foreach (var zone in _world.Zones)
        {
            // Si disegna solo ciò che è entro la nebbia E dentro l'inquadratura
            Vector4 area = ZoneArea(zone);
            float lift = zone.YOffset;
            if (!InView(area) || !InFrustum(area, -5f + MathF.Min(lift, 0f), 8f + MathF.Max(lift, 0f))) continue;
            Matrix4x4 m = zone.Model;
            _opaque.Add(new DrawItem(zone.Opaque, m, Area: area));
            _opaque.Add(new DrawItem(zone.Scenery, m, SeeThrough: true, Area: area));
            if (zone.Water != null) _water.Add(new DrawItem(zone.Water, m, Area: area));

            for (int i = 0; i < zone.Campfires.Count; i++)
            {
                float t = _time * 1f + i;
                var flicker = Matrix4x4.CreateScale(1f + 0.12f * MathF.Sin(t * 11f), 0.9f + 0.2f * MathF.Sin(t * 17f + 1f), 1f + 0.12f * MathF.Cos(t * 13f));
                _opaque.Add(new DrawItem(_flame, flicker * Matrix4x4.CreateRotationY(t * 0.8f)
                                                 * Matrix4x4.CreateTranslation(zone.Campfires[i] + new Vector3(0, 0.1f, 0)) * m,
                                         0.9f, CastShadow: false));
            }
        }

        _cloudItems.Clear();
        _clouds.Collect(_cloudItems, _camera.Target, _time);

        _player.Collect(_opaque);
        _kid.Collect(_opaque);
        _particles.Collect(_opaque);

        // Indicatore di destinazione: "atterra" ingrandito e poi pulsa
        if ((_onFoot ? _kid.Target : _player.Target) is { } target)
        {
            float pop = 1f + 0.8f * MathF.Max(0f, 1f - _markerAge / 0.2f);
            float pulse = 1f + 0.08f * MathF.Sin(_time * 7f);
            _opaque.Add(new DrawItem(_marker,
                Matrix4x4.CreateScale(pop * pulse) * Matrix4x4.CreateRotationY(_time * 1.5f)
                * Matrix4x4.CreateTranslation(target + new Vector3(0, 0.07f, 0)), 0.6f, CastShadow: false));
        }
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
        // Bagliore degli stop: visibile di notte (di giorno il sole lo copre), forte in frenata
        float darkness = 1f - _day.Daylight;
        _lit.SetVector3("uTailPos", _player.TailLightPos);
        _lit.SetVector3("uTailDir", _player.TailLightDir);
        _lit.SetFloat("uTailGlow", darkness * ((_player.Headlights ? 0.25f : 0f) + 0.9f * _player.BrakeLight));
        _lit.SetVector3("uCameraPos", _camera.Position);
        _lit.SetVector3("uFocus", ActivePos + new Vector3(0, _onFoot ? 0.35f : 0.45f, 0));

        _lit.SetFloat("uFogNear", _camera.EffectiveDistance + 14f);
        _lit.SetFloat("uFogFar", ViewRange);
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
            item.Mesh.Draw();
        }
        _lit.SetInt("uSmooth", 0);

        _lit.SetInt("uSeeThrough", 0);
        _lit.SetInt("uCloud", 1);
        foreach (var item in _cloudItems)
        {
            _lit.SetMatrix("uModel", item.Model);
            _lit.SetFloat("uEmissive", item.Emissive);
            item.Mesh.Draw();
        }
        _lit.SetInt("uCloud", 0);

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

        DrawMinimap(size);
        TakeScreenshotIfDue(size);
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

    // ------------------------------------------------------------------ minimappa

    // Dimensioni in punti della finestra (sugli schermi Retina vengono moltiplicate)
    private const float MapRadius = 62f, MapMargin = 18f;
    private static readonly Vector2 MapCenter = new(MapMargin + MapRadius, MapMargin + MapRadius);

    private bool OverMinimap(Vector2 windowPos) => Vector2.Distance(windowPos, MapCenter) < MapRadius;

    /// <summary>
    /// Minimappa in alto a sinistra: ruota con la camera (in alto c'è sempre la direzione in cui si guarda),
    /// con i punti cardinali sul bordo e al centro una freccia con la direzione dell'auto.
    /// Pronta per mostrare in futuro gli obiettivi.
    /// </summary>
    private void DrawMinimap(Vector2D<int> framebuffer)
    {
        float k = _window.Size.X > 0 ? (float)framebuffer.X / _window.Size.X : 1f; // pixel per punto
        Vector2 c = MapCenter * k;
        float r = MapRadius * k;

        // Direzione del mondo → schermo: l'avanti della camera punta in alto
        Vector3 right = _camera.Right, forward = _camera.Forward;
        Vector2 ToMap(Vector3 world) => new(Vector3.Dot(world, right), -Vector3.Dot(world, forward));

        var white = new Vector4(0.93f, 0.95f, 0.98f, 0.95f);
        var north = new Vector4(0.98f, 0.38f, 0.32f, 1f);

        _hud.Disc(c, r, 32, new Vector4(0.05f, 0.07f, 0.11f, 0.55f));
        _hud.Ring(c, r - 2f * k, r, 32, new Vector4(0.93f, 0.95f, 0.98f, 0.8f));

        // Tacche: lunghe sui punti cardinali, corte su quelli intermedi
        for (int i = 0; i < 8; i++)
        {
            float a = MathF.Tau * i / 8f;
            Vector2 d = ToMap(new Vector3(MathF.Sin(a), 0f, -MathF.Cos(a))); // i = 0 → nord (-Z), poi in senso orario
            float inner = i % 2 == 0 ? r - 9f * k : r - 6f * k;
            _hud.Line(c + d * inner, c + d * (r - 2f * k), 2f * k, i == 0 ? north : white with { W = 0.7f });
        }

        // Lettere sempre dritte, che girano lungo il bordo
        foreach (var (letter, dir) in new[]
                 { ('N', new Vector3(0, 0, -1)), ('E', new Vector3(1, 0, 0)), ('S', new Vector3(0, 0, 1)), ('O', new Vector3(-1, 0, 0)) })
            _hud.Letter(letter, c + ToMap(dir) * (r - 20f * k), 12f * k, 2.2f * k, letter == 'N' ? north : white);

        // Auto al centro: freccia a punta nella sua direzione, con un bordo scuro per staccarla dallo sfondo
        Vector2 h = ToMap(new Vector3(MathF.Sin(ActiveYaw), 0f, MathF.Cos(ActiveYaw)));
        if (h.LengthSquared() > 1e-6f) h = Vector2.Normalize(h);
        Vector2 side = new(-h.Y, h.X);
        void Arrow(float s, Vector4 color)
        {
            Vector2 tip = c + h * 11f * k * s, back = c - h * 3f * k * s;
            Vector2 left = c - h * 8f * k * s + side * 7f * k * s, rightCorner = c - h * 8f * k * s - side * 7f * k * s;
            _hud.Triangle(tip, left, back, color);
            _hud.Triangle(tip, back, rightCorner, color);
        }
        Arrow(1.3f, new Vector4(0.05f, 0.07f, 0.11f, 0.9f));
        Arrow(1f, new Vector4(1f, 0.84f, 0.38f, 1f));

        _hud.Flush(framebuffer.X, framebuffer.Y);
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
                _onFoot = true;
                _driving = false;
                _player.ClearTarget();
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
            item.Mesh.Draw();
        }
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
        Console.WriteLine($"Screenshot salvato: {_debug.Shots[_shotIndex].Path}");
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
        _flame?.Dispose();
        _marker?.Dispose();
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
