using ContentPatcher;
using Lua;

namespace ContentEditor.App.Lua;

[LuaObject]
public partial class LuaWorkspaceWrapper(ContentWorkspace workspace)
{
    [LuaMember("game")]
    public string Game => workspace.Game.name;

    [LuaMember("messages")]
    public LuaMessagesManager Messages { get; } = new LuaMessagesManager(workspace.Messages);

    [LuaMember("files")]
    public LuaResourceManager Files { get; } = new LuaResourceManager(workspace.ResourceManager, workspace);

    [LuaMember("current_bundle")]
    public LuaBundleWrapper? CurrentBundle { get; } = workspace.CurrentBundle == null ? null : new LuaBundleWrapper(workspace.CurrentBundle);

    [LuaMember("find_files")]
    public LuaTable FindFiles(string pathOrPattern)
    {
        var paths = workspace.Env.ListFile?.GetFiles(pathOrPattern) ?? [];
        return LuaJson.ToLua(paths);
    }
}
