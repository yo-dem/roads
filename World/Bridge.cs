using System.Numerics;
using Roads.Rendering;

namespace Roads.World;

/// <summary>Ponte di legno massiccio, largo quanto la strada e appena ad arco. Coordinate mondo.</summary>
public sealed class Bridge
{
    public Vector3 Center;
    public Vector3 Dir;       // direzione di attraversamento (orizzontale, normalizzata)
    public float Span;        // lunghezza
    public float HalfWidth = Zone.PathHalf; // largo quanto la carreggiata
    public float BaseY = 0.04f;
    public float Arch = 0.3f;

    private Vector3 Perp => new(-Dir.Z, 0, Dir.X);

    public float DeckY(float along) =>
        BaseY + Arch * MathF.Cos(Math.Clamp(along / (Span * 0.5f), -1f, 1f) * MathF.PI * 0.5f);

    /// <summary>Se il punto è sull'impalcato restituisce l'altezza del piano di calpestio.</summary>
    public bool TryGetHeight(Vector3 p, float margin, out float height)
    {
        Vector3 d = p - Center;
        float along = d.X * Dir.X + d.Z * Dir.Z;
        float across = d.X * Perp.X + d.Z * Perp.Z;
        if (MathF.Abs(along) > Span * 0.5f || MathF.Abs(across) > HalfWidth - margin)
        {
            height = 0f;
            return false;
        }
        height = DeckY(along);
        return true;
    }

    /// <summary>
    /// Costruisce la geometria centrata in <paramref name="localCenter"/> (coordinate della mesh): assi spesse
    /// sopra travi portanti, pile di tronchi con controventi piantate nel fiume, parapetti pieni con montanti
    /// robusti e grossi pali alle testate.
    /// </summary>
    public void Build(MeshBuilder mb, Vector3 localCenter)
    {
        var plank = new Vector3(0.55f, 0.36f, 0.20f);
        var plankAlt = new Vector3(0.50f, 0.32f, 0.17f);
        var beam = new Vector3(0.38f, 0.23f, 0.12f);
        var post = new Vector3(0.44f, 0.27f, 0.14f);
        var rail = new Vector3(0.86f, 0.68f, 0.44f); // passamano in legno chiaro: staccano dal resto del ponte
        var cap = new Vector3(0.33f, 0.20f, 0.10f);

        Matrix4x4 old = mb.Transform;
        float yaw = MathF.Atan2(-Dir.Z, Dir.X); // l'asse X locale diventa Dir
        mb.Transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(localCenter) * old;

        float half = Span * 0.5f;
        float w = HalfWidth;
        float side = w + 0.1f; // asse dei parapetti, appena fuori dalla carreggiata

        // Impalcato: assi spesse di traverso, alternate di tono, appoggiate su tre travi longitudinali
        int planks = Math.Max(4, (int)(Span / 0.32f));
        float step = Span / planks;
        for (int k = 0; k < planks; k++)
        {
            float s = -half + (k + 0.5f) * step;
            float y = DeckY(s);
            mb.AddBox(new Vector3(s, y - 0.06f, 0), new Vector3(step - 0.03f, 0.12f, w * 2f + 0.4f), k % 2 == 0 ? plank : plankAlt);
            foreach (float z in new[] { -w + 0.15f, 0f, w - 0.15f })
                mb.AddBox(new Vector3(s, y - 0.24f, z), new Vector3(step + 0.02f, 0.24f, 0.22f), beam);
            // Fasce laterali (travi di bordo) ben visibili dal fianco
            foreach (float z in new[] { -(w + 0.22f), w + 0.22f })
                mb.AddBox(new Vector3(s, y - 0.14f, z), new Vector3(step + 0.02f, 0.3f, 0.12f), beam);
        }

        // Pile: coppie di tronchi verticali piantati nel letto del fiume, con traverso e croce di controvento
        int piers = Span > 6f ? 2 : 1;
        for (int i = 0; i < piers; i++)
        {
            float s = piers == 1 ? 0f : (i == 0 ? -1f : 1f) * Span * 0.2f;
            float top = DeckY(s) - 0.36f, bottom = -1.4f, h = top - bottom;
            foreach (float z in new[] { -w + 0.1f, w - 0.1f })
                mb.AddPrism(new Vector3(s, bottom, z), 0.16f, 0.16f, h, 8, post);
            mb.AddBox(new Vector3(s, top - 0.1f, 0f), new Vector3(0.3f, 0.2f, w * 2f + 0.5f), beam);
            // Controventi incrociati (due travi inclinate tra i tronchi)
            float braceY = (top + bottom) * 0.5f + 0.2f, len = MathF.Sqrt(4f * w * w + 1.2f * 1.2f);
            float ang = MathF.Atan2(1.2f, 2f * w);
            foreach (float sign in new[] { -1f, 1f })
            {
                Matrix4x4 keep = mb.Transform;
                mb.Transform = Matrix4x4.CreateRotationX(sign * ang) * Matrix4x4.CreateTranslation(s, braceY, 0f) * keep;
                mb.AddBox(Vector3.Zero, new Vector3(0.12f, 0.12f, len), cap);
                mb.Transform = keep;
            }
        }

        // Parapetti: montanti squadrati ogni metro circa, due correnti pieni e un cappello sopra
        int bays = Math.Max(2, (int)MathF.Round(Span / 1.1f));
        float bay = Span / bays;
        foreach (float z in new[] { -side, side })
        {
            for (int k = 0; k <= bays; k++)
            {
                float s = -half + k * bay;
                bool end = k == 0 || k == bays;
                float y = DeckY(s);
                float size = end ? 0.32f : 0.17f;
                float height = end ? 1.15f : 0.85f;
                mb.AddBox(new Vector3(s, y + height * 0.5f - 0.05f, z), new Vector3(size, height, size), end ? beam : post);
                if (end) mb.AddBox(new Vector3(s, y + height + 0.0f, z), new Vector3(size + 0.08f, 0.1f, size + 0.08f), cap);
            }
            for (int k = 0; k < planks; k++)
            {
                float s = -half + (k + 0.5f) * step;
                float y = DeckY(s);
                mb.AddBox(new Vector3(s, y + 0.38f, z), new Vector3(step + 0.02f, 0.1f, 0.1f), rail);
                mb.AddBox(new Vector3(s, y + 0.8f, z), new Vector3(step + 0.02f, 0.14f, 0.16f), rail);
            }
        }

        mb.Transform = old;
    }
}
