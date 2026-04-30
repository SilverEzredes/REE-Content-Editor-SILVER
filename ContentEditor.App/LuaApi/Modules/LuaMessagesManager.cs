using ContentPatcher;
using Lua;

namespace ContentEditor.App.Lua;

[LuaObject]
public partial class LuaMessagesManager(MessageManager manager)
{
    [LuaMember("translate")]
    public string GetByName(string text) => manager.GetText(text) ?? "";

    [LuaMember("translate_guid")]
    public string GetByGuid(string text)
    {
        if (Guid.TryParse(text, out var guid)) {
            return manager.GetText(guid) ?? "";
        }

        return "";
    }
}
