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
    private float _pitch = 0.8f, _gain;

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

    /// <param name="speed01">Velocità rispetto al massimo (0..1).</param>
    /// <param name="throttle">Acceleratore (0..1).</param>
    public void Update(float dt, float speed01, float throttle)
    {
        if (!Enabled || _al == null) return;
        float targetPitch = 0.75f + 1.1f * speed01 + 0.15f * throttle;
        float targetGain = 0.10f + 0.12f * throttle + 0.06f * speed01;
        float k = 1f - MathF.Exp(-8f * dt);
        _pitch += (targetPitch - _pitch) * k;
        _gain += (targetGain - _gain) * k;
        _al.SetSourceProperty(_source, SourceFloat.Pitch, _pitch);
        _al.SetSourceProperty(_source, SourceFloat.Gain, _gain);
    }

    public void Dispose()
    {
        if (_al == null || _alc == null) return;
        if (_source != 0)
        {
            _al.SourceStop(_source);
            _al.DeleteSource(_source);
        }
        if (_buffer != 0) _al.DeleteBuffer(_buffer);
        if (_context != null)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
        }
        if (_device != null) _alc.CloseDevice(_device);
    }
}
