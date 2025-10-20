using System.Numerics;
using ContentEditor.App.FileLoaders;
using ContentEditor.App.Graphics;
using ContentEditor.App.ImguiHandling;
using ContentEditor.App.ImguiHandling.Mesh;
using ContentEditor.App.Windowing;
using ContentEditor.Core;
using ContentEditor.Editor;
using ContentPatcher;
using ImGuiNET;
using ReeLib;
using Silk.NET.Maths;

namespace ContentEditor.App;

public class MeshViewer : IWindowHandler, IDisposable, IFocusableFileHandleReferenceHolder
{
    public bool HasUnsavedChanges => false;

    public string HandlerName => $"Mesh Viewer";

    public bool CanClose => true;
    public bool CanFocus => true;

    IRectWindow? IFileHandleReferenceHolder.Parent => data.ParentWindow;

    public ContentWorkspace Workspace { get; }
    private Scene? scene;
    private GameObject? previewGameobject;

    public Scene? Scene => scene;

    private const float TopMargin = 64;
    public const string MeshFilesFilter = "FBX (*.fbx)|*.fbx|GLB (*.glb)|*.glb|GLTF (*.gltf)|*.gltf";

    private string? loadedMdf;
    private string? mdfSource;
    private string? originalMDF;
    private UIContext? mdfPickerContext;

    private UIContext? animationPickerContext;
    private string animationSourceFile = "";
    private Animator? animator;
    private string motFilter = "";
    private string? loadedAnimationSource;
    public Animator? Animator => animator;

    private string exportTemplate;

    private CommonMeshResource? mesh;
    private string? meshPath;
    private FileHandle fileHandle;

    public CommonMeshResource? Mesh => mesh;

    private WindowData data = null!;
    protected UIContext context = null!;

    private bool isDragging;
    private const float pitchLimit = MathF.PI / 2 - 0.01f;
    private enum TextureMode
    {
        HighRes,
        LowRes
    }
    private bool isMDFUpdateRequest = false;
    private TextureMode textureMode = TextureMode.HighRes;

    private bool showAnimationsMenu = false;
    private bool isSynced;

    private bool exportAnimations = true;
    private bool exportCurrentAnimationOnly;
    private bool exportInProgress;

    private string? lastImportSourcePath;

    public MeshViewer(ContentWorkspace workspace, FileHandle file)
    {
        Workspace = workspace;
        ChangeMesh(fileHandle = file);

        exportTemplate = mesh?.NativeMesh.CurrentVersionConfig ?? MeshFile.AllVersionConfigs.Last();
    }

    private void TryGuessMdfFilepath()
    {
        if (!Workspace.Env.TryGetFileExtensionVersion("mdf2", out var mdfVersion)) {
            mdfVersion = -1;
        }

        var meshBasePath = PathUtils.GetFilepathWithoutExtensionOrVersion(fileHandle.Filepath);
        var mdfPath = meshBasePath.ToString() + ".mdf2";
        // if (Path.IsPathRooted(meshBasePath)) {
        //     mdfPath = Directory.EnumerateFiles(Path.GetDirectoryName(meshBasePath!).ToString(), Path.GetFileName(meshBasePath).ToString() + ".mdf2.*").FirstOrDefault();
        // } else {
        //     if (mdfVersion != -1) mdfPath += "." + mdfVersion;
        // }
        if (mdfVersion != -1) mdfPath += "." + mdfVersion;
        if (!File.Exists(mdfPath) && fileHandle.NativePath != null) {
            mdfPath = PathUtils.GetFilepathWithoutExtensionOrVersion(fileHandle.NativePath).ToString() + ".mdf2" + (mdfVersion == -1 ? "" : "." + mdfVersion);
        }
        this.mdfSource = mdfPath;
        this.originalMDF = mdfPath;
        loadedMdf = null;
    }

    public void Focus()
    {
        var data = context.Get<WindowData>();
        ImGui.SetWindowFocus(data.Name ?? $"{data.Handler}##{data.ID}");
    }

    public void Close()
    {
        var data = context.Get<WindowData>();
        EditorWindow.CurrentWindow?.CloseSubwindow(data);
    }

    public void Init(UIContext context)
    {
        this.context = context;
        data = context.Get<WindowData>();
    }

    public void ChangeMesh(string newMesh)
    {
        if (Workspace.ResourceManager.TryResolveFile(newMesh, out var newFile) && newFile != fileHandle) {
            ChangeMesh(newFile);
        }
    }

    private void ChangeMesh(FileHandle newFile)
    {
        fileHandle?.References.Remove(this);
        fileHandle = newFile;
        meshPath = fileHandle.Filepath;
        fileHandle.References.Add(this);
        mesh = fileHandle.GetResource<CommonMeshResource>();
        if (mesh == null) {
            if (fileHandle.Filepath.Contains("streaming/")) {
                Logger.Error("Can't directly open streaming meshes. Open the non-streaming file instead.");
            }
            return;
        }
        TryGuessMdfFilepath();

        var meshComponent = previewGameobject?.GetComponent<MeshComponent>();
        if (meshComponent != null) {
            meshComponent.SetMesh(fileHandle, fileHandle);
        }
        if (mesh.HasAnimations && string.IsNullOrEmpty(animationSourceFile)) {
            animationSourceFile = newFile.Filepath;
        }
    }

    public void OnWindow()
    {
        if (!ImguiHelpers.BeginWindow(data, flags: ImGuiWindowFlags.MenuBar)) {
            WindowManager.Instance.CloseWindow(data);
            return;
        }
        ImGui.BeginGroup();
        OnIMGUI();
        ImGui.EndGroup();
        ImGui.End();
    }
    private void CenterCameraToSceneObject()
    {
        if (previewGameobject == null || scene == null) return;

        scene.Camera.LookAt(previewGameobject, true);
    }

    public void OnIMGUI()
    {
        if (scene == null) {
            scene = EditorWindow.CurrentWindow!.SceneManager.CreateScene(fileHandle, true);
            scene.Controller = new SceneController();
            scene.Controller.Keyboard = EditorWindow.CurrentWindow.LastKeyboard;
            scene.Controller.Scene = scene;
            scene.MouseHandler = new SceneMouseHandler();
            scene.MouseHandler.scene = scene;
            scene.ActiveCamera.GameObject.MoveToScene(scene);
            scene.AddWidget<SceneVisibilitySettings>();
        }

        MeshComponent meshComponent;
        if (previewGameobject == null) {
            scene.Add(previewGameobject = new GameObject("_preview", Workspace.Env, null, scene));
            meshComponent = previewGameobject.AddComponent<MeshComponent>();
        } else {
            meshComponent = previewGameobject.RequireComponent<MeshComponent>();
        }

        if (!meshComponent.HasMesh) {
            meshComponent.IsStreamingTex = true;
            meshComponent.SetMesh(fileHandle, fileHandle);
            scene.ActiveCamera.ProjectionMode = CameraProjection.Orthographic;
            CenterCameraToSceneObject();
        }

        if (mesh == null) {
            ImGui.Text("No mesh selected");
            return;
        }

        Vector2? embeddedMenuPos = null;
        if (!ShowMenu(meshComponent) && !isSynced) {
            embeddedMenuPos = ImGui.GetCursorPos();
        }
        var expectedSize = ImGui.GetWindowSize() - ImGui.GetCursorPos() - ImGui.GetStyle().WindowPadding;
        expectedSize.X = Math.Max(expectedSize.X, 4);
        expectedSize.Y = Math.Max(expectedSize.Y, 4);
        var nativeSize = data.ParentWindow.Size;
        float meshSize = meshComponent.LocalBounds.Size.Length();
        scene.ActiveCamera.FarPlane = meshSize + 100.0f;
        scene.RenderContext.SetRenderToTexture(expectedSize);

        if (scene.RenderContext.RenderTargetTextureHandle == 0) return;

        var c = ImGui.GetCursorPos();
        var cc = ImGui.GetCursorScreenPos();
        scene.OwnRenderContext.ViewportOffset = cc;
        ImGui.Image((nint)scene.RenderContext.RenderTargetTextureHandle, expectedSize, new System.Numerics.Vector2(0, 1), new System.Numerics.Vector2(1, 0));
        if (embeddedMenuPos != null) {
            ImGui.SetCursorPos(embeddedMenuPos.Value);
            ShowEmbeddedMenu(meshComponent);
        }
        scene.RenderUI();
        ImGui.SetCursorPos(c);
        ImGui.InvisibleButton("##image", expectedSize, ImGuiButtonFlags.MouseButtonLeft | ImGuiButtonFlags.MouseButtonRight);
        // need to store the click/hover events for after so we can handle clicks on the empty area below the info window same as a mesh image click event
        var meshClick = ImGui.IsItemClicked(ImGuiMouseButton.Right) || ImGui.IsItemClicked(ImGuiMouseButton.Left);
        var hoveredMesh = ImGui.IsItemHovered();

        if (isSynced) {
            isSynced = false;
        }

        if (showAnimationsMenu) {
            ImGui.SetCursorPos(new Vector2(17, TopMargin));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, 0);
            ImGui.BeginChild("OverlayControlsContainer", new Vector2(480, ImGui.GetContentRegionAvail().Y - ImGui.GetStyle().WindowPadding.Y), 0, ImGuiWindowFlags.NoMove);
            ImGui.PushStyleColor(ImGuiCol.ChildBg, ImguiHelpers.GetColor(ImGuiCol.WindowBg) with { W = 0.5f });
            ImGui.BeginChild("OverlayControls", new Vector2(480, 0), ImGuiChildFlags.AlwaysUseWindowPadding | ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysAutoResize);

            ImGui.SameLine();
            ShowAnimationMenu(meshComponent);

            ImGui.PopStyleColor(2);
            ImGui.EndChild();
            hoveredMesh = hoveredMesh || ImGui.IsWindowHovered();
            ImGui.EndChild();
        }

        // 3D view controls
        meshClick = meshClick || ImGui.IsItemClicked(ImGuiMouseButton.Right) || ImGui.IsItemClicked(ImGuiMouseButton.Left);
        if (!isSynced) ShowPlaybackControls(meshComponent);

        if (meshClick) {
            if (!isDragging) {
                isDragging = true;
            }
        } else if (isDragging && !ImGui.IsMouseDown(ImGuiMouseButton.Left) && !ImGui.IsMouseDown(ImGuiMouseButton.Right)) {
            isDragging = false;
        }

        if (isDragging || hoveredMesh) {
            AppImguiHelpers.RedirectMouseInputToScene(scene, hoveredMesh);
        }
    }

    private void ShowEmbeddedMenu(MeshComponent meshComponent)
    {
        ImGui.SetCursorPos(ImGui.GetCursorPos() + new Vector2(6, 6));
        if (ImGui.Button($"{AppIcons.GameObject}")) ImGui.OpenPopup("CameraSettings");
        if (ImGui.BeginPopup("CameraSettings")) {
            scene!.Controller?.ShowCameraControls();
            ImGui.EndPopup();
        }
    }

    private bool ShowMenu(MeshComponent meshComponent)
    {
        if (ImGui.BeginMenuBar()) {
            if (ImGui.MenuItem($"{AppIcons.GameObject} Controls")) ImGui.OpenPopup("CameraSettings");
            if (ImGui.BeginPopup("CameraSettings")) {
                scene!.Controller?.ShowCameraControls();
                ImGui.EndPopup();
            }

            if (!isSynced) {
                if (ImGui.MenuItem($"{AppIcons.SI_GenericInfo} Mesh Info")) ImGui.OpenPopup("MeshInfo");
                if (ImGui.MenuItem($"{AppIcons.Eye} Mesh Groups")) ImGui.OpenPopup("MeshGroups");
                if (ImGui.MenuItem($"{AppIcons.EfxEntry} Material")) ImGui.OpenPopup("Material"); // placeholder icon
                if (ImGui.MenuItem($"{AppIcons.SI_FileExtractTo} Import / Export")) ImGui.OpenPopup("Export");
                if (ImGui.MenuItem($"{AppIcons.Play} Animations")) showAnimationsMenu = !showAnimationsMenu;
                if (showAnimationsMenu) ImguiHelpers.HighlightMenuItem($"{AppIcons.Play} Animations");
                if (ImGui.MenuItem($"{AppIcons.Mesh} RCOL")) ImGui.OpenPopup("RCOL");

                if (ImGui.BeginPopup("MeshInfo")) {
                    ShowMeshInfo();
                    ImGui.EndPopup();
                }
                if (ImGui.BeginPopup("Material")) {
                    ShowMaterialSettings(meshComponent);
                    ImGui.EndPopup();
                }
                if (ImGui.BeginPopup("RCOL")) {
                    ShowRcolPicker();
                    ImGui.EndPopup();
                }
                if (ImGui.BeginPopup("MeshGroups")) {
                    ShowMeshGroupSettings(meshComponent);
                    ImGui.EndPopup();
                }
                if (ImGui.BeginPopup("Export")) {
                    ShowImportExportMenu();
                    ImGui.EndPopup();
                }
                if (ImGui.BeginPopup("Animations")) {
                    ShowAnimationMenu(meshComponent);
                    ImGui.EndPopup();
                }
            }
            ImGui.EndMenuBar();
            UpdateMaterial(meshComponent);
            return true;
        } else {
            UpdateMaterial(meshComponent);
            return false;
        }
    }

    private void ShowMeshGroupSettings(MeshComponent meshComponent)
    {
        var meshGroupIds = mesh!.GroupIDs.ToList();
        var parts = RszFieldCache.Mesh.PartsEnable.Get(meshComponent.Data);
        foreach (var group in meshGroupIds) {
            if (group < 0 || group >= parts.Count) continue;

            var enabled = (bool)parts[group];
            if (ImGui.Checkbox(group.ToString(), ref enabled)) {
                parts[group] = (object)enabled;
                meshComponent.RefreshIfActive();
            }
        }
    }

    private void ShowMeshInfo()
    {
        ImGui.Text($"Path: {fileHandle.Filepath} ({fileHandle.HandleType})");
        if (ImGui.BeginPopupContextItem("##filepath")) {
            if (ImGui.Selectable("Copy path")) {
                EditorWindow.CurrentWindow?.CopyToClipboard(fileHandle.Filepath);
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        ImGui.Text("Total Vertices: " + mesh!.VertexCount);
        ImGui.Text("Total Polygons: " + mesh.PolyCount);
        ImGui.Text("Sub Meshes: " + mesh.MeshCount);
        ImGui.Text("Materials: " + mesh.MaterialCount);
        ImGui.Text("Bones: " + mesh.BoneCount);
        if (ImGui.TreeNode("Raw Data")) {
            var meshCtx = context.GetChild<MeshFileHandler>();
            if (meshCtx == null) {
                meshCtx = context.AddChild("Raw Mesh Data", mesh.NativeMesh, new MeshFileHandler());
            }
            meshCtx.ShowUI();
            ImGui.TreePop();
        }
    }

    private void ShowMaterialSettings(MeshComponent meshComponent)
    {
        bool useHighRes = textureMode == TextureMode.HighRes;
        if (ImGui.Checkbox("Textures: " + (useHighRes ? "Hi-Res" : "Low-Res"), ref useHighRes)) {
            textureMode = useHighRes ? TextureMode.HighRes : TextureMode.LowRes;
            isMDFUpdateRequest = true;
            meshComponent.IsStreamingTex = useHighRes;
            UpdateMaterial(meshComponent);
        }
        ImGui.SameLine();
        if (ImGui.Button($"{AppIcons.SI_ResetMaterial}")) {
            mdfSource = originalMDF;
            UpdateMaterial(meshComponent);
        }
        ImguiHelpers.Tooltip("Reset MDF");
        if (mdfPickerContext == null) {
            mdfPickerContext = context.AddChild<MeshViewer, string>(
                "MDF2 Material",
                this,
                new ResourcePathPicker(Workspace, Workspace.Env.TypeCache.GetResourceSubtypes(KnownFileFormats.MaterialDefinition)) { UseNativesPath = true, IsPathForIngame = false },
                (v) => v!.mdfSource,
                (v, p) => v.mdfSource = p ?? "");
        }
        mdfPickerContext.ShowUI();
    }

    private UIContext? rcolPicker;
    private string rcolPath = "";
    private void ShowRcolPicker()
    {
        if (rcolPicker == null) {
            rcolPicker = context.AddChild<MeshViewer, string>(
                "RCOL File",
                this,
                new ResourcePathPicker(Workspace, KnownFileFormats.RequestSetCollider) { UseNativesPath = true, IsPathForIngame = false },
                (v) => v!.rcolPath,
                (v, p) => v.rcolPath = p ?? "");
        }
        rcolPicker.ShowUI();
        var settings = AppConfig.Settings;
        if (settings.RecentRcols.Count > 0) {
            var selection = rcolPath;
            var options = settings.RecentRcols.ToArray();
            if (ImguiHelpers.ValueCombo("Recent files", options, options, ref selection)) {
                rcolPath = selection;
            }
        }

        if (previewGameobject == null) return;
        var rcolComp = previewGameobject.GetComponent<RequestSetColliderComponent>();

        if (string.IsNullOrEmpty(rcolPath)) {
            if (rcolComp != null) {
                RszFieldCache.RequestSetCollider.RequestSetGroups.Get(rcolComp.Data).Clear();
            }
            return;
        }

        rcolComp ??= previewGameobject.AddComponent<RequestSetColliderComponent>();

        var storedGroups = RszFieldCache.RequestSetCollider.RequestSetGroups.Get(rcolComp.Data);
        if (storedGroups.Count == 0) {
            storedGroups.Add(Workspace.Env.CreateRszInstance("via.physics.RequestSetCollider.RequestSetGroup"));
        }
        var storedGroup = (RszInstance)storedGroups[0];
        var storedPath = RszFieldCache.RequestSetGroup.Resource.Get(storedGroup);
        if (storedPath != rcolPath) {
            RszFieldCache.RequestSetGroup.Resource.Set(storedGroup, rcolPath);
            AppConfig.Instance.AddRecentRcol(rcolPath);
        }
        if (rcolComp != null) {
            if (ImGui.Button("Open Editor")) {
                rcolComp.OpenEditor(0);
            }
        }
    }

    private void ShowImportExportMenu()
    {
        if (mesh == null) return;

        using var _ = ImguiHelpers.Disabled(exportInProgress);
        if (ImGui.Button("Export Mesh ...")) {
            // potential export enhancement: include (embed) textures
            if (fileHandle.Resource is CommonMeshResource assmesh) {
                PlatformUtils.ShowSaveFileDialog((exportPath) => {
                    exportInProgress = true;
                    try {
                        if (!exportAnimations) {
                            assmesh.ExportToFile(exportPath);
                        } else if (exportCurrentAnimationOnly) {
                            assmesh.ExportToFile(exportPath, singleMot: animator?.ActiveMotion);
                        } else {
                            assmesh.ExportToFile(exportPath, motlist: animator?.File?.GetFile<MotlistFile>());
                        }
                    } catch (Exception e) {
                        Logger.Error(e, "Mesh export failed");
                    } finally {
                        exportInProgress = false;
                    }
                }, PathUtils.GetFilenameWithoutExtensionOrVersion(fileHandle.Filename).ToString(), MeshFilesFilter);
            } else {
                throw new NotImplementedException();
            }
        }
        if (fileHandle.HandleType is FileHandleType.Bundle or FileHandleType.Disk && File.Exists(fileHandle.Filepath) && fileHandle.Format.format == KnownFileFormats.Mesh) {
            ImGui.SameLine();
            if (ImGui.Button("Import From File...")) {
                var window = EditorWindow.CurrentWindow!;
                PlatformUtils.ShowFileDialog((files) => {
                    window.InvokeFromUIThread(() => {
                        lastImportSourcePath = files[0];
                        if (Workspace.ResourceManager.TryLoadUniqueFile(lastImportSourcePath, out var importedFile)) {
                            var importAsset = importedFile.GetResource<CommonMeshResource>();
                            var tmpHandler = new FileHandler(new MemoryStream(), fileHandle.Filepath);
                            importAsset.NativeMesh.WriteTo(tmpHandler);
                            fileHandle.Stream = tmpHandler.Stream.ToMemoryStream(disposeStream: false, forceCopy: true);
                            fileHandle.Revert(Workspace);
                            ChangeMesh(fileHandle);
                            importedFile.Dispose();
                        }
                    });
                }, lastImportSourcePath, fileExtension: MeshFilesFilter);
            }
        }
        if (exportInProgress) {
            ImGui.SameLine();
            // we have no way of showing any progress from assimp's side (which is 99% of the export duration) so this is the best we can do
            ImGui.TextWrapped($"Exporting in progress. This may take a while for large files and for many animations...");
        }
        if (animator?.File != null) ImGui.Checkbox("Include animations", ref exportAnimations);
        if (animator?.File != null && exportAnimations) ImGui.Checkbox("Selected animation only", ref exportCurrentAnimationOnly);
        ImGui.SeparatorText("Convert Mesh");
        ImguiHelpers.ValueCombo("Mesh Version", MeshFile.AllVersionConfigsWithExtension, MeshFile.AllVersionConfigs, ref exportTemplate);
        var conv1 = ImGui.Button("Convert ...");
        var bundleConvert = Workspace.CurrentBundle != null && ImguiHelpers.SameLine() && ImGui.Button("Save to bundle ...");
        if (conv1 || bundleConvert) {
            var ver = MeshFile.GetFileExtension(exportTemplate);
            var ext = $".mesh.{ver}";
            var defaultFilename = PathUtils.GetFilenameWithoutExtensionOrVersion(fileHandle.Filepath).ToString() + ext;
            if (mesh.NativeMesh.Header.version == 0) {
                mesh.NativeMesh.ChangeVersion(exportTemplate);
            }
            var exportMesh = mesh.NativeMesh.RewriteClone(Workspace);
            exportMesh.ChangeVersion(exportTemplate);
            if (bundleConvert) {
                var tempres = new CommonMeshResource(defaultFilename, Workspace.Env) { NativeMesh = exportMesh };
                ResourcePathPicker.ShowSaveToBundle(fileHandle.Loader, tempres, Workspace, defaultFilename, fileHandle.NativePath);
            } else {
                PlatformUtils.ShowSaveFileDialog((path) => exportMesh.SaveAs(path), defaultFilename);
            }
        }
    }

    private void ShowAnimationMenu(MeshComponent meshComponent)
    {
        ImGui.SeparatorText("Animations");
        if (animator == null) {
            animator = new (Workspace);
        }
        if (!UpdateAnimatorMesh(meshComponent)) return;

        if (animationPickerContext == null) {
            animationPickerContext = context.AddChild<MeshViewer, string>(
                "Source File",
                this,
                new ResourcePathPicker(Workspace, MeshFilesFilter, KnownFileFormats.MotionList, KnownFileFormats.Motion) { UseNativesPath = true, IsPathForIngame = false },
                (v) => v!.animationSourceFile,
                (v, p) => v.animationSourceFile = p ?? "");
        }
        animationPickerContext.ShowUI();
        var settings = AppConfig.Settings;
        if (settings.RecentMotlists.Count > 0) {
            var selection = animationSourceFile;
            var options = settings.RecentMotlists.ToArray();
            if (ImguiHelpers.ValueCombo("Recent files", options, options, ref selection)) {
                animationSourceFile = selection;
            }
        }
        if (animationSourceFile != loadedAnimationSource) {
            if (!string.IsNullOrEmpty(animationSourceFile)) {
                animator.LoadAnimationList(loadedAnimationSource = animationSourceFile);
                AppConfig.Instance.AddRecentMotlist(animationSourceFile);
            } else {
                animator.Unload();
                loadedAnimationSource = animationSourceFile;
            }
        }

        if (animator?.AnimationCount > 0) {
            var ignoreRoot = animator.IgnoreRootMotion;
            if (ImGui.Button("View Data")) {
                EditorWindow.CurrentWindow?.AddSubwindow(new MotlistEditor(Workspace, animator.File!));
            }
            ImGui.SameLine();
            if (ImGui.Checkbox("Ignore Root Motion", ref ignoreRoot)) {
                animator.IgnoreRootMotion = ignoreRoot;
            }

            ImGui.InputText("Filter", ref motFilter, 200);
            foreach (var (name, mot) in animator.Animations) {
                if (!string.IsNullOrEmpty(motFilter) && !name.Contains(motFilter, StringComparison.InvariantCultureIgnoreCase)) continue;

                if (ImGui.RadioButton(name, animator.ActiveMotion == mot)) {
                    animator.SetActiveMotion(mot);
                }
            }
        } else if (animator?.File != null) {
            ImGui.TextColored(Colors.Note, "Selected file contains no playable animations");
        }
    }

    private void UpdateMaterial(MeshComponent meshComponent)
    {
        var mesh = meshComponent.MeshHandle;
        if (loadedMdf != mdfSource || isMDFUpdateRequest) {
            loadedMdf = mdfSource;
            isMDFUpdateRequest = false;
            if (string.IsNullOrEmpty(mdfSource)) {
                meshComponent.SetMesh(fileHandle, fileHandle);
            } else if (Workspace.ResourceManager.TryResolveFile(mdfSource, out var mdfHandle)) {
                meshComponent.SetMesh(fileHandle, mdfHandle);
            } else {
                meshComponent.SetMesh(fileHandle, fileHandle);
                Logger.Error("Could not locate mdf2 file " + mdfSource);
            }
        }
    }

    private bool UpdateAnimatorMesh(MeshComponent meshComponent)
    {
        if (animator == null) return false;
        var mesh = meshComponent.MeshHandle;
        if (animator.Mesh != mesh) {
            if (mesh is not AnimatedMeshHandle anim) {
                ImGui.BeginChild("PlaybackError", new Vector2(300, 42), ImGuiChildFlags.AlwaysUseWindowPadding | ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysAutoResize);
                ImGui.TextColored(Colors.Error, "Mesh is not animatable");
                ImGui.EndChild();
                return false;
            }

            animator.SetMesh(anim);
        }
        return true;
    }

    private void ShowPlaybackControls(MeshComponent meshComponent)
    {
        if (animator?.ActiveMotion == null) return;
        if (!UpdateAnimatorMesh(meshComponent)) return;

        var windowSize = ImGui.GetWindowSize();
        var timestamp = $"{animator.CurrentTime:0.00} / {animator.TotalTime:0.00} ({animator.CurrentFrame:000} / {animator.TotalFrames:000})";
        var timestampSize = ImGui.CalcTextSize(timestamp) + new Vector2(5 * 48, 0);
        ImGui.SetCursorPos(new Vector2(windowSize.X - timestampSize.X - ImGui.GetStyle().WindowPadding.X * 2 - 42, TopMargin));
        ImGui.PushStyleColor(ImGuiCol.ChildBg, ImguiHelpers.GetColor(ImGuiCol.WindowBg) with { W = 0.5f });
        ImGui.BeginChild("PlaybackControls", new Vector2(timestampSize.X, 46), ImGuiChildFlags.AlwaysUseWindowPadding | ImGuiChildFlags.Borders | ImGuiChildFlags.AutoResizeY | ImGuiChildFlags.AlwaysAutoResize);

        var p = ImGui.GetStyle().FramePadding;
        // the margins are weird on the buttons by default - font issue maybe, either way adding a bit of extra Y here
        var btnHeight = new Vector2(0, UI.FontSize + p.Y);
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 4);
        if (ImGui.Button((animator.IsPlaying ? AppIcons.Pause : AppIcons.Play).ToString(), btnHeight)) {
            if (animator.IsPlaying) {
                animator.Pause();
            } else {
                animator.Play();
            }
        }

        ImGui.SameLine();
        using (var _ = ImguiHelpers.Disabled(animator.CurrentTime == 0)) {
            if (ImGui.Button(AppIcons.SeekStart.ToString(), btnHeight)) {
                animator.Restart();
            }
        }

        ImGui.SameLine();
        using (var _ = ImguiHelpers.Disabled(!animator.IsPlaying && !animator.IsActive)) {
            if (ImGui.Button(AppIcons.Stop.ToString(), btnHeight)) {
                animator.Stop();
            }
        }

        ImGui.SameLine();
        using (var _ = ImguiHelpers.Disabled(!animator.IsActive)) {
            if (ImGui.Button(AppIcons.Previous.ToString(), btnHeight)) {
                animator.Pause();
                animator.Seek((animator.CurrentFrame - 1) * animator.FrameDuration);
                animator.Update(0);
            }
        }

        ImGui.SameLine();
        using (var _ = ImguiHelpers.Disabled(!animator.IsActive)) {
            if (ImGui.Button(AppIcons.Next.ToString(), btnHeight)) {
                animator.Pause();
                animator.Seek((animator.CurrentFrame + 1) * animator.FrameDuration);
                animator.Update(0);
            }
        }

        ImGui.SameLine();
        ImGui.SetCursorPosY(ImGui.GetCursorPosY() - 4);
        ImGui.Text(timestamp);

        ImGui.EndChild();
        ImGui.PopStyleColor();

        if (animator.IsPlaying) animator.Update(Time.Delta);
    }

    public void SetAnimation(string animlist)
    {
        if (animator == null) {
            animator = new (Workspace);
        }
        animator.LoadAnimationList(animlist);
        var firstAnim = animator.Animations.FirstOrDefault();
        if (firstAnim.Value != null) {
            animator.SetActiveMotion(firstAnim.Value);
        }
    }

    public void SetAnimation(MotFile mot)
    {
        if (animator == null) {
            animator = new (Workspace);
        }
        animator.SetActiveMotion(mot);
    }

    public static bool IsSupportedFileExtension(string filepathOrExtension)
    {
        var format = PathUtils.ParseFileFormat(filepathOrExtension);
        if (format.format == KnownFileFormats.Mesh) {
            return true;
        }

        return MeshLoader.StandardFileExtensions.Contains(Path.GetExtension(filepathOrExtension));
    }

    public bool RequestClose()
    {
        return false;
    }
    public void Dispose()
    {
        fileHandle?.References.Remove(this);
        if (scene != null) {
            EditorWindow.CurrentWindow?.SceneManager.UnloadScene(scene);
            scene = null;
        }
        mesh = null;
    }
    public void SyncFromScene(MeshViewer other, bool ignoreMotionClip)
    {
        if (scene == null || other.scene == null) return;

        scene.ActiveCamera.Transform.CopyFrom(other.scene.ActiveCamera.Transform);
        scene.ActiveCamera.ProjectionMode = other.scene.ActiveCamera.ProjectionMode;
        scene.ActiveCamera.NearPlane = other.scene.ActiveCamera.NearPlane;
        scene.ActiveCamera.FarPlane = other.scene.ActiveCamera.FarPlane;
        scene.ActiveCamera.FieldOfView = other.scene.ActiveCamera.FieldOfView;
        scene.ActiveCamera.OrthoSize = other.scene.ActiveCamera.OrthoSize;

        if (other.animator != null) {
            if (animator == null) {
                animator = new Animator(Workspace);
            }
            if (!ignoreMotionClip && animator.ActiveMotion != other.animator.ActiveMotion && other.animator.ActiveMotion != null) {
                animator.SetActiveMotion(other.animator.ActiveMotion);
            }
            if (other.animator.IsPlaying != animator.IsPlaying) {
                if (other.animator.IsPlaying) {
                    animator.Play();
                } else {
                    animator.Pause();
                }
            }
            animator.Seek(other.animator.CurrentTime);
        }

        isSynced = true;
    }
}
