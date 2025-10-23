using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using ContentEditor.App.FileLoaders;
using ContentPatcher;
using ReeLib;
using ReeLib.via;
using Silk.NET.OpenGL;
using Silk.NET.Windowing;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace ContentEditor.App.Graphics;

public sealed class FilePreviewGenerator : IDisposable
{
    public string PreviewCacheFolder { get; }

    private readonly ConcurrentQueue<string> _pendingPreviews = new();
    private readonly ContentWorkspace workspace;
    private readonly ConcurrentDictionary<string, Texture> _loadedPreviews = new();
    private readonly ConcurrentDictionary<string, PreviewImageStatus> _statuses = new();

    private static readonly Int2 ThumbnailSize = new Int2(128, 128);
    private readonly GL _mainGl;
    private static WindowOptions _workerWindowOptions = WindowOptions.Default with {
        IsVisible = false,
        ShouldSwapAutomatically = false,
        Size = new Silk.NET.Maths.Vector2D<int>(1, 1),
    };
    private IWindow? _workerWindow;
    private GL _threadGL = null!;

    private bool _cancelRequested;
    private bool _stopRequested;
    private bool _threadRunning;

    private Thread? thread;

    public FilePreviewGenerator(ContentWorkspace originalWorkspace, GL gl)
    {
        this._mainGl = gl;
        PreviewCacheFolder = Path.Combine(AppConfig.Instance.ThumbnailCacheFilepath.Get()!, originalWorkspace.Game.name);
        workspace = originalWorkspace.CreateTempClone();
        workspace.ResourceManager.SetupFileLoaders(typeof(MeshLoader).Assembly);
        // we manually load tex files so we can skip unnecessary mipmaps
        workspace.ResourceManager.RemoveFileLoader<TexFileLoader>();
    }

    public void CancelCurrentQueue()
    {
        _cancelRequested = true;
        _pendingPreviews.Clear();
        foreach (var item in _statuses) {
            if (item.Value is PreviewImageStatus.Pending) {
                _statuses.TryRemove(item);
            }
        }
    }

    private string GetThumbnailPath(string filepath)
    {
        // should we worry about file path length limits? if so, we could use hashes instead of full file paths
        return Path.Combine(PreviewCacheFolder, PathUtils.RemoveNativesFolder(filepath) + ".jpg");
    }

    public PreviewImageStatus FetchPreview(string file, out Texture? texture)
    {
        texture = null;
        if (_statuses.TryGetValue(file, out var status)) {
            if (status == PreviewImageStatus.Generated) {
                if (LoadGeneratedThumbnail(file, out texture)) {
                    return PreviewImageStatus.Ready;
                } else {
                    _statuses[file] = PreviewImageStatus.Failed;
                }
            }
            if (status == PreviewImageStatus.Ready) {
                texture = _loadedPreviews[file];
            }
            return status;
        }

        var fmt = PathUtils.ParseFileFormat(file);
        if (fmt.format is KnownFileFormats.Texture or KnownFileFormats.Mesh) {
            _statuses[file] = PreviewImageStatus.Pending;
            EnqueueFile(file);
            return PreviewImageStatus.Pending;
        } else {
            return _statuses[file] = PreviewImageStatus.Unsupported;
        }
    }

    private void EnqueueFile(string file)
    {
        if (_pendingPreviews.IsEmpty) {
            _cancelRequested = true;
            thread?.Join();
            _cancelRequested = false;
            _stopRequested = false;
            thread = new Thread(RunPreviewGenerationQueue);
            thread.Start();
        }
        _pendingPreviews.Enqueue(file);
    }

    private bool LoadGeneratedThumbnail(string file, out Texture? texture)
    {
        var path = GetThumbnailPath(file);
        using var fs = File.OpenRead(path);
        texture = new Texture(_mainGl);
        texture.LoadFromStream(fs);
        _loadedPreviews[file] = texture;
        _statuses[file] = PreviewImageStatus.Ready;
        return true;
    }

    private void RunPreviewGenerationQueue()
    {
        while (_threadRunning) {
            Thread.Sleep(1);
        }
        _threadRunning = true;

        if (_workerWindow == null) {
            var newWindow = Window.Create(_workerWindowOptions);
            Interlocked.CompareExchange(ref _workerWindow, newWindow, _workerWindow);
            _workerWindow = Window.Create(_workerWindowOptions);
            _workerWindow.Load += () => {
                _threadGL = _workerWindow.CreateOpenGL();
            };
            _workerWindow.Initialize();
            _workerWindow.MakeCurrent();
            Debug.Assert(_threadGL != null);
        }

        try {
            while (!_stopRequested && !_cancelRequested && _pendingPreviews.TryDequeue(out var path)) {
                var thumbPath = GetThumbnailPath(path);
                if (File.Exists(thumbPath)) {
                    // TODO verify content hash for changes
                    _statuses[path] = PreviewImageStatus.Generated;
                    continue;
                }

                if (!workspace.ResourceManager.TryResolveFile(path, out var f)) {
                    _statuses[path] = PreviewImageStatus.Failed;
                    continue;
                }
                if (_stopRequested || _cancelRequested) break;
                if (f == null) {
                    Logger.Debug("Failed to resolve preview file: " + path);
                    _statuses[path] = PreviewImageStatus.Failed;
                    continue;
                }

                var fmt = PathUtils.ParseFileFormat(path);

                try {
                    switch (fmt.format) {
                        case KnownFileFormats.Texture:
                            GenerateTextureThumbnail(path, f.Stream);
                            break;
                        case KnownFileFormats.Mesh:
                            GenerateMeshThumbnail(path);
                            break;
                        default:
                            _statuses[path] = PreviewImageStatus.Failed;
                            break;
                    }
                } catch (Exception e) {
                    Logger.Debug("Failed to generate file thumbnail: " + e.Message);
                    _statuses[path] = PreviewImageStatus.Failed;
                }
                workspace.ResourceManager.CloseAllFiles();
            }
        } finally {
            _workerWindow?.Dispose();
            _workerWindow = null;
            _threadRunning = false;
            _cancelRequested = false;
        }
    }

    private void GenerateTextureThumbnail(string path, Stream stream)
    {
        _statuses[path] = PreviewImageStatus.Loading;
        var texFile = new TexFile(new FileHandler(stream, path));
        texFile.Read();
        var totalMips = texFile.Header.mipCount;
        var targetMip = texFile.GetBestMipLevelForDimensions(ThumbnailSize.x, ThumbnailSize.y);
        var outPath = GetThumbnailPath(path);

        using var fullTexture = new Texture(_threadGL);
        fullTexture.LoadFromTex(texFile);
        fullTexture.Bind();
        fullTexture.SetChannel(Texture.TextureChannel.RGB);

        using var img = fullTexture.GetAsImage(targetMip);
        img.Mutate(x => x.Resize(ThumbnailSize.x, ThumbnailSize.y));
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        img.SaveAsJpeg(outPath, null);
        _statuses[path] = PreviewImageStatus.Generated;
    }

    private void GenerateMeshThumbnail(string path)
    {
        _statuses[path] = PreviewImageStatus.Loading;
        var outPath = GetThumbnailPath(path);

        using var tmpScene = new Scene("_preview", "", workspace, null, null, _threadGL);
        tmpScene.SetActive(true);
        tmpScene.ActiveCamera.ProjectionMode = CameraProjection.Orthographic;

        var go = new GameObject("mesh", workspace.Env, tmpScene.RootFolder, tmpScene);
        var comp = go.AddComponent<MeshComponent>();
        var basePath = PathUtils.GetFilepathWithoutExtensionOrVersion(path).ToString();
        workspace.ResourceManager.TryResolveFile(basePath + ".mdf2", out var mdfHandle);
        if (mdfHandle == null) workspace.ResourceManager.TryResolveFile(basePath + "_Mat.mdf2", out mdfHandle);

        comp.SetMesh(path, mdfHandle?.Filepath);
        if (comp.MeshHandle == null) {
            _statuses[path] = PreviewImageStatus.Failed;
            Logger.Debug($"Failed to generate thumbnail for mesh " + path);
            return;
        }

        tmpScene.ActiveCamera.LookAt(go, true);
        tmpScene.ActiveCamera.OrthoSize = go.GetWorldSpaceBounds().Size.Length() * 0.8f;
        tmpScene.OwnRenderContext.SetRenderToTexture(new Vector2(ThumbnailSize.x, ThumbnailSize.y));
        tmpScene.Render(0);
        _threadGL.Flush();
        var texture = new Texture(tmpScene.OwnRenderContext.RenderTargetTextureHandle, _threadGL, ThumbnailSize.x, ThumbnailSize.y);
        using var img = texture.GetAsImage(0);
        img.Mutate(x => x.Flip(FlipMode.Vertical));
        tmpScene.SetActive(false);

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        img.SaveAsJpeg(outPath, null);

        _statuses[path] = PreviewImageStatus.Generated;
    }

    public void Dispose()
    {
        CancelCurrentQueue();
        foreach (var pv in _loadedPreviews) {
            pv.Value.Dispose();
        }

        _loadedPreviews.Clear();
        _stopRequested = true;
    }
}

public enum PreviewImageStatus
{
    Unsupported,
    Pending,
    Loading,
    Generated,
    Ready,
    Failed,
}
