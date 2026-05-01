using ContentEditor.Core;
using Lua;

namespace ContentEditor.App.Lua;

[LuaObject]
public partial class LuaBundleWrapper(Bundle bundle)
{
    [LuaMember("name")]
    public string Name { get => bundle.Name; set => bundle.Name = value; }

    [LuaMember("author")]
    public string? Author { get => bundle.Author; set => bundle.Author = value; }

    [LuaMember("description")]
    public string? Description { get => bundle.Description; set => bundle.Description = value; }

    [LuaMember("version")]
    public string? Version { get => bundle.Version; set => bundle.Version = value; }
}
