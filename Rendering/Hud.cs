using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Roads.Rendering;

/// <summary>
/// Interfaccia 2D sopra la scena: triangoli colorati (anche semitrasparenti) in coordinate pixel,
/// origine in alto a sinistra. Si accumula la geometria del fotogramma e la si disegna in un colpo.
/// Include un piccolo font "a tratti" spigoloso, coerente con lo stile low poly.
/// </summary>
public sealed class Hud : IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec4 aColor;
        uniform vec2 uScreen;
        out vec4 vColor;
        void main()
        {
            vColor = aColor;
            gl_Position = vec4(aPos.x / uScreen.x * 2.0 - 1.0, 1.0 - aPos.y / uScreen.y * 2.0, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec4 vColor;
        out vec4 FragColor;
        void main() { FragColor = vColor; }
        """;

    private const int FloatsPerVertex = 6; // x, y, r, g, b, a

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo;
    private readonly List<float> _vertices = new();

    public unsafe Hud(GL gl)
    {
        _gl = gl;
        _shader = new Shader(gl, VertexSource, FragmentSource);
        _vao = _gl.GenVertexArray();
        _vbo = _gl.GenBuffer();
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.BindVertexArray(0);
    }

    // ------------------------------------------------------------------ primitive

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Vector4 color)
    {
        foreach (var p in new[] { a, b, c })
        {
            _vertices.Add(p.X); _vertices.Add(p.Y);
            _vertices.Add(color.X); _vertices.Add(color.Y); _vertices.Add(color.Z); _vertices.Add(color.W);
        }
    }

    public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector4 color)
    {
        Triangle(a, b, c, color);
        Triangle(a, c, d, color);
    }

    /// <summary>Segmento spesso <paramref name="width"/> pixel.</summary>
    public void Line(Vector2 a, Vector2 b, float width, Vector4 color)
    {
        Vector2 d = b - a;
        if (d.LengthSquared() < 1e-6f) return;
        Vector2 n = Vector2.Normalize(new Vector2(-d.Y, d.X)) * (width * 0.5f);
        Quad(a + n, b + n, b - n, a - n, color);
    }

    /// <summary>Disco a facce (poligono regolare), come le forme del mondo.</summary>
    public void Disc(Vector2 center, float radius, int sides, Vector4 color)
    {
        for (int i = 0; i < sides; i++)
            Triangle(center, center + Polar(radius, MathF.Tau * i / sides), center + Polar(radius, MathF.Tau * (i + 1) / sides), color);
    }

    /// <summary>Anello tra <paramref name="inner"/> e <paramref name="outer"/>.</summary>
    public void Ring(Vector2 center, float inner, float outer, int sides, Vector4 color)
    {
        for (int i = 0; i < sides; i++)
        {
            float a0 = MathF.Tau * i / sides, a1 = MathF.Tau * (i + 1) / sides;
            Quad(center + Polar(inner, a0), center + Polar(outer, a0), center + Polar(outer, a1), center + Polar(inner, a1), color);
        }
    }

    private static Vector2 Polar(float r, float angle) => new(MathF.Cos(angle) * r, MathF.Sin(angle) * r);

    // ------------------------------------------------------------------ testo a tratti

    // Ogni lettera è una serie di spezzate in un quadrato 0..1 (y verso il basso)
    private static readonly Dictionary<char, Vector2[][]> Glyphs = new()
    {
        ['N'] = [[new(0, 1), new(0, 0), new(1, 1), new(1, 0)]],
        ['E'] = [[new(1, 0), new(0, 0), new(0, 1), new(1, 1)], [new(0, 0.5f), new(0.8f, 0.5f)]],
        ['S'] = [[new(1, 0), new(0, 0), new(0, 0.5f), new(1, 0.5f), new(1, 1), new(0, 1)]],
        ['O'] = [[new(0, 0), new(1, 0), new(1, 1), new(0, 1), new(0, 0)]],
    };

    /// <summary>Lettera centrata in <paramref name="center"/>, alta <paramref name="size"/> pixel.</summary>
    public void Letter(char c, Vector2 center, float size, float stroke, Vector4 color)
    {
        if (!Glyphs.TryGetValue(c, out var strokes)) return;
        float w = size * 0.72f;
        Vector2 origin = center - new Vector2(w, size) * 0.5f;
        foreach (var poly in strokes)
            for (int i = 0; i + 1 < poly.Length; i++)
            {
                Vector2 a = origin + poly[i] * new Vector2(w, size), b = origin + poly[i + 1] * new Vector2(w, size);
                // Allungo di mezzo tratto agli estremi: gli angoli restano pieni
                Vector2 d = Vector2.Normalize(b - a) * (stroke * 0.5f);
                Line(a - d, b + d, stroke, color);
            }
    }

    // ------------------------------------------------------------------ disegno

    /// <summary>Disegna tutto ciò che è stato accumulato e svuota la lista.</summary>
    public void Flush(int width, int height)
    {
        if (_vertices.Count == 0) return;
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shader.Use();
        _shader.SetVector2("uScreen", new Vector2(width, height));
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)CollectionsMarshal.AsSpan(_vertices), BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_vertices.Count / FloatsPerVertex));
        _gl.BindVertexArray(0);

        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _vertices.Clear();
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}
