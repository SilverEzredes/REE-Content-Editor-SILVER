using ContentPatcher;
using Lua;
using ReeLib;

namespace ContentEditor.App.Lua;

[LuaObject]
public partial class LuaFileHandleWrapper(FileHandle file, ContentWorkspace workspace)
{
    public FileHandle File => file;
    public static LuaValue GetFileResource(LuaFileHandleWrapper file)
    {
        switch (file.File.Format.format) {
            case ReeLib.KnownFileFormats.Message:
                return new LuaMsg(file);
        }
        return LuaValue.Nil;
    }

    [LuaMember("resource")]
    public LuaValue Resource => GetFileResource(this);

    [LuaMember("type")]
    public string FileType => file.Format.format.ToString();

    [LuaMember("format")]
    public string Format => file.Format.ToString();

    [LuaMember("filename")]
    public string Filename => file.Filename.ToString();

    [LuaMember("filepath")]
    public string Filepath => file.Filepath;

    [LuaMember("native_path")]
    public string? NativePath => file.NativePath;

    [LuaMember("internal_path")]
    public string? InternalPath => file.InternalPath;

    [LuaMember("modified")]
    public bool Modified { get => file.Modified; set => file.Modified = value; }

    [LuaMember("revert")]
    public void Revert() => file.Revert(workspace);

    [LuaMember("save")]
    public void Save() => file.Save(workspace);

    [LuaMember("save_as")]
    public void SaveAs(string path) => file.Save(workspace, path);

    [LuaMember("close")]
    public void Close() => workspace.ResourceManager.CloseFile(file);
}

[LuaObject]
public partial class LuaBaseResource(LuaFileHandleWrapper file)
{
    [LuaMember("handle")]
    public LuaFileHandleWrapper Handle { get; } = file;
}

[LuaObject]
public partial class LuaBaseResource<TFile>(LuaFileHandleWrapper file) : LuaBaseResource(file) where TFile : BaseFile
{
    public TFile File { get; } = file.File.GetFile<TFile>();
}
