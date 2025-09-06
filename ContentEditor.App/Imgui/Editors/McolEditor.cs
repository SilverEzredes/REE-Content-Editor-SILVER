using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using Assimp;
using ContentEditor.App.Windowing;
using ContentEditor.Core;
using ContentPatcher;
using ImGuiNET;
using ReeLib;
using ReeLib.Bvh;
using ReeLib.UVar;

namespace ContentEditor.App.ImguiHandling;

public class McolEditor : FileEditor, IWorkspaceContainer, IObjectUIHandler
{
    public override string HandlerName => "CollisionMesh";

    public RszInstance? Instance { get; private set; }
    public string Filename => Handle.Filepath;
    public McolFile File => Handle.GetFile<McolFile>();

    public ContentWorkspace Workspace { get; }

    protected override bool IsRevertable => context.Changed;

    public McolEditor(ContentWorkspace env, FileHandle file) : base (file)
    {
        Workspace = env;
    }

    protected override void OnFileReverted()
    {
        Reset();
    }

    private void Reset()
    {
        if (context.children.Count > 0) {
            context.children.Clear();
        }
        failedToReadfile = false;
    }

    private string? lastFilepath;

    protected override void DrawFileContents()
    {
        if (context.children.Count == 0) {
            // I think shapes werent't supported for earlier games (re7?)
            context.AddChild<McolFile, List<(string name1, string name2)>>("Layers", File, getter: (f) => f!.bvh!.stringTable!).AddDefaultHandler();

            context.AddChild<McolFile, List<BvhSphere>>("Spheres", File, getter: (f) => f!.bvh!.spheres).AddDefaultHandler();
            context.AddChild<McolFile, List<BvhCapsule>>("Capsules", File, getter: (f) => f!.bvh!.capsules).AddDefaultHandler();
            context.AddChild<McolFile, List<BvhOBB>>("Boxes", File, getter: (f) => f!.bvh!.boxes).AddDefaultHandler();

            context.AddChild<McolFile, List<BvhTriangle>>("Raw triangles", File, getter: (f) => f!.bvh!.triangles).AddDefaultHandler();
            context.AddChild<McolFile, List<Vector3>>("Raw vertices", File, getter: (f) => f!.bvh!.vertices).AddDefaultHandler();
        }

        var window = EditorWindow.CurrentWindow!;
        if (ImGui.Button("Export to mesh ...")) {
            PlatformUtils.ShowSaveFileDialog((fn) => {
                lastFilepath = fn;
                window.InvokeFromUIThread(() => {
                    ExportMcolToGlb(File, fn);
                });
            }, lastFilepath ?? Handle.Filename.ToString(), "GLB (*.glb)|*.glb");
        }
        ImGui.SameLine();
        if (ImGui.Button("Import mesh ...")) {
            PlatformUtils.ShowFileDialog((files) => {
                var fn = files[0];
                lastFilepath = fn;
                window.InvokeFromUIThread(() => {
                    ImportGlbIntoMcol(File, fn);
                    context.ClearChildren();
                });
            }, lastFilepath ?? Handle.Filename.ToString(), "GLB (*.glb)|*.glb");
        }

        ImGui.Spacing();
        context.children[0].ShowUI();
        ImGui.Spacing();
        ImGui.Spacing();
        for (int i = 1; i < context.children.Count; ++i) {
            context.children[i].ShowUI();
        }
    }

    void IObjectUIHandler.OnIMGUI(UIContext container)
    {
        this.OnIMGUI();
    }

    private static readonly Color[] LayerColors = [
        Color.White, Color.Blue, Color.Green, Color.Red, Color.Magenta,
        Color.Yellow, Color.AliceBlue, Color.AntiqueWhite, Color.Aqua, Color.Aquamarine,
        Color.Beige, Color.Bisque, Color.BlanchedAlmond, Color.BlueViolet, Color.Brown,
        Color.BurlyWood, Color.CadetBlue, Color.Chartreuse, Color.Chocolate, Color.Coral,
        Color.CornflowerBlue, Color.Cornsilk, Color.Crimson, Color.Cyan, Color.DarkBlue,
        Color.DarkCyan, Color.DarkGoldenrod, Color.DarkGray, Color.DarkGreen, Color.DarkKhaki,
        Color.DarkMagenta, Color.DarkOliveGreen, Color.DarkOrange, Color.DarkOrchid, Color.DarkRed,
        Color.DarkSalmon, Color.DarkSeaGreen
    ];

    private const float MaxPartId = 256;

    public static void ExportMcolToGlb(McolFile file, string outputFilepath)
    {
        var scene = GetMeshScene(file);
        if (scene == null) {
            Logger.Error("Selected .mcol file has no triangles, can't export.");
            return;
        }

        using var ctx = new AssimpContext();
        ctx.ExportFile(scene, outputFilepath, "glb2");
    }

    public static bool ImportGlbIntoMcol(McolFile file, string meshFilepath)
    {
        using var ctx = new AssimpContext();
        var scene = ctx.ImportFile(meshFilepath);
        return LoadTrianglesFromScene(file, scene);
    }

    public static Assimp.Scene? GetMeshScene(McolFile file)
    {
        if (!(file.bvh?.vertices.Count > 0)) {
            return null;
        }

        var bvh = file.bvh;

        var scene = new Assimp.Scene();
        scene.RootNode = new Node() { Transform = Matrix4x4.Identity };
        for (int i = 0; i < bvh.stringTable.Count; ++i) {

            var mat = new Material();
            scene.Materials.Add(mat);
            mat.Name = LayerToMaterialName(i, bvh.stringTable[i].main);
            mat.ColorDiffuse = ImGui.ColorConvertU32ToFloat4((uint)LayerColors[i].ToArgb());

            var mesh = new Assimp.Mesh("mesh" + i, PrimitiveType.Triangle);
            mesh.MaterialIndex = i;
            var uv0 = mesh.TextureCoordinateChannels[0];
            mesh.UVComponentCount[0] = 2;
            var col = mesh.VertexColorChannels[0];
            int index = 0;
            var bounds = new ReeLib.via.AABB();
            foreach (var tri in bvh.triangles) {
                if (tri.info.layerIndex != i) continue;
                var uv = new Vector3(tri.info.partId / MaxPartId, 1, 0);
                var maskCol = ImGui.ColorConvertU32ToFloat4(tri.info.mask == 0 ? uint.MaxValue : tri.info.mask);

                mesh.Vertices.Add(bvh.vertices[tri.posIndex1]);
                mesh.Vertices.Add(bvh.vertices[tri.posIndex2]);
                mesh.Vertices.Add(bvh.vertices[tri.posIndex3]);
                mesh.Faces.Add(new Face([index++, index++, index++]));
                uv0.Add(uv);
                uv0.Add(uv);
                uv0.Add(uv);
                col.Add(maskCol);
                col.Add(maskCol);
                col.Add(maskCol);

                bounds = bounds.Extend(bvh.vertices[tri.posIndex1]).Extend(bvh.vertices[tri.posIndex2]).Extend(bvh.vertices[tri.posIndex3]);
            }

            mesh.BoundingBox = new BoundingBox(bounds.minpos, bounds.maxpos);

            // don't add empty meshes
            if (index == 0) continue;

            scene.Meshes.Add(mesh);
            scene.RootNode.MeshIndices.Add(scene.Meshes.Count - 1);
        }

        return scene;
    }

    public static bool LoadTrianglesFromScene(McolFile file, Assimp.Scene scene)
    {
        var bvh = file.bvh!;
        bvh.tree ??= new BvhTree();
        var tree = bvh.tree;
        bvh.triangles.Clear();
        bvh.vertices.Clear();

        var unsetEdgeIndex = file.FileHandler.FileVersion <= 3017 ? 0 : -1;

        foreach (var mesh in scene.Meshes) {
            if (mesh.Vertices.Count == 0 || mesh.FaceCount == 0) continue;

            var mat = scene.Materials[mesh.MaterialIndex];
            var layerIndex = GetLayerIndexFromMaterialName(mat.Name);

            var vertsOffset = bvh.vertices.Count; // this should let us seamlessly handle multi-surface meshes
            var colors = mesh.VertexColorChannels[0];
            var uvs = mesh.TextureCoordinateChannels[0];

            bvh.vertices.AddRange(mesh.Vertices);

            foreach (var face in mesh.Faces) {
                var vert1 = face.Indices[0];
                var vert2 = face.Indices[1];
                var vert3 = face.Indices[2];

                var indexData = new BvhTriangle() {
                    posIndex1 = vert1 + vertsOffset,
                    posIndex2 = vert2 + vertsOffset,
                    posIndex3 = vert3 + vertsOffset,
                    edgeIndex1 = unsetEdgeIndex,
                    edgeIndex2 = unsetEdgeIndex,
                    edgeIndex3 = unsetEdgeIndex,
                };

                // NOTE: edges ignored for now, because I can't get them right and they don't seem to make a difference either
                indexData.info.mask = colors != null ? ImGui.ColorConvertFloat4ToU32(colors[vert1]) : uint.MaxValue;
                indexData.info.layerIndex = layerIndex;
                indexData.info.partId = (int)MathF.Round(uvs[vert1].X * MaxPartId);
                bvh.AddTriangle(indexData);
            }
        }

        return true;
    }

    private const string LayerNameDescSeparator = "___";
    private static string LayerToMaterialName(int index, string description)
    {
        var matname = "Layer" + index + LayerNameDescSeparator + description;
        if (matname.Length > 63) {
            // blender object name limit
            matname = matname.Substring(0, 63);
        }
        return matname;
    }

    private static int GetLayerIndexFromMaterialName(string name)
    {
        var id = name.IndexOf(LayerNameDescSeparator);
        if (id == -1) {
            throw new Exception("Unsupported mcol material - material name does not meet the Layer##__ requirement");
        }
        return id == -1 ? 0 : int.Parse(name.AsSpan()[5..id]);
    }
}
