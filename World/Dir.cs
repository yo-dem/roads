using System.Numerics;

namespace Roads.World;

/// <summary>Direzioni cardinali. Nord = -Z, Est = +X, Sud = +Z, Ovest = -X.</summary>
public enum Dir { North, East, South, West }

public static class DirExtensions
{
    public static readonly Dir[] All = [Dir.North, Dir.East, Dir.South, Dir.West];

    public static Vector3 Vec(this Dir d) => d switch
    {
        Dir.North => new Vector3(0, 0, -1),
        Dir.East => new Vector3(1, 0, 0),
        Dir.South => new Vector3(0, 0, 1),
        _ => new Vector3(-1, 0, 0),
    };

    public static (int Dx, int Dz) Offset(this Dir d) => d switch
    {
        Dir.North => (0, -1),
        Dir.East => (1, 0),
        Dir.South => (0, 1),
        _ => (-1, 0),
    };

    public static Dir Opposite(this Dir d) => (Dir)(((int)d + 2) % 4);
}
