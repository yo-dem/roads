using Silk.NET.OpenAL;

namespace Roads.Audio;

/// <summary>
/// Motorino da auto giocattolo sintetizzato al volo: un breve campione "putt-putt" in loop, di cui
/// si varia l'intonazione con la velocità e il volume con l'acceleratore. Se l'audio non è
/// disponibile resta silenzioso senza errori.
/// </summary>
public sealed unsafe class EngineSound : IDisposable
{
    private const int SampleRate = 22050;
    private const float BaseHz = 42f;    // scoppi al minimo
    private const int Cycles = 21;       // il campione contiene un numero intero di cicli: loop senza click

    private readonly ALContext? _alc;
    private readonly AL? _al;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly uint _source, _buffer;
    private readonly uint _skidSource, _skidBuffer;
    private float _pitch = 0.8f, _gain, _skidGain;

    // Stridio "vivo": raffiche e pause casuali (la gomma che si aggrappa e molla) e un timbro diverso a ogni derapata
    private readonly Random _rng = new();
    private bool _skidActive;
    private float _skidPitchBase = 1f, _pitchWander, _wanderTarget, _wanderTimer;
    private float _chatter = 1f, _chatterTarget = 1f, _chatterTimer;

    public bool Enabled { get; }

    public EngineSound()
    {
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice(null);
            if (_device == null) return;
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);

            _buffer = _al.GenBuffer();
            short[] samples = Synthesize();
            fixed (short* p = samples)
                _al.BufferData(_buffer, BufferFormat.Mono16, p, samples.Length * sizeof(short), SampleRate);

            _source = _al.GenSource();
            _al.SetSourceProperty(_source, SourceInteger.Buffer, (int)_buffer);
            _al.SetSourceProperty(_source, SourceBoolean.Looping, true);
            _al.SetSourceProperty(_source, SourceFloat.Gain, 0f);
            _al.SourcePlay(_source);

            // Stridio delle gomme: un secondo suono in loop, a volume zero finché l'auto non derapa
            _skidBuffer = _al.GenBuffer();
            short[] skid = SynthesizeSkid();
            fixed (short* p = skid)
                _al.BufferData(_skidBuffer, BufferFormat.Mono16, p, skid.Length * sizeof(short), SampleRate);
            _skidSource = _al.GenSource();
            _al.SetSourceProperty(_skidSource, SourceInteger.Buffer, (int)_skidBuffer);
            _al.SetSourceProperty(_skidSource, SourceBoolean.Looping, true);
            _al.SetSourceProperty(_skidSource, SourceFloat.Gain, 0f);
            _al.SourcePlay(_skidSource);
            Enabled = _al.GetError() == AudioError.NoError;
        }
        catch (Exception e)
        {
            Console.WriteLine($"Audio non disponibile: {e.Message}");
        }
    }

    private static short[] Synthesize()
    {
        int length = (int)(SampleRate * Cycles / BaseHz);
        var data = new short[length];
        float hz = Cycles * (float)SampleRate / length; // esattamente Cycles cicli nel campione
        var rng = new Random(3);
        var noise = new float[SampleRate / (int)BaseHz + 1];
        for (int i = 0; i < noise.Length; i++) noise[i] = rng.NextSingle() * 2f - 1f;

        for (int i = 0; i < length; i++)
        {
            float t = i / (float)SampleRate;
            float cycle = t * hz;
            float phase = cycle - MathF.Floor(cycle);              // 0..1 dentro ogni scoppio
            float pulse = MathF.Exp(-phase * 5f);                  // ogni scoppio decade
            float tone = 0.55f * MathF.Sin(MathF.Tau * hz * t)
                       + 0.30f * MathF.Sin(MathF.Tau * hz * 2f * t + 0.5f)
                       + 0.15f * MathF.Sin(MathF.Tau * hz * 3f * t + 1.1f);
            float rattle = noise[(int)(phase * (noise.Length - 1))] * MathF.Exp(-phase * 9f);
            float s = tone * (0.45f + 0.55f * pulse) + rattle * 0.25f;
            data[i] = (short)(Math.Clamp(s * 0.7f, -1f, 1f) * short.MaxValue);
        }
        return data;
    }

    /// <summary>
    /// Stridio delle gomme: tre secondi di "stick-slip", cioè la gomma che si aggrappa e scivola a scatti.
    /// È una sequenza di brevi strisciate (25-140 ms) separate da micro-pause; ognuna ha una sua nota
    /// (700-1500 Hz) e un suo volume ed è fatta di rumore filtrato attorno a quella nota, non di toni puri:
    /// suona ruvida come gomma sull'asfalto e non come un fischio. Il campione finisce in una pausa,
    /// quindi il loop non fa click.
    /// </summary>
    private static short[] SynthesizeSkid()
    {
        int length = SampleRate * 3;
        var mix = new float[length];
        var rng = new Random(7);
        int i = 0;
        while (i < length - SampleRate / 10)
        {
            int burst = (int)(SampleRate * (0.025f + 0.115f * rng.NextSingle()));
            burst = Math.Min(burst, length - SampleRate / 20 - i);
            float f0 = 700f + 800f * rng.NextSingle();
            float glide = 1f + (rng.NextSingle() * 2f - 1f) * 0.08f; // la nota scivola un po' durante la strisciata
            float amp = 0.55f + 0.45f * rng.NextSingle();
            float q = 6f + 14f * rng.NextSingle();                   // più alto = più "cantato", più basso = più ruvido

            // Filtro passa-banda risonante (biquad) applicato a rumore bianco, con la frequenza che cambia
            float x1 = 0, x2 = 0, y1 = 0, y2 = 0;
            for (int k = 0; k < burst; k++)
            {
                float u = k / (float)burst;
                float f = f0 * (1f + (glide - 1f) * u);
                float w = MathF.Tau * f / SampleRate, alpha = MathF.Sin(w) / (2f * q);
                float a0 = 1f + alpha;
                float b0 = alpha / a0, b2 = -alpha / a0, a1 = -2f * MathF.Cos(w) / a0, a2 = (1f - alpha) / a0;
                float x = rng.NextSingle() * 2f - 1f;
                float y = b0 * x + b2 * x2 - a1 * y1 - a2 * y2;
                x2 = x1; x1 = x; y2 = y1; y1 = y;
                // Attacco e rilascio rapidi; dentro la strisciata il volume tremola (micro scatti)
                float env = MathF.Min(1f, k / (SampleRate * 0.003f)) * MathF.Min(1f, (burst - k) / (SampleRate * 0.008f));
                float flutter = 0.7f + 0.3f * MathF.Sin(MathF.Tau * (35f + 30f * u) * k / SampleRate);
                float grit = (rng.NextSingle() * 2f - 1f) * 0.12f; // fruscio di fondo della gomma
                mix[i + k] += (y * 3.2f + grit) * env * flutter * amp;
            }
            i += burst;
            // Micro-pausa (la gomma fa presa) oppure attacco subito della strisciata successiva
            if (rng.NextSingle() < 0.55f) i += (int)(SampleRate * (0.008f + 0.05f * rng.NextSingle()));
        }
        // Normalizza: il picco a 0.9, così il volume finale dipende solo da quello impostato in gioco
        float peak = 1e-6f;
        foreach (float v in mix) peak = MathF.Max(peak, MathF.Abs(v));
        var data = new short[length];
        for (int k = 0; k < length; k++)
            data[k] = (short)(Math.Clamp(mix[k] * 0.9f / peak, -1f, 1f) * short.MaxValue);
        return data;
    }

    /// <param name="rpm">Giri del motore normalizzati (minimo ≈ 0.2, limite = 1).</param>
    /// <param name="throttle">Acceleratore (0..1): cala di colpo durante le cambiate.</param>
    /// <param name="running">Falso a motore spento (quando si scende dall'auto): il suono sfuma a zero.</param>
    /// <param name="skid">Stridio delle gomme (0..1), in derapata.</param>
    public void Update(float dt, float rpm, float throttle, bool running = true, float skid = 0f)
    {
        if (!Enabled || _al == null) return;
        // L'intonazione segue i giri: sale dentro la marcia e crolla a ogni cambiata
        float targetPitch = 0.55f + 1.45f * rpm;
        float targetGain = running ? 0.08f + 0.15f * throttle + 0.05f * rpm : 0f;
        // Segue i giri quasi all'istante: il suono corrisponde sempre alla velocità vera (in un urto crolla subito)
        _pitch += (targetPitch - _pitch) * (1f - MathF.Exp(-45f * dt));
        _gain += (targetGain - _gain) * (1f - MathF.Exp(-30f * dt));
        _al.SetSourceProperty(_source, SourceFloat.Pitch, _pitch);
        _al.SetSourceProperty(_source, SourceFloat.Gain, _gain);

        UpdateSkid(dt, skid);
    }

    private void UpdateSkid(float dt, float skid)
    {
        if (_al == null) return;

        // Inizio di una nuova derapata: timbro e punto di partenza nel campione scelti a caso,
        // così due derapate non suonano mai identiche
        if (!_skidActive && skid > 0.05f)
        {
            _skidActive = true;
            _skidPitchBase = 0.86f + 0.28f * _rng.NextSingle();
            _al.SetSourceProperty(_skidSource, SourceFloat.SecOffset, 2.9f * _rng.NextSingle());
            _chatterTimer = 0f;
        }
        else if (_skidActive && skid < 0.02f && _skidGain < 0.005f) _skidActive = false;

        // Presa e non presa: segmenti brevi (30-130 ms) a volume pieno alternati a "prese" quasi mute.
        // Con poca derapata le pause sono frequenti (stridio spezzato), di traverso pieno quasi continuo.
        _chatterTimer -= dt;
        if (_chatterTimer <= 0f)
        {
            float gapChance = 0.35f - 0.25f * skid;
            bool gap = _rng.NextSingle() < gapChance;
            _chatterTarget = gap ? 0.1f * _rng.NextSingle() : 0.65f + 0.35f * _rng.NextSingle();
            _chatterTimer = gap ? 0.025f + 0.06f * _rng.NextSingle() : 0.04f + 0.09f * _rng.NextSingle();
        }
        _chatter += (_chatterTarget - _chatter) * (1f - MathF.Exp(-90f * dt));

        // Il tono vaga lentamente attorno a quello della derapata
        _wanderTimer -= dt;
        if (_wanderTimer <= 0f)
        {
            _wanderTarget = (_rng.NextSingle() * 2f - 1f) * 0.07f;
            _wanderTimer = 0.1f + 0.25f * _rng.NextSingle();
        }
        _pitchWander += (_wanderTarget - _pitchWander) * (1f - MathF.Exp(-8f * dt));

        // Volume legato all'intensità (che dipende dalla velocità): entra deciso e si spegne quasi subito.
        // Il tono invece NON segue l'intensità (altrimenti, mentre la derapata si chiude, sembra un fischio
        // che scende): dipende solo dal timbro scelto per questa derapata e da piccole variazioni casuali.
        float targetSkid = 0.4f * skid;
        _skidGain += (targetSkid - _skidGain) * (1f - MathF.Exp((targetSkid > _skidGain ? -30f : -25f) * dt));
        _al.SetSourceProperty(_skidSource, SourceFloat.Gain, _skidGain * _chatter);
        float pitch = _skidPitchBase * (1f + _pitchWander);
        _al.SetSourceProperty(_skidSource, SourceFloat.Pitch, pitch);
    }

    public void Dispose()
    {
        if (_al == null || _alc == null) return;
        if (_source != 0)
        {
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
        }
        if (_skidSource != 0)
        {
            _al.SourceStop(_skidSource);
            _al.DeleteSource(_skidSource);
        }
        if (_buffer != 0) _al.DeleteBuffer(_buffer);
        if (_skidBuffer != 0) _al.DeleteBuffer(_skidBuffer);
        if (_context != null)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
        }
        if (_device != null) _alc.CloseDevice(_device);
    }
}
