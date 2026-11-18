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
    /// Stridio: un fischio acuto che "sbanda" di intonazione (gomma che salta sull'asfalto) più un fruscio
    /// filtrato. Un secondo esatto: tutte le frequenze sono intere, quindi il loop non fa click.
    /// </summary>
    private static short[] SynthesizeSkid()
    {
        int length = SampleRate;
        var data = new short[length];
        var rng = new Random(7);
        float band = 0f, low = 0f;
        for (int i = 0; i < length; i++)
        {
            float t = i / (float)SampleRate;
            // Fischio principale con vibrato irregolare (due modulazioni lente) e un'armonica
            float wobble = 2.2f * MathF.Sin(MathF.Tau * 6f * t) + 1.3f * MathF.Sin(MathF.Tau * 13f * t + 1f);
            float squeal = 0.6f * MathF.Sin(MathF.Tau * 1180f * t + wobble)
                         + 0.25f * MathF.Sin(MathF.Tau * 2360f * t + wobble * 2f)
                         + 0.2f * MathF.Sin(MathF.Tau * 1530f * t + wobble * 1.3f);
            // Fruscio: rumore filtrato passa-banda (differenza di due passa-basso)
            float white = rng.NextSingle() * 2f - 1f;
            band += (white - band) * 0.35f;
            low += (white - low) * 0.06f;
            float hiss = band - low;
            // Intensità che pulsa un po', come una gomma che si aggrappa e scivola
            float grab = 0.75f + 0.25f * MathF.Sin(MathF.Tau * 9f * t) * MathF.Sin(MathF.Tau * 4f * t);
            // Raccordo morbido ai bordi del campione per il fruscio (il fischio è già periodico)
            float edge = MathF.Min(1f, MathF.Min(i, length - 1 - i) / 300f);
            float s = squeal * grab + hiss * 0.9f * edge;
            data[i] = (short)(Math.Clamp(s * 0.45f, -1f, 1f) * short.MaxValue);
        }
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
        _pitch += (targetPitch - _pitch) * (1f - MathF.Exp(-16f * dt));
        _gain += (targetGain - _gain) * (1f - MathF.Exp(-12f * dt));
        _al.SetSourceProperty(_source, SourceFloat.Pitch, _pitch);
        _al.SetSourceProperty(_source, SourceFloat.Gain, _gain);

        // Lo stridio entra deciso e sfuma un po' più lento; l'intonazione sale con l'intensità
        float targetSkid = 0.32f * skid;
        _skidGain += (targetSkid - _skidGain) * (1f - MathF.Exp((targetSkid > _skidGain ? -25f : -9f) * dt));
        _al.SetSourceProperty(_skidSource, SourceFloat.Gain, _skidGain);
        _al.SetSourceProperty(_skidSource, SourceFloat.Pitch, 0.85f + 0.25f * skid);
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
