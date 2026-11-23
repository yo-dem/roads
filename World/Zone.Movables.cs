using System.Numerics;
using Roads.Rendering;
using Silk.NET.OpenGL;

namespace Roads.World;

/// <summary>Oggetti di scena che l'auto può spostare urtandoli (i pali li abbatte).</summary>
public enum Push { None, Rock, Bush, Pole }

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
        // Pali (lampioni, cartelli, semafori): si abbattono e basta; la lampada accesa sta nella mesh luminosa
        public int GlowStart, GlowCount;
        public Vector3? Lamp;           // luce del lampione (coordinate del mondo), da spegnere
        public bool Broken;
    }

    /// <summary>Un palo abbattuto che vola via ruotando e poi si sbriciola (sparisce).</summary>
    private sealed class Flying
    {
        public required Mesh Mesh;
        public Vector3 Pos, Vel, Axis;
        public Quaternion Rotation = Quaternion.Identity;
        public float Spin, Age, CenterY;
    }

    private readonly List<Flying> _flying = new();
    private const float FlyTime = 1.3f; // poi del palo restano solo le schegge

    /// <summary>Basi dei pali ancora in piedi (coordinate del mondo): per i test.</summary>
    public IEnumerable<Vector3> PoleBases() =>
        _movables.Where(m => m.Kind == Push.Pole && !m.Broken).Select(m => Origin + m.Anchor);

    /// <summary>Sotto questa velocità d'urto (unità/s) il palo regge: l'auto si ferma contro.</summary>
    private const float PoleBreakSpeed = 2f;

    /// <summary>
    /// Registra un palo abbattibile: base in <paramref name="anchor"/> (locale), triangoli della scenografia e della
    /// mesh luminosa (lampada) che gli appartengono, ed eventuale luce del lampione da spegnere quando cade.
    /// </summary>
    private void AddPole(Vector3 anchor, float radius, int start, int count, int glowStart = 0, int glowCount = 0,
                         Vector3? lamp = null)
    {
        _movables.Add(new Movable
        {
            Kind = Push.Pole, Anchor = anchor, Radius = radius, Mass = 1f,
            IndexStart = start, IndexCount = count, GlowStart = glowStart, GlowCount = glowCount, Lamp = lamp,
        });
        Bucket(_movables.Count - 1, add: true);
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

    /// <summary>
    /// Quota della cima della roccia o del cespuglio che sta sopra il punto (coordinate del mondo), o null se non ce n'è.
    /// Serve alla camera bassa a piedi: questi oggetti non diventano trasparenti, la visuale ci passa sopra.
    /// </summary>
    public float? ObstacleTop(Vector3 world)
    {
        float x = world.X - Origin.X, z = world.Z - Origin.Z;
        if (MathF.Abs(x) >= Half || MathF.Abs(z) >= Half) return null;
        var bucket = _movBuckets[BucketIndex(x), BucketIndex(z)];
        if (bucket == null) return null;
        float? top = null;
        foreach (int i in bucket)
        {
            var m = _movables[i];
            if (m.Kind == Push.Pole) continue; // i pali sono sottili: non coprono la visuale
            Vector2 p = MovablePos(m);
            float dx = x - p.X, dz = z - p.Y, r = m.Radius + 0.15f;
            if (dx * dx + dz * dz >= r * r) continue;
            // Un cespuglio è alto circa 1.75 volte il suo ingombro, una roccia un po' meno
            float h = HeightLocal(p.X, p.Y) + PathLift(p.X, p.Y) + m.Radius * 1.8f;
            top = MathF.Max(top ?? h, h);
        }
        return top;
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
    public float PushFrom(Vector3 center, float radius, ref Vector2 velocity, float mass, out Vector3 contact,
                          List<(Vector3 At, Vector2 Dir, float Speed)>? broken = null)
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

            if (m.Kind == Push.Pole)
            {
                float into = Vector2.Dot(velocity, n);
                if (velocity.Length() >= PoleBreakSpeed && into > 0f)
                {
                    // Il palo cede: l'auto quasi non se ne accorge, il palo vola via in frantumi
                    Break(i, velocity);
                    broken?.Add((Origin + new Vector3(p.X, m.Anchor.Y + 1.0f, p.Y), velocity, velocity.Length()));
                    velocity *= 0.97f;
                }
                else if (into > 0f) velocity -= n * into; // piano piano il palo regge: fa da muro
                continue;
            }

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
        UpdateFlying(dt);
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

    /// <summary>
    /// Abbatte il palo <paramref name="i"/>: sparisce dalla scenografia (e la sua lampada dalla mesh luminosa), non fa
    /// più da ostacolo, si spengono la luce del lampione e le lenti del semaforo, e il palo vola via nella direzione
    /// dell'urto ruotando. Non si ricorda: quando il pezzo viene rigenerato il palo è di nuovo al suo posto.
    /// </summary>
    private void Break(int i, Vector2 carVelocity)
    {
        var m = _movables[i];
        if (m.Broken) return;
        m.Broken = true;
        Bucket(i, add: false);
        if (m.Lamp is { } lamp) LampLights.Remove(lamp);
        Vector3 baseWorld = Origin + m.Anchor;
        TrafficHeads.RemoveAll(h => Vector3.DistanceSquared(h.World, baseWorld) < 0.01f);
        if (m.GlowCount > 0) Glow?.ZeroIndices(m.GlowStart, m.GlowCount);
        if (DetachedMesh(m, out var mesh, out float centerY))
        {
            Scenery.ZeroIndices(m.IndexStart, m.IndexCount);
            float speed = carVelocity.Length();
            Vector2 dir = speed > 1e-3f ? carVelocity / speed : Vector2.UnitX;
            var rng = new Random(i * 7919 + (int)(speed * 100f));
            Vector2 side = new Vector2(-dir.Y, dir.X) * (rng.NextSingle() - 0.5f) * 0.6f;
            _flying.Add(new Flying
            {
                Mesh = mesh, CenterY = centerY,
                Pos = Origin + m.Anchor,
                Vel = new Vector3(dir.X + side.X, 0f, dir.Y + side.Y) * speed * 0.85f + new Vector3(0f, 2.2f + speed * 0.12f, 0f),
                Axis = Vector3.Normalize(new Vector3(dir.Y, 0f, -dir.X)), // cade in avanti, nel verso dell'auto
                Spin = -(3f + speed * 0.35f),
            });
        }
    }

    private void UpdateFlying(float dt)
    {
        for (int k = _flying.Count - 1; k >= 0; k--)
        {
            var f = _flying[k];
            f.Age += dt;
            if (f.Age >= FlyTime)
            {
                f.Mesh.Dispose();
                _flying.RemoveAt(k);
                continue;
            }
            f.Vel.Y -= 12f * dt;
            f.Pos += f.Vel * dt;
            float ground = HeightLocal(f.Pos.X - Origin.X, f.Pos.Z - Origin.Z) + PathRaise;
            if (f.Pos.Y < ground)
            {
                // Tocca terra: rimbalza appena e striscia
                f.Pos.Y = ground;
                f.Vel = new Vector3(f.Vel.X * 0.55f, MathF.Abs(f.Vel.Y) * 0.25f, f.Vel.Z * 0.55f);
                f.Spin *= 0.6f;
            }
            f.Rotation = Quaternion.Normalize(Quaternion.Concatenate(f.Rotation, Quaternion.CreateFromAxisAngle(f.Axis, f.Spin * dt)));
        }
    }

    /// <summary>Copia i triangoli dell'oggetto in una mesh sua, con l'origine nel punto d'appoggio.</summary>
    private bool DetachedMesh(Movable m, out Mesh mesh, out float centerY)
    {
        mesh = null!;
        centerY = 0f;
        if (_gl == null || _sceneryKeep == null || m.IndexCount <= 0) return false;
        const int F = Rendering.Mesh.FloatsPerVertex;
        var data = new float[m.IndexCount * F];
        Array.Copy(_sceneryKeep.Vertices, m.IndexStart * F, data, 0, data.Length); // con gli spigoli, vertice k = indice k
        float minY = float.MaxValue, maxY = float.MinValue;
        for (int v = 0; v < m.IndexCount; v++)
        {
            int o = v * F;
            data[o] -= m.Anchor.X; data[o + 1] -= m.Anchor.Y; data[o + 2] -= m.Anchor.Z;
            minY = MathF.Min(minY, data[o + 1]);
            maxY = MathF.Max(maxY, data[o + 1]);
        }
        centerY = (minY + maxY) * 0.5f;
        var indices = new uint[m.IndexCount];
        for (int k = 0; k < indices.Length; k++) indices[k] = (uint)k;
        mesh = new Mesh(_gl, data, indices);
        return true;
    }

    /// <summary>Stacca l'oggetto dalla mesh della scenografia per disegnarlo a parte (alla prima mossa).</summary>
    private void Detach(int i)
    {
        var m = _movables[i];
        if (m.Detached || !DetachedMesh(m, out var mesh, out float centerY)) return;
        m.Mesh = mesh;
        m.CenterY = centerY; // perno della rotazione: centro dell'oggetto
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
        // Pali abbattuti in volo: ruotano attorno al loro centro e verso la fine si rimpiccioliscono fino a sparire
        foreach (var f in _flying)
        {
            float scale = 1f - Noise.Smooth(FlyTime * 0.55f, FlyTime, f.Age);
            var pivot = new Vector3(0f, f.CenterY, 0f);
            Matrix4x4 model = Matrix4x4.CreateTranslation(-pivot) * Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(f.Rotation)
                              * Matrix4x4.CreateTranslation(pivot) * Matrix4x4.CreateTranslation(f.Pos);
            items.Add(new DrawItem(f.Mesh, model));
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
        foreach (var f in _flying) f.Mesh.Dispose();
    }
}
