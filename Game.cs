using System.Numerics;
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
    public bool Slow;
}

public sealed class Game : IDisposable
{
    private static readonly Vector3 Background = new(0.55f, 0.77f, 0.95f);
    private static readonly Vector3 LightDir = Vector3.Normalize(new Vector3(0.45f, -1f, -0.35f));

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
    private int _clickIndex;

    private WorldMap _world = null!;
    private Car _player = null!; // il personaggio (Villager) è messo da parte
    private Particles _particles = null!;
    private readonly IsoCamera _camera = new();

    private readonly List<DrawItem> _opaque = new();
    private readonly List<DrawItem> _water = new();
    private readonly Random _rng = new();
    private float _time;
    private double _titleTimer;
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
        _keyboard.KeyDown += (_, key, _) => { if (key == Key.Escape) _window.Close(); };
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
                _mouseHeld = true;
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
        _flame = Props.BuildFlame(_gl);
        _marker = BuildMarker(_gl);

        _world = new WorldMap(_gl);
        _particles = new Particles(_gl);
        var start = new Vector3(-2.5f, 0f, 0.3f);
        start.Y = _world.GroundHeight(start);
        _player = new Car(_gl, start, MathF.PI / 2f);
        _camera.Snap(_player.Position);
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
        _player.SetTarget(hit);
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

        // Rotazione e input relativo alla camera
        if (_keyboard.IsKeyPressed(Key.Q)) _camera.Yaw -= 1.8f * dt;
        if (_keyboard.IsKeyPressed(Key.E)) _camera.Yaw += 1.8f * dt;

        float ix = (Down(Key.D, Key.Right) ? 1f : 0f) - (Down(Key.A, Key.Left) ? 1f : 0f);
        float iy = (Down(Key.W, Key.Up) ? 1f : 0f) - (Down(Key.S, Key.Down) ? 1f : 0f);
        Vector3 input = _camera.Right * ix + _camera.Forward * iy;
        bool slow = _keyboard.IsKeyPressed(Key.ShiftLeft) || _keyboard.IsKeyPressed(Key.ShiftRight);

        // Tenendo premuto il tasto sinistro il personaggio segue il cursore
        if (_mouseHeld && _mouse != null) Pick(_mouse.Position);

        if (_debug.Walk is { } walk && _time > 0.5f && _time < _debug.WalkUntil)
            input = walk;
        if (_debug.Slow) slow = true;
        while (_clickIndex < _debug.Clicks.Count && _time >= _debug.Clicks[_clickIndex].Time)
        {
            if (Pick(_debug.Clicks[_clickIndex].Screen)) _markerAge = 0f;
            _clickIndex++;
        }

        _player.Update(dt, input, slow, _world, _particles);
        _markerAge += dt;
        Zone? spawned = _world.Update(dt, _player.Position);
        if (spawned != null)
            Console.WriteLine($"Nuova zona ({spawned.X},{spawned.Z}): {spawned.Name}");

        // Scintille dai falò
        foreach (var zone in _world.Zones)
            foreach (var c in zone.Campfires)
                if (_rng.NextSingle() < dt * 7f)
                    _particles.Ember(zone.Origin + c + new Vector3(0, zone.YOffset, 0));

        _particles.Update(dt);
        _camera.Update(dt, _player.Position);

        _titleTimer += deltaTime;
        if (_titleTimer > 0.25)
        {
            _titleTimer = 0;
            var here = _world.ZoneAt(_player.Position);
            _window.Title = $"Roads  |  {here?.Name ?? "..."}  |  isole scoperte: {_world.Count}";
        }
    }

    private void CollectScene()
    {
        _opaque.Clear();
        _water.Clear();

        foreach (var zone in _world.Zones)
        {
            Matrix4x4 m = zone.Model;
            _opaque.Add(new DrawItem(zone.Opaque, m));
            if (zone.Water != null) _water.Add(new DrawItem(zone.Water, m));

            for (int i = 0; i < zone.Campfires.Count; i++)
            {
                float t = _time * 1f + i;
                var flicker = Matrix4x4.CreateScale(1f + 0.12f * MathF.Sin(t * 11f), 0.9f + 0.2f * MathF.Sin(t * 17f + 1f), 1f + 0.12f * MathF.Cos(t * 13f));
                _opaque.Add(new DrawItem(_flame, flicker * Matrix4x4.CreateRotationY(t * 0.8f)
                                                 * Matrix4x4.CreateTranslation(zone.Campfires[i] + new Vector3(0, 0.1f, 0)) * m,
                                         0.9f, CastShadow: false));
            }
        }

        foreach (var c in _world.Connectors)
            _opaque.Add(new DrawItem(c.Mesh, c.Model));

        _cloudItems.Clear();
        _clouds.Collect(_cloudItems, _camera.Target, _time);

        _player.Collect(_opaque);
        _particles.Collect(_opaque);

        // Indicatore di destinazione: "atterra" ingrandito e poi pulsa
        if (_player.Target is { } target)
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
        Vector2D<int> size = _window.FramebufferSize;
        if (size.X == 0 || size.Y == 0) return;

        CollectScene();

        // --- 1. Shadow map dal punto di vista del sole, centrata sulla camera ---
        Vector3 center = _camera.Target;
        Matrix4x4 lightView = Matrix4x4.CreateLookAt(center - LightDir * 50f, center, Vector3.UnitY);
        Matrix4x4 lightSpace = lightView * Matrix4x4.CreateOrthographic(56f, 56f, 1f, 110f);

        _shadow.Bind();
        _gl.Clear(ClearBufferMask.DepthBufferBit);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(1.5f, 3f);
        _depth.Use();
        _depth.SetMatrix("uLightSpace", lightSpace);
        foreach (var item in _opaque)
        {
            if (!item.CastShadow) continue;
            _depth.SetMatrix("uModel", item.Model);
            item.Mesh.Draw();
        }
        _gl.Disable(EnableCap.PolygonOffsetFill);

        // --- 2. Scena principale ---
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _gl.ClearColor(Background.X, Background.Y, Background.Z, 1f);
        _gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var proj = Projection((float)size.X / size.Y);

        // Cielo a gradiente dietro a tutto
        if (Matrix4x4.Invert(_camera.View * proj, out Matrix4x4 invViewProj))
        {
            _gl.Disable(EnableCap.DepthTest);
            _gl.DepthMask(false);
            _sky.Use();
            _sky.SetMatrix("uInvViewProj", invViewProj);
            _gl.BindVertexArray(_skyVao);
            _gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
            _gl.DepthMask(true);
            _gl.Enable(EnableCap.DepthTest);
        }

        _lit.Use();
        _lit.SetMatrix("uProj", proj);
        _lit.SetMatrix("uView", _camera.View);
        _lit.SetMatrix("uLightSpace", lightSpace);
        _lit.SetVector3("uLightDir", LightDir);
        _lit.SetVector3("uCameraPos", _camera.Position);

        _lit.SetFloat("uFogNear", _camera.Distance + 14f);
        _lit.SetFloat("uFogFar", _camera.Distance + 55f);
        _lit.SetFloat("uTime", _time);
        _gl.ActiveTexture(TextureUnit.Texture0);
        _gl.BindTexture(TextureTarget.Texture2D, _shadow.Texture);
        _lit.SetInt("uShadowMap", 0);

        _lit.SetInt("uWater", 0);
        _lit.SetInt("uCloud", 0);
        foreach (var item in _opaque)
        {
            _lit.SetMatrix("uModel", item.Model);
            _lit.SetFloat("uEmissive", item.Emissive);
            item.Mesh.Draw();
        }

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
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);

        TakeScreenshotIfDue(size);
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
        _world?.Dispose();
        _player?.Dispose();
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
