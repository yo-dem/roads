using System.Numerics;
using Roads.Characters;
using Silk.NET.OpenGL;

namespace Roads.Rendering;

/// <summary>
/// Impronte delle gomme a terra: strisce scure lasciate dalle ruote bloccate in frenata (tutte e quattro) o
/// dalle posteriori in derapata col freno a mano. Restano qualche secondo, poi sbiadiscono e spariscono.
/// Si disegnano moltiplicando il colore di ciò che sta sotto (scuriscono asfalto ed erba, di giorno e di notte).
/// </summary>
public sealed class SkidMarks : IDisposable
{
    private const float Life = 18f;        // s: durata complessiva di un segno
    private const float FadeFrom = 5f;     // s: da qui comincia a sbiadire
    private const float Spacing = 0.12f;   // distanza minima tra due punti della striscia
    private const float HalfWidth = 0.075f;
    private const float Darkness = 0.28f;  // quanto scurisce il terreno (appena lasciato): grigio, non nero
    private const float Lift = 0.02f;
    private const int MaxQuads = 6000;

    private struct Quad
    {
        public Vector3 A0, A1, B0, B1; // A = inizio, B = fine (0 / 1 = i due bordi)
        public float Born, Strength;
    }

    private struct Track
    {
        public bool Active;
        public Vector3 Last;
        public Vector3 LastSide;
        public float Dash;   // metri che mancano alla fine del tratto in corso (segno o pausa)
        public bool Gap;     // in pausa: la gomma "riprende aderenza" e non segna
        public float Grain;  // intensità del tratto in corso (variabile, come una gomma che morde a scatti)
    }

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo;
    private readonly List<Quad> _quads = new();
    private readonly Track[] _tracks = new Track[4];
    private float[] _buffer = Array.Empty<float>();
    private float _time;
    private readonly Random _rng = new();

    private static readonly (float Sx, float Sz)[] Wheels = { (-1f, -1f), (1f, -1f), (-1f, 1f), (1f, 1f) };

    private const string Vertex = """
        #version 330 core
        layout (location = 0) in vec3 aPos;
        layout (location = 1) in float aAlpha;
        uniform mat4 uView;
        uniform mat4 uProj;
        out float vAlpha;
        void main() { vAlpha = aAlpha; gl_Position = uProj * uView * vec4(aPos, 1.0); }
        """;

    private const string Fragment = """
        #version 330 core
        in float vAlpha;
        out vec4 FragColor;
        void main() { FragColor = vec4(vec3(1.0 - vAlpha), 1.0); }
        """;

    public unsafe SkidMarks(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, Vertex, Fragment);
        _vao = gl.GenVertexArray();
        _vbo = gl.GenBuffer();
        gl.BindVertexArray(_vao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(1, 1, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(3 * sizeof(float)));
        gl.EnableVertexAttribArray(1);
        gl.BindVertexArray(0);
    }

    public void Update(float dt, Car car)
    {
        _time += dt;
        _quads.RemoveAll(q => _time - q.Born > Life);

        int marking = car.MarkingWheels;
        Vector3 fwd = car.Heading;
        for (int w = 0; w < 4; w++)
        {
            var (sx, sz) = Wheels[w];
            bool on = marking == 4 || (marking == 2 && sz < 0f);
            ref var t = ref _tracks[w];
            if (!on) { t.Active = false; continue; }
            Vector3 p = car.WheelContact(sx, sz) + new Vector3(0f, Lift, 0f);
            if (!t.Active)
            {
                t.Active = true;
                t.Last = p;
                t.LastSide = default;
                t.Gap = false;
                t.Dash = 0.5f + _rng.NextSingle() * 2f;
                t.Grain = 0.6f + _rng.NextSingle() * 0.4f;
                continue;
            }
            Vector3 d = p - t.Last;
            float len = MathF.Sqrt(d.X * d.X + d.Z * d.Z);
            if (len < Spacing) continue;
            if (len > 1.5f) { t.Last = p; t.LastSide = default; continue; } // salto (teletrasporto): niente striscia
            // La striscia è larga quanto la gomma, di traverso rispetto alla direzione in cui la ruota si muove:
            // in derapata la gomma striscia di lato e il segno si allarga un po'
            // Tratteggio irregolare: lunghi tratti continui alternati, a volte, a brevi pause
            t.Dash -= len;
            if (t.Dash <= 0f)
            {
                t.Gap = !t.Gap && _rng.NextSingle() < 0.45f;
                t.Dash = t.Gap ? 0.15f + _rng.NextSingle() * 0.35f : 0.5f + _rng.NextSingle() * 2.5f;
                t.Grain = 0.6f + _rng.NextSingle() * 0.4f;
            }
            if (t.Gap) { t.Last = p; t.LastSide = default; continue; }
            Vector3 dir = new(d.X / len, 0f, d.Z / len);
            Vector3 across = new(dir.Z, 0f, -dir.X);
            float slide = 1f - MathF.Abs(Vector3.Dot(dir, fwd));
            Vector3 n = across * (HalfWidth * (1f + 1.2f * slide));
            if (_quads.Count >= MaxQuads) _quads.RemoveAt(0);
            _quads.Add(new Quad
            {
                A0 = t.Last - (t.LastSide == default ? n : t.LastSide), A1 = t.Last + (t.LastSide == default ? n : t.LastSide),
                B0 = p - n, B1 = p + n,
                Born = _time,
                Strength = car.MarkStrength * t.Grain * (0.85f + _rng.NextSingle() * 0.15f),
            });
            t.Last = p;
            t.LastSide = n; // il prossimo tratto parte dallo stesso bordo: striscia continua
        }
    }

    public unsafe void Draw(Matrix4x4 view, Matrix4x4 proj)
    {
        if (_quads.Count == 0) return;
        int need = _quads.Count * 6 * 4;
        if (_buffer.Length < need) _buffer = new float[need * 2];
        int o = 0;
        foreach (var q in _quads)
        {
            float age = _time - q.Born;
            float a = Darkness * q.Strength * (1f - Smooth(FadeFrom, Life, age));
            Put(ref o, q.A0, a); Put(ref o, q.B0, a); Put(ref o, q.B1, a);
            Put(ref o, q.A0, a); Put(ref o, q.B1, a); Put(ref o, q.A1, a);
        }

        _shader.Use();
        _shader.SetMatrix("uView", view);
        _shader.SetMatrix("uProj", proj);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* ptr = _buffer)
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(o * sizeof(float)), ptr, BufferUsageARB.StreamDraw);

        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.Zero, BlendingFactor.SrcColor); // moltiplica: scurisce ciò che c'è sotto
        _gl.DepthMask(false);
        _gl.Disable(EnableCap.CullFace);
        _gl.Enable(EnableCap.PolygonOffsetFill);
        _gl.PolygonOffset(-2f, -2f);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(o / 4));
        _gl.Disable(EnableCap.PolygonOffsetFill);
        _gl.DepthMask(true);
        _gl.Disable(EnableCap.Blend);
        _gl.BindVertexArray(0);
    }

    private void Put(ref int o, Vector3 p, float a)
    {
        _buffer[o++] = p.X; _buffer[o++] = p.Y; _buffer[o++] = p.Z; _buffer[o++] = a;
    }

    private static float Smooth(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
        _shader.Dispose();
    }
}
