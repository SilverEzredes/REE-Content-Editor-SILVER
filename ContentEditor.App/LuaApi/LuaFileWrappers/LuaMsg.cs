using Lua;
using ReeLib;
using ReeLib.Msg;

namespace ContentEditor.App.Lua;

[LuaObject]
public partial class LuaMsg : LuaFileResource<MsgFile>
{
    public LuaMsg(LuaFileHandleWrapper file) : base(file)
    {
        LuaReflectionObject.CreateObjectMixedMetaTable(this);
    }

    [LuaMember("translate")]
    public string? GetTranslation(string key) => File.FindEntryByKey(key)?.Strings[(int)Language.English];

    [LuaMember("translate_guid")]
    public string? GetTranslationGuid(string key)
    {
        if (Guid.TryParse(key, out var guid)) {
            foreach (var entry in File.Entries) {
                if (entry.Guid == guid) {
                    return entry.Strings[(int)Language.English];
                }
            }
        }
        return null;
    }
}
