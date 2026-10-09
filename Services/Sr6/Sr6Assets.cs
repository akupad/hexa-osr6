using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Hexa.Services.Sr6;

/// <summary>OBJ 里的一个对象组（o / g 段落）。</summary>
internal sealed class Sr6MeshGroup
{
    public Sr6MeshGroup(string name, MeshGeometry3D mesh)
    {
        Name = name;
        Mesh = mesh;
    }

    /// <summary>OBJ 里的 o 名（如 pitcherLink / rodEndBearing）。</summary>
    public string Name { get; }

    /// <summary>已冻结的三角网格（位置 + 法线 + 索引）。</summary>
    public MeshGeometry3D Mesh { get; }
}

/// <summary>
/// SR6 机械模型网格资源。
///
/// 网格来自 osr-emu（https://github.com/ayvasoftware/osr-emu，MIT License，
/// 模型作者 soritesparadox），由 Hexa 以嵌入资源形式随程序发布，
/// 首次访问时解析并缓存 —— 动作页 / 测试台 / 悬停预览三处的预览控件共享同一份冻结几何，
/// 不会重复解析 2.2MB 的 OBJ，也不会重复占用显存。
///
/// 只实现 OBJ 里实际用到的语法：v / vn / f（支持 v、v//vn、v/vt、v/vt/vn 四种写法，
/// 多边形面按扇形三角化），o / g 分段，以及可选的“合并同位置顶点 + 重算法线”。
/// </summary>
internal static class Sr6Assets
{
    private const string ResourcePrefix = "Hexa.Assets.Models.Sr6.";

    private static readonly Dictionary<string, IReadOnlyList<Sr6MeshGroup>> Cache =
        new(StringComparer.Ordinal);

    private static readonly object CacheLock = new();

    /// <summary>读取某个 OBJ 资源（按文件名，如 "sr6_base.obj"），结果缓存。</summary>
    /// <param name="fileName">嵌入资源文件名（不含命名空间前缀）。</param>
    /// <param name="smoothNormals">true = 合并同位置顶点并重算平滑法线（球头轴承用）。</param>
    public static IReadOnlyList<Sr6MeshGroup> Get(string fileName, bool smoothNormals = false)
    {
        string key = smoothNormals ? fileName + "|smooth" : fileName;
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out IReadOnlyList<Sr6MeshGroup>? cached)) return cached;
        }

        string text = ReadResource(fileName);
        IReadOnlyList<Sr6MeshGroup> groups = Parse(text, smoothNormals);

        lock (CacheLock)
        {
            Cache[key] = groups;
        }
        return groups;
    }

    private static string ReadResource(string fileName)
    {
        var assembly = typeof(Sr6Assets).Assembly;
        string name = ResourcePrefix + fileName;
        using Stream? stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
            throw new InvalidOperationException($"缺少模型资源 {name}（检查 Hexa.csproj 的 EmbeddedResource 配置）。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    // ══════════════════════════════════════════════════════════════
    //  OBJ 解析
    // ══════════════════════════════════════════════════════════════

    private static List<Sr6MeshGroup> Parse(string text, bool smoothNormals)
    {
        var rawPositions = new List<Point3D>(4096);
        var rawNormals = new List<Vector3D>(4096);
        var groups = new List<Sr6MeshGroup>(4);

        var positions = new List<Point3D>();
        var normals = new List<Vector3D>();
        var indices = new List<int>();
        var vertexMap = new Dictionary<long, int>();
        string groupName = "default";
        bool groupHasFaces = false;

        // 面顶点索引缓冲：放在循环外复用（CA2014：不要在循环里 stackalloc）
        Span<int> faceVertices = stackalloc int[16];

        foreach (string rawLine in text.Split('\n'))
        {
            ReadOnlySpan<char> line = rawLine.AsSpan().Trim();
            if (line.Length == 0 || line[0] == '#') continue;

            if (line.StartsWith("v "))
            {
                if (TryParseTriple(line[2..], out double x, out double y, out double z))
                    rawPositions.Add(new Point3D(x, y, z));
                continue;
            }
            if (line.StartsWith("vn "))
            {
                if (TryParseTriple(line[3..], out double x, out double y, out double z))
                    rawNormals.Add(new Vector3D(x, y, z));
                continue;
            }
            if (line.StartsWith("o ") || line.StartsWith("g "))
            {
                if (groupHasFaces)
                {
                    groups.Add(BuildGroup(groupName, positions, normals, indices, smoothNormals));
                    positions = new List<Point3D>();
                    normals = new List<Vector3D>();
                    indices = new List<int>();
                    vertexMap = new Dictionary<long, int>();
                    groupHasFaces = false;
                }
                groupName = line[2..].Trim().ToString();
                if (groupName.Length == 0) groupName = "default";
                continue;
            }
            if (line[0] != 'f') continue;

            // 面：f v1 v2 v3 [v4 ...]，每个 token 可能是 v、v/vt、v//vn、v/vt/vn
            int count = 0;
            int cursor = 1;
            while (cursor < line.Length && count < faceVertices.Length)
            {
                while (cursor < line.Length && line[cursor] == ' ') cursor++;
                int start = cursor;
                while (cursor < line.Length && line[cursor] != ' ') cursor++;
                if (cursor == start) break;

                ReadOnlySpan<char> token = line[start..cursor];
                int vIndex = ResolveIndex(FirstField(token), rawPositions.Count);
                int nIndex = ResolveIndex(NormalField(token), rawNormals.Count);
                if (vIndex < 0) continue;

                long key = ((long)vIndex << 32) | (uint)(nIndex + 1);
                if (!vertexMap.TryGetValue(key, out int outIndex))
                {
                    outIndex = positions.Count;
                    vertexMap[key] = outIndex;
                    positions.Add(rawPositions[vIndex]);
                    normals.Add(nIndex >= 0 ? rawNormals[nIndex] : default);
                }
                faceVertices[count++] = outIndex;
            }

            if (count < 3) continue;
            for (int i = 1; i + 1 < count; i++)
            {
                indices.Add(faceVertices[0]);
                indices.Add(faceVertices[i]);
                indices.Add(faceVertices[i + 1]);
            }
            groupHasFaces = true;
        }

        if (groupHasFaces) groups.Add(BuildGroup(groupName, positions, normals, indices, smoothNormals));
        if (groups.Count == 0) throw new InvalidOperationException("OBJ 里没有可用的三角面。");
        return groups;
    }

    /// <summary>token 的第一段（顶点索引），如 "3//5" → 3。</summary>
    private static ReadOnlySpan<char> FirstField(ReadOnlySpan<char> token)
    {
        int slash = token.IndexOf('/');
        return slash < 0 ? token : token[..slash];
    }

    /// <summary>token 的法线索引（第 3 段），没有则返回空。</summary>
    private static ReadOnlySpan<char> NormalField(ReadOnlySpan<char> token)
    {
        int first = token.IndexOf('/');
        if (first < 0) return default;
        ReadOnlySpan<char> rest = token[(first + 1)..];
        int second = rest.IndexOf('/');
        if (second < 0) return default;
        return rest[(second + 1)..];
    }

    /// <summary>OBJ 索引 → 0 基索引；负数表示从末尾倒数；解析失败返回 -1。</summary>
    private static int ResolveIndex(ReadOnlySpan<char> field, int total)
    {
        if (field.Length == 0) return -1;
        if (!int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)) return -1;
        if (value > 0) return value - 1;
        if (value < 0) return total + value;
        return -1;
    }

    private static bool TryParseTriple(ReadOnlySpan<char> span, out double x, out double y, out double z)
    {
        x = y = z = 0;
        int i = 0;
        if (!TryNext(span, ref i, out x)) return false;
        if (!TryNext(span, ref i, out y)) return false;
        if (!TryNext(span, ref i, out z)) return false;
        return true;

        static bool TryNext(ReadOnlySpan<char> span, ref int i, out double value)
        {
            while (i < span.Length && span[i] == ' ') i++;
            int start = i;
            while (i < span.Length && span[i] != ' ') i++;
            if (i == start) { value = 0; return false; }
            return double.TryParse(span[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }

    private static Sr6MeshGroup BuildGroup(
        string name, List<Point3D> positions, List<Vector3D> normals, List<int> indices, bool smoothNormals)
    {
        bool needsNormals = smoothNormals || normals.Any(n => n.LengthSquared < 1e-12);
        MeshGeometry3D mesh;

        if (needsNormals)
        {
            (Point3D[] merged, Vector3D[] computed, int[] newIndices) =
                MergeAndComputeNormals(positions, indices);
            mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(merged),
                Normals = new Vector3DCollection(computed),
                TriangleIndices = new Int32Collection(newIndices),
            };
        }
        else
        {
            var normalized = new Vector3D[normals.Count];
            for (int i = 0; i < normals.Count; i++)
            {
                Vector3D n = normals[i];
                n.Normalize();
                normalized[i] = n;
            }
            mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection(positions),
                Normals = new Vector3DCollection(normalized),
                TriangleIndices = new Int32Collection(indices),
            };
        }

        mesh.Freeze();
        return new Sr6MeshGroup(name, mesh);
    }

    /// <summary>
    /// 按位置合并重复顶点（1e-6 容差）并重算平滑法线 —— 对应 osr-emu 的
    /// BufferGeometryUtils.mergeVertices + computeVertexNormals，用于球头轴承。
    /// </summary>
    private static (Point3D[] positions, Vector3D[] normals, int[] indices) MergeAndComputeNormals(
        List<Point3D> positions, List<int> indices)
    {
        var map = new Dictionary<(long, long, long), int>(positions.Count);
        var merged = new List<Point3D>(positions.Count);
        var remap = new int[positions.Count];
        for (int i = 0; i < positions.Count; i++)
        {
            Point3D p = positions[i];
            var key = (
                (long)Math.Round(p.X * 1e6),
                (long)Math.Round(p.Y * 1e6),
                (long)Math.Round(p.Z * 1e6));
            if (!map.TryGetValue(key, out int target))
            {
                target = merged.Count;
                map[key] = target;
                merged.Add(p);
            }
            remap[i] = target;
        }

        var newIndices = new int[indices.Count];
        for (int i = 0; i < indices.Count; i++) newIndices[i] = remap[indices[i]];

        var accumulated = new Vector3D[merged.Count];
        for (int t = 0; t + 2 < newIndices.Length; t += 3)
        {
            int a = newIndices[t], b = newIndices[t + 1], c = newIndices[t + 2];
            Vector3D normal = Vector3D.CrossProduct(merged[b] - merged[a], merged[c] - merged[a]);
            if (normal.LengthSquared < 1e-20) continue;
            accumulated[a] += normal;
            accumulated[b] += normal;
            accumulated[c] += normal;
        }
        for (int i = 0; i < accumulated.Length; i++)
        {
            if (accumulated[i].LengthSquared < 1e-20) accumulated[i] = new Vector3D(0, 0, 1);
            else accumulated[i].Normalize();
        }

        return (merged.ToArray(), accumulated, newIndices);
    }
}
