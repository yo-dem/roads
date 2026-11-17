using System.Numerics;
using Roads.Rendering;

namespace Roads.World;

/// <summary>Ponte di legno e corda, leggermente ad arco. Coordinate mondo.</summary>
public sealed class Bridge
{
    public Vector3 Center;
    public Vector3 Dir;       // direzione di attraversamento (orizzontale, normalizzata)
    public float Span;        // lunghezza
    public float HalfWidth = 0.9f;
    public float BaseY = 0.04f;
    public float Arch = 0.35f;

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

    /// <summary>Costruisce la geometria centrata in <paramref name="localCenter"/> (coordinate della mesh).</summary>
    public void Build(MeshBuilder mb, Vector3 localCenter)
    {
        var wood = new Vector3(0.58f, 0.36f, 0.19f);
        var woodDark = new Vector3(0.40f, 0.24f, 0.12f);
        var rope = new Vector3(0.86f, 0.76f, 0.56f);

        Matrix4x4 old = mb.Transform;
        float yaw = MathF.Atan2(-Dir.Z, Dir.X); // l'asse X locale diventa Dir
        mb.Transform = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateTranslation(localCenter) * old;

        float half = Span * 0.5f;
        int planks = Math.Max(3, (int)(Span / 0.3f));
        float step = Span / planks;
        float side = HalfWidth + 0.05f;

        for (int k = 0; k < planks; k++)
        {
            float s = -half + (k + 0.5f) * step;
            float y = DeckY(s);
            mb.AddBox(new Vector3(s, y - 0.04f, 0), new Vector3(step - 0.05f, 0.08f, HalfWidth * 2f + 0.15f), wood);
            foreach (float z in new[] { -HalfWidth, HalfWidth })
                mb.AddBox(new Vector3(s, y - 0.13f, z), new Vector3(step + 0.01f, 0.1f, 0.12f), woodDark);

            // Corrimano di corda e montanti sottili
            foreach (float z in new[] { -side, side })
            {
                mb.AddBox(new Vector3(s, y + 0.85f, z), new Vector3(step + 0.02f, 0.045f, 0.045f), rope);
                if (k % 2 == 0)
                    mb.AddBox(new Vector3(s, y + 0.43f, z), new Vector3(0.03f, 0.84f, 0.03f), rope);
            }
        }

        var posts = new List<float> { -half + 0.08f, half - 0.08f };
        if (Span > 5f) posts.Add(0f);
        foreach (float s in posts)
        {
            float y = DeckY(s);
            foreach (float z in new[] { -side, side })
                mb.AddBox(new Vector3(s, y + 0.4f, z), new Vector3(0.15f, 1.1f, 0.15f), woodDark);
        }

        mb.Transform = old;
    }
}
