using System.Numerics;

namespace Roads.World;

/// <summary>Una testa di semaforo: dove sta (mondo), verso dove guarda, a quale incrocio e gruppo di direzioni appartiene.</summary>
public readonly record struct TrafficHead(Vector3 World, float Yaw, RoadNode Node, int Group);

/// <summary>
/// I semafori di alcuni incroci di città. Quali incroci li hanno e come cambiano è deciso solo dalla rete stradale e
/// dall'ora del gioco: ogni pezzo di territorio arriva alle stesse conclusioni, senza stato condiviso.
/// Le direzioni dell'incrocio si dividono in due gruppi (le strade allineate con la prima e quelle di traverso) che
/// hanno il verde a turno: verde, giallo, poi un attimo tutto rosso prima che parta l'altro gruppo.
/// </summary>
public static class TrafficLights
{
    public const float Green = 8f, Yellow = 2f, AllRed = 1f;
    public const float Period = 2f * (Green + Yellow + AllRed);

    public enum Light { Red, Yellow, Green }

    private static int Seed(RoadNode n) => (int)MathF.Round(n.Pos.X * 3f) * 7919 + (int)MathF.Round(n.Pos.Y * 3f);

    /// <summary>
    /// L'incrocio ha il semaforo? Solo incroci veri dei centri abitati, tutti asfaltati, e non le piazze: circa tre su
    /// quattro di quelli a quattro (o più) vie e la metà di quelli a tre vie che toccano un viale o una statale.
    /// </summary>
    public static bool Has(RoadNode n)
    {
        if (!n.Junction || n.Piazza || n.Destination || n.Degree < 3) return false;
        if (!n.Edges.All(e => e.Paved) || Terrain.Urban(n.Pos.X, n.Pos.Y) < Terrain.WalkFrom) return false;
        if (Railway.Current.Distance(n.Pos.X, n.Pos.Y) < 14f) return false; // accanto ai binari c'è già il passaggio a livello
        float h = Noise.Hash01(Seed(n), n.Degree, 811);
        if (n.Degree >= 4) return h < 0.75f;
        bool important = n.Edges.Any(e => e.Layout == RoadLayout.Avenue || e.Kind == RoadKind.Main);
        return important && h < 0.5f;
    }

    /// <summary>Gruppo di direzioni del tratto all'incrocio: 0 se allineato col primo tratto, 1 se di traverso.</summary>
    public static int Group(RoadNode n, RoadEdge e)
    {
        Vector2 reference = n.Edges[0].LeaveDir(n);
        return MathF.Abs(Vector2.Dot(e.LeaveDir(n), reference)) > 0.7f ? 0 : 1;
    }

    /// <summary>Che luce mostra all'istante <paramref name="time"/> (secondi di gioco) il semaforo di quel gruppo.</summary>
    public static Light State(RoadNode n, int group, float time)
    {
        float offset = Noise.Hash01(Seed(n), 3, 812) * Period;
        float t = ((time + offset) % Period + Period) % Period;
        if (group == 1) t = (t + Period * 0.5f) % Period; // l'altro gruppo è sfasato di mezzo ciclo
        if (t < Green) return Light.Green;
        if (t < Green + Yellow) return Light.Yellow;
        return Light.Red;
    }
}
