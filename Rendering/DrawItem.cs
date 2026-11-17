using System.Numerics;

namespace Roads.Rendering;

/// <summary>Una mesh da disegnare in questo frame con la sua trasformazione.</summary>
public readonly record struct DrawItem(Mesh Mesh, Matrix4x4 Model, float Emissive = 0f, bool CastShadow = true);
