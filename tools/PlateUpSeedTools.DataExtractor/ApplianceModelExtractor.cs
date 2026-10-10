using System.Globalization;
using System.Numerics;
using YamlDotNet.RepresentationModel;

namespace PlateUpSeedTools.DataExtractor;

/// <summary>
/// Parses a prefab's full GameObject/Transform/MeshFilter/MeshRenderer hierarchy and the raw
/// interleaved vertex/index buffers of each referenced Mesh asset, baking everything into a single
/// combined Wavefront OBJ+MTL pair per appliance - this is what tools/IconRenderer/render_icons.py
/// actually imports into Blender.
///
/// Why this exists instead of using AssetRipper to do it: AssetRipper's "GUI.Free" edition (v2.0.0,
/// the current release at the time this was written) turned out to have NO mesh/model export format
/// setting at all (confirmed via its own exposed OpenAPI spec and Settings form - FBX/glTF export
/// isn't a thing in this edition), and its one relevant live-API endpoint
/// (`GET /Assets/Model.glb?Path=...`) only exports a single raw Mesh's geometry with no Transform/
/// hierarchy assembly, addressed by session-scoped runtime Bundle/Collection indices that don't
/// correspond to anything resolvable from the static GUID-based data this whole extractor otherwise
/// relies on. Both would have required either paying for AssetRipper's gated "Premium" tier or
/// building a whole separate runtime-indexing resolution layer. Parsing the geometry directly from
/// the YAML already sitting in the existing `AssetRipper/ExportedProject/` export avoids all of
/// that, and reuses the exact same guid-resolution infrastructure (GuidResolver) already proven
/// elsewhere in this project.
///
/// Scope: position data only (Unity's default channel 0: stream 0, offset 0, Float32 x3) - no
/// normals/tangents/UVs. Good enough for a recognizable icon silhouette (Blender can
/// auto-recompute shading normals), and most appliance materials are flat-colored anyway (only
/// ~91/428 have any texture at all - see ApplianceIconManifestExtractor's material extraction).
/// Skinned/blend-shape meshes aren't handled (no appliance needs them - these are all static props).
/// </summary>
public static class ApplianceModelExtractor
{
    // Unity's sentinel guid for built-in engine primitives (default Plane/Cube/etc. meshes bundled
    // inside Unity itself, not a project asset) - same constant ApplianceIconManifestExtractor uses.
    private const string BuiltinMeshGuid = "0000000000000000e000000000000000";

    public readonly record struct MeshPart(string MeshGuid, List<string> MaterialGuids, Matrix4x4 LocalToRoot);

    /// <summary>
    /// Walks every document in a (multi-document) prefab file, joining Transform/MeshFilter/
    /// MeshRenderer components by their shared `m_GameObject` reference (NOT document order/
    /// adjacency - confirmed real prefabs don't guarantee that), and returns one <see cref="MeshPart"/>
    /// per enabled, non-builtin mesh-bearing GameObject, with its full local-to-prefab-root transform
    /// already composed (walking the Transform `m_Father` chain to the root).
    /// </summary>
    public static List<MeshPart> ParsePrefabMeshParts(string prefabPath)
    {
        var stream = new YamlStream();
        using (var fileReader = new StreamReader(prefabPath))
        {
            stream.Load(fileReader);
        }

        var transformsByOwnId = new Dictionary<string, (string GameObjectId, Vector3 Pos, Quaternion Rot, Vector3 Scale, string FatherId)>();
        var transformIdByGameObjectId = new Dictionary<string, string>();
        var meshGuidByGameObjectId = new Dictionary<string, string>();
        var renderersByGameObjectId = new Dictionary<string, (List<string> MaterialGuids, bool Enabled)>();

        foreach (var doc in stream.Documents)
        {
            if (doc.RootNode is not YamlMappingNode root || doc.RootNode is not YamlMappingNode)
            {
                continue;
            }

            var anchor = doc.RootNode is YamlNode n ? n.Anchor.Value ?? "" : "";

            if (root.Children.TryGetValue(new YamlScalarNode("Transform"), out var tNode) && tNode is YamlMappingNode transform)
            {
                var gameObjectId = ReadFileId(transform, "m_GameObject");
                var fatherId = ReadFileId(transform, "m_Father");
                var pos = ReadVector3(transform, "m_LocalPosition");
                var rot = ReadQuaternion(transform, "m_LocalRotation");
                var scale = ReadVector3(transform, "m_LocalScale", Vector3.One);
                transformsByOwnId[anchor] = (gameObjectId, pos, rot, scale, fatherId);
                if (gameObjectId is not "")
                {
                    transformIdByGameObjectId[gameObjectId] = anchor;
                }
            }
            else if (root.Children.TryGetValue(new YamlScalarNode("MeshFilter"), out var mfNode) && mfNode is YamlMappingNode meshFilter)
            {
                var gameObjectId = ReadFileId(meshFilter, "m_GameObject");
                var meshGuid = ReadGuid(meshFilter, "m_Mesh");
                if (gameObjectId is not "" && meshGuid is not null && meshGuid != BuiltinMeshGuid)
                {
                    meshGuidByGameObjectId[gameObjectId] = meshGuid;
                }
            }
            else if (root.Children.TryGetValue(new YamlScalarNode("MeshRenderer"), out var mrNode) && mrNode is YamlMappingNode meshRenderer)
            {
                var gameObjectId = ReadFileId(meshRenderer, "m_GameObject");
                var enabled = meshRenderer.Children.TryGetValue(new YamlScalarNode("m_Enabled"), out var enabledNode)
                    && enabledNode is YamlScalarNode enabledScalar && enabledScalar.Value != "0";
                var materialGuids = new List<string>();
                if (meshRenderer.Children.TryGetValue(new YamlScalarNode("m_Materials"), out var materialsNode) && materialsNode is YamlSequenceNode materialsSeq)
                {
                    foreach (var entry in materialsSeq.Children.Cast<YamlMappingNode>())
                    {
                        if (entry.Children.TryGetValue(new YamlScalarNode("guid"), out var guidNode) && guidNode is YamlScalarNode guidScalar
                            && !string.IsNullOrEmpty(guidScalar.Value))
                        {
                            materialGuids.Add(guidScalar.Value);
                        }
                    }
                }

                if (gameObjectId is not "")
                {
                    renderersByGameObjectId[gameObjectId] = (materialGuids, enabled);
                }
            }
        }

        var parts = new List<MeshPart>();
        foreach (var (gameObjectId, meshGuid) in meshGuidByGameObjectId)
        {
            if (!renderersByGameObjectId.TryGetValue(gameObjectId, out var renderer) || !renderer.Enabled || renderer.MaterialGuids.Count == 0)
            {
                continue;
            }

            if (!transformIdByGameObjectId.TryGetValue(gameObjectId, out var transformId))
            {
                continue;
            }

            var localToRoot = ComposeLocalToRoot(transformId, transformsByOwnId);
            parts.Add(new MeshPart(meshGuid, renderer.MaterialGuids, localToRoot));
        }

        return parts;
    }

    private static Matrix4x4 ComposeLocalToRoot(
        string transformId,
        Dictionary<string, (string GameObjectId, Vector3 Pos, Quaternion Rot, Vector3 Scale, string FatherId)> transformsByOwnId)
    {
        var combined = Matrix4x4.Identity;
        var currentId = transformId;
        var guard = 0; // defends against a malformed/cyclic m_Father chain - no real prefab is this deep.
        while (transformsByOwnId.TryGetValue(currentId, out var t) && guard++ < 64)
        {
            var local = Matrix4x4.CreateScale(t.Scale) * Matrix4x4.CreateFromQuaternion(t.Rot) * Matrix4x4.CreateTranslation(t.Pos);
            combined *= local;
            if (t.FatherId == "0" || t.FatherId == "")
            {
                break;
            }

            currentId = t.FatherId;
        }

        return combined;
    }

    /// <summary>
    /// Reads a Mesh asset's position-only geometry (channel 0: Float32 x3 at byte offset 0 of each
    /// interleaved vertex record - unambiguous regardless of how later channels are packed) plus its
    /// per-submesh triangle index ranges, and bakes <paramref name="localToRoot"/> into every vertex.
    /// Also mirrors X and reverses triangle winding, converting Unity's left-handed coordinate space
    /// to OBJ/Blender's right-handed one (the standard, well-known conversion - without it, every
    /// part would be mirrored and inside-out).
    /// </summary>
    private static (List<Vector3> Vertices, List<(int A, int B, int C, int SubMeshIndex)> Triangles)? ReadMeshGeometry(
        string meshPath, Matrix4x4 localToRoot)
    {
        var stream = new YamlStream();
        using (var fileReader = new StreamReader(meshPath))
        {
            stream.Load(fileReader);
        }

        var doc = stream.Documents.SingleOrDefault();
        if (doc?.RootNode is not YamlMappingNode root || !root.Children.TryGetValue(new YamlScalarNode("Mesh"), out var meshNode)
            || meshNode is not YamlMappingNode mesh)
        {
            return null;
        }

        if (!mesh.Children.TryGetValue(new YamlScalarNode("m_VertexData"), out var vdNode) || vdNode is not YamlMappingNode vertexData)
        {
            return null;
        }

        var vertexCount = ReadInt(vertexData, "m_VertexCount");
        var dataSize = ReadInt(vertexData, "m_DataSize");
        if (vertexCount <= 0 || dataSize <= 0 || !vertexData.Children.TryGetValue(new YamlScalarNode("_typelessdata"), out var rawDataNode)
            || rawDataNode is not YamlScalarNode rawDataScalar || string.IsNullOrEmpty(rawDataScalar.Value))
        {
            return null;
        }

        var stride = dataSize / vertexCount;
        if (stride <= 0 || dataSize % vertexCount != 0)
        {
            return null; // Non-uniform/compressed layout this parser doesn't handle - skip rather than guess.
        }

        var bytes = Convert.FromHexString(rawDataScalar.Value);
        var vertices = new List<Vector3>(vertexCount);
        for (var i = 0; i < vertexCount; i++)
        {
            var baseOffset = i * stride;
            if (baseOffset + 12 > bytes.Length)
            {
                return null;
            }

            var x = BitConverter.ToSingle(bytes, baseOffset);
            var y = BitConverter.ToSingle(bytes, baseOffset + 4);
            var z = BitConverter.ToSingle(bytes, baseOffset + 8);
            var local = new Vector3(x, y, z);
            var world = Vector3.Transform(local, localToRoot);
            vertices.Add(new Vector3(-world.X, world.Y, world.Z)); // mirror X for left->right-handed conversion
        }

        var indexFormat = ReadInt(mesh, "m_IndexFormat"); // 0 = UInt16, 1 = UInt32
        if (!mesh.Children.TryGetValue(new YamlScalarNode("m_IndexBuffer"), out var indexBufferNode) || indexBufferNode is not YamlScalarNode indexBufferScalar
            || string.IsNullOrEmpty(indexBufferScalar.Value))
        {
            return null;
        }

        var indexBytes = Convert.FromHexString(indexBufferScalar.Value);
        var indexWidth = indexFormat == 1 ? 4 : 2;

        var triangles = new List<(int, int, int, int)>();
        if (!mesh.Children.TryGetValue(new YamlScalarNode("m_SubMeshes"), out var subMeshesNode) || subMeshesNode is not YamlSequenceNode subMeshesSeq)
        {
            return null;
        }

        var subMeshIndex = 0;
        foreach (var subMeshEntry in subMeshesSeq.Children.Cast<YamlMappingNode>())
        {
            var topology = ReadInt(subMeshEntry, "topology");
            var firstByte = ReadInt(subMeshEntry, "firstByte");
            var indexCount = ReadInt(subMeshEntry, "indexCount");
            var baseVertex = ReadInt(subMeshEntry, "baseVertex");

            if (topology == 0) // Triangles only - Quads/Strips/Lines/Points don't occur on appliance props.
            {
                var firstIndex = firstByte / indexWidth;
                for (var i = 0; i + 2 < indexCount; i += 3)
                {
                    var a = ReadIndex(indexBytes, firstIndex + i, indexWidth) + baseVertex;
                    var b = ReadIndex(indexBytes, firstIndex + i + 1, indexWidth) + baseVertex;
                    var c = ReadIndex(indexBytes, firstIndex + i + 2, indexWidth) + baseVertex;
                    if (a >= vertexCount || b >= vertexCount || c >= vertexCount)
                    {
                        continue;
                    }

                    // Reverse winding (b, a, c) to match the X-mirror above - keeps faces front-facing
                    // instead of inside-out once combined with the handedness flip.
                    triangles.Add((b, a, c, subMeshIndex));
                }
            }

            subMeshIndex++;
        }

        return (vertices, triangles);
    }

    /// <summary>
    /// Writes one combined OBJ+MTL pair for an appliance - every mesh part's geometry (already
    /// baked into prefab-root space by <see cref="ParsePrefabMeshParts"/>) merged into a single
    /// vertex/face list, grouped by `usemtl` per part's resolved material name so Blender assigns
    /// the right color/texture to each part on import with no further material-matching logic
    /// needed on the Python side.
    /// </summary>
    public static bool TryWriteObjModel(
        string prefabPath,
        GuidResolver resolver,
        IReadOnlyDictionary<string, (string Name, float[] Color, string? TexturePath)> materialsByGuid,
        string outputDir,
        string slug)
    {
        List<MeshPart> parts;
        try
        {
            parts = ParsePrefabMeshParts(prefabPath);
        }
        catch
        {
            return false;
        }

        if (parts.Count == 0)
        {
            return false;
        }

        var partGeometry = new List<(List<Vector3> Vertices, List<(int A, int B, int C, int SubMeshIndex)> Triangles, List<string> MaterialGuids, PartBounds Bounds)>();
        foreach (var part in parts)
        {
            if (!resolver.TryResolve(part.MeshGuid, out var meshPath))
            {
                continue;
            }

            var geometry = SafeReadMeshGeometry(meshPath, part.LocalToRoot);
            if (geometry is null || geometry.Value.Vertices.Count == 0 || geometry.Value.Triangles.Count == 0)
            {
                continue;
            }

            partGeometry.Add((geometry.Value.Vertices, geometry.Value.Triangles, part.MaterialGuids, ComputeBounds(geometry.Value.Vertices)));
        }

        var coverPartIndices = FindDisguiseCoverParts(partGeometry.Select(p => p.Bounds).ToList());

        var objLines = new List<string> { $"mtllib {slug}.mtl" };
        var mtlLines = new List<string>();
        var writtenMaterials = new HashSet<string>();
        var vertexOffset = 1; // OBJ indices are 1-based.
        var wroteAnyGeometry = false;

        for (var partIndex = 0; partIndex < partGeometry.Count; partIndex++)
        {
            if (coverPartIndices.Contains(partIndex))
            {
                continue;
            }

            var (vertices, triangles, materialGuids, _) = partGeometry[partIndex];
            foreach (var v in vertices)
            {
                objLines.Add(string.Create(CultureInfo.InvariantCulture, $"v {v.X} {v.Y} {v.Z}"));
            }

            // Group triangles by submesh index so each one gets its own `usemtl` - submesh i uses
            // MeshRenderer.m_Materials[i] by Unity convention (clamped to the last slot if a submesh
            // index somehow exceeds the material list, rather than skip real geometry over it).
            foreach (var subGroup in triangles.GroupBy(t => t.SubMeshIndex).OrderBy(g => g.Key))
            {
                var materialSlot = Math.Min(subGroup.Key, materialGuids.Count - 1);
                if (materialSlot < 0)
                {
                    continue;
                }

                var materialGuid = materialGuids[materialSlot];
                if (!materialsByGuid.TryGetValue(materialGuid, out var material))
                {
                    continue;
                }

                var materialKey = Sanitize(material.Name);
                if (writtenMaterials.Add(materialKey))
                {
                    mtlLines.Add($"newmtl {materialKey}");
                    mtlLines.Add(string.Create(CultureInfo.InvariantCulture, $"Kd {material.Color[0]} {material.Color[1]} {material.Color[2]}"));
                    if (material.TexturePath is not null)
                    {
                        mtlLines.Add($"map_Kd {material.TexturePath}");
                    }

                    mtlLines.Add("");
                }

                objLines.Add($"usemtl {materialKey}");
                foreach (var (a, b, c, _) in subGroup)
                {
                    objLines.Add($"f {a + vertexOffset} {b + vertexOffset} {c + vertexOffset}");
                    wroteAnyGeometry = true;
                }
            }

            vertexOffset += vertices.Count;
        }

        if (!wroteAnyGeometry)
        {
            return false;
        }

        Directory.CreateDirectory(outputDir);
        File.WriteAllLines(Path.Combine(outputDir, $"{slug}.obj"), objLines);
        File.WriteAllLines(Path.Combine(outputDir, $"{slug}.mtl"), mtlLines);
        return true;
    }

    public readonly record struct PartBounds(float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ)
    {
        // Unity's native Y-up convention still applies here (the Y-up -> Z-up axis conversion
        // happens later, at OBJ-import time in render_icons.py) - so Y is genuinely "height" in
        // this struct, not an arbitrary axis.
        public float Height => MaxY - MinY;
    }

    private static PartBounds ComputeBounds(List<Vector3> vertices)
    {
        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var v in vertices)
        {
            minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
            minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
        }

        return new PartBounds(minX, maxX, minY, maxY, minZ, maxZ);
    }

    /// <summary>
    /// Detects "disguise cover" parts - thin, near-flat horizontal plates whose footprint
    /// substantially overlaps a visibly taller/more-detailed part positioned in the same vertical
    /// neighborhood, and excludes them from the rendered model.
    ///
    /// Why this exists: found via a real, confirmed case - "Hob"'s prefab contains a fully correct,
    /// fully detailed burner grate (12 mesh parts, ~5500 vertices, matching the real wiki reference
    /// almost exactly) that NEVER appears in any render, from any camera angle, because three
    /// separate near-zero-thickness plates named "Top"/"Counter Surface"/"Counter Top" - confirmed
    /// via GameObject names in the source prefab, all using a plain default material unrelated to
    /// the actual appliance - sit directly above it, fully covering the whole top footprint. The
    /// user confirmed they've never seen anything like this in actual gameplay (no "closed lid"
    /// state exists in practice, ruling out an initial animated-lid hypothesis), so these plates
    /// are being treated as non-representative geometry that shouldn't be rendered, not as a real
    /// visual state to preserve. A geometric rule (not a name-based list) was chosen so this
    /// generalizes across the other ~427 appliances without needing to manually catalog every
    /// occurrence - "Base" (the real cabinet body, confirmed via its own non-trivial Height) is
    /// correctly NOT flagged by this same rule on the same appliance, confirming the rule
    /// discriminates real structure from disguise plates rather than being overly broad.
    /// </summary>
    private static HashSet<int> FindDisguiseCoverParts(List<PartBounds> bounds)
    {
        const float FlatThreshold = 0.045f; // "thin plate" in Unity units. Deliberately NOT raised
        // further to also catch "Hob"'s "Counter Top" (height 0.076, a thin raised lip on an
        // otherwise-confirmed disguise plate) - tried that, and it also caught a confirmed-good
        // decorative rim ("Cube_81", height 0.058) sitting in the same narrow height band with no
        // reliable footprint-area gap between them (0.54 vs 1.0, too close to the grate's own parts
        // at ~0.20-0.23 to set a clean cutoff between all three). Given the primary bug (grate
        // completely invisible) is already fixed and confirmed, not regressing a confirmed-good
        // part takes priority over also catching this one remaining thin lip - a real, acknowledged,
        // minor imperfection, not treated as fully solved.
        const float MinDenseHeight = 0.08f; // the part being covered must have real, non-trivial volume.
        const float HeightRatioThreshold = 3.0f; // the covering part must be clearly flatter, not just "a bit flatter".
        const float OverlapFractionThreshold = 0.6f; // most of the flat part's own footprint must be covered.
        const float AboveTolerance = 0.02f; // candidate must sit at/above the dense part's own top, not embedded within it.
        // The real discriminator between a full-coverage disguise plate and legitimate small detail
        // (door handles, grate rings) that happens to also be thin/short - confirmed from real
        // measured values on Hob: disguise plates ("Top"/"Counter Surface"/"Counter Top") all have a
        // footprint of 1.0x1.0 (full appliance width) = area 1.0; the grate's own thin ring
        // components have area ~0.20-0.23; door handles ~0.01-0.03. A part must cover a substantial
        // fraction of the whole appliance's footprint to be a believable "disguise the appliance as
        // something else" candidate - small internal detail, however thin, never does.
        const float MinFootprintArea = 0.3f;

        var debug = Environment.GetEnvironmentVariable("DEBUG_COVER_FILTER") == "1";
        var covers = new HashSet<int>();
        for (var i = 0; i < bounds.Count; i++)
        {
            var candidate = bounds[i];
            if (candidate.Height >= FlatThreshold)
            {
                continue;
            }

            var candidateFootprintArea = (candidate.MaxX - candidate.MinX) * (candidate.MaxZ - candidate.MinZ);
            if (candidateFootprintArea < MinFootprintArea)
            {
                continue;
            }

            for (var j = 0; j < bounds.Count; j++)
            {
                if (i == j)
                {
                    continue;
                }

                var other = bounds[j];
                if (other.Height < MinDenseHeight || other.Height < candidate.Height * HeightRatioThreshold)
                {
                    continue;
                }

                // Must sit at/above the dense part's own top - a thin accent piece embedded within
                // (or flush with the middle of) the dense part's own height range is real detail,
                // not a cover hiding it. Only something floating above the dense part's top is a
                // plausible "disguise lid" candidate.
                if (candidate.MinY < other.MaxY - AboveTolerance)
                {
                    continue;
                }

                var overlapX = Math.Max(0, Math.Min(candidate.MaxX, other.MaxX) - Math.Max(candidate.MinX, other.MinX));
                var overlapZ = Math.Max(0, Math.Min(candidate.MaxZ, other.MaxZ) - Math.Max(candidate.MinZ, other.MinZ));
                var candidateFootprint = Math.Max(1e-6f, (candidate.MaxX - candidate.MinX) * (candidate.MaxZ - candidate.MinZ));
                var overlapFraction = (overlapX * overlapZ) / candidateFootprint;

                if (debug)
                {
                    Console.Error.WriteLine($"  candidate[{i}] h={candidate.Height:F3} Y=[{candidate.MinY:F3},{candidate.MaxY:F3}] vs other[{j}] h={other.Height:F3} Y=[{other.MinY:F3},{other.MaxY:F3}] overlapFrac={overlapFraction:F3}");
                }

                if (overlapFraction >= OverlapFractionThreshold)
                {
                    covers.Add(i);
                    break;
                }
            }
        }

        return covers;
    }

    private static (List<Vector3> Vertices, List<(int A, int B, int C, int SubMeshIndex)> Triangles)? SafeReadMeshGeometry(string meshPath, Matrix4x4 localToRoot)
    {
        try
        {
            return ReadMeshGeometry(meshPath, localToRoot);
        }
        catch
        {
            return null;
        }
    }

    private static string Sanitize(string name) => name.Replace(' ', '_').Replace('.', '_');

    private static int ReadIndex(byte[] indexBytes, int indexPosition, int indexWidth)
    {
        var byteOffset = indexPosition * indexWidth;
        return indexWidth == 4
            ? BitConverter.ToInt32(indexBytes, byteOffset)
            : BitConverter.ToUInt16(indexBytes, byteOffset);
    }

    private static int ReadInt(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            && int.TryParse(scalar.Value, out var value)
            ? value
            : 0;

    private static string ReadFileId(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlMappingNode refMap
            && refMap.Children.TryGetValue(new YamlScalarNode("fileID"), out var fileIdNode) && fileIdNode is YamlScalarNode fileIdScalar
            ? fileIdScalar.Value ?? ""
            : "";

    private static string? ReadGuid(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlMappingNode refMap
            && refMap.Children.TryGetValue(new YamlScalarNode("guid"), out var guidNode) && guidNode is YamlScalarNode guidScalar
            ? guidScalar.Value
            : null;

    private static Vector3 ReadVector3(YamlMappingNode map, string key, Vector3 fallback = default)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node) || node is not YamlMappingNode vecMap)
        {
            return fallback;
        }

        return new Vector3(ReadFloat(vecMap, "x"), ReadFloat(vecMap, "y"), ReadFloat(vecMap, "z"));
    }

    private static Quaternion ReadQuaternion(YamlMappingNode map, string key)
    {
        if (!map.Children.TryGetValue(new YamlScalarNode(key), out var node) || node is not YamlMappingNode quatMap)
        {
            return Quaternion.Identity;
        }

        return new Quaternion(ReadFloat(quatMap, "x"), ReadFloat(quatMap, "y"), ReadFloat(quatMap, "z"), ReadFloat(quatMap, "w", 1f));
    }

    private static float ReadFloat(YamlMappingNode map, string key, float fallback = 0f) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            && float.TryParse(scalar.Value, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;
}
