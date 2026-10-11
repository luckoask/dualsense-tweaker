using System.Windows.Media.Media3D;

namespace DualSenseTweaker.Controls.Model;

/// <summary>Projects touch coordinates and markings onto the actual imported surface, including its slope.</summary>
internal sealed class MeshSurface(MeshGeometry3D mesh)
{
    public Point3D Point(double u, double v)
    {
        double z = mesh.Bounds.Z + mesh.Bounds.SizeZ * v;
        double left = double.PositiveInfinity, right = double.NegativeInfinity;
        for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
        {
            var a = mesh.Positions[mesh.TriangleIndices[i]];
            var b = mesh.Positions[mesh.TriangleIndices[i + 1]];
            var c = mesh.Positions[mesh.TriangleIndices[i + 2]];
            Edge(a, b); Edge(b, c); Edge(c, a);
        }
        void Edge(Point3D a, Point3D b)
        {
            if (z < Math.Min(a.Z, b.Z) - 1e-8 || z > Math.Max(a.Z, b.Z) + 1e-8) return;
            double t = Math.Abs(a.Z - b.Z) < 1e-9 ? 0 : (z - a.Z) / (b.Z - a.Z);
            double x = a.X + (b.X - a.X) * t;
            left = Math.Min(left, x); right = Math.Max(right, x);
        }
        if (!double.IsFinite(left)) { left = mesh.Bounds.X; right = mesh.Bounds.X + mesh.Bounds.SizeX; }
        return Project(left + (right - left) * u, z);
    }

    public Point3D Project(double x, double z)
    {
        double y = double.NegativeInfinity;
        for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
        {
            var a = mesh.Positions[mesh.TriangleIndices[i]];
            var b = mesh.Positions[mesh.TriangleIndices[i + 1]];
            var c = mesh.Positions[mesh.TriangleIndices[i + 2]];
            double denominator = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (Math.Abs(denominator) < 1e-12) continue;
            double wa = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / denominator;
            double wb = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / denominator;
            double wc = 1 - wa - wb;
            if (wa >= -1e-7 && wb >= -1e-7 && wc >= -1e-7) y = Math.Max(y, wa * a.Y + wb * b.Y + wc * c.Y);
        }
        if (!double.IsFinite(y)) y = mesh.Bounds.Y + mesh.Bounds.SizeY;
        return new Point3D(x, y, z);
    }
}
