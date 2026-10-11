using System.Numerics;
using System.Windows.Media.Media3D;

namespace DualSenseTweaker.Controls.Model;

/// <summary>
/// Turns a signed distance function into a smooth triangle mesh (naive surface nets): one vertex
/// per surface cell at the mean of its edge crossings, one quad per sign-changing grid edge, and
/// normals from the field gradient so the result shades smoothly.
/// </summary>
internal static class SurfaceNets
{
    /// <param name="sdf">Distance in millimeters, negative inside.</param>
    /// <param name="min">Lower corner of the sampled box.</param>
    /// <param name="max">Upper corner of the sampled box (must enclose the whole shape with a margin).</param>
    /// <param name="cell">Grid spacing in millimeters.</param>
    /// <param name="keep">Optional filter on triangle centroid and normal (splits one shape into differently colored meshes).</param>
    /// <param name="scale">Applied to positions (model units per millimeter).</param>
    public static MeshGeometry3D Build(Func<Vector3, float> sdf, Vector3 min, Vector3 max, float cell,
        Func<Vector3, Vector3, bool>? keep = null, float scale = 0.01f)
    {
        int nx = (int)MathF.Ceiling((max.X - min.X) / cell) + 1;
        int ny = (int)MathF.Ceiling((max.Y - min.Y) / cell) + 1;
        int nz = (int)MathF.Ceiling((max.Z - min.Z) / cell) + 1;
        var field = new float[nx * ny * nz];
        int Index(int x, int y, int z) => (z * ny + y) * nx + x;

        Parallel.For(0, nz, z =>
        {
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    field[Index(x, y, z)] = sdf(min + new Vector3(x, y, z) * cell);
        });

        // One vertex per cell that the surface passes through.
        var cellVertex = new int[(nx - 1) * (ny - 1) * (nz - 1)];
        Array.Fill(cellVertex, -1);
        int CellIndex(int x, int y, int z) => (z * (ny - 1) + y) * (nx - 1) + x;
        var vertices = new List<Vector3>();
        Span<float> corner = stackalloc float[8];
        for (int z = 0; z < nz - 1; z++)
            for (int y = 0; y < ny - 1; y++)
                for (int x = 0; x < nx - 1; x++)
                {
                    int mask = 0;
                    for (int c = 0; c < 8; c++)
                    {
                        corner[c] = field[Index(x + (c & 1), y + ((c >> 1) & 1), z + ((c >> 2) & 1))];
                        if (corner[c] < 0)
                            mask |= 1 << c;
                    }
                    if (mask == 0 || mask == 255)
                        continue;

                    var sum = Vector3.Zero;
                    int crossings = 0;
                    for (int a = 0; a < 8; a++)
                    {
                        for (int axis = 0; axis < 3; axis++)
                        {
                            int b = a | (1 << axis);
                            if (b == a)
                                continue;
                            float va = corner[a], vb = corner[b];
                            if ((va < 0) == (vb < 0))
                                continue;
                            float t = va / (va - vb);
                            var pa = new Vector3(a & 1, (a >> 1) & 1, (a >> 2) & 1);
                            var pb = new Vector3(b & 1, (b >> 1) & 1, (b >> 2) & 1);
                            sum += Vector3.Lerp(pa, pb, t);
                            crossings++;
                        }
                    }
                    cellVertex[CellIndex(x, y, z)] = vertices.Count;
                    vertices.Add(min + (new Vector3(x, y, z) + sum / crossings) * cell);
                }

        // Normals from the gradient of the field.
        var normals = new Vector3[vertices.Count];
        float h = cell * 0.5f;
        Parallel.For(0, vertices.Count, i =>
        {
            var p = vertices[i];
            var n = new Vector3(
                sdf(p + new Vector3(h, 0, 0)) - sdf(p - new Vector3(h, 0, 0)),
                sdf(p + new Vector3(0, h, 0)) - sdf(p - new Vector3(0, h, 0)),
                sdf(p + new Vector3(0, 0, h)) - sdf(p - new Vector3(0, 0, h)));
            normals[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
        });

        // A quad around every grid edge whose ends differ in sign.
        var triangles = new List<int>();
        void Quad(int a, int b, int c, int d)
        {
            if (a < 0 || b < 0 || c < 0 || d < 0)
                return;
            AddTriangle(a, b, c);
            AddTriangle(a, c, d);
        }
        void AddTriangle(int a, int b, int c)
        {
            var pa = vertices[a];
            var face = Vector3.Cross(vertices[b] - pa, vertices[c] - pa);
            var smooth = normals[a] + normals[b] + normals[c];
            if (Vector3.Dot(face, smooth) < 0)
                (b, c) = (c, b);
            if (keep is not null)
            {
                var centroid = (pa + vertices[b] + vertices[c]) / 3;
                if (!keep(centroid, Vector3.Normalize(smooth)))
                    return;
            }
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        for (int z = 1; z < nz - 1; z++)
            for (int y = 1; y < ny - 1; y++)
                for (int x = 0; x < nx - 1; x++)
                    if ((field[Index(x, y, z)] < 0) != (field[Index(x + 1, y, z)] < 0))
                        Quad(cellVertex[CellIndex(x, y - 1, z - 1)], cellVertex[CellIndex(x, y, z - 1)],
                             cellVertex[CellIndex(x, y, z)], cellVertex[CellIndex(x, y - 1, z)]);
        for (int z = 1; z < nz - 1; z++)
            for (int y = 0; y < ny - 1; y++)
                for (int x = 1; x < nx - 1; x++)
                    if ((field[Index(x, y, z)] < 0) != (field[Index(x, y + 1, z)] < 0))
                        Quad(cellVertex[CellIndex(x - 1, y, z - 1)], cellVertex[CellIndex(x, y, z - 1)],
                             cellVertex[CellIndex(x, y, z)], cellVertex[CellIndex(x - 1, y, z)]);
        for (int z = 0; z < nz - 1; z++)
            for (int y = 1; y < ny - 1; y++)
                for (int x = 1; x < nx - 1; x++)
                    if ((field[Index(x, y, z)] < 0) != (field[Index(x, y, z + 1)] < 0))
                        Quad(cellVertex[CellIndex(x - 1, y - 1, z)], cellVertex[CellIndex(x, y - 1, z)],
                             cellVertex[CellIndex(x, y, z)], cellVertex[CellIndex(x - 1, y, z)]);

        // Only vertices used by kept triangles go into the mesh.
        var remap = new int[vertices.Count];
        Array.Fill(remap, -1);
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var meshNormals = new Vector3DCollection();
        var indices = new System.Windows.Media.Int32Collection(triangles.Count);
        foreach (int v in triangles)
        {
            if (remap[v] < 0)
            {
                remap[v] = positions.Count;
                var p = vertices[v] * scale;
                positions.Add(new Point3D(p.X, p.Y, p.Z));
                meshNormals.Add(new Vector3D(normals[v].X, normals[v].Y, normals[v].Z));
            }
            indices.Add(remap[v]);
        }
        mesh.Positions = positions;
        mesh.Normals = meshNormals;
        mesh.TriangleIndices = indices;
        mesh.Freeze();
        return mesh;
    }
}
