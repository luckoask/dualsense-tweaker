using System.Numerics;

namespace DualSenseTweaker.Controls.Model;

/// <summary>
/// Signed distance functions (negative inside) for modeling the controller in millimeters.
/// Formulas after Inigo Quilez (iquilezles.org/articles/distfunctions).
/// </summary>
internal static class Sdf
{
    public static float Sphere(Vector3 p, float r) => p.Length() - r;

    /// <summary>Box with half extents <paramref name="b"/> and edges rounded by <paramref name="r"/>.</summary>
    public static float RoundBox(Vector3 p, Vector3 b, float r)
    {
        var q = Vector3.Abs(p) - b + new Vector3(r);
        return Vector3.Max(q, Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0) - r;
    }

    /// <summary>Ellipsoid with radii <paramref name="r"/> (bound, accurate near the surface).</summary>
    public static float Ellipsoid(Vector3 p, Vector3 r)
    {
        float k0 = (p / r).Length();
        float k1 = (p / (r * r)).Length();
        return k1 == 0 ? -MathF.Min(r.X, MathF.Min(r.Y, r.Z)) : k0 * (k0 - 1) / k1;
    }

    /// <summary>Capsule from <paramref name="a"/> (radius <paramref name="ra"/>) to <paramref name="b"/> (radius <paramref name="rb"/>).</summary>
    public static float RoundCone(Vector3 p, Vector3 a, Vector3 b, float ra, float rb)
    {
        var ba = b - a;
        float l2 = Vector3.Dot(ba, ba);
        float rr = ra - rb;
        float a2 = l2 - rr * rr;
        float il2 = 1f / l2;
        var pa = p - a;
        float y = Vector3.Dot(pa, ba);
        float z = y - l2;
        var w = pa * l2 - ba * y;
        float x2 = Vector3.Dot(w, w);
        float y2 = y * y * l2;
        float z2 = z * z * l2;
        float k = MathF.Sign(rr) * rr * rr * x2;
        if (MathF.Sign(z) * a2 * z2 > k)
            return MathF.Sqrt(x2 + z2) * il2 - rb;
        if (MathF.Sign(y) * a2 * y2 < k)
            return MathF.Sqrt(x2 + y2) * il2 - ra;
        return (MathF.Sqrt(x2 * a2 * il2) + y * rr) * il2 - ra;
    }

    /// <summary>Vertical cylinder centered at the origin, edges rounded by <paramref name="round"/>.</summary>
    public static float Cylinder(Vector3 p, float radius, float halfHeight, float round = 0)
    {
        float dx = new Vector2(p.X, p.Z).Length() - radius + round;
        float dy = MathF.Abs(p.Y) - halfHeight + round;
        return MathF.Min(MathF.Max(dx, dy), 0) + new Vector2(MathF.Max(dx, 0), MathF.Max(dy, 0)).Length() - round;
    }

    /// <summary>A 2D shape (distance <paramref name="d2"/> in the XZ plane) extruded between two heights, edges rounded.</summary>
    public static float Extrude(float d2, float y, float bottom, float top, float round)
    {
        float dy = MathF.Abs(y - (bottom + top) / 2) - (top - bottom) / 2 + round;
        float dx = d2 + round;
        return MathF.Min(MathF.Max(dx, dy), 0) + new Vector2(MathF.Max(dx, 0), MathF.Max(dy, 0)).Length() - round;
    }

    /// <summary>Smooth union (polynomial smooth minimum), blend width <paramref name="k"/>.</summary>
    public static float SmoothUnion(float a, float b, float k)
    {
        float h = MathF.Max(k - MathF.Abs(a - b), 0) / k;
        return MathF.Min(a, b) - h * h * k * 0.25f;
    }

    public static float SmoothSubtract(float a, float b, float k) => -SmoothUnion(-a, b, k);

    public static float SmoothIntersect(float a, float b, float k) => -SmoothUnion(-a, -b, k);

    // ── 2D (x, z) ───────────────────────────────────────────────────

    public static float RoundRect2(Vector2 p, Vector2 half, float r)
    {
        var q = Vector2.Abs(p) - half + new Vector2(r);
        return Vector2.Max(q, Vector2.Zero).Length() + MathF.Min(MathF.Max(q.X, q.Y), 0) - r;
    }

    public static float Circle2(Vector2 p, float r) => p.Length() - r;

    public static float Segment2(Vector2 p, Vector2 a, Vector2 b)
    {
        var pa = p - a;
        var ba = b - a;
        float h = Math.Clamp(Vector2.Dot(pa, ba) / Vector2.Dot(ba, ba), 0, 1);
        return (pa - ba * h).Length();
    }

    /// <summary>Convex polygon (counter-clockwise or clockwise), exact outside distance, signed.</summary>
    public static float Polygon2(Vector2 p, ReadOnlySpan<Vector2> v)
    {
        float d = Vector2.DistanceSquared(p, v[0]);
        float s = 1;
        for (int i = 0, j = v.Length - 1; i < v.Length; j = i, i++)
        {
            var e = v[j] - v[i];
            var w = p - v[i];
            var b = w - e * Math.Clamp(Vector2.Dot(w, e) / Vector2.Dot(e, e), 0, 1);
            d = MathF.Min(d, Vector2.Dot(b, b));
            bool c1 = p.Y >= v[i].Y, c2 = p.Y < v[j].Y, c3 = e.X * w.Y > e.Y * w.X;
            if ((c1 && c2 && c3) || (!c1 && !c2 && !c3))
                s = -s;
        }
        return s * MathF.Sqrt(d);
    }

    public static Vector2 Rotate(Vector2 p, float degrees)
    {
        float a = degrees * MathF.PI / 180, c = MathF.Cos(a), s = MathF.Sin(a);
        return new Vector2(c * p.X - s * p.Y, s * p.X + c * p.Y);
    }
}
