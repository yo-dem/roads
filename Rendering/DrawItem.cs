using System.Numerics;

namespace Roads.Rendering;

/// <summary>
/// Una mesh da disegnare in questo frame con la sua trasformazione.
/// <paramref name="Area"/> = ingombro sul piano (minX, minZ, maxX, maxZ) per scartare ciò che è fuori vista;
/// tutto a zero = sempre disegnato. <paramref name="Far"/> = nebbia "lunga" che non copre mai del tutto (oggi non usata).
/// <paramref name="Static"/> = non si muove mai (terreno, case, alberi): la sua ombra resta in cache.
/// </summary>
public readonly record struct DrawItem(Mesh Mesh, Matrix4x4 Model, float Emissive = 0f, bool CastShadow = true,
                                       bool SeeThrough = false, Vector4 Area = default, bool Smooth = false, bool Far = false,
                                       bool Static = false)
{
    public bool HasArea => Area.Z > Area.X;
}
