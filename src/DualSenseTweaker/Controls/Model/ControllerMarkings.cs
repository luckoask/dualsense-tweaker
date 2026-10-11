using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace DualSenseTweaker.Controls.Model;

/// <summary>Printed markings projected onto the imported caps and shoulder surfaces.</summary>
internal static class ControllerMarkings
{
    public static GeometryModel3D Face(string name, MeshGeometry3D cap)
    {
        var mesh = new MeshGeometry3D();
        var bounds = cap.Bounds;
        double x = bounds.X + bounds.SizeX / 2, z = bounds.Z + bounds.SizeZ / 2;
        var surface = new MeshSurface(cap);
        void Line(Point a, Point b)
        {
            var direction = b - a;
            direction.Normalize();
            var perpendicular = new Vector(-direction.Y, direction.X) * 0.0017;
            // Subdivide long strokes so the center of a cross or square cannot sink below a domed cap.
            const int divisions = 12;
            for (int segment = 0; segment < divisions; segment++)
            {
                var start = a + (b - a) * (segment / (double)divisions);
                var end = a + (b - a) * ((segment + 1) / (double)divisions);
                int first = mesh.Positions.Count;
                foreach (var p in new[] { start + perpendicular, end + perpendicular, end - perpendicular, start - perpendicular })
                {
                    var at = surface.Project(x + p.X, z + p.Y);
                    at.Y += 0.0008;
                    mesh.Positions.Add(at);
                }
                foreach (int index in new[] { 0, 2, 1, 0, 3, 2 }) mesh.TriangleIndices.Add(first + index);
            }
        }
        void Outline(Point[] points)
        {
            for (int i = 0; i < points.Length; i++) Line(points[i], points[(i + 1) % points.Length]);
        }
        switch (name)
        {
            case "triangle": Outline([new(0, -0.021), new(0.021, 0.015), new(-0.021, 0.015)]); break;
            case "square": Outline([new(-0.017, -0.017), new(0.017, -0.017), new(0.017, 0.017), new(-0.017, 0.017)]); break;
            case "circle": Outline([.. Enumerable.Range(0, 48).Select(i => new Point(Math.Cos(i * Math.PI / 24) * 0.02, Math.Sin(i * Math.PI / 24) * 0.02))]); break;
            case "cross": Line(new(-0.016, -0.016), new(0.016, 0.016)); Line(new(-0.016, 0.016), new(0.016, -0.016)); break;
            default: throw new ArgumentException("Unknown face button.", nameof(name));
        }
        mesh.Freeze();
        var material = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(112, 119, 133)));
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }

    public static GeometryModel3D Shoulder(string name, MeshGeometry3D shoulder)
    {
        var front = new MeshGeometry3D { TriangleIndices = shoulder.TriangleIndices };
        foreach (var p in shoulder.Positions) front.Positions.Add(new Point3D(p.X, -p.Z, p.Y));
        front.Freeze();
        var surface = new MeshSurface(front);
        var bounds = shoulder.Bounds;
        double centerX = bounds.X + bounds.SizeX / 2, centerY = bounds.Y + bounds.SizeY / 2;
        double width = Math.Min(0.10, bounds.SizeX * 0.65), height = Math.Min(0.05, bounds.SizeY * 0.6);
        var mesh = new MeshGeometry3D();
        const int columns = 8, rows = 4;
        for (int row = 0; row <= rows; row++)
            for (int column = 0; column <= columns; column++)
            {
                double u = column / (double)columns, v = row / (double)rows;
                double x = centerX + (0.5 - u) * width, y = centerY + (0.5 - v) * height;
                var at = surface.Project(x, y);
                mesh.Positions.Add(new Point3D(x, y, -at.Y - 0.0008));
                mesh.TextureCoordinates.Add(new Point(u, v));
                if (row < rows && column < columns)
                {
                    int first = row * (columns + 1) + column;
                    foreach (int index in new[] { first, first + columns + 2, first + 1, first, first + columns + 1, first + columns + 2 }) mesh.TriangleIndices.Add(index);
                }
            }
        mesh.Freeze();
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 64, 32));
            var text = new FormattedText(name.ToUpperInvariant(), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 25,
                new SolidColorBrush(Color.FromRgb(125, 130, 141)), 1);
            context.DrawText(text, new Point((64 - text.Width) / 2, (32 - text.Height) / 2));
        }
        drawing.Freeze();
        var brush = new DrawingBrush(drawing) { Stretch = Stretch.Fill };
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        return new GeometryModel3D(mesh, material) { BackMaterial = material };
    }
}
