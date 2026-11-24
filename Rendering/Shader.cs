using System.Numerics;
using Silk.NET.OpenGL;

namespace Roads.Rendering;

/// <summary>Programma GLSL (vertex + fragment) con helper per gli uniform.</summary>
public sealed class Shader : IDisposable
{
    private readonly GL _gl;
    private readonly uint _handle;
    private readonly Dictionary<string, int> _locations = new();

    public Shader(GL gl, string vertexSource, string fragmentSource)
    {
        _gl = gl;
        uint vs = Compile(ShaderType.VertexShader, vertexSource);
        uint fs = Compile(ShaderType.FragmentShader, fragmentSource);

        _handle = _gl.CreateProgram();
        _gl.AttachShader(_handle, vs);
        _gl.AttachShader(_handle, fs);
        _gl.LinkProgram(_handle);
        _gl.GetProgram(_handle, ProgramPropertyARB.LinkStatus, out int status);
        if (status == 0)
            throw new Exception($"Errore link shader: {_gl.GetProgramInfoLog(_handle)}");

        _gl.DetachShader(_handle, vs);
        _gl.DetachShader(_handle, fs);
        _gl.DeleteShader(vs);
        _gl.DeleteShader(fs);
    }

    private uint Compile(ShaderType type, string source)
    {
        uint shader = _gl.CreateShader(type);
        _gl.ShaderSource(shader, source);
        _gl.CompileShader(shader);
        _gl.GetShader(shader, ShaderParameterName.CompileStatus, out int status);
        if (status == 0)
            throw new Exception($"Errore compilazione {type}: {_gl.GetShaderInfoLog(shader)}");
        return shader;
    }

    public void Use() => _gl.UseProgram(_handle);

    private int Location(string name)
    {
        if (!_locations.TryGetValue(name, out int loc))
        {
            loc = _gl.GetUniformLocation(_handle, name);
            _locations[name] = loc;
        }
        return loc;
    }

    public unsafe void SetMatrix(string name, Matrix4x4 m)
    {
        // System.Numerics (row-major, vettori riga) ha in memoria lo stesso layout
        // di GLSL (column-major, vettori colonna): niente transpose.
        _gl.UniformMatrix4(Location(name), 1, false, (float*)&m);
    }

    /// <summary>mat3 da nove valori, colonna per colonna.</summary>
    public void SetMatrix3(string name, ReadOnlySpan<float> m) => _gl.UniformMatrix3(Location(name), 1, false, m);

    public void SetVector2(string name, Vector2 v) => _gl.Uniform2(Location(name), v.X, v.Y);
    public void SetVector3(string name, Vector3 v) => _gl.Uniform3(Location(name), v.X, v.Y, v.Z);
    /// <summary>Array di vec3 (es. "uLampPos[0]"): <paramref name="xyz"/> contiene le terne una dopo l'altra.</summary>
    public void SetVector3Array(string name, ReadOnlySpan<float> xyz) => _gl.Uniform3(Location(name), (uint)(xyz.Length / 3), xyz);
    // Gli scalari si reimpostano solo se cambiano: tra un oggetto e l'altro quasi sempre sono uguali
    private readonly Dictionary<int, float> _floats = new();
    private readonly Dictionary<int, int> _ints = new();

    public void SetFloat(string name, float v)
    {
        int loc = Location(name);
        if (_floats.TryGetValue(loc, out float old) && old == v) return;
        _floats[loc] = v;
        _gl.Uniform1(loc, v);
    }

    public void SetInt(string name, int v)
    {
        int loc = Location(name);
        if (_ints.TryGetValue(loc, out int old) && old == v) return;
        _ints[loc] = v;
        _gl.Uniform1(loc, v);
    }

    public void Dispose() => _gl.DeleteProgram(_handle);
}
