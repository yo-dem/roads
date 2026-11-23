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
        public bool Ember, Cloud, Shard;
        public float Floor; // le schegge cadono fin qui e rimbalzano
    }

    private readonly List<P> _list = new();
    private readonly Random _rng = new();
    private readonly Mesh _dust, _ember, _cloud, _shard;

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
        mb = new MeshBuilder();
        mb.AddBox(Vector3.Zero, new Vector3(1f, 0.6f, 1.4f), new Vector3(0.3f, 0.31f, 0.34f));
        _shard = mb.Build(gl);
    }

    /// <summary>
    /// Schegge di un palo abbattuto: pezzetti scuri che schizzano nella direzione dell'urto (<paramref name="dir"/>,
    /// la velocità dell'auto), ricadono a terra rimbalzando, più un po' di polvere.
    /// </summary>
    public void Shards(Vector3 at, Vector2 dir, float speed)
    {
        Vector2 d = speed > 1e-3f ? dir / speed : Vector2.UnitX;
        float floor = at.Y - 1.0f;
        for (int i = 0; i < 14; i++)
        {
            float push = speed * R(0.35f, 0.9f);
            _list.Add(new P
            {
                Pos = at + new Vector3(R(-0.15f, 0.15f), R(-0.8f, 0.9f), R(-0.15f, 0.15f)),
                Vel = new Vector3(d.X * push + R(-1.6f, 1.6f), R(1.2f, 3.6f), d.Y * push + R(-1.6f, 1.6f)),
                Life = R(0.9f, 1.6f),
                Size = R(0.06f, 0.14f),
                Spin = R(-9f, 9f),
                Shard = true,
                Floor = floor,
            });
        }
        Dust(at with { Y = floor + 0.1f }, 5);
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
            if (p.Shard)
            {
                p.Vel.Y -= 11f * dt; // le schegge cadono
                if (p.Pos.Y < p.Floor)
                {
                    p.Pos.Y = p.Floor;
                    p.Vel = new Vector3(p.Vel.X * 0.5f, MathF.Abs(p.Vel.Y) * 0.3f, p.Vel.Z * 0.5f);
                    p.Spin *= 0.5f;
                }
            }
            else p.Vel *= MathF.Exp((p.Cloud ? -1.6f : -2.5f) * dt);
            if (p.Ember) p.Pos.X += MathF.Sin(p.Age * 7f + p.Spin) * 0.3f * dt;
            _list[i] = p;
        }
    }

    public void Collect(List<DrawItem> items)
    {
        foreach (var p in _list)
        {
            float t = p.Age / p.Life;
            float s = p.Size * (p.Shard ? 1f - Roads.World.Noise.Smooth(0.7f, 1f, t)
                : p.Ember ? 1f - t
                : p.Cloud ? MathF.Sqrt(MathF.Min(1f, t * 4f)) * (1f - t * t) * (1f + t)  // si gonfia e poi svanisce
                : MathF.Sin(MathF.PI * MathF.Min(1f, t * 1.3f + 0.15f)));
            if (s <= 0.001f) continue;
            var m = Matrix4x4.CreateScale(s) * Matrix4x4.CreateRotationY(p.Age * p.Spin)
                    * (p.Shard ? Matrix4x4.CreateRotationX(p.Age * p.Spin * 0.7f) : Matrix4x4.Identity) * Matrix4x4.CreateTranslation(p.Pos);
            items.Add(new DrawItem(p.Shard ? _shard : p.Ember ? _ember : p.Cloud ? _cloud : _dust, m,
                                   p.Shard ? 0f : p.Ember ? 1f : p.Cloud ? 0.15f : 0.3f, CastShadow: false));
        }
    }

    public void Dispose()
    {
        _dust.Dispose();
        _ember.Dispose();
        _cloud.Dispose();
        _shard.Dispose();
    }
}
