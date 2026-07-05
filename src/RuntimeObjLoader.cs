using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.Rendering;

namespace SP2ReferenceModel
{
    internal sealed class RuntimeObjLoader
    {
        private readonly ManualLogSource _log;
        private readonly bool _swapYz;
        private readonly List<Vector3> _positions = new List<Vector3>();
        private readonly List<Vector2> _uvs = new List<Vector2>();
        private readonly List<Vector3> _normals = new List<Vector3>();
        private readonly List<ObjGroup> _groups = new List<ObjGroup>();
        private readonly Dictionary<string, ObjMaterial> _materials = new Dictionary<string, ObjMaterial>(StringComparer.OrdinalIgnoreCase);
        private ObjGroup _group;
        private string _materialName = "default";
        private string _objDirectory;

        public readonly List<GameObject> MeshObjects = new List<GameObject>();
        public int VertexCount { get; private set; }

        public RuntimeObjLoader(ManualLogSource log, bool swapYz)
        {
            _log = log;
            _swapYz = swapYz;
        }

        public GameObject Load(string path)
        {
            _objDirectory = Path.GetDirectoryName(path) ?? "";
            _group = NewGroup(Path.GetFileNameWithoutExtension(path));
            ParseObj(path);

            GameObject root = new GameObject("OBJ Reference Root");
            root.hideFlags = HideFlags.DontSave;
            Dictionary<string, Material> unityMaterials = BuildMaterials();

            foreach (ObjGroup group in _groups.Where(g => g.TrianglesByMaterial.Values.Any(v => v.Count > 0)))
            {
                GameObject child = BuildGroup(group, unityMaterials);
                child.transform.SetParent(root.transform, false);
                MeshObjects.Add(child);
            }

            if (MeshObjects.Count == 0)
            {
                UnityEngine.Object.Destroy(root);
                throw new InvalidDataException("The OBJ contained no usable faces.");
            }

            return root;
        }

        private void ParseObj(string path)
        {
            int lineNumber = 0;
            foreach (string raw in File.ReadLines(path))
            {
                lineNumber++;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int split = line.IndexOfAny(new[] { ' ', '\t' });
                string command = split < 0 ? line : line.Substring(0, split);
                string args = split < 0 ? "" : line.Substring(split + 1).Trim();

                try
                {
                    switch (command)
                    {
                        case "v": _positions.Add(ParseVector3(args, true)); break;
                        case "vt": _uvs.Add(ParseVector2(args)); break;
                        case "vn": _normals.Add(ParseVector3(args, false).normalized); break;
                        case "f": ParseFace(args); break;
                        case "o":
                        case "g":
                            if (!string.IsNullOrWhiteSpace(args)) _group = NewGroup(args);
                            break;
                        case "usemtl": _materialName = string.IsNullOrWhiteSpace(args) ? "default" : args; break;
                        case "mtllib": LoadMaterialLibraries(args); break;
                    }
                }
                catch (Exception ex)
                {
                    throw new InvalidDataException("OBJ line " + lineNumber + ": " + ex.Message, ex);
                }
            }
        }

        private ObjGroup NewGroup(string name)
        {
            string clean = string.IsNullOrWhiteSpace(name) ? "unnamed" : name.Trim();
            string unique = clean;
            int suffix = 2;
            while (_groups.Any(g => string.Equals(g.Name, unique, StringComparison.OrdinalIgnoreCase)))
            {
                unique = clean + " (" + suffix++ + ")";
            }
            ObjGroup group = new ObjGroup(unique);
            _groups.Add(group);
            return group;
        }

        private void ParseFace(string args)
        {
            string[] tokens = SplitWhitespace(args);
            if (tokens.Length < 3) return;
            ObjIndex[] face = tokens.Select(ParseIndex).ToArray();
            List<ObjIndex> target = _group.GetTriangles(_materialName);
            for (int i = 1; i < face.Length - 1; i++)
            {
                target.Add(face[0]);
                if (_swapYz)
                {
                    target.Add(face[i + 1]);
                    target.Add(face[i]);
                }
                else
                {
                    target.Add(face[i]);
                    target.Add(face[i + 1]);
                }
            }
        }

        private ObjIndex ParseIndex(string token)
        {
            string[] pieces = token.Split('/');
            int p = ResolveIndex(ParseInt(pieces[0]), _positions.Count);
            int uv = pieces.Length > 1 && pieces[1].Length > 0 ? ResolveIndex(ParseInt(pieces[1]), _uvs.Count) : -1;
            int n = pieces.Length > 2 && pieces[2].Length > 0 ? ResolveIndex(ParseInt(pieces[2]), _normals.Count) : -1;
            if (p < 0 || p >= _positions.Count) throw new InvalidDataException("Vertex index is out of range: " + token);
            return new ObjIndex(p, uv, n);
        }

        private static int ResolveIndex(int objIndex, int count)
        {
            return objIndex > 0 ? objIndex - 1 : count + objIndex;
        }

        private GameObject BuildGroup(ObjGroup group, Dictionary<string, Material> materials)
        {
            List<Vector3> vertices = new List<Vector3>();
            List<Vector2> texcoords = new List<Vector2>();
            List<Vector3> normals = new List<Vector3>();
            Dictionary<ObjIndex, int> vertexMap = new Dictionary<ObjIndex, int>();
            List<List<int>> submeshes = new List<List<int>>();
            List<Material> groupMaterials = new List<Material>();
            bool allNormalsPresent = true;

            foreach (KeyValuePair<string, List<ObjIndex>> pair in group.TrianglesByMaterial)
            {
                List<int> indices = new List<int>();
                foreach (ObjIndex index in pair.Value)
                {
                    if (!vertexMap.TryGetValue(index, out int mapped))
                    {
                        mapped = vertices.Count;
                        vertexMap[index] = mapped;
                        vertices.Add(_positions[index.Position]);
                        texcoords.Add(index.Uv >= 0 && index.Uv < _uvs.Count ? _uvs[index.Uv] : Vector2.zero);
                        if (index.Normal >= 0 && index.Normal < _normals.Count)
                        {
                            normals.Add(_normals[index.Normal]);
                        }
                        else
                        {
                            normals.Add(Vector3.zero);
                            allNormalsPresent = false;
                        }
                    }
                    indices.Add(mapped);
                }
                submeshes.Add(indices);
                groupMaterials.Add(materials.TryGetValue(pair.Key, out Material material) ? material : materials["default"]);
            }

            Mesh mesh = new Mesh { name = group.Name };
            if (vertices.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, texcoords);
            mesh.subMeshCount = submeshes.Count;
            for (int i = 0; i < submeshes.Count; i++) mesh.SetTriangles(submeshes[i], i, false);
            if (allNormalsPresent) mesh.SetNormals(normals); else mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            VertexCount += vertices.Count;

            GameObject child = new GameObject(group.Name);
            child.hideFlags = HideFlags.DontSave;
            MeshFilter filter = child.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            MeshRenderer renderer = child.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = groupMaterials.ToArray();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            return child;
        }

        private Dictionary<string, Material> BuildMaterials()
        {
            Dictionary<string, Material> result = new Dictionary<string, Material>(StringComparer.OrdinalIgnoreCase);
            result["default"] = CreateUnityMaterial(new ObjMaterial("default"));
            foreach (KeyValuePair<string, ObjMaterial> pair in _materials)
            {
                result[pair.Key] = CreateUnityMaterial(pair.Value);
            }
            return result;
        }

        private Material CreateUnityMaterial(ObjMaterial source)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard") ?? Shader.Find("Unlit/Texture");
            if (shader == null) throw new InvalidOperationException("No compatible Unity material shader was found.");
            Material material = new Material(shader) { name = source.Name, hideFlags = HideFlags.DontSave };
            Color color = new Color(source.Diffuse.x, source.Diffuse.y, source.Diffuse.z, source.Alpha);
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Color")) material.SetColor("_Color", color);
            if (material.HasProperty("_Cull")) material.SetFloat("_Cull", 0f);

            if (!string.IsNullOrWhiteSpace(source.DiffuseMap))
            {
                string texturePath = ResolveTexturePath(source.DiffuseMap);
                if (texturePath != null)
                {
                    try
                    {
                        byte[] data = File.ReadAllBytes(texturePath);
                        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = Path.GetFileName(texturePath), hideFlags = HideFlags.DontSave };
                        if (LoadTextureImage(texture, data))
                        {
                            if (material.HasProperty("_BaseMap")) material.SetTexture("_BaseMap", texture);
                            if (material.HasProperty("_MainTex")) material.SetTexture("_MainTex", texture);
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning("Could not load texture " + texturePath + ": " + ex.Message);
                    }
                }
                else
                {
                    _log.LogWarning("Texture referenced by MTL was not found: " + source.DiffuseMap);
                }
            }
            return material;
        }

        private static bool LoadTextureImage(Texture2D texture, byte[] data)
        {
            Type imageConversion = Type.GetType("UnityEngine.ImageConversion, UnityEngine.ImageConversionModule", false);
            if (imageConversion == null)
            {
                try
                {
                    imageConversion = Assembly.Load("UnityEngine.ImageConversionModule").GetType("UnityEngine.ImageConversion", false);
                }
                catch
                {
                    return false;
                }
            }

            MethodInfo load = imageConversion.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "LoadImage" &&
                                     m.GetParameters().Length >= 2 &&
                                     m.GetParameters()[0].ParameterType == typeof(Texture2D) &&
                                     m.GetParameters()[1].ParameterType == typeof(byte[]));
            if (load == null) return false;
            object[] args = load.GetParameters().Length == 2
                ? new object[] { texture, data }
                : new object[] { texture, data, false };
            object result = load.Invoke(null, args);
            return result is bool ok && ok;
        }

        private string ResolveTexturePath(string value)
        {
            string clean = value.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
            string direct = Path.IsPathRooted(clean) ? clean : Path.Combine(_objDirectory, clean);
            if (File.Exists(direct)) return direct;
            string name = Path.GetFileName(clean);
            return Directory.GetFiles(_objDirectory, name, SearchOption.AllDirectories).FirstOrDefault();
        }

        private void LoadMaterialLibraries(string args)
        {
            foreach (string library in SplitWhitespace(args))
            {
                string path = Path.Combine(_objDirectory, library.Trim('"'));
                if (File.Exists(path)) ParseMtl(path);
                else _log.LogWarning("MTL not found: " + path);
            }
        }

        private void ParseMtl(string path)
        {
            ObjMaterial current = null;
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int split = line.IndexOfAny(new[] { ' ', '\t' });
                string command = split < 0 ? line : line.Substring(0, split);
                string args = split < 0 ? "" : line.Substring(split + 1).Trim();
                switch (command)
                {
                    case "newmtl":
                        current = new ObjMaterial(args);
                        _materials[args] = current;
                        break;
                    case "Kd" when current != null: current.Diffuse = ParseVector3Raw(args); break;
                    case "d" when current != null: current.Alpha = ParseFloat(args); break;
                    case "Tr" when current != null: current.Alpha = 1f - ParseFloat(args); break;
                    case "map_Kd" when current != null: current.DiffuseMap = ParseMapPath(args); break;
                }
            }
        }

        private Vector3 ParseVector3(string value, bool position)
        {
            Vector3 v = ParseVector3Raw(value);
            if (!_swapYz) return v;
            return position ? new Vector3(v.x, v.z, v.y) : new Vector3(v.x, v.z, v.y);
        }

        private static Vector3 ParseVector3Raw(string value)
        {
            string[] p = SplitWhitespace(value);
            if (p.Length < 3) throw new FormatException("Expected three numbers.");
            return new Vector3(ParseFloat(p[0]), ParseFloat(p[1]), ParseFloat(p[2]));
        }

        private static Vector2 ParseVector2(string value)
        {
            string[] p = SplitWhitespace(value);
            if (p.Length < 2) throw new FormatException("Expected two numbers.");
            return new Vector2(ParseFloat(p[0]), ParseFloat(p[1]));
        }

        private static string ParseMapPath(string args)
        {
            string[] tokens = SplitWhitespace(args);
            if (tokens.Length == 0) return "";
            // Common MTL options precede the filename; exporters normally leave the path last.
            return tokens[tokens.Length - 1].Trim('"');
        }

        private static string[] SplitWhitespace(string value)
        {
            return (value ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static float ParseFloat(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static int ParseInt(string value)
        {
            return int.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
        }

        private sealed class ObjGroup
        {
            public readonly string Name;
            public readonly Dictionary<string, List<ObjIndex>> TrianglesByMaterial =
                new Dictionary<string, List<ObjIndex>>(StringComparer.OrdinalIgnoreCase);

            public ObjGroup(string name) { Name = name; }

            public List<ObjIndex> GetTriangles(string material)
            {
                if (!TrianglesByMaterial.TryGetValue(material, out List<ObjIndex> list))
                {
                    list = new List<ObjIndex>();
                    TrianglesByMaterial[material] = list;
                }
                return list;
            }
        }

        private sealed class ObjMaterial
        {
            public readonly string Name;
            public Vector3 Diffuse = Vector3.one;
            public float Alpha = 1f;
            public string DiffuseMap;
            public ObjMaterial(string name) { Name = string.IsNullOrWhiteSpace(name) ? "default" : name; }
        }

        private readonly struct ObjIndex : IEquatable<ObjIndex>
        {
            public readonly int Position;
            public readonly int Uv;
            public readonly int Normal;
            public ObjIndex(int position, int uv, int normal) { Position = position; Uv = uv; Normal = normal; }
            public bool Equals(ObjIndex other) => Position == other.Position && Uv == other.Uv && Normal == other.Normal;
            public override bool Equals(object obj) => obj is ObjIndex other && Equals(other);
            public override int GetHashCode()
            {
                unchecked { return ((Position * 397) ^ Uv) * 397 ^ Normal; }
            }
        }
    }
}
