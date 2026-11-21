using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>Particelle semplici: sbuffi di polvere ai passi e scintille del falò.</summary>
public sealed class Particles : IDisposable
{
    private struct P
    {
        public Vector3 Pos, Vel;
        public float Age, Life, Size, Spin;
        public bool Ember, Cloud;
    }

    private readonly List<P> _list = new();
    private readonly Random _rng = new();
    private readonly Mesh _dust, _ember, _cloud;

    public Particles(GL gl)
    {
        var mb = new MeshBuilder();
        mb.AddIcosphere(Vector3.Zero, Vector3.One * 0.5f, 0, new Vector3(0.92f, 0.88f, 0.78f));
        _dust = mb.Build(gl);
        mb = new MeshBuilder();
        mb.AddIcosphere(Vector3.Zero, Vector3.One * 0.5f, 0, new Vector3(0.93f, 0.85f, 0.68f));
        _cloud = mb.Build(gl);
        mb = new MeshBuilder();
        mb.AddBox(Vector3.Zero, Vector3.One, new Vector3(1f, 0.6f, 0.15f));
        _ember = mb.Build(gl);
    }

    private float R(float a, float b) => a + _rng.NextSingle() * (b - a);

    public void Dust(Vector3 pos, int count = 3)
    {
        for (int i = 0; i < count; i++)
            _list.Add(new P
            {
                Pos = pos + new Vector3(R(-0.1f, 0.1f), 0.03f, R(-0.1f, 0.1f)),
                Vel = new Vector3(R(-0.5f, 0.5f), R(0.4f, 0.9f), R(-0.5f, 0.5f)),
                Life = R(0.35f, 0.55f),
                Size = R(0.12f, 0.2f),
                Spin = R(-3f, 3f),
            });
    }

    /// <summary>
    /// Nuvola di polvere sollevata da una gomma sullo sterrato: color terra, più grande e lenta degli sbuffi, resta
    /// sospesa dietro l'auto. <paramref name="back"/> è la direzione verso cui la gomma la lancia (all'indietro).
    /// </summary>
    public void RoadDust(Vector3 pos, Vector3 back, float strength)
    {
        _list.Add(new P
        {
            Pos = pos + new Vector3(R(-0.15f, 0.15f), 0.05f, R(-0.15f, 0.15f)),
            Vel = back * R(0.4f, 1.2f) * strength + new Vector3(R(-0.5f, 0.5f), R(0.35f, 0.9f), R(-0.5f, 0.5f)),
            Life = R(0.6f, 1.1f) * (0.6f + 0.4f * strength),
            Size = R(0.14f, 0.26f) * (0.7f + 0.5f * strength),
            Spin = R(-1.5f, 1.5f),
            Cloud = true,
        });
    }

    public void Ember(Vector3 pos) =>
        _list.Add(new P
        {
            Pos = pos + new Vector3(R(-0.15f, 0.15f), 0.3f, R(-0.15f, 0.15f)),
            Vel = new Vector3(R(-0.2f, 0.2f), R(0.9f, 1.6f), R(-0.2f, 0.2f)),
            Life = R(0.8f, 1.4f),
            Size = R(0.05f, 0.08f),
            Spin = R(-6f, 6f),
            Ember = true,
        });

    public void Update(float dt)
    {
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var p = _list[i];
            p.Age += dt;
            if (p.Age >= p.Life) { _list.RemoveAt(i); continue; }
            p.Pos += p.Vel * dt;
            p.Vel *= MathF.Exp((p.Cloud ? -1.6f : -2.5f) * dt);
            if (p.Ember) p.Pos.X += MathF.Sin(p.Age * 7f + p.Spin) * 0.3f * dt;
            _list[i] = p;
        }
    }

    public void Collect(List<DrawItem> items)
    {
        foreach (var p in _list)
        {
            float t = p.Age / p.Life;
            float s = p.Size * (p.Ember ? 1f - t
                : p.Cloud ? MathF.Sqrt(MathF.Min(1f, t * 4f)) * (1f - t * t) * (1f + t)  // si gonfia e poi svanisce
                : MathF.Sin(MathF.PI * MathF.Min(1f, t * 1.3f + 0.15f)));
            if (s <= 0.001f) continue;
            var m = Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationY(p.Age * p.Spin) * Matrix4x4.CreateTranslation(p.Pos);
            items.Add(new DrawItem(p.Ember ? _ember : p.Cloud ? _cloud : _dust, m, p.Ember ? 1f : p.Cloud ? 0.15f : 0.3f, CastShadow: false));
        }
    }

    public void Dispose()
    {
        _dust.Dispose();
        _ember.Dispose();
        _cloud.Dispose();
    }
}
