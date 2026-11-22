using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

/// <summary>Oggetti di scena che l'auto può spostare urtandoli.</summary>
public enum Push { None, Rock, Bush }

/// <summary>
/// Oggetti spostabili: rocce e cespugli. Finché nessuno li tocca restano nella mesh unica della scenografia
/// (non costano nulla); al primo urto vengono "staccati": i loro triangoli spariscono dalla mesh del pezzo e
/// l'oggetto si disegna a parte, nella sua nuova posizione. Gli alberi restano piantati.
/// La fisica è semplice: l'urto scambia quantità di moto secondo le masse (una roccia pesa in proporzione al
/// suo volume, un cespuglio poco), poi l'oggetto scivola (le rocce rotolano) frenato dall'attrito e si ferma
/// contro alberi, acqua, bordo del pezzo e altri oggetti.
/// </summary>
public sealed partial class Zone
{
    private sealed class Movable
    {
        public Push Kind;
        public Vector3 Anchor;          // punto di appoggio originale (coordinate locali)
        public float Radius, Mass, Friction, Restitution;
        public int IndexStart, IndexCount; // tratto della mesh della scenografia
        public Vector2 Offset, Velocity;
        public Quaternion Rotation = Quaternion.Identity;
        public float CenterY;           // altezza del centro sopra l'appoggio (perno della rotazione)
        public Mesh? Mesh;
        public bool Detached;
    }

    private readonly List<Movable> _movables = new();
    private readonly List<int>?[,] _movBuckets = new List<int>?[BucketCount, BucketCount];
    private readonly HashSet<int> _awake = new();
    private readonly List<int> _detached = new();
    private MeshData? _sceneryKeep;   // dati della scenografia, per staccare gli oggetti quando servono
    private GL? _gl;

    private void AddMovable(Push kind, Vector3 anchor, float radius, float size, int indexStart, int indexCount)
    {
        float volume = size * size * size;
        var m = new Movable
        {
            Kind = kind,
            Anchor = anchor,
            Radius = radius,
            Mass = kind == Push.Rock ? 3.5f * volume : 0.35f * volume,
            Friction = kind == Push.Rock ? 4.5f : 9f,
            Restitution = kind == Push.Rock ? 0.4f : 0.05f,  // una roccia grossa fa rimbalzare l'auto
            IndexStart = indexStart,
            IndexCount = indexCount,
        };
        _movables.Add(m);
        Bucket(_movables.Count - 1, add: true);
    }

    private Vector2 MovablePos(Movable m) => new(m.Anchor.X + m.Offset.X, m.Anchor.Z + m.Offset.Y);

    /// <summary>Registra (o toglie) l'oggetto nei secchielli che il suo raggio può toccare.</summary>
    private void Bucket(int i, bool add)
    {
        var m = _movables[i];
        Vector2 p = MovablePos(m);
        float reach = m.Radius + MaxQueryRadius;
        for (int a = BucketIndex(p.X - reach); a <= BucketIndex(p.X + reach); a++)
            for (int b = BucketIndex(p.Y - reach); b <= BucketIndex(p.Y + reach); b++)
            {
                if (add) (_movBuckets[a, b] ??= new()).Add(i);
                else _movBuckets[a, b]?.Remove(i);
            }
    }

    /// <summary>Un cerchio (coordinate locali) tocca un oggetto spostabile (escluso <paramref name="except"/>)?</summary>
    private bool HitsMovable(float x, float z, float radius, int except = -1)
    {
        var bucket = _movBuckets[BucketIndex(x), BucketIndex(z)];
        if (bucket == null) return false;
        foreach (int i in bucket)
        {
            if (i == except) continue;
            var m = _movables[i];
            Vector2 p = MovablePos(m);
            float dx = x - p.X, dz = z - p.Y, rr = m.Radius + radius;
            if (dx * dx + dz * dz < rr * rr) return true;
        }
        return false;
    }

    /// <summary>Un cerchio (coordinate locali) tocca un ostacolo fisso (albero, falò)?</summary>
    private bool HitsStatic(float x, float z, float radius)
    {
        if (HitsBox(x, z, radius)) return true; // muri degli edifici
        var bucket = _buckets[BucketIndex(x), BucketIndex(z)];
        if (bucket == null) return false;
        foreach (var c in bucket)
        {
            float dx = x - c.X, dz = z - c.Z;
            if (dx * dx + dz * dz < (c.R + radius) * (c.R + radius)) return true;
        }
        return false;
    }

    /// <summary>Prova a portare l'oggetto <paramref name="i"/> nella nuova posizione; false se lì non c'è posto.</summary>
    private bool TryMove(int i, Vector2 offset)
    {
        var m = _movables[i];
        float x = m.Anchor.X + offset.X, z = m.Anchor.Z + offset.Y;
        float lim = Half - m.Radius - 0.3f;                       // resta nel suo pezzo
        if (MathF.Abs(x) > lim || MathF.Abs(z) > lim) return false;
        if (HeightLocal(x, z) < WaterY + 0.05f) return false;      // niente oggetti in acqua
        foreach (var b in Bridges)
            if (b.TryGetHeight(Origin + new Vector3(x, 0f, z), -m.Radius, out _)) return false;
        if (HitsStatic(x, z, m.Radius) || HitsMovable(x, z, m.Radius, i)) return false;
        Bucket(i, add: false);
        m.Offset = offset;
        Bucket(i, add: true);
        Detach(i);
        return true;
    }

    /// <summary>
    /// L'auto (un cerchio di raggio <paramref name="radius"/> in <paramref name="center"/>, coordinate del mondo)
    /// urta gli oggetti spostabili: scambio di quantità di moto secondo le masse e oggetto spinto fuori
    /// dall'ingombro. <paramref name="velocity"/> (XZ) viene ridotta di conseguenza. Restituisce il punto
    /// d'urto più violento e la sua velocità d'impatto (0 se nessun urto).
    /// </summary>
    public float PushFrom(Vector3 center, float radius, ref Vector2 velocity, float mass, out Vector3 contact)
    {
        contact = default;
        float worst = 0f;
        float x = center.X - Origin.X, z = center.Z - Origin.Z;
        var bucket = _movBuckets[BucketIndex(x), BucketIndex(z)];
        if (bucket == null) return 0f;
        foreach (int i in bucket.ToArray())
        {
            var m = _movables[i];
            Vector2 p = MovablePos(m), d = p - new Vector2(x, z);
            float dist = d.Length(), pen = radius + m.Radius - dist;
            if (pen <= 0f) continue;
            // Direzione dell'urto: dal centro dell'auto a quello dell'oggetto (se coincidono, quella di marcia)
            Vector2 n = dist > 1e-4f ? d / dist
                      : velocity.LengthSquared() > 1e-6f ? Vector2.Normalize(velocity) : Vector2.UnitX;

            // Urto: si scambiano quantità di moto lungo la normale (l'oggetto può già allontanarsi)
            float vn = Vector2.Dot(velocity - m.Velocity, n);
            if (vn > 0f)
            {
                float j = (1f + m.Restitution) * vn / (1f / mass + 1f / m.Mass);
                m.Velocity += n * (j / m.Mass);
                velocity -= n * (j / mass);
                if (vn > worst)
                {
                    worst = vn;
                    contact = Origin + new Vector3(p.X - n.X * m.Radius, m.Anchor.Y + 0.2f, p.Y - n.Y * m.Radius);
                }
            }
            // E viene spinto fuori dall'ingombro dell'auto (se lì c'è posto, altrimenti fa da muro)
            TryMove(i, m.Offset + n * (pen + 0.01f));
            _awake.Add(i);
        }
        return worst;
    }

    /// <summary>Gli oggetti in movimento scivolano (le rocce rotolano) finché l'attrito non li ferma.</summary>
    public void UpdateMovables(float dt)
    {
        if (_awake.Count == 0) return;
        foreach (int i in _awake.ToArray())
        {
            var m = _movables[i];
            float speed = m.Velocity.Length();
            if (speed < 0.05f)
            {
                m.Velocity = Vector2.Zero;
                _awake.Remove(i);
                continue;
            }
            Vector2 step = m.Velocity * dt;
            if (!TryMove(i, m.Offset + step))
            {
                m.Velocity = Vector2.Zero; // contro un albero, l'acqua o un altro oggetto: si ferma
                _awake.Remove(i);
                continue;
            }
            float dist = step.Length();
            Vector3 dir = new(step.X / dist, 0f, step.Y / dist);
            Quaternion spin = m.Kind == Push.Rock
                ? Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(Vector3.UnitY, dir)), dist / MathF.Max(m.Radius, 0.2f))
                : Quaternion.CreateFromAxisAngle(Vector3.UnitY, dist * 0.6f);
            m.Rotation = Quaternion.Normalize(Quaternion.Concatenate(m.Rotation, spin));
            m.Velocity *= MathF.Max(0f, speed - m.Friction * dt) / speed;
        }
    }

    /// <summary>Stacca l'oggetto dalla mesh della scenografia per disegnarlo a parte (alla prima mossa).</summary>
    private void Detach(int i)
    {
        var m = _movables[i];
        if (m.Detached || _gl == null || _sceneryKeep == null) return;
        const int F = Rendering.Mesh.FloatsPerVertex;
        var src = _sceneryKeep.Vertices;
        var data = new float[m.IndexCount * F];
        Array.Copy(src, m.IndexStart * F, data, 0, data.Length); // con gli spigoli calcolati, vertice k = indice k
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int v = 0; v < m.IndexCount; v++)
        {
            int o = v * F;
            data[o] -= m.Anchor.X; data[o + 1] -= m.Anchor.Y; data[o + 2] -= m.Anchor.Z;   // posizione
            minY = MathF.Min(minY, data[o + 1]);
            maxY = MathF.Max(maxY, data[o + 1]);
        }
        if (m.IndexCount > 0) m.CenterY = (minY + maxY) * 0.5f; // perno della rotazione: centro dell'oggetto
        var indices = new uint[m.IndexCount];
        for (int k = 0; k < indices.Length; k++) indices[k] = (uint)k;
        m.Mesh = new Mesh(_gl, data, indices);
        Scenery.ZeroIndices(m.IndexStart, m.IndexCount);
        m.Detached = true;
        _detached.Add(i);
    }

    /// <summary>Aggiunge gli oggetti staccati, nella loro posizione e rotazione attuali.</summary>
    public void CollectMovables(List<DrawItem> items, Vector4 area)
    {
        foreach (int i in _detached)
        {
            var m = _movables[i];
            Vector2 p = MovablePos(m);
            float y = HeightLocal(p.X, p.Y) + PathLift(p.X, p.Y) - 0.05f;
            var pivot = new Vector3(0f, m.CenterY, 0f);
            Matrix4x4 model = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateFromQuaternion(m.Rotation)
                              * Matrix4x4.CreateTranslation(pivot) * Matrix4x4.CreateTranslation(Origin + new Vector3(p.X, y, p.Y));
            items.Add(new DrawItem(m.Mesh!, model, Area: area)); // rocce e cespugli non diventano trasparenti
        }
    }

    /// <summary>Oggetti spostati (per ricordarli quando il pezzo viene liberato e poi rigenerato).</summary>
    public List<(int Index, Vector2 Offset, Quaternion Rotation)> MovedState()
    {
        var list = new List<(int, Vector2, Quaternion)>();
        foreach (int i in _detached) list.Add((i, _movables[i].Offset, _movables[i].Rotation));
        return list;
    }

    /// <summary>Rimette gli oggetti dove erano stati spostati (dopo <see cref="Upload"/>).</summary>
    public void RestoreMoved(List<(int Index, Vector2 Offset, Quaternion Rotation)> moved)
    {
        foreach (var (i, offset, rotation) in moved)
        {
            if (i < 0 || i >= _movables.Count) continue;
            Bucket(i, add: false);
            _movables[i].Offset = offset;
            _movables[i].Rotation = rotation;
            Bucket(i, add: true);
            Detach(i);
        }
    }

    private void DisposeMovables()
    {
        foreach (var m in _movables) m.Mesh?.Dispose();
    }
}
