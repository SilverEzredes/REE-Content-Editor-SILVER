using System.Text.Json;
using ContentEditor;
using ContentEditor.Core;
using ContentEditor.Editor;
using ContentPatcher.FileFormats;
using ReeLib;

namespace ContentPatcher;

public sealed class ContentWorkspace : IDisposable
{
    public Workspace Env { get; }
    public ResourceManager ResourceManager { get; }
    public ContentWorkspaceData Data { get; set; } = new();
    public PatchDataContainer Config { get; }
    public BundleManager BundleManager { get; set; }
    public BundleManager? EditedBundleManager { get; private set; }
    public Bundle? CurrentBundle { get; private set; }
    public DiffHandler Diff { get; }
    public MessageManager Messages { get; }
    public GameIdentifier Game => Env.Config.Game;
    public string VersionHash { get; private set; }

    public ContentWorkspace(Workspace env, PatchDataContainer patchConfig, BundleManager? rootBundleManager = null)
    {
        Env = env;
        Config = patchConfig;
        BundleManager = rootBundleManager ?? new() { VersionHash = VersionHash };
        BundleManager.GamePath = env.Config.GamePath;
        Diff = new DiffHandler(env);
        ResourceManager = new ResourceManager(patchConfig);
        Messages = new MessageManager(env);
        if (!patchConfig.IsLoaded) {
            var valid = false;
            try {
                _ = env.RszParser;
                valid = true;
            } catch (Exception) {
                Logger.Error("RSZ files are not supported at the moment, the RSZ template json is either unconfigured or invalid.");
            }
            if (valid) patchConfig.Load(this);
        }
        VersionHash = ExeUtils.GetGameVersionHash(env);
        ResourceManager.Setup(this);
    }

    public void SetBundle(string? bundle)
    {
        if (bundle == null) {
            EditedBundleManager = null;
            if (Data.ContentBundle != null) {
                Data.ContentBundle = null;
            }
            CurrentBundle = null;
            Data.Name = null;
            ResourceManager.SetBundle(BundleManager, null);
            return;
        }

        if (Data.ContentBundle != bundle || EditedBundleManager == null) {
            if (Data.Name == null) Data.Name = bundle;
            Data.ContentBundle = bundle;
            EditedBundleManager = BundleManager.CreateBundleSpecificManager(bundle);
            CurrentBundle = EditedBundleManager.GetBundle(bundle, null);
            ResourceManager.SetBundle(EditedBundleManager, CurrentBundle);
        }
    }

    public int SaveModifiedFiles(IRectWindow? parent = null)
    {
        Logger.Info("Saving files ...");
        SaveBundle();
        int count = 0;
        foreach (var file in ResourceManager.GetModifiedResourceFiles()) {
            if (file.Modified) {
                if (parent == null || file.References.Any(ff => ff.Parent == parent)) {
                    if (file.Save(this)) {
                        count++;
                    }
                }
            }
        }
        return count;
    }

    public void SaveBundle(bool forceDiffAllFiles = false)
    {
        if (Data.ContentBundle == null) {
            if (ResourceManager.HasAnyActivatedEntities) {
                Logger.Warn("Entities may have been modified - A bundle is required to save such changes.");
            }
            return;
        }

        var bundle = EditedBundleManager?.GetBundle(Data.ContentBundle, null) ?? BundleManager.GetBundle(Data.ContentBundle, null);
        if (bundle == null) {
            WindowManager.Instance.ShowError($"Bundle '{Data.ContentBundle}' not found!");
            return;
        }
        var didAllowLoose = Env.AllowUseLooseFiles;
        Env.AllowUseLooseFiles = false;
        if (bundle.ResourceListing != null) {
            // update the diffs for all open bundle resource files that are part of the bundle
            // we don't check for file.Modified because it can be marked as false but still be different from the current diff
            // e.g. if we manually replaced the file or undo'ed our changes
            if (forceDiffAllFiles) {
                foreach (var (localPath, info) in bundle.ResourceListing) {
                    FileHandle? file;
                    try {
                        file = ResourceManager.GetFileHandle(info.Target);
                        if (file == null) {
                            Logger.Error("Failed to open bundle file " + info.Target);
                            continue;
                        }
                    } catch (Exception e) {
                        Logger.Error("Failed to open bundle file " + info.Target + ": " + e.Message);
                        continue;
                    }

                    TryExecuteDiff(bundle, file);
                    ResourceManager.CloseFile(file);
                }
            } else {
                foreach (var file in ResourceManager.GetOpenFiles()) {
                    TryExecuteDiff(bundle, file);
                }
            }
        }

        var deletes = new List<int>();
        for (int i = 0; i < bundle.Entities.Count; i++) {
            var entity = bundle.Entities[i];
            if (entity is not ResourceEntity) {
                // if it wasn't updated to a ResourceEntity, no resources were activated, therefore nothing was changed
                continue;
            }

            var activeEntity = ResourceManager.GetActiveEntityInstance(entity.Type, entity.Id);
            if (activeEntity == null) {
                deletes.Add(i);
                continue;
            }

            entity.Data = activeEntity?.CalculateDiff(this);
            if (entity.Data == null) {
                deletes.Add(i);
                continue;
            }

            Logger.Debug($"Saving modified entity {entity.Label}");
        }

        deletes.Reverse();
        foreach (var del in deletes) {
            Logger.Info($"Removed entity {bundle.Entities[del].Label}");
            bundle.Entities.RemoveAt(del);
        }
        bundle.GameVersion = VersionHash;
        Env.AllowUseLooseFiles = didAllowLoose;

        (EditedBundleManager ?? BundleManager).SaveBundle(bundle);
    }

    public void SaveBundleFileDiff(FileHandle file)
    {
        if (CurrentBundle == null) {
            Logger.Error("No active bundle");
            return;
        }

        TryExecuteDiff(CurrentBundle, file);
        (EditedBundleManager ?? BundleManager).SaveBundle(CurrentBundle);
    }

    private static void TryExecuteDiff(Bundle bundle, FileHandle file)
    {
        if (file.NativePath != null && bundle.TryFindResourceByNativePath(file.NativePath, out var localPath) && file.DiffHandler != null) {
            var resourceListing = bundle.ResourceListing![localPath];
            var newdiff = file.DiffHandler.FindDiff(file);
            if (newdiff?.ToJsonString() != resourceListing.Diff?.ToJsonString()) {
                resourceListing.Diff = newdiff;
            }
            resourceListing.DiffTime = DateTime.UtcNow;
        }
    }

    public void CreateBundleFromPAK(string bundleName, string pakFilepath)
    {
        var bundlePath = BundleManager.GetBundleFolder(bundleName);

        var pak = new PakReader();
        if (!pak.TryReadManifestFileList(pakFilepath)) {
            pak.AddFiles(Env.ListFile?.Files ?? []);
        }
        pak.UnpackFilesTo(bundlePath);
        InitializeUnlabelledBundle(bundlePath, null, bundleName);
    }

    public void InitializeUnlabelledBundle(string bundlePath, string? sourcePath = null, string? name = null)
    {
        if (!Path.IsPathFullyQualified(bundlePath)) bundlePath = BundleManager.GetBundleFolder(bundlePath);
        if (sourcePath != null && sourcePath.NormalizeFilepath() != bundlePath.NormalizeFilepath()) {
            // copy all files from source to the bundle path
            foreach (var srcfile in Directory.EnumerateFiles(sourcePath, "*.*", SearchOption.AllDirectories)) {
                var destFile = Path.Combine(bundlePath, srcfile.NormalizeFilepath().Replace(sourcePath.NormalizeFilepath(), "").TrimStart('/'));
                Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
                File.Copy(srcfile, destFile, true);
            }
        }
        var originalBundle = this.CurrentBundle;
        if (originalBundle != null) {
            SetBundle(null);
        }
        var bundleJsonPath = Path.Combine(bundlePath, "bundle.json");
        var bundleName = name ?? new DirectoryInfo(bundlePath).Name;
        var bundle = BundleManager.GetBundle(bundleName, null) ?? new Bundle();
        bundle.Name = bundleName;
        var modIni = Path.Combine(bundlePath, "modinfo.ini");
        if (File.Exists(modIni)) {
            foreach (var (key, value) in IniFile.ReadFileIgnoreKeyCasing(modIni)) {
                if (key == "author") {
                    bundle.Author = value;
                } else if (key == "description") {
                    bundle.Description = bundle.Description == null ? value : bundle.Description + "\n\n" + value;
                } else if (key == "name") {
                    bundle.Description = "Mod name: " + (bundle.Description == null ? value : value + "\n\n" + bundle.Description);
                }
            }
        }

        var hasPreviousBundleData = false;
        if (Path.Exists(bundleJsonPath)) {
            using var fs = File.OpenRead(bundleJsonPath);
            var data = JsonSerializer.Deserialize<Bundle>(fs);
            if (data != null) {
                bundle = data;
                bundle.Name = bundleName;
                hasPreviousBundleData = true;
            }
        }

        var bundlePathNorm = bundlePath.NormalizeFilepath();
        if (!hasPreviousBundleData) {
            bundle.GameVersion = VersionHash;
            bundle.ResourceListing ??= new();
            foreach (var file in Directory.EnumerateFiles(bundlePath, "*.pak")) {
                if (file.EndsWith(".pak")) {
                    Logger.Info("Unpacking PAK file: " + file);
                    var pak = new PakReader();
                    if (!pak.TryReadManifestFileList(file)) {
                        pak.AddFiles(Env.ListFile?.Files ?? []);
                    }
                    var count = pak.UnpackFilesTo(bundlePath);
                    if (count == 0) {
                        Logger.Warn("Couldn't find any files within PAK file: " + file);
                    }
                }
            }
        }

        foreach (var file in Directory.EnumerateFiles(bundlePath, "*.*", SearchOption.AllDirectories)) {
            var localFile = file.NormalizeFilepath().Replace(bundlePathNorm, "").TrimStart('/');
            if (localFile == "modinfo.ini") continue;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".txt" || ext == ".json") {
                // probably cover images
                continue;
            }
            if (localFile.EndsWith(".pak")) {
                continue;
            }

            if (localFile.StartsWith("natives/")) {
                bundle.ResourceListing ??= new();
                bundle.ResourceListing[localFile] = new ResourceListItem() {
                    Target = localFile,
                };
            } else if (localFile.StartsWith("reframework/data/usercontent/bundles/") && localFile.EndsWith(".json")) {
                Logger.Error("Found nested bundle file, the mod was not installed correctly. Aborting.\nFile: " + file);
                // we'll need a different flow for upgrading legacy DD2 bundles, as well as some sort of migration of legacy data
                return;
            } else if (localFile.StartsWith("reframework/")) {
                Logger.Warn("REFramework files are not currently supported for patch installation. Install this manually for now: " + localFile);
            } else {
                var listfile = Env.ListFile;
                var nativePath = localFile;
                var isProbablyCorrect = false;
                if (listfile != null) {
                    var fn = Path.GetFileName(localFile);
                    var possibleNatives = listfile.GetFiles($".*/{fn}$", 2);
                    if (possibleNatives.Length == 1) {
                        nativePath = possibleNatives[0];
                        isProbablyCorrect = true;
                    } else if (possibleNatives.Length > 1) {
                        nativePath = possibleNatives[0];
                    }
                    listfile.ResetResultCache();
                }
                bundle.ResourceListing ??= new();
                bundle.ResourceListing[localFile] = new ResourceListItem() {
                    Target = nativePath,
                };
                if (!isProbablyCorrect) {
                    Logger.Warn("Resource file at the bundle root. Unable to guarantee correctly determined native path. Please recheck the generated bundle json.\nFile: " + localFile);
                }
            }
        }

        bundle.Touch();
        BundleManager.ActiveBundles.Add(bundle);
        BundleManager.AllBundles.Add(bundle);
        SetBundle(bundle.Name);
        SaveBundle(true);
        BundleManager.UninitializedBundleFolders.Remove(bundlePath);
        SetBundle(originalBundle?.Name);
    }

    public override string ToString() => Data.Name ?? "New Workspace";

    public void Dispose()
    {
        ResourceManager?.Dispose();
    }
}

// serialized
public class ContentWorkspaceData
{
    public string? Name { get; set; }
    public string? ContentBundle { get; set; }
    public List<WindowData> Windows { get; set; } = new();
}
