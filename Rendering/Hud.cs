using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace Roads.Rendering;

/// <summary>
/// Interfaccia 2D sopra la scena: triangoli colorati (anche semitrasparenti) in coordinate pixel,
/// origine in alto a sinistra. Si accumula la geometria del fotogramma e la si disegna in un colpo.
/// Include un piccolo font "a tratti" spigoloso, coerente con lo stile low poly.
/// </summary>
public sealed class Hud : HudGeometry, IDisposable
{
    private const string VertexSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec4 aColor;
        uniform vec2 uScreen;
        uniform mat3 uXform;   // trasformazione dei vertici (identità per la geometria del fotogramma)
        out vec4 vColor;
        out vec2 vPos;
        void main()
        {
            vColor = aColor;
            vec2 p = (uXform * vec3(aPos, 1.0)).xy;
            vPos = p;
            gl_Position = vec4(p.x / uScreen.x * 2.0 - 1.0, 1.0 - p.y / uScreen.y * 2.0, 0.0, 1.0);
        }
        """;

    private const string FragmentSource = """
        #version 330 core
        in vec4 vColor;
        in vec2 vPos;
        uniform vec3 uClip;    // ritaglio circolare: centro (pixel) e raggio; raggio 0 = niente ritaglio
        out vec4 FragColor;
        void main()
        {
            if (uClip.z > 0.0 && distance(vPos, uClip.xy) > uClip.z) discard;
            FragColor = vColor;
        }
        """;

    private readonly GL _gl;
    private readonly Shader _shader;
    private readonly uint _vao, _vbo;

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

    // ------------------------------------------------------------------ disegno

    /// <summary>
    /// Disegna tutto ciò che è stato accumulato e svuota la lista; con <paramref name="clip"/> (centro e raggio in
    /// pixel) solo dentro quel cerchio.
    /// </summary>
    public void Flush(int width, int height, Vector3 clip = default)
    {
        if (_vertices.Count == 0) return;
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shader.Use();
        _shader.SetVector2("uScreen", new Vector2(width, height));
        SetXform(Matrix3x2.Identity);
        _shader.SetVector3("uClip", clip);
        _gl.BindVertexArray(_vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)CollectionsMarshal.AsSpan(_vertices), BufferUsageARB.StreamDraw);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, (uint)(_vertices.Count / FloatsPerVertex));
        _gl.BindVertexArray(0);

        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
        _vertices.Clear();
    }

    private unsafe void SetXform(Matrix3x2 m)
    {
        // mat3 colonna per colonna: x' = M11 x + M21 y + M31, y' = M12 x + M22 y + M32
        Span<float> v = stackalloc float[] { m.M11, m.M12, 0f, m.M21, m.M22, 0f, m.M31, m.M32, 1f };
        _shader.SetMatrix3("uXform", v);
    }

    // ------------------------------------------------------------------ strati in cache

    /// <summary>Quanta geometria è stata accumulata finora (per catturarne una parte in uno strato).</summary>
    public int Mark() => _vertices.Count;

    /// <summary>
    /// Toglie dal fotogramma la geometria accumulata da <paramref name="from"/> in poi e la carica una volta sola sulla
    /// scheda grafica: poi la si ridisegna a ogni fotogramma con <see cref="DrawLayer"/>, solo cambiandone la posizione.
    /// </summary>
    public HudLayer Capture(int from)
    {
        var layer = Upload(CollectionsMarshal.AsSpan(_vertices)[from..]);
        _vertices.RemoveRange(from, _vertices.Count - from);
        return layer;
    }

    /// <summary>Carica sulla scheda grafica una geometria costruita a parte (anche su un altro thread).</summary>
    public HudLayer Capture(HudGeometry geometry) => Upload(CollectionsMarshal.AsSpan(geometry.Vertices));

    private unsafe HudLayer Upload(ReadOnlySpan<float> data)
    {
        uint vao = _gl.GenVertexArray(), vbo = _gl.GenBuffer();
        _gl.BindVertexArray(vao);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, (ReadOnlySpan<float>)data, BufferUsageARB.StaticDraw);
        uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 4, VertexAttribPointerType.Float, false, stride, (void*)(2 * sizeof(float)));
        _gl.BindVertexArray(0);
        return new HudLayer(_gl, vao, vbo, (uint)(data.Length / FloatsPerVertex));
    }

    /// <summary>Disegna uno strato in cache trasformato da <paramref name="m"/>, ritagliato nel cerchio <paramref name="clip"/>.</summary>
    public void DrawLayer(HudLayer layer, int width, int height, Matrix3x2 m, Vector3 clip)
    {
        if (layer.Count == 0) return;
        _gl.Disable(EnableCap.DepthTest);
        _gl.Enable(EnableCap.Blend);
        _gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        _shader.Use();
        _shader.SetVector2("uScreen", new Vector2(width, height));
        SetXform(m);
        _shader.SetVector3("uClip", clip);
        _gl.BindVertexArray(layer.Vao);
        _gl.DrawArrays(PrimitiveType.Triangles, 0, layer.Count);
        _gl.BindVertexArray(0);
        _gl.Disable(EnableCap.Blend);
        _gl.Enable(EnableCap.DepthTest);
    }

    public void Dispose()
    {
        _shader.Dispose();
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteVertexArray(_vao);
    }
}

/// <summary>Geometria 2D caricata una volta sulla scheda grafica (vedi <see cref="Hud.Capture"/>).</summary>
public sealed class HudLayer(GL gl, uint vao, uint vbo, uint count) : IDisposable
{
    public uint Vao => vao;
    public uint Count => count;

    public void Dispose()
    {
        gl.DeleteBuffer(vbo);
        gl.DeleteVertexArray(vao);
    }
}

/// <summary>
/// Geometria 2D in costruzione: triangoli colorati in coordinate pixel (origine in alto a sinistra), più un piccolo
/// font "a tratti". Non tocca la scheda grafica, quindi si può costruire su qualunque thread.
/// </summary>
public class HudGeometry
{
    protected const int FloatsPerVertex = 6; // x, y, r, g, b, a
    protected readonly List<float> _vertices = new();
    internal List<float> Vertices => _vertices;

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

}
