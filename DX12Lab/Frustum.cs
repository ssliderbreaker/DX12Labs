using System;
using System.Numerics;

namespace DX12Lab;

public struct Plane
{
    public Vector3 Normal;
    public float D;

    public Plane(float a, float b, float c, float d)
    {
        var n = new Vector3(a, b, c);
        float len = n.Length();
        Normal = n / len;
        D = d / len;
    }
}

public class Frustum
{
    public readonly Plane[] Planes = new Plane[6];

    public static Frustum FromViewProjection(Matrix4x4 m)
    {
        var f = new Frustum();

        f.Planes[0] = new Plane(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41);
        f.Planes[1] = new Plane(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41);
        f.Planes[2] = new Plane(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42);
        f.Planes[3] = new Plane(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42);
        f.Planes[4] = new Plane(m.M13, m.M23, m.M33, m.M43);
        f.Planes[5] = new Plane(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43);

        return f;
    }

    public bool Intersects(BoundingBox box)
    {
        foreach (var plane in Planes)
        {
            var p = box.Min;
            if (plane.Normal.X >= 0) p.X = box.Max.X;
            if (plane.Normal.Y >= 0) p.Y = box.Max.Y;
            if (plane.Normal.Z >= 0) p.Z = box.Max.Z;

            if (Vector3.Dot(plane.Normal, p) + plane.D < 0)
                return false;
        }
        return true;
    }
}