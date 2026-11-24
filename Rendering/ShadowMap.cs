using Silk.NET.OpenGL;

namespace Roads.Rendering;

/// <summary>Framebuffer con sola texture di profondità, renderizzata dal punto di vista del sole.</summary>
public sealed unsafe class ShadowMap : IDisposable
{
    private readonly GL _gl;
    private readonly uint _fbo;
    public uint Texture { get; }
    public uint Size { get; }

    public ShadowMap(GL gl, uint size)
    {
        _gl = gl;
        Size = size;

        Texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, Texture);
        _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.DepthComponent24, size, size, 0,
            PixelFormat.DepthComponent, PixelType.Float, null);
        // Confronto di profondità fatto dalla scheda grafica, con interpolazione: ogni lettura dà già la media di 4
        // campioni (ombra morbida con meno letture)
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareMode, (int)TextureCompareMode.CompareRefToTexture);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureCompareFunc, (int)DepthFunction.Lequal);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToBorder);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToBorder);
        float* border = stackalloc float[] { 1f, 1f, 1f, 1f };
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureBorderColor, border);

        _fbo = _gl.GenFramebuffer();
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
            TextureTarget.Texture2D, Texture, 0);
        _gl.DrawBuffer(DrawBufferMode.None);
        _gl.ReadBuffer(ReadBufferMode.None);

        var status = _gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
        if (status != GLEnum.FramebufferComplete)
            throw new Exception($"Shadow framebuffer incompleto: {status}");

        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
    }

    /// <summary>Copia qui la profondità di un'altra mappa delle stesse dimensioni (la parte fissa in cache).</summary>
    public void CopyFrom(ShadowMap other)
    {
        _gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, other._fbo);
        _gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo);
        _gl.BlitFramebuffer(0, 0, (int)Size, (int)Size, 0, 0, (int)Size, (int)Size, ClearBufferMask.DepthBufferBit, BlitFramebufferFilter.Nearest);
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, Size, Size);
    }

    public void Bind()
    {
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo);
        _gl.Viewport(0, 0, Size, Size);
    }

    public void Dispose()
    {
        _gl.DeleteFramebuffer(_fbo);
        _gl.DeleteTexture(Texture);
    }
}
