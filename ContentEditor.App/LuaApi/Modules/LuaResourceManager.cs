using ContentPatcher;
using Lua;
using ReeLib;

namespace ContentEditor.App.Lua;

[LuaObject]
public partial class LuaResourceManager(ResourceManager resources, ContentWorkspace workspace)
{
    [LuaMember("load")]
    public LuaFileHandleWrapper? Load(string filepath)
    {
        if (resources.TryResolveGameFile(filepath, out var file)) {
            return new LuaFileHandleWrapper(file, workspace);
        }

        return null;
    }

    [LuaMember("create")]
    public LuaFileHandleWrapper? Create(string format)
    {
        if (Enum.TryParse<KnownFileFormats>(format, out var fmt)) {
            var newFile = resources.CreateNewFile(fmt);
            if (newFile == null) {
                Logger.Error("Failed to create new file of type " + fmt);
                return null;
            }

            return new LuaFileHandleWrapper(newFile, workspace);
        }

        Logger.Error("Unknown or unsupported file format " + format);
        return null;
    }

    [LuaMember("close_all")]
    public void CloseAll() => resources.CloseAllFiles();
}
