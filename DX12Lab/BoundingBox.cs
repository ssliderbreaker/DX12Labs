using System.Numerics;

namespace DX12Lab;

public struct BoundingBox
{
    public Vector3 Min;
    public Vector3 Max;

    public BoundingBox(Vector3 min, Vector3 max)
    {
        Min = min;
        Max = max;
    }

    public Vector3 Center => (Min + Max) * 0.5f;

    public static BoundingBox FromCenterExtents(Vector3 center, Vector3 extents)
        => new(center - extents, center + extents);

    public BoundingBox Union(BoundingBox other)
        => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));

    public bool Contains(BoundingBox other)
        => other.Min.X >= Min.X && other.Min.Y >= Min.Y && other.Min.Z >= Min.Z &&
           other.Max.X <= Max.X && other.Max.Y <= Max.Y && other.Max.Z <= Max.Z;
}