using System.Numerics;
using Roads.Rendering;
using Roads.World;
using Silk.NET.OpenGL;

namespace Roads.Characters;

/// <summary>
/// Personaggino che scende dall'auto e va a piedi. L'aspetto è un modello .glb con licenza libera (Kenney, KayKit...),
/// con le loro animazioni (fermo, camminata, corsa, salto); si cambia con il tasto C.
/// Alto circa 0.78, in proporzione all'automobilina (alta 0.95 e lunga 1.9). Il modello guarda verso +Z locale.
/// </summary>
public sealed class Villager : IDisposable
{
    private const float RunSpeed = 3.0f;    // andatura normale
    private const float SlowSpeed = 1.5f;   // con Shift
    public const float Radius = 0.2f;
    private const float HopTime = 0.32f;    // durata del saltello dentro/fuori dall'auto

    public Vector3 Position;
    public float Yaw;

    /// <summary>Nascosto: è seduto in auto.</summary>
    public bool Hidden { get; private set; } = true;
    /// <summary>Sta saltando dentro o fuori dall'auto: in quel momento non si comanda.</summary>
    public bool Busy => _hop < 1f;

    private Vector3 _velocity;
    private float _time, _move;

    // Saltello: da _hopFrom a _hopTo, crescendo (uscita) o rimpicciolendo (entrata)
    private float _hop = 1f;
    private Vector3 _hopFrom, _hopTo;
    private bool _hopOut;

    // ------------------------------------------------------------------ modello

    /// <summary>Cartella dei personaggi (CC0: "Mini Characters" di Kenney, pacchetti KayKit...), accanto all'eseguibile.</summary>
    private static readonly string AssetDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Characters");

    /// <summary>
    /// Personaggi disponibili: nome → file. I Kenney si chiamano come il file senza "character-" (es. "female-a"),
    /// quelli nelle sottocartelle con la cartella davanti (es. "kaykit/rogue").
    /// </summary>
    private static readonly Dictionary<string, string> Files = FindCharacters();

    private static Dictionary<string, string> FindCharacters()
    {
        var files = new Dictionary<string, string>();
        if (!Directory.Exists(AssetDir)) return files;
        foreach (var f in Directory.GetFiles(AssetDir, "*.glb", SearchOption.AllDirectories))
        {
            string rel = System.IO.Path.GetRelativePath(AssetDir, f).Replace('\\', '/');
            string name = System.IO.Path.ChangeExtension(rel, null).ToLowerInvariant();
            if (!rel.Contains('/'))
            {
                if (!name.StartsWith("character-")) continue; // nella cartella principale solo i personaggi Kenney
                name = name[10..];
            }
            files[name] = f;
        }
        return files;
    }

    /// <summary>Nomi dei personaggi disponibili, in ordine alfabetico.</summary>
    public static IReadOnlyList<string> Characters { get; } = Files.Keys.Order().ToArray();

    /// <summary>Altezza a cui si riporta ogni modello, qualunque sia la sua scala (l'auto è alta 0.95).</summary>
    private const float TargetHeight = 0.78f;

    public const string DefaultCharacter = "female-e";

    private readonly GL _gl;
    private GlbModel _model = null!;
    private GlbModel.Pose _pose = null!, _layer = null!;
    private int _rootNode = -1;
    private float _scale = 1f;
    private GlbModel.Clip? _idle, _walk, _sprint, _jump;
    public string Character { get; private set; } = "";

    // Tempo di ciascuna animazione e pesi delle andature (per mescolarle senza scatti)
    private float _walkTime, _walkWeight, _sprintWeight;

    public Villager(GL gl, Vector3 position, float yaw, string character = DefaultCharacter)
    {
        _gl = gl;
        Position = position;
        Yaw = yaw;
        SetCharacter(character);
    }

    /// <summary>Cambia l'aspetto del personaggio (uno di <see cref="Characters"/>).</summary>
    public void SetCharacter(string name)
    {
        if (!Characters.Contains(name)) name = Characters.Count > 0 ? Characters[0] : name;
        var model = new GlbModel(_gl, Files[name]);
        _model?.Dispose();
        _model = model;
        _pose = model.NewPose();
        _layer = model.NewPose();
        _rootNode = model.NodeIndex("root");
        _scale = model.Height > 0.01f ? TargetHeight / model.Height : 1f;
        // I pacchetti chiamano le animazioni in modi diversi (Kenney: "walk", KayKit: "Walking_A"...)
        _idle = model.Find("idle", "Idle", "Unarmed_Idle");
        _walk = model.Find("walk", "Walking_A", "Walking_B");
        _sprint = model.Find("sprint", "Running_A", "Running_B");
        _jump = model.Find("jump", "Jump_Full_Short", "Jump_Idle");
        Character = name;
    }

    /// <summary>Passa al personaggio successivo (o precedente) della lista.</summary>
    public void CycleCharacter(int step = 1)
    {
        if (Characters.Count == 0) return;
        int i = Math.Max(0, Characters.ToList().IndexOf(Character));
        SetCharacter(Characters[((i + step) % Characters.Count + Characters.Count) % Characters.Count]);
    }

    // ------------------------------------------------------------------ entrare e uscire dall'auto

    /// <summary>Esce dall'auto: saltello da <paramref name="from"/> (dentro l'abitacolo) a <paramref name="to"/>.</summary>
    public void HopOut(Vector3 from, Vector3 to, float yaw)
    {
        Hidden = false;
        _hopOut = true;
        _hop = 0f;
        _hopFrom = from;
        _hopTo = to;
        Position = from;
        Yaw = yaw;
        _velocity = Vector3.Zero;
    }

    /// <summary>Rientra in auto: saltello verso <paramref name="door"/> rimpicciolendo, poi sparisce.</summary>
    public void HopIn(Vector3 door)
    {
        _hopOut = false;
        _hop = 0f;
        _hopFrom = Position;
        _hopTo = door;
        _velocity = Vector3.Zero;
        Vector3 d = Flat(door - Position);
        if (d.LengthSquared() > 1e-4f) Yaw = MathF.Atan2(d.X, d.Z);
    }

    /// <summary>
    /// A bordo del treno: posizione e direzione (mondo) le decide chi lo porta, senza collisioni col mondo; qui si
    /// tiene solo l'animazione, camminata o ferma secondo <paramref name="speed"/>.
    /// </summary>
    public void Ride(float dt, Vector3 pos, float yaw, float speed)
    {
        Hidden = false;
        _hop = 1f;
        _time += dt;
        Position = pos;
        Yaw = yaw;
        _velocity = Vector3.Zero;
        _move = Noise.Lerp(_move, speed, Noise.Damp(10f, dt));
        _walkWeight = Noise.Lerp(_walkWeight, Math.Clamp(speed / 0.6f, 0f, 1f), Noise.Damp(12f, dt));
        _sprintWeight = Noise.Lerp(_sprintWeight, 0f, Noise.Damp(8f, dt));
        _walkTime += dt * MathF.Min(MathF.Max(_move, 0.5f) * 0.95f, 2.1f);
    }

    /// <summary>Non si disegna (seduto in treno: la visuale è dai suoi occhi).</summary>
    public void Hide() => Hidden = true;

    /// <summary>Arriva oltre 1 e torna indietro: il "pop" quando salta fuori.</summary>
    private static float EaseOutBack(float t)
    {
        const float c1 = 1.70158f, c3 = c1 + 1f;
        float u = t - 1f;
        return 1f + c3 * u * u * u + c1 * u * u;
    }

    private float HopScale => _hop >= 1f ? 1f
        : _hopOut ? 0.35f + 0.65f * EaseOutBack(_hop)
        : 1f - 0.75f * _hop * _hop;

    // ------------------------------------------------------------------ movimento

    /// <param name="input">Direzione da tastiera nel mondo (XZ); lunghezza 0 = nessun input.</param>
    /// <param name="slow">Shift premuto: passo lento.</param>
    /// <param name="blocked">Ostacoli in più rispetto al mondo (l'auto parcheggiata).</param>
    /// <param name="face">Se dato, il personaggio guarda in questa direzione (yaw) qualunque sia il movimento: guida
    /// "da mini auto", dove le frecce laterali lo girano e quella indietro lo fa arretrare senza voltarsi.</param>
    public void Update(float dt, Vector3 input, bool slow, WorldMap world, Particles fx, Func<Vector3, float, bool>? blocked = null,
                       float? face = null)
    {
        _time += dt;

        if (_hop < 1f)
        {
            _hop = MathF.Min(1f, _hop + dt / HopTime);
            float arc = MathF.Sin(MathF.PI * _hop) * 0.28f; // balzo
            Position = Vector3.Lerp(_hopFrom, _hopTo, _hop) + new Vector3(0, arc, 0);
            if (_hop >= 1f)
            {
                if (_hopOut) fx.Dust(Position, 3);
                else Hidden = true;
            }
            _move = Noise.Lerp(_move, 0f, Noise.Damp(10f, dt));
            return;
        }
        if (Hidden) return;

        bool Free(Vector3 p) => world.IsWalkable(p, Radius) && !(blocked?.Invoke(p, Radius) ?? false);

        float maxSpeed = slow ? SlowSpeed : RunSpeed;
        bool moving = input.LengthSquared() > 0.01f;

        Vector3 dir = moving ? Vector3.Normalize(input) : Vector3.Zero;
        _velocity = Vector3.Lerp(_velocity, dir * (moving ? maxSpeed : 0f), Noise.Damp(14f, dt));
        if (face is { } f) Yaw += Noise.WrapAngle(f - Yaw) * MathF.Min(1f, 20f * dt);
        else if (moving) Yaw += Noise.WrapAngle(MathF.Atan2(dir.X, dir.Z) - Yaw) * MathF.Min(1f, 16f * dt);

        // Movimento con "scivolamento" lungo gli ostacoli
        Vector3 step = new(_velocity.X * dt, 0, _velocity.Z * dt);
        if (step.LengthSquared() > 0f)
        {
            Vector3 p = Position;
            if (Free(p + step)) p += step;
            else
            {
                var sx = new Vector3(step.X, 0, 0);
                var sz = new Vector3(0, 0, step.Z);
                if (Free(p + sx)) p += sx; else _velocity.X = 0;
                if (Free(p + sz)) p += sz; else _velocity.Z = 0;
            }
            Position = p;
        }
        Position.Y = Noise.Lerp(Position.Y, world.GroundHeight(Position), Noise.Damp(25f, dt));

        // Animazione: si passa con dolcezza da fermo a camminata a corsa, al ritmo della velocità
        float speed = new Vector2(_velocity.X, _velocity.Z).Length();
        _move = Noise.Lerp(_move, speed, Noise.Damp(10f, dt));
        _walkWeight = Noise.Lerp(_walkWeight, Math.Clamp(speed / 0.6f, 0f, 1f), Noise.Damp(12f, dt));
        _sprintWeight = Noise.Lerp(_sprintWeight, Math.Clamp((speed - 1.7f) / 1.0f, 0f, 1f), Noise.Damp(8f, dt));
        float before01 = _walkTime % 1f;
        _walkTime += dt * MathF.Min(MathF.Max(_move, 0.5f) * 0.95f, 2.1f); // cicli al secondo (due passi per ciclo)
        float now01 = _walkTime % 1f;

        // Sbuffi di polvere a ogni passo quando corre
        if (_sprintWeight > 0.5f && (now01 < before01 || (before01 < 0.5f && now01 >= 0.5f)))
            fx.Dust(Vector3.Transform(new Vector3(now01 < 0.5f ? -0.05f : 0.05f, 0, 0), RootMatrix), 1);
    }

    private static Vector3 Flat(Vector3 v) => new(v.X, 0, v.Z);

    public Vector3 Velocity => _velocity;

    private Matrix4x4 RootMatrix =>
        Matrix4x4.CreateScale(HopScale * _scale) * Matrix4x4.CreateRotationY(Yaw) * Matrix4x4.CreateTranslation(Position);

    public void Collect(List<DrawItem> items)
    {
        if (Hidden) return;

        _model.Sample(_idle, _time, _pose);
        if (_walkWeight > 0.01f)
        {
            var walk = _walk;
            _model.Sample(walk, _walkTime * (walk?.Duration ?? 1f), _layer);
            GlbModel.Blend(_pose, _layer, _walkWeight);
        }
        if (_sprintWeight > 0.01f)
        {
            var sprint = _sprint;
            _model.Sample(sprint, _walkTime * (sprint?.Duration ?? 1f), _layer);
            GlbModel.Blend(_pose, _layer, _sprintWeight);
        }
        // La corsa originale solleva il corpo di 0.2 a ogni passo (un quarto dell'altezza): troppo saltellante
        _pose.ScaleHeight(_rootNode, Noise.Lerp(1f, 0.3f, _sprintWeight), _model.RestTranslation(_rootNode).Y);
        if (_hop < 1f && _jump is { } jump)
        {
            // Durante il saltello dentro/fuori dall'auto: posa del salto
            _model.Sample(jump, MathF.Min(_hop, 0.99f) * jump.Duration, _layer);
            GlbModel.Blend(_pose, _layer, MathF.Sin(MathF.PI * _hop));
        }
        _model.Collect(items, _pose, RootMatrix);
    }

    public void Dispose() => _model.Dispose();
}
