using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DualSenseTweaker.Controls.Model;

/// <summary>Loads our bundled, transform-baked GLB with its original geometry and texture coordinates.</summary>
internal sealed record GlbModel(IReadOnlyList<GlbPart> Parts, Rect3D Bounds, double Scale, double StickPivotHeight)
{
    public static GlbModel Load()
    {
        using var source = typeof(GlbModel).Assembly.GetManifestResourceStream("DualSenseTweaker.Assets.Controller.dualsense.glb")
                           ?? throw new InvalidDataException(L.Text("Chýba pribalený 3D model ovládača."));
        using var copy = new MemoryStream();
        source.CopyTo(copy);
        var bytes = copy.ToArray();
        uint U32(int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at, 4));
        if (bytes.Length < 28 || U32(0) != 0x46546C67 || U32(4) != 2 || U32(8) != bytes.Length || U32(16) != 0x4E4F534A)
            throw new InvalidDataException(L.Text("Neplatný GLB model."));
        int jsonLength = checked((int)U32(12));
        int binHeader = checked(20 + jsonLength);
        if (binHeader > bytes.Length - 8 || U32(binHeader + 4) != 0x004E4942)
            throw new InvalidDataException("GLB neobsahuje geometriu.");
        int binLength = checked((int)U32(binHeader));
        int binStart = binHeader + 8;
        if (binLength > bytes.Length - binStart)
            throw new InvalidDataException(L.Text("Neúplná geometria GLB."));
        using var document = JsonDocument.Parse(bytes.AsMemory(20, jsonLength));
        var root = document.RootElement;
        var views = root.GetProperty("bufferViews");
        var accessors = root.GetProperty("accessors");

        static int OptionalInt(JsonElement value, string name, int fallback = 0) =>
            value.TryGetProperty(name, out var property) ? property.GetInt32() : fallback;

        (int Offset, int Stride, int Count, int Component) Accessor(int index, int columns)
        {
            var accessor = accessors[index];
            if (accessor.TryGetProperty("sparse", out _))
                throw new InvalidDataException(L.Text("Model používa nepodporovaný sparse accessor."));
            var view = views[accessor.GetProperty("bufferView").GetInt32()];
            int component = accessor.GetProperty("componentType").GetInt32();
            int size = component switch { 5121 => 1, 5123 => 2, 5125 or 5126 => 4, _ => throw new InvalidDataException(L.Text("Nepodporované dáta geometrie.")) };
            int count = accessor.GetProperty("count").GetInt32();
            int offset = checked(OptionalInt(view, "byteOffset") + OptionalInt(accessor, "byteOffset"));
            int stride = OptionalInt(view, "byteStride", columns * size);
            long end = (long)offset + (long)(count - 1) * stride + columns * size;
            if (count <= 0 || stride < columns * size || offset < 0 || end > binLength)
                throw new InvalidDataException(L.Text("Accessor je mimo dát modelu."));
            return (binStart + offset, stride, count, component);
        }

        float[] Floats(int index, int columns)
        {
            var a = Accessor(index, columns);
            if (a.Component != 5126)
                throw new InvalidDataException(L.Text("Očakávané float súradnice modelu."));
            var values = new float[a.Count * columns];
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < columns; j++)
                {
                    float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(a.Offset + i * a.Stride + j * 4, 4)));
                    if (!float.IsFinite(value))
                        throw new InvalidDataException(L.Text("Model obsahuje neplatné súradnice."));
                    values[i * columns + j] = value;
                }
            return values;
        }

        int[] Indices(int index)
        {
            var a = Accessor(index, 1);
            if (a.Count % 3 != 0)
                throw new InvalidDataException(L.Text("Neúplný trojuholník modelu."));
            var values = new int[a.Count];
            for (int i = 0; i < a.Count; i++)
            {
                int offset = a.Offset + i * a.Stride;
                values[i] = a.Component switch
                {
                    5121 => bytes[offset],
                    5123 => BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)),
                    5125 => checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))),
                    _ => throw new InvalidDataException(L.Text("Neplatný index modelu.")),
                };
            }
            return values;
        }

        var raw = new List<(string Name, string Control, string Part, int Material, float[] Positions, float[] Normals, float[] Uv, int[] Indices)>();
        var sourceBounds = Rect3D.Empty;
        foreach (var node in root.GetProperty("nodes").EnumerateArray())
        {
            if (node.TryGetProperty("matrix", out _) || node.TryGetProperty("translation", out _) || node.TryGetProperty("rotation", out _) || node.TryGetProperty("scale", out _))
                throw new InvalidDataException(L.Text("Pribalený model musí mať zapečené transformácie."));
            if (!node.TryGetProperty("mesh", out var meshIndex))
                continue;
            string name = node.GetProperty("name").GetString()!;
            string control = "", part = "";
            if (node.TryGetProperty("extras", out var extras))
            {
                control = extras.TryGetProperty("control", out var c) ? c.GetString() ?? "" : "";
                part = extras.TryGetProperty("part", out var p) ? p.GetString() ?? "" : "";
            }
            foreach (var primitive in root.GetProperty("meshes")[meshIndex.GetInt32()].GetProperty("primitives").EnumerateArray())
            {
                if (OptionalInt(primitive, "mode", 4) != 4)
                    throw new InvalidDataException(L.Text("Model musí obsahovať trojuholníky."));
                var attributes = primitive.GetProperty("attributes");
                var positions = Floats(attributes.GetProperty("POSITION").GetInt32(), 3);
                var normals = Floats(attributes.GetProperty("NORMAL").GetInt32(), 3);
                var uv = Floats(attributes.GetProperty("TEXCOORD_0").GetInt32(), 2);
                var indices = Indices(primitive.GetProperty("indices").GetInt32());
                if (normals.Length != positions.Length || uv.Length / 2 != positions.Length / 3 || indices.Any(i => i < 0 || i >= positions.Length / 3))
                    throw new InvalidDataException(L.Text("Nekonzistentná geometria modelu."));
                for (int i = 0; i < positions.Length; i += 3)
                    sourceBounds.Union(new Point3D(positions[i], positions[i + 1], positions[i + 2]));
                raw.Add((name, control, part, OptionalInt(primitive, "material"), positions, normals, uv, indices));
            }
        }
        if (sourceBounds.IsEmpty || sourceBounds.SizeX <= 0)
            throw new InvalidDataException(L.Text("Prázdny model ovládača."));
        double scale = 1.6 / sourceBounds.SizeX; // 160 mm; same units and IMU axes as the original viewer
        var center = new Point3D(sourceBounds.X + sourceBounds.SizeX / 2, sourceBounds.Y + sourceBounds.SizeY / 2, sourceBounds.Z + sourceBounds.SizeZ / 2);
        Point3D Position(float x, float y, float z) => new((x - center.X) * scale, (z - center.Z) * scale, -(y - center.Y) * scale);

        var images = new Dictionary<int, ImageSource>();
        ImageSource Image(int index)
        {
            if (images.TryGetValue(index, out var existing))
                return existing;
            var image = root.GetProperty("images")[index];
            var view = views[image.GetProperty("bufferView").GetInt32()];
            int offset = OptionalInt(view, "byteOffset"), length = view.GetProperty("byteLength").GetInt32();
            if (offset < 0 || length < 0 || (long)offset + length > binLength)
                throw new InvalidDataException(L.Text("Neplatná textúra modelu."));
            using var stream = new MemoryStream(bytes, binStart + offset, length, writable: false);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            images[index] = bitmap;
            return bitmap;
        }

        static byte Srgb(double value) => (byte)Math.Clamp(Math.Round(255 * (value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055)), 0, 255);
        var materials = new Dictionary<int, GlbMaterial>();
        GlbMaterial Material(int index)
        {
            if (materials.TryGetValue(index, out var cached))
                return cached;
            var material = root.GetProperty("materials")[index];
            var pbr = material.GetProperty("pbrMetallicRoughness");
            string name = material.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            Brush brush;
            if (pbr.TryGetProperty("baseColorTexture", out var texture))
            {
                int image = root.GetProperty("textures")[texture.GetProperty("index").GetInt32()].GetProperty("source").GetInt32();
                brush = new ImageBrush(Image(image)) { ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 1, 1), TileMode = TileMode.Tile, Stretch = Stretch.Fill };
            }
            else
            {
                var color = pbr.TryGetProperty("baseColorFactor", out var factor)
                    ? Color.FromArgb((byte)Math.Round(factor[3].GetDouble() * 255), Srgb(factor[0].GetDouble()), Srgb(factor[1].GetDouble()), Srgb(factor[2].GetDouble()))
                    : Colors.White;
                if (name == "VRayMtl37") color = Color.FromRgb(30, 32, 37);
                brush = new SolidColorBrush(color);
            }
            brush.Freeze();
            double roughness = pbr.TryGetProperty("roughnessFactor", out var rough) ? rough.GetDouble() : 0.65;
            bool transparent = material.TryGetProperty("alphaMode", out var alpha) && alpha.GetString() == "BLEND";
            cached = new GlbMaterial(name, brush, roughness, transparent);
            materials[index] = cached;
            return cached;
        }

        var parts = new List<GlbPart>();
        var bounds = Rect3D.Empty;
        foreach (var part in raw)
        {
            var mesh = new MeshGeometry3D { TriangleIndices = new Int32Collection(part.Indices) };
            for (int i = 0; i < part.Positions.Length; i += 3)
            {
                mesh.Positions.Add(Position(part.Positions[i], part.Positions[i + 1], part.Positions[i + 2]));
                mesh.Normals.Add(new Vector3D(part.Normals[i], part.Normals[i + 2], -part.Normals[i + 1]));
                mesh.TextureCoordinates.Add(new Point(part.Uv[i / 3 * 2], part.Uv[i / 3 * 2 + 1]));
            }
            mesh.Freeze();
            bounds.Union(mesh.Bounds);
            parts.Add(new GlbPart(part.Name, part.Control, part.Part, mesh, Material(part.Material)));
        }
        return new GlbModel(parts, bounds, scale, (0.74 - center.Z) * scale);
    }
}

internal sealed record GlbPart(string Name, string Control, string Part, MeshGeometry3D Mesh, GlbMaterial Material);
internal sealed record GlbMaterial(string Name, Brush Color, double Roughness, bool Transparent);
