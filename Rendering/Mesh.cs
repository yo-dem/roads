using Silk.NET.OpenGL;

namespace Roads.Rendering;

/// <summary>
/// Mesh su GPU. Formato vertice: posizione (3) + normale (3) + colore (3) + baricentriche per gli spigoli (3)
/// + ingombro dell'oggetto di appartenenza: centro x/z e raggio (3), quota minima e massima (2).
/// </summary>
public sealed class Mesh : IDisposable
{
    public const int FloatsPerVertex = 17;

    private readonly GL _gl;
    private readonly uint _vao, _vbo, _ebo;
    private readonly uint _indexCount;

    public Mesh(GL gl, MeshData data) : this(gl, data.Vertices, data.Indices) { }

    public unsafe Mesh(GL gl, ReadOnlySpan<float> vertices, ReadOnlySpan<uint> indices)
    {
        _gl = gl;
        _indexCount = (uint)indices.Length;

        _vao = _gl.GenVertexArray();
        _gl.BindVertexArray(_vao);

        _vbo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        _gl.BufferData(BufferTargetARB.ArrayBuffer, vertices, BufferUsageARB.StaticDraw);

        _ebo = _gl.GenBuffer();
        _gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, _ebo);
        _gl.BufferData(BufferTargetARB.ElementArrayBuffer, indices, BufferUsageARB.StaticDraw);

        uint stride = FloatsPerVertex * sizeof(float);
        _gl.EnableVertexAttribArray(0);
        _gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);
        _gl.EnableVertexAttribArray(1);
        _gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)(3 * sizeof(float)));
        _gl.EnableVertexAttribArray(2);
        _gl.VertexAttribPointer(2, 3, VertexAttribPointerType.Float, false, stride, (void*)(6 * sizeof(float)));
        _gl.EnableVertexAttribArray(3);
        _gl.VertexAttribPointer(3, 3, VertexAttribPointerType.Float, false, stride, (void*)(9 * sizeof(float)));
        _gl.EnableVertexAttribArray(4);
        _gl.VertexAttribPointer(4, 3, VertexAttribPointerType.Float, false, stride, (void*)(12 * sizeof(float)));
        _gl.EnableVertexAttribArray(5);
        _gl.VertexAttribPointer(5, 2, VertexAttribPointerType.Float, false, stride, (void*)(15 * sizeof(float)));

        _gl.BindVertexArray(0);
    }

    public unsafe void Draw()
    {
        _gl.BindVertexArray(_vao);
        _gl.DrawElements(PrimitiveType.Triangles, _indexCount, DrawElementsType.UnsignedInt, (void*)0);
    }

    /// <summary>Riscrive tutti i vertici (stessa quantità): per le mesh deformate dallo scheletro a ogni fotogramma.</summary>
    public unsafe void Update(ReadOnlySpan<float> vertices)
    {
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
        fixed (float* p = vertices)
            _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(vertices.Length * sizeof(float)), p);
        _gl.BindBuffer(BufferTargetARB.ArrayBuffer, 0);
    }

    /// <summary>
    /// Azzera un tratto di indici: quei triangoli degenerano e spariscono (per "staccare" un oggetto dalla mesh
    /// unica del pezzo e disegnarlo a parte quando viene spostato).
    /// </summary>
    public unsafe void ZeroIndices(int start, int count)
    {
        if (count <= 0) return;
        var zeros = new uint[count];
        // Nel profilo "core" il buffer degli indici fa parte del VAO: si lega il VAO della mesh stessa
        _gl.BindVertexArray(_vao);
        fixed (uint* p = zeros)
            _gl.BufferSubData(BufferTargetARB.ElementArrayBuffer, (nint)(start * sizeof(uint)), (nuint)(count * sizeof(uint)), p);
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        _gl.DeleteBuffer(_vbo);
        _gl.DeleteBuffer(_ebo);
        _gl.DeleteVertexArray(_vao);
    }
}
