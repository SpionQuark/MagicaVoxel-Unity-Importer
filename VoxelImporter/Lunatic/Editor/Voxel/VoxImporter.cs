using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;

namespace Lunatic.Voxel
{
    /// <summary>
    /// ScriptedImporter for MagicaVoxel ".vox" files.
    ///
    /// Format reference:
    ///   https://github.com/ephtracy/voxel-model/blob/master/MagicaVoxel-file-format-vox.txt
    ///   https://github.com/ephtracy/voxel-model/blob/master/MagicaVoxel-file-format-vox-extension.txt
    ///
    /// Verified against Unity 2022.3 LTS.
    /// </summary>
    [ScriptedImporter(152, "vox")]
    public class VoxImporter : ScriptedImporter
    {
        // Inspector options. These are serialized by name inside every .vox .meta
        // file, so do not rename them.
        public bool buildModel = true;
        public bool buildScriptableObjects = true;
        public bool buildPaletteTexture = true;
        public bool buildMaterial = true;
        public bool buildAnimation = true;
        public int animationFramesPerSecond = 12;

        // MagicaVoxel limits a single model to 256^3 voxels.
        private const int MaxModelDimension = 256;
        private const int MaxVoxelsPerModel = MaxModelDimension * MaxModelDimension * MaxModelDimension;

        // Submesh order also drives the fallback material set below.
        private static readonly string[] MaterialTypeNames = { "_diffuse", "_metal", "_glass", "_emit" };

        // --- per-import state (importer instances are reused, so everything is reset in OnImportAsset) ---
        private List<Vector3Int> _sizes;
        private List<GameObject> _models;
        private List<Vector3Int> _modelSizes;
        private List<byte[,,]> _modelGrids;
        private Material[] _materials;
        private VoxDict[] _materialProps;
        private Color32[] _palette;
        private Texture2D _paletteTexture;
        private bool _paletteTextureAdded;
        private bool _hasEmbeddedPalette;
        private bool _mainObjectSet;
        private int _animationCount;
        private readonly HashSet<Material> _addedMaterials = new HashSet<Material>();
        private readonly List<string> _unknownChunkIds = new List<string>();
        private readonly List<VoxDatas> _createdDatas = new List<VoxDatas>();

        public override void OnImportAsset(AssetImportContext ctx)
        {
            _sizes = new List<Vector3Int>();
            _models = new List<GameObject>();
            _modelSizes = new List<Vector3Int>();
            _modelGrids = new List<byte[,,]>();
            _materialProps = new VoxDict[MaxModelDimension];
            _palette = BuildFallbackPalette();
            _paletteTexture = null;
            _paletteTextureAdded = false;
            _hasEmbeddedPalette = false;
            _mainObjectSet = false;
            _animationCount = 0;
            _addedMaterials.Clear();
            _unknownChunkIds.Clear();
            _createdDatas.Clear();
            _materials = LoadMaterials();

            string fileName = Path.GetFileNameWithoutExtension(ctx.assetPath);
            var nodesById = new Dictionary<int, VoxNode>();
            GameObject voxContainer = null;

            try
            {
                using (FileStream fs = File.OpenRead(ctx.assetPath))
                using (BinaryReader reader = new BinaryReader(fs))
                {
                    string signature = VoxIO.ReadFourCC(reader);
                    if (signature != "VOX ")
                    {
                        ctx.LogImportError(
                            $"'{ctx.assetPath}' is not a MagicaVoxel file (expected the 'VOX ' signature, found '{signature.Trim()}').");
                        FinishAsEmpty(ctx);
                        return;
                    }

                    int version = reader.ReadInt32();
                    if (version != 150 && version != 200)
                        Debug.Log($"[VoxImporter] '{fileName}': unusual format version {version}; importing anyway.");

                    var mainChunk = new VoxChunk();
                    mainChunk.ReadChunkHeader(reader);
                    if (mainChunk.id != "MAIN")
                    {
                        ctx.LogImportError(
                            $"'{ctx.assetPath}' is malformed: expected a 'MAIN' chunk, found '{mainChunk.id}'.");
                        FinishAsEmpty(ctx);
                        return;
                    }

                    ReadChunkTree(ctx, reader, mainChunk, fileName, nodesById);
                }

                if (!_hasEmbeddedPalette)
                    Debug.Log($"[VoxImporter] '{fileName}': no palette chunk; using an approximate fallback palette.");
                if (_unknownChunkIds.Count > 0)
                    Debug.Log($"[VoxImporter] '{fileName}': skipped unrecognised chunk(s): {string.Join(", ", _unknownChunkIds)}.");

                voxContainer = new GameObject(fileName);

                EnsurePaletteTexture(ctx);
                BuildMeshes(ctx, fileName);

                if (nodesById.ContainsKey(0))
                    NodeHierarchy(ctx, fileName, nodesById, voxContainer, 0, new HashSet<int>());

                FinishAsset(ctx, voxContainer);

                string animNote = _animationCount > 0
                    ? $", {_animationCount} animation(s) @ {Mathf.Clamp(animationFramesPerSecond, 1, 60)}fps"
                    : string.Empty;
                Debug.Log($"[VoxImporter] Imported '{fileName}': {_models.Count} model(s){animNote}.");
            }
            catch (Exception e)
            {
                ctx.LogImportError($"Failed to import '{ctx.assetPath}': {e}");
                if (voxContainer != null) DestroyImmediate(voxContainer);
                FinishAsEmpty(ctx);
            }
        }

        private void ReadChunkTree(AssetImportContext ctx, BinaryReader reader, VoxChunk mainChunk,
            string fileName, Dictionary<int, VoxNode> nodesById)
        {
            long streamLength = reader.BaseStream.Length;
            long childrenStart = reader.BaseStream.Position;

            long declaredChildren = mainChunk.childrenSize > 0
                ? mainChunk.childrenSize
                : (mainChunk.contentSize > 0 ? mainChunk.contentSize : 0);
            long childrenEnd = declaredChildren > 0
                ? Math.Min(childrenStart + declaredChildren, streamLength)
                : streamLength;

            while (reader.BaseStream.Position + 12 <= childrenEnd)
            {
                long headerStart = reader.BaseStream.Position;

                var chunk = new VoxChunk();
                chunk.ReadChunkHeader(reader);

                if (chunk.contentSize < 0 || chunk.childrenSize < 0)
                {
                    ctx.LogImportWarning(
                        $"'{fileName}': corrupt chunk header ('{chunk.id}', size {chunk.contentSize}/{chunk.childrenSize}); stopping.");
                    break;
                }

                long contentStart = reader.BaseStream.Position;
                long nextChunk = contentStart + chunk.contentSize + chunk.childrenSize;
                if (nextChunk < contentStart || nextChunk > childrenEnd)
                    nextChunk = childrenEnd;

                try
                {
                    ReadChunk(ctx, reader, chunk, fileName, nodesById);
                }
                catch (EndOfStreamException)
                {
                    ctx.LogImportWarning(
                        $"'{fileName}': file ended while reading a '{chunk.id}' chunk; the import may be incomplete.");
                    break;
                }
                catch (Exception e)
                {
                    ctx.LogImportWarning(
                        $"'{fileName}': skipped a malformed '{chunk.id}' chunk ({e.GetType().Name}: {e.Message}).");
                }

                long resume = Math.Min(nextChunk, streamLength);
                if (resume <= headerStart) break;
                reader.BaseStream.Position = resume;
            }
        }

        private void ReadChunk(AssetImportContext ctx, BinaryReader reader, VoxChunk chunk,
            string fileName, Dictionary<int, VoxNode> nodesById)
        {
            switch (chunk.id)
            {
                case "PACK":
                    reader.ReadInt32();
                    break;

                case "SIZE":
                {
                    int rawX = reader.ReadInt32();
                    int rawZ = reader.ReadInt32();
                    int rawY = reader.ReadInt32();
                    int sx = Mathf.Clamp(rawX, 0, MaxModelDimension);
                    int sy = Mathf.Clamp(rawY, 0, MaxModelDimension);
                    int sz = Mathf.Clamp(rawZ, 0, MaxModelDimension);
                    if (sx != rawX || sy != rawY || sz != rawZ)
                        ctx.LogImportWarning(
                            $"'{fileName}': model size ({rawX},{rawY},{rawZ}) is out of range, clamped to ({sx},{sy},{sz}).");
                    _sizes.Add(new Vector3Int(sx, sy, sz));
                    break;
                }

                case "XYZI":
                {
                    int declaredVoxels = reader.ReadInt32();
                    if (_sizes.Count == 0)
                    {
                        ctx.LogImportWarning($"'{fileName}': voxel data (XYZI) with no preceding SIZE chunk; model skipped.");
                        break;
                    }

                    Vector3Int size = _sizes[_sizes.Count - 1];
                    var grid = new byte[size.x + 1, size.y + 1, size.z + 1];

                    int voxelCount = VoxIO.SafeCount(declaredVoxels, VoxIO.BytesRemaining(reader), 4, MaxVoxelsPerModel);
                    int dropped = 0;
                    for (int j = 0; j < voxelCount; j++)
                    {
                        byte[] v = reader.ReadBytes(4);
                        if (v.Length < 4) break;

                        int gx = v[0];
                        int gy = v[2];
                        int gz = v[1];
                        if (gx >= 0 && gx < size.x && gy >= 0 && gy < size.y && gz >= 0 && gz < size.z)
                            grid[gx, gy, gz] = v[3];
                        else
                            dropped++;
                    }
                    if (dropped > 0)
                        ctx.LogImportWarning($"'{fileName}': {dropped} voxel(s) outside the declared model bounds were ignored.");

                    var go = new GameObject("vox_model #" + _models.Count);
                    VoxModel vm = go.AddComponent<VoxModel>();
                    vm.size = size;
                    vm.grid = grid;

                    _models.Add(go);
                    _modelSizes.Add(size);
                    _modelGrids.Add(grid);
                    break;
                }

                case "RGBA":
                {
                    _palette[0] = new Color32(0, 0, 0, 0);
                    for (int i = 0; i < 255; i++)
                    {
                        byte[] rgba = reader.ReadBytes(4);
                        if (rgba.Length < 4) break;
                        _palette[i + 1] = new Color32(rgba[0], rgba[1], rgba[2], rgba[3]);
                    }
                    _hasEmbeddedPalette = true;
                    break;
                }

                case "MATL":
                {
                    int matId = reader.ReadInt32();
                    var attributes = new VoxDict();
                    attributes.Read(reader);
                    if (matId >= 0 && matId < _materialProps.Length)
                        _materialProps[matId] = attributes;
                    break;
                }

                case "nTRN":
                {
                    var node = new VoxTransformNode();
                    node.Read(reader);
                    nodesById[node.id] = node;
                    break;
                }
                case "nGRP":
                {
                    var node = new VoxGroupNode();
                    node.Read(reader);
                    nodesById[node.id] = node;
                    break;
                }
                case "nSHP":
                {
                    var node = new VoxShapeNode();
                    node.Read(reader);
                    nodesById[node.id] = node;
                    break;
                }

                case "LAYR": // scene layers
                case "MATT": // legacy (pre-0.98) materials
                case "rOBJ": // render settings
                case "rCAM": // cameras
                case "NOTE": // palette note strings
                case "IMAP": // palette index map
                case "META": // metadata (MagicaVoxel 0.99.7+)
                    break;

                default:
                    if (!_unknownChunkIds.Contains(chunk.id))
                        _unknownChunkIds.Add(chunk.id);
                    break;
            }
        }

        private void EnsurePaletteTexture(AssetImportContext ctx)
        {
            _paletteTexture = new Texture2D(256, 1, TextureFormat.RGBA32, false)
            {
                name = "vox_palette",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            _paletteTexture.SetPixels32(_palette);
            _paletteTexture.Apply(false, false);

            for (int i = 0; i < _materials.Length; i++)
                if (_materials[i] != null)
                    _materials[i].mainTexture = _paletteTexture;

            if (buildPaletteTexture || buildMaterial || buildModel)
            {
                ctx.AddObjectToAsset("vox_palette", _paletteTexture);
                _paletteTextureAdded = true;
            }
        }

        private void BuildMeshes(AssetImportContext ctx, string fileName)
        {
            bool embedMaterials = buildModel || buildMaterial;

            for (int i = 0; i < _models.Count; i++)
            {
                GameObject go = _models[i];
                if (go == null) continue;

                Vector3Int size = _modelSizes[i];
                byte[,,] grid = _modelGrids[i];

                var vertices = new List<Vector3>();
                var normals = new List<Vector3>();
                var uvs = new List<Vector2>();
                var submeshes = new List<int>[4];

                VoxDatas datas = ScriptableObject.CreateInstance<VoxDatas>();
                datas.name = go.name + " datas";
                datas.map = new VoxelMap { size = size };
                datas.palette = ScriptableObject.CreateInstance<VoxPalette>();
                datas.palette.name = go.name + " palette";
                datas.palette.colors = _palette;
                datas.palette.matRefs = new VoxPalette.MaterialType[256];
                for (int c = 0; c < 256; c++)
                    datas.palette.matRefs[c] = ToMaterialType(MaterialTypeName(c));

                if (grid != null && size.x > 0 && size.y > 0 && size.z > 0)
                {
                    Vector3 pivot = size / 2;

                    for (int x = 0; x < size.x; x++)
                    for (int y = 0; y < size.y; y++)
                    for (int z = 0; z < size.z; z++)
                    {
                        byte c = grid[x, y, z];
                        if (c == 0) continue;

                        int matIndex = MaterialIndex(MaterialTypeName(c));
                        List<int> tris = submeshes[matIndex] ?? (submeshes[matIndex] = new List<int>());
                        Vector2 texel = new Vector2((c + 0.5f) / 256f, 0.5f);

                        datas.map.v.Add(new Voxel((byte)x, (byte)y, (byte)z, c));

                        if (y == 0 || grid[x, y - 1, z] == 0)
                            AddFace(vertices, normals, uvs, tris, texel, Vector3.down,
                                new Vector3(x + 0, y + 0, z + 0) - pivot,
                                new Vector3(x + 1, y + 0, z + 0) - pivot,
                                new Vector3(x + 0, y + 0, z + 1) - pivot,
                                new Vector3(x + 1, y + 0, z + 1) - pivot);

                        if (y + 1 >= size.y || grid[x, y + 1, z] == 0)
                            AddFace(vertices, normals, uvs, tris, texel, Vector3.up,
                                new Vector3(x + 0, y + 1, z + 1) - pivot,
                                new Vector3(x + 1, y + 1, z + 1) - pivot,
                                new Vector3(x + 0, y + 1, z + 0) - pivot,
                                new Vector3(x + 1, y + 1, z + 0) - pivot);

                        if (x + 1 >= size.x || grid[x + 1, y, z] == 0)
                            AddFace(vertices, normals, uvs, tris, texel, Vector3.right,
                                new Vector3(x + 1, y + 1, z + 0) - pivot,
                                new Vector3(x + 1, y + 1, z + 1) - pivot,
                                new Vector3(x + 1, y + 0, z + 0) - pivot,
                                new Vector3(x + 1, y + 0, z + 1) - pivot);

                        if (x == 0 || grid[x - 1, y, z] == 0)
                            AddFace(vertices, normals, uvs, tris, texel, Vector3.left,
                                new Vector3(x + 0, y + 0, z + 0) - pivot,
                                new Vector3(x + 0, y + 0, z + 1) - pivot,
                                new Vector3(x + 0, y + 1, z + 0) - pivot,
                                new Vector3(x + 0, y + 1, z + 1) - pivot);

                        if (z + 1 >= size.z || grid[x, y, z + 1] == 0)
                            AddFace(vertices, normals, uvs, tris, texel, Vector3.forward,
                                new Vector3(x + 0, y + 0, z + 1) - pivot,
                                new Vector3(x + 1, y + 0, z + 1) - pivot,
                                new Vector3(x + 0, y + 1, z + 1) - pivot,
                                new Vector3(x + 1, y + 1, z + 1) - pivot);

                        if (z == 0 || grid[x, y, z - 1] == 0)
                            AddFace(vertices, normals, uvs, tris, texel, Vector3.back,
                                new Vector3(x + 0, y + 1, z + 0) - pivot,
                                new Vector3(x + 1, y + 1, z + 0) - pivot,
                                new Vector3(x + 0, y + 0, z + 0) - pivot,
                                new Vector3(x + 1, y + 0, z + 0) - pivot);
                    }
                }

                var mesh = new Mesh { name = fileName + "_mesh_" + i };
                mesh.indexFormat = vertices.Count > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
                mesh.SetVertices(vertices);

                var usedMaterials = new List<Material>();
                mesh.subMeshCount = 4;
                int submeshCursor = 0;
                for (int j = 0; j < 4; j++)
                {
                    if (submeshes[j] == null || submeshes[j].Count == 0) continue;
                    mesh.SetTriangles(submeshes[j], submeshCursor);
                    usedMaterials.Add(_materials[j] != null ? _materials[j] : _materials[0]);
                    submeshCursor++;
                }
                mesh.subMeshCount = submeshCursor;
                mesh.SetNormals(normals);
                mesh.SetUVs(0, uvs);
                mesh.RecalculateBounds();

                if (buildModel)
                {
                    ctx.AddObjectToAsset("mesh_" + i, mesh);

                    MeshFilter mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = mesh;

                    MeshRenderer mr = go.AddComponent<MeshRenderer>();
                    if (embedMaterials)
                        foreach (Material m in usedMaterials)
                            RegisterMaterial(ctx, m);
                    mr.sharedMaterials = usedMaterials.ToArray();
                }
                else
                {
                    DestroyImmediate(mesh);
                }

                if (buildScriptableObjects)
                {
                    ctx.AddObjectToAsset("datas_" + i, datas);
                    ctx.AddObjectToAsset("palette_" + i, datas.palette);
                    _createdDatas.Add(datas);
                }
                else
                {
                    DestroyImmediate(datas.palette);
                    DestroyImmediate(datas);
                }
            }

            if (buildMaterial)
                for (int j = 0; j < _materials.Length; j++)
                    RegisterMaterial(ctx, _materials[j]);

            for (int j = 0; j < _materials.Length; j++)
                if (_materials[j] != null && !_addedMaterials.Contains(_materials[j]))
                {
                    DestroyImmediate(_materials[j]);
                    _materials[j] = null;
                }

            if (!_paletteTextureAdded && _paletteTexture != null)
            {
                DestroyImmediate(_paletteTexture);
                _paletteTexture = null;
            }
        }

        private static void AddFace(
            List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles,
            Vector2 texel, Vector3 normal, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int baseIndex = vertices.Count;

            vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
            normals.Add(normal); normals.Add(normal); normals.Add(normal); normals.Add(normal);
            uvs.Add(texel); uvs.Add(texel); uvs.Add(texel); uvs.Add(texel);

            triangles.Add(baseIndex + 0);
            triangles.Add(baseIndex + 1);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 3);
            triangles.Add(baseIndex + 2);
            triangles.Add(baseIndex + 1);
        }

        private void NodeHierarchy(AssetImportContext ctx, string fileName, Dictionary<int, VoxNode> nodes,
            GameObject parent, int nodeId, HashSet<int> visited)
        {
            if (parent == null) return;
            if (!nodes.TryGetValue(nodeId, out VoxNode node)) return;
            if (!visited.Add(nodeId)) return;

            string label = node.GetAttribute("_name");
            if (string.IsNullOrEmpty(label)) label = node.GetType().Name;

            var go = new GameObject(label + " #" + nodeId);
            go.transform.SetParent(parent.transform, false);
            go.transform.localPosition = Vector3.zero;

            switch (node)
            {
                case VoxTransformNode trn:
                    if (trn.pose != null && TryParseTranslation(trn.pose.GetValue("_t"), out Vector3Int translation))
                        go.transform.localPosition = translation;
                    if (trn.numFrames > 1 && buildAnimation)
                        ctx.LogImportWarning(
                            $"'{fileName}': node #{nodeId} has {trn.numFrames} transform keyframes; " +
                            "transform (move/rotate) animation is not imported yet, only frame-by-frame shape animation.");
                    NodeHierarchy(ctx, fileName, nodes, go, trn.childNodeId, visited);
                    break;

                case VoxGroupNode grp:
                    if (grp.children != null)
                        for (int i = 0; i < grp.children.Length; i++)
                            NodeHierarchy(ctx, fileName, nodes, go, grp.children[i], visited);
                    break;

                case VoxShapeNode shp:
                    BuildShape(ctx, fileName, go, nodeId, shp);
                    break;
            }
        }

        /// <summary>
        /// A shape node references one model, or - for a frame-by-frame animation -
        /// several models each tagged with an "_f" frame index.
        /// </summary>
        private void BuildShape(AssetImportContext ctx, string fileName, GameObject shapeGo, int nodeId, VoxShapeNode shp)
        {
            List<KeyValuePair<int, int>> frames = ExtractFrames(shp); // (frameIndex, modelId), sorted, deduped
            if (frames.Count == 0) return;

            int valid = 0;
            foreach (var f in frames)
                if (f.Value >= 0 && f.Value < _models.Count && _models[f.Value] != null) valid++;

            bool animated = buildAnimation && buildModel && frames.Count > 1 && valid > 1;

            if (animated)
            {
                try
                {
                    BuildAnimatedShape(ctx, fileName, shapeGo, nodeId, frames);
                    return;
                }
                catch (Exception e)
                {
                    ctx.LogImportWarning(
                        $"'{fileName}': failed to build the frame animation ({e.GetType().Name}: {e.Message}); " +
                        "importing the first frame as a static model.");
                }
            }

            AttachModel(shapeGo, frames[0].Value);
            for (int k = 1; k < frames.Count; k++)
                DiscardModel(frames[k].Value);
        }

        private void AttachModel(GameObject parent, int modelId)
        {
            if (modelId < 0 || modelId >= _models.Count || _models[modelId] == null) return;
            _models[modelId].transform.SetParent(parent.transform, false);
            _models[modelId].transform.localPosition = Vector3.zero;
        }

        private void DiscardModel(int modelId)
        {
            if (modelId < 0 || modelId >= _models.Count || _models[modelId] == null) return;
            DestroyImmediate(_models[modelId]);
            _models[modelId] = null;
        }

        private static List<KeyValuePair<int, int>> ExtractFrames(VoxShapeNode shp)
        {
            var list = new List<KeyValuePair<int, int>>();
            if (shp.modelRefs == null) return list;

            for (int i = 0; i < shp.modelRefs.Length; i++)
            {
                int modelId = shp.modelRefs[i].Key;
                int frame = i;
                string fs = shp.modelRefs[i].Value != null ? shp.modelRefs[i].Value.GetValue("_f") : null;
                if (!string.IsNullOrEmpty(fs) &&
                    int.TryParse(fs, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                    frame = parsed;
                list.Add(new KeyValuePair<int, int>(frame, modelId));
            }

            list.Sort((a, b) => a.Key.CompareTo(b.Key));

            var result = new List<KeyValuePair<int, int>>();
            foreach (var kv in list)
            {
                if (result.Count > 0 && result[result.Count - 1].Key == kv.Key)
                    result[result.Count - 1] = kv;
                else
                    result.Add(kv);
            }
            return result;
        }

        private void BuildAnimatedShape(AssetImportContext ctx, string fileName, GameObject shapeGo, int nodeId,
            List<KeyValuePair<int, int>> frames)
        {
            float fps = Mathf.Clamp(animationFramesPerSecond, 1, 60);
            int n = frames.Count;

            var childNames = new string[n];
            var childObjects = new GameObject[n];
            for (int k = 0; k < n; k++)
            {
                int modelId = frames[k].Value;
                GameObject frameGo = (modelId >= 0 && modelId < _models.Count) ? _models[modelId] : null;
                if (frameGo == null) continue;

                frameGo.name = "frame_" + frames[k].Key;
                frameGo.transform.SetParent(shapeGo.transform, false);
                frameGo.transform.localPosition = Vector3.zero;
                frameGo.SetActive(k == 0);
                childNames[k] = frameGo.name;
                childObjects[k] = frameGo;
            }

            var times = new float[n + 1];
            for (int k = 0; k < n; k++) times[k] = frames[k].Key / fps;
            times[n] = (frames[n - 1].Key + 1) / fps;

            var clip = new AnimationClip { name = fileName + "_anim", frameRate = fps };
            for (int k = 0; k < n; k++)
            {
                if (childNames[k] == null) continue;

                var curve = new AnimationCurve();
                for (int b = 0; b <= n; b++)
                {
                    bool on = (b == k) || (b == n && k == 0);
                    curve.AddKey(new Keyframe(times[b], on ? 1f : 0f, float.PositiveInfinity, float.PositiveInfinity));
                }

                var binding = new EditorCurveBinding
                {
                    path = childNames[k],
                    type = typeof(GameObject),
                    propertyName = "m_IsActive"
                };
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            ctx.AddObjectToAsset("clip_" + nodeId, clip);
            _animationCount++;

            AnimatorController controller = TryBuildController(ctx, fileName + "_controller_" + nodeId, nodeId, clip);
            if (controller != null)
            {
                Animator animator = shapeGo.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
                animator.applyRootMotion = false;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            else
            {
                var player = shapeGo.AddComponent<VoxFrameAnimator>();
                player.framesPerSecond = fps;
                player.loop = true;

                var used = new List<GameObject>();
                foreach (GameObject c in childObjects)
                    if (c != null) used.Add(c);
                player.frames = used.ToArray();
            }
        }

        private AnimatorController TryBuildController(AssetImportContext ctx, string name, int nodeId, AnimationClip clip)
        {
            try
            {
                var controller = new AnimatorController { name = name };
                controller.AddLayer("Base Layer");

                AnimatorStateMachine sm = controller.layers[0].stateMachine;
                sm.name = "Base Layer";
                sm.hideFlags = HideFlags.HideInHierarchy;

                AnimatorState state = sm.AddState(clip.name);
                state.motion = clip;
                state.writeDefaultValues = true;
                state.hideFlags = HideFlags.HideInHierarchy;
                sm.defaultState = state;

                ctx.AddObjectToAsset("animator_controller_" + nodeId, controller);
                ctx.AddObjectToAsset("animator_sm_" + nodeId, sm);
                ctx.AddObjectToAsset("animator_state_" + nodeId, state);
                return controller;
            }
            catch (Exception e)
            {
                ctx.LogImportWarning(
                    $"Could not embed an AnimatorController ({e.GetType().Name}: {e.Message}); " +
                    "using a runtime frame player instead. The AnimationClip is still available.");
                return null;
            }
        }

        private static bool TryParseTranslation(string value, out Vector3Int result)
        {
            result = Vector3Int.zero;
            if (string.IsNullOrEmpty(value)) return false;

            string[] parts = value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;

            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z)) return false;
            if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int y)) return false;

            result = new Vector3Int(x, y, z);
            return true;
        }

        private Material[] LoadMaterials()
        {
            var mats = new Material[4];
            for (int i = 0; i < 4; i++)
            {
                Material template = Resources.Load<Material>("Materials/" + MaterialTypeNames[i]);
                mats[i] = template != null
                    ? new Material(template) { name = "vox" + MaterialTypeNames[i] }
                    : CreateFallbackMaterial(i);
            }
            return mats;
        }

        private static Material CreateFallbackMaterial(int type)
        {
            Shader shader = FindFirstShader(
                "Universal Render Pipeline/Lit",
                "Standard",
                "Unlit/Texture",
                "Sprites/Default",
                "Hidden/InternalErrorShader");

            var mat = new Material(shader) { name = "vox" + MaterialTypeNames[type] };

            switch (type)
            {
                case 1: // metal
                    TrySetFloat(mat, "_Metallic", 1f);
                    TrySetFloat(mat, "_Glossiness", 0.75f);
                    TrySetFloat(mat, "_Smoothness", 0.75f);
                    break;
                case 2: // glass
                    MakeTransparent(mat);
                    break;
                case 3: // emit
                    if (mat.HasProperty("_EmissionColor"))
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetColor("_EmissionColor", Color.white);
                        mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
                    }
                    break;
            }
            return mat;
        }

        private static Shader FindFirstShader(params string[] names)
        {
            foreach (string n in names)
            {
                Shader s = Shader.Find(n);
                if (s != null) return s;
            }
            return null;
        }

        private static void TrySetFloat(Material mat, string prop, float value)
        {
            if (mat.HasProperty(prop)) mat.SetFloat(prop, value);
        }

        private static void MakeTransparent(Material mat)
        {
            TrySetFloat(mat, "_Mode", 3f);    // Standard: Transparent
            TrySetFloat(mat, "_Surface", 1f); // URP Lit: Transparent
            if (mat.HasProperty("_SrcBlend")) mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (mat.HasProperty("_DstBlend")) mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            if (mat.HasProperty("_ZWrite")) mat.SetFloat("_ZWrite", 0f);
            mat.DisableKeyword("_ALPHATEST_ON");
            mat.EnableKeyword("_ALPHABLEND_ON");
            mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            mat.renderQueue = (int)RenderQueue.Transparent;
        }

        private void RegisterMaterial(AssetImportContext ctx, Material material)
        {
            if (material == null) return;
            if (!_addedMaterials.Add(material)) return;
            ctx.AddObjectToAsset("material_" + material.name, material);
        }

        private string MaterialTypeName(int paletteIndex)
        {
            if (paletteIndex < 0 || paletteIndex >= _materialProps.Length) return "_diffuse";
            VoxDict props = _materialProps[paletteIndex];
            if (props == null) return "_diffuse";
            string type = props.GetValue("_type");
            return string.IsNullOrEmpty(type) ? "_diffuse" : type;
        }

        private static int MaterialIndex(string type)
        {
            switch (type)
            {
                case "_metal": return 1;
                case "_glass": return 2;
                case "_emit": return 3;
                default: return 0;
            }
        }

        private static VoxPalette.MaterialType ToMaterialType(string type)
        {
            switch (type)
            {
                case "_metal": return VoxPalette.MaterialType.METAL;
                case "_glass": return VoxPalette.MaterialType.GLASS;
                case "_emit": return VoxPalette.MaterialType.EMIT;
                default: return VoxPalette.MaterialType.DIFFUSE;
            }
        }

        /// <summary>
        /// Well-formed .vox files always carry their own RGBA palette. This is only a
        /// visible-but-approximate stand-in for the rare file that omits it: a 6x6x6
        /// RGB cube followed by a grayscale ramp. Index 0 is always transparent.
        /// </summary>
        private static Color32[] BuildFallbackPalette()
        {
            var palette = new Color32[256];
            palette[0] = new Color32(0, 0, 0, 0);

            int i = 1;
            for (int r = 0; r < 6 && i < 256; r++)
            for (int g = 0; g < 6 && i < 256; g++)
            for (int b = 0; b < 6 && i < 256; b++)
                palette[i++] = new Color32((byte)(r * 51), (byte)(g * 51), (byte)(b * 51), 255);

            for (int step = 0; i < 256; step++)
            {
                byte v = (byte)Mathf.Clamp(8 + step * 6, 0, 255);
                palette[i++] = new Color32(v, v, v, 255);
            }
            return palette;
        }

        private void FinishAsset(AssetImportContext ctx, GameObject voxContainer)
        {
            if (_mainObjectSet) return;

            if (buildModel)
            {
                // Adopt any model that no shape node referenced (or files with no scene graph).
                foreach (GameObject m in _models)
                    if (m != null && m.transform.parent == null)
                        m.transform.SetParent(voxContainer.transform, false);

                ctx.AddObjectToAsset("root", voxContainer);
                ctx.SetMainObject(voxContainer);
                _mainObjectSet = true;
                return;
            }

            UnityEngine.Object main = _createdDatas.Count > 0 ? _createdDatas[0] : null;
            if (main == null && _paletteTextureAdded) main = _paletteTexture;

            foreach (GameObject m in _models)
                if (m != null) DestroyImmediate(m);

            if (main != null)
            {
                DestroyImmediate(voxContainer);
                ctx.SetMainObject(main);
            }
            else
            {
                ctx.AddObjectToAsset("root", voxContainer);
                ctx.SetMainObject(voxContainer);
            }
            _mainObjectSet = true;
        }

        private void FinishAsEmpty(AssetImportContext ctx)
        {
            if (_mainObjectSet) return;
            var placeholder = new GameObject("vox (import failed)");
            ctx.AddObjectToAsset("root", placeholder);
            ctx.SetMainObject(placeholder);
            _mainObjectSet = true;
        }
    }

    internal static class VoxIO
    {
        public static string ReadFourCC(BinaryReader reader)
        {
            byte[] b = reader.ReadBytes(4);
            return b.Length == 4 ? Encoding.ASCII.GetString(b) : string.Empty;
        }

        public static long BytesRemaining(BinaryReader reader)
        {
            return reader.BaseStream.Length - reader.BaseStream.Position;
        }

        /// <summary>
        /// Clamp a length/count read from the file so a corrupt value can never make
        /// us allocate a huge array or read past the end of the stream.
        /// </summary>
        public static int SafeCount(int declared, long bytesRemaining, int minBytesPerItem, int hardCap)
        {
            if (declared <= 0) return 0;
            long limit = declared;
            if (minBytesPerItem > 0)
                limit = Math.Min(limit, bytesRemaining / minBytesPerItem);
            limit = Math.Min(limit, hardCap);
            return limit < 0 ? 0 : (int)limit;
        }
    }

    internal class VoxChunk
    {
        public string id;
        public int contentSize;
        public int childrenSize;

        public void ReadChunkHeader(BinaryReader reader)
        {
            id = VoxIO.ReadFourCC(reader);
            contentSize = reader.ReadInt32();
            childrenSize = reader.ReadInt32();
        }
    }

    internal class VoxNode
    {
        public int id;
        public VoxDict attributes;

        public virtual void Read(BinaryReader reader)
        {
            id = reader.ReadInt32();
            attributes = new VoxDict();
            attributes.Read(reader);
        }

        public string GetAttribute(string key)
        {
            return attributes != null ? attributes.GetValue(key) : null;
        }
    }

    internal class VoxTransformNode : VoxNode
    {
        public int childNodeId;
        public int reservedId;
        public int layerId;
        public int numFrames;
        public VoxDict pose;
        public VoxDict[] frames;

        public override void Read(BinaryReader reader)
        {
            base.Read(reader);

            childNodeId = reader.ReadInt32();
            reservedId = reader.ReadInt32();
            layerId = reader.ReadInt32();
            numFrames = reader.ReadInt32();

            int safeFrames = VoxIO.SafeCount(numFrames, VoxIO.BytesRemaining(reader), 4, 4096);
            frames = new VoxDict[safeFrames];
            for (int i = 0; i < safeFrames; i++)
            {
                frames[i] = new VoxDict();
                frames[i].Read(reader);
            }
            numFrames = safeFrames;
            pose = safeFrames > 0 ? frames[0] : new VoxDict();
        }
    }

    internal class VoxGroupNode : VoxNode
    {
        public int numChildren;
        public int[] children;

        public override void Read(BinaryReader reader)
        {
            base.Read(reader);

            numChildren = reader.ReadInt32();
            int safe = VoxIO.SafeCount(numChildren, VoxIO.BytesRemaining(reader), 4, 1 << 20);
            children = new int[safe];
            for (int i = 0; i < safe; i++)
                children[i] = reader.ReadInt32();
            numChildren = safe;
        }
    }

    internal class VoxShapeNode : VoxNode
    {
        public int numModels;
        public KeyValuePair<int, VoxDict>[] modelRefs;

        public override void Read(BinaryReader reader)
        {
            base.Read(reader);

            numModels = reader.ReadInt32();
            int safe = VoxIO.SafeCount(numModels, VoxIO.BytesRemaining(reader), 8, 4096);
            modelRefs = new KeyValuePair<int, VoxDict>[safe];
            for (int i = 0; i < safe; i++)
            {
                int modelId = reader.ReadInt32();
                var modelAttrs = new VoxDict();
                modelAttrs.Read(reader);
                modelRefs[i] = new KeyValuePair<int, VoxDict>(modelId, modelAttrs);
            }
            numModels = safe;
        }
    }

    internal class VoxLayerNode : VoxNode
    {
        public int reservedId;

        public override void Read(BinaryReader reader)
        {
            base.Read(reader);
            reservedId = reader.ReadInt32();
        }
    }

    internal class VoxString
    {
        public int size;
        public string value = string.Empty;

        public void Read(BinaryReader reader)
        {
            size = reader.ReadInt32();
            if (size <= 0)
            {
                size = 0;
                value = string.Empty;
                return;
            }

            long remaining = VoxIO.BytesRemaining(reader);
            if (size > remaining) size = (int)Math.Max(0, remaining);

            value = Encoding.UTF8.GetString(reader.ReadBytes(size));
        }

        public override string ToString()
        {
            return value;
        }
    }

    internal class VoxDict
    {
        public int count;
        public KeyValuePair<VoxString, VoxString>[] entries = new KeyValuePair<VoxString, VoxString>[0];

        public void Read(BinaryReader reader)
        {
            count = reader.ReadInt32();
            // Every entry is at least 8 bytes (two int32 length prefixes).
            count = VoxIO.SafeCount(count, VoxIO.BytesRemaining(reader), 8, 1 << 16);

            entries = new KeyValuePair<VoxString, VoxString>[count];
            for (int i = 0; i < count; i++)
            {
                var key = new VoxString();
                key.Read(reader);
                var val = new VoxString();
                val.Read(reader);
                entries[i] = new KeyValuePair<VoxString, VoxString>(key, val);
            }
        }

        internal string GetValue(string key)
        {
            if (entries == null) return null;
            for (int i = 0; i < entries.Length; i++)
                if (entries[i].Key != null && entries[i].Key.ToString() == key)
                    return entries[i].Value != null ? entries[i].Value.ToString() : null;
            return null;
        }

        public override string ToString()
        {
            if (entries == null) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < entries.Length; i++)
                sb.Append(entries[i].Key).Append(" = ").Append(entries[i].Value).Append("\r\n");
            return sb.ToString();
        }
    }

    [CustomEditor(typeof(VoxImporter))]
    public class VoxImporterEditor : ScriptedImporterEditor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            EditorGUILayout.LabelField("MagicaVoxel (.vox) Import", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buildModel"),
                new GUIContent("Build 3D Models", "Create a GameObject hierarchy with one mesh per model."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buildPaletteTexture"),
                new GUIContent("Build Palette Texture", "Embed the 256x1 palette lookup texture."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buildMaterial"),
                new GUIContent("Instantiate Materials", "Embed generated diffuse / metal / glass / emit materials."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buildScriptableObjects"),
                new GUIContent("Build Voxel Data", "Embed VoxDatas / VoxPalette ScriptableObjects."));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Animation", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("buildAnimation"),
                new GUIContent("Build Animations",
                    "Turn a MagicaVoxel frame-by-frame shape into an AnimationClip + Animator that loops on its own."));

            using (new EditorGUI.DisabledScope(!serializedObject.FindProperty("buildAnimation").boolValue))
            {
                SerializedProperty fps = serializedObject.FindProperty("animationFramesPerSecond");
                EditorGUILayout.IntSlider(fps, 1, 60, new GUIContent("Frames Per Second", "Playback speed of imported frame animations."));
            }

            serializedObject.ApplyModifiedProperties();
            ApplyRevertGUI();
        }
    }
}
