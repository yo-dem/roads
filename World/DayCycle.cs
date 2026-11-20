using System.Numerics;

namespace Roads.World;

/// <summary>
/// Ciclo giorno/notte. Dall'ora del giorno ricava la posizione di sole e luna e tutto ciò che
/// colora la scena: luce principale (sole di giorno, luna di notte), luce ambiente, cielo, stelle.
/// Il sole sorge a est (+X) alle 6, è alto a mezzogiorno e tramonta a ovest alle 18; la luna gli è opposta
/// (sorge a est verso le 18 e tramonta a ovest verso le 6).
/// </summary>
public sealed class DayCycle
{
    /// <summary>Durata reale del giorno (6-18): 3 minuti; della notte (18-6): 5 minuti.</summary>
    public const float DaySeconds = 180f, NightSeconds = 300f;

    /// <summary>Ora del giorno, 0..24.</summary>
    public float Hours;

    public DayCycle(float startHours) => Hours = startHours;

    public void Update(float dt)
    {
        bool day = Hours >= 6f && Hours < 18f;
        Hours = (Hours + dt * 12f / (day ? DaySeconds : NightSeconds)) % 24f;
    }

    public string Clock => $"{(int)Hours:00}:{(int)(Hours % 1f * 60f):00}";

    // ------------------------------------------------------------------ astri

    /// <summary>Direzione verso il sole (unitaria).</summary>
    public Vector3 SunDir
    {
        get
        {
            float a = (Hours - 6f) / 24f * MathF.Tau; // 0 all'alba, π/2 a mezzogiorno
            return Vector3.Normalize(new Vector3(MathF.Cos(a), MathF.Sin(a), -0.35f));
        }
    }

    /// <summary>Direzione verso la luna: opposta al sole, ma su un arco esattamente est-ovest.</summary>
    public Vector3 MoonDir
    {
        get
        {
            Vector3 s = SunDir;
            return Vector3.Normalize(new Vector3(-s.X, -s.Y, 0f));
        }
    }

    /// <summary>Altezza del sole sull'orizzonte (-1..1).</summary>
    public float SunHeight => SunDir.Y;

    /// <summary>0 di notte, 1 di giorno, con un passaggio morbido attorno ad alba e tramonto.</summary>
    public float Daylight => Smooth(-0.12f, 0.25f, SunHeight);

    /// <summary>Quanto si è vicini ad alba o tramonto (cieli aranciati).</summary>
    public float Twilight => 1f - Smooth(0.02f, 0.4f, MathF.Abs(SunHeight + 0.05f));

    // ------------------------------------------------------------------ luce principale

    private float SunStrength => Smooth(-0.02f, 0.18f, SunHeight);
    private float MoonStrength => Smooth(-0.02f, 0.18f, -SunHeight);

    /// <summary>
    /// Direzione in cui viaggia la luce principale (verso il basso). Sole o luna, a seconda di chi
    /// è sopra l'orizzonte: lo scambio avviene quando entrambi sono deboli, quindi non si nota.
    /// L'inclinazione è limitata, così le ombre all'alba non diventano infinite.
    /// </summary>
    public Vector3 LightDir
    {
        get
        {
            Vector3 toLight = SunHeight >= 0f ? SunDir : MoonDir;
            toLight.Y = MathF.Max(toLight.Y, 0.35f);
            return -Vector3.Normalize(toLight);
        }
    }

    public Vector3 LightColor
    {
        get
        {
            var noon = new Vector3(1f, 0.93f, 0.80f) * 0.68f;
            var golden = new Vector3(1f, 0.62f, 0.38f) * 0.55f;
            var sun = Vector3.Lerp(noon, golden, Twilight);
            var moon = new Vector3(0.55f, 0.65f, 0.95f) * 0.22f;
            return sun * SunStrength + moon * MoonStrength;
        }
    }

    // ------------------------------------------------------------------ ambiente e cielo

    public Vector3 AmbientLow => Mix(new Vector3(0.035f, 0.045f, 0.085f), new Vector3(0.30f, 0.32f, 0.38f), new Vector3(0.32f, 0.24f, 0.26f));
    public Vector3 AmbientHigh => Mix(new Vector3(0.10f, 0.13f, 0.24f), new Vector3(0.62f, 0.70f, 0.76f), new Vector3(0.62f, 0.50f, 0.50f));

    public Vector3 SkyZenith => Mix(new Vector3(0.015f, 0.025f, 0.08f), new Vector3(0.30f, 0.58f, 0.92f), new Vector3(0.28f, 0.34f, 0.60f));
    public Vector3 SkyHorizon => Mix(new Vector3(0.05f, 0.07f, 0.15f), new Vector3(0.88f, 0.94f, 0.98f), new Vector3(0.98f, 0.60f, 0.40f));
    public Vector3 SkyBelow => Mix(new Vector3(0.03f, 0.04f, 0.10f), new Vector3(0.52f, 0.76f, 0.96f), new Vector3(0.45f, 0.40f, 0.55f));

    /// <summary>Visibilità delle stelle (solo a notte fonda).</summary>
    public float Stars => 1f - Smooth(-0.3f, 0.0f, SunHeight);

    /// <summary>Notte → giorno secondo la luce, poi una sfumatura calda attorno ad alba e tramonto.</summary>
    private Vector3 Mix(Vector3 night, Vector3 day, Vector3 dusk) =>
        Vector3.Lerp(Vector3.Lerp(night, day, Daylight), dusk, Twilight * 0.75f);

    private static float Smooth(float e0, float e1, float x)
    {
        float t = Math.Clamp((x - e0) / (e1 - e0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
