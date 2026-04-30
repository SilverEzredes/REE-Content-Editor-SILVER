using ContentPatcher;
using ReeLib;

namespace ContentEditor.App.FileLoaders;

public class LuaFileLoader : IFileLoader
{
    public static readonly LuaFileLoader Instance = new();

    public bool CanHandleFile(string filepath, REFileFormat format, FileHandle? file)
    {
        return filepath.EndsWith(".lua", StringComparison.OrdinalIgnoreCase);
    }

    public IResourceFilePatcher? CreateDiffHandler() => null;

    public IResourceFile? CreateNewFile(ContentWorkspace workspace, FileHandle handle) => new LuaScript();

    public IResourceFile? Load(ContentWorkspace workspace, FileHandle handle)
    {
        handle.Stream.Seek(0, SeekOrigin.Begin);
        var reader = new StreamReader(handle.Stream);
        return new LuaScript(reader.ReadToEnd());
    }

    public bool Save(ContentWorkspace workspace, FileHandle handle, string outputPath)
    {
        handle.GetResource<LuaScript>().WriteTo(outputPath);
        return true;
    }
}

public sealed class LuaScript : IResourceFile
{
    public string Script { get; set; } = "";

    public LuaScript()
    {
    }

    public LuaScript(string script)
    {
        Script = script;
    }

    public void WriteTo(string filepath)
    {
        File.WriteAllText(filepath, Script);
    }
}
