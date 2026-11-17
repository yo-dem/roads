using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

/// <summary>
/// Nuvolette low poly che galleggiano sotto le isole e scorrono lentamente col vento.
/// Le posizioni si ripetono su una piastrella attorno alla camera, quindi il cielo è infinito.
/// </summary>
public sealed class Clouds : IDisposable
{
    private const float Tile = 200f;
    private static readonly Vector3 Wind = new(0.9f, 0f, 0.35f);

    private readonly Mesh[] _meshes;
    private readonly (Vector3 Pos, float Scale, float Yaw, int Mesh, float Speed)[] _clouds;

    public Clouds(GL gl, int seed = 11)
    {
        var rng = new Random(seed);
        var white = new Vector3(0.97f, 0.98f, 1.0f);

        _meshes = new Mesh[6];
        for (int m = 0; m < _meshes.Length; m++)
        {
            var mb = new MeshBuilder { ColorRng = rng, ColorJitter = 0.025f };
            int puffs = 3 + rng.Next(4);
            for (int p = 0; p < puffs; p++)
            {
                float offset = p - (puffs - 1) * 0.5f;
                float r = (0.85f + rng.NextSingle() * 0.5f) * (1f - MathF.Abs(offset) / puffs * 0.6f);
                var center = new Vector3(offset * 1.05f + (rng.NextSingle() - 0.5f) * 0.3f, r * 0.2f,
                                         (rng.NextSingle() - 0.5f) * 0.9f);
                mb.AddIcosphere(center, new Vector3(r, r * 0.78f, r * 0.9f), 1, white, rng, 0.06f);
            }
            _meshes[m] = mb.Build(gl);
        }

        _clouds = new (Vector3, float, float, int, float)[48];
        for (int i = 0; i < _clouds.Length; i++)
        {
            var pos = new Vector3(rng.NextSingle() * Tile, -11f - rng.NextSingle() * 16f, rng.NextSingle() * Tile);
            _clouds[i] = (pos, 2.2f + rng.NextSingle() * 2.4f, rng.NextSingle() * MathF.Tau,
                          rng.Next(_meshes.Length), 0.6f + rng.NextSingle() * 0.8f);
        }
    }

    private static float Wrap(float v) => v - MathF.Floor(v / Tile + 0.5f) * Tile;

    public void Collect(List<DrawItem> items, Vector3 center, float time)
    {
        foreach (var c in _clouds)
        {
            Vector3 p = c.Pos + Wind * (time * c.Speed);
            float x = center.X + Wrap(p.X - center.X);
            float z = center.Z + Wrap(p.Z - center.Z);
            items.Add(new DrawItem(_meshes[c.Mesh],
                Matrix4x4.CreateScale(c.Scale) * Matrix4x4.CreateRotationY(c.Yaw) * Matrix4x4.CreateTranslation(x, p.Y, z),
                0.35f, CastShadow: false));
        }
    }

    public void Dispose()
    {
        foreach (var m in _meshes) m.Dispose();
    }
}
