using System.Text.Json;
using System.Text.Json.Nodes;
using Lua;

namespace ContentEditor.App.Lua;

[LuaObject]
public partial class LuaJson
{
    [LuaMember("load_string")]
    public static LuaValue LoadString(string json)
    {
        try {
            var value = JsonSerializer.Deserialize<JsonNode>(json);
            return JsonToLua(value);
        } catch (Exception e) {
            Logger.Error("Failed to deserialize JSON: " + e.Message);
            return LuaValue.Nil;
        }
    }

    [LuaMember("load_file")]
    public static LuaValue LoadFile(string file)
    {
        if (!File.Exists(file)) {
            Logger.Error("JSON File does not exist: " + file);
            return LuaValue.Nil;
        }

        var json = File.ReadAllText(file);
        return LoadString(json);
    }

    [LuaMember("dump_string")]
    public static string DumpString(LuaValue value, int indent = -1)
    {
        var json = LuaToJson(value);
        JsonSerializerOptions options;
        if (indent < 0) {
            options = new JsonSerializerOptions();
        } else {
            options = new JsonSerializerOptions() { WriteIndented = true, IndentSize = indent };
        }
        return json?.ToJsonString(options) ?? "null";
    }

    [LuaMember("dump_file")]
    public static void DumpFile(string filepath, LuaValue value, int indent = -1)
    {
        var str = DumpString(value, indent);
        if (FileSystemUtils.EnsureDirectoryExists(Path.GetDirectoryName(filepath)!)) {
            File.WriteAllText(filepath, str);
        }
    }

    public static string LuaToString(LuaValue value, JsonSerializerOptions? options = default)
    {
        if (value.Type == LuaValueType.String) return value.ToString();
        return LuaToJson(value, options)?.ToString() ?? "";
    }

    public static LuaValue JsonToLua(JsonNode? node)
    {
        if (node == null) return LuaValue.Nil;

        var kind = node.GetValueKind();
        switch (kind) {
            case JsonValueKind.False: return LuaValue.FromObject(false);
            case JsonValueKind.True: return LuaValue.FromObject(true);
            case JsonValueKind.Number: {
                    try {
                        return LuaValue.FromObject(node.GetValue<long>());
                    } catch (Exception) {
                        return LuaValue.FromObject(node.GetValue<ulong>());
                    }
                }
            case JsonValueKind.String: return LuaValue.FromObject(node.GetValue<string>());
            case JsonValueKind.Array: {
                    var arr = node.AsArray();
                    var result = new LuaTable(arr.Count, 0);
                    for (int i = 0; i < arr.Count; i++) {
                        var item = arr[i];
                        result[LuaValue.FromObject(i + 1)] = JsonToLua(item);
                    }
                    return result;
                }
            case JsonValueKind.Object: {
                    var obj = node.AsObject();
                    var result = new LuaTable(0, obj.Count);
                    foreach (var pair in obj) {
                        result[LuaValue.FromObject(pair.Key)] = JsonToLua(pair.Value);
                    }
                    return result;
                }
            default:
                return LuaValue.Nil;
        }
    }

    public static JsonNode? LuaToJson(LuaValue value, JsonSerializerOptions? options = default)
    {
        switch (value.Type) {
            case LuaValueType.Nil: return null;
            case LuaValueType.Boolean: return value.ToBoolean();
            case LuaValueType.Number: {
                    if (value.TryRead<long>(out var ll)) {
                        return ll;
                    } else if (value.TryRead<double>(out var dd)) {
                        return dd;
                    }
                    break;
                }
            case LuaValueType.String: return value.Read<string>();
            case LuaValueType.Thread:
            case LuaValueType.Function: return value.ToString();
            case LuaValueType.Table:
                if (value.TryRead<LuaTable>(out var table)) {
                    if (table.HashMapCount > 0) {
                        var dict = new JsonObject();
                        foreach (var pair in table) {
                            var key = LuaToJson(pair.Key)?.ToString() ?? "";
                            dict.TryAdd(key, LuaToJson(pair.Value));
                        }
                        return dict;
                    } else {
                        var arr = new JsonArray();
                        foreach (var item in table) {
                            if (item.Key.TryRead<int>(out var index)) {
                                if (index < 1) continue;
                                while (arr.Count < index) {
                                    arr.Add(null);
                                }
                                arr[index - 1] = LuaToJson(item.Value);
                            }
                        }
                        return arr;
                    }
                }
                break;
            case LuaValueType.LightUserData: {
                    var obj = value.Read<object>();
                    if (obj is JsonNode jn) return jn;
                    try {
                        return JsonSerializer.SerializeToNode(obj, options);
                    } catch (Exception) {
                        return JsonSerializer.SerializeToNode(LuaWrapper.ToLua(obj), options);
                    }
                }
            case LuaValueType.UserData: {
                    if (value.TryRead<ILuaUserData>(out var lud) && lud is ILuaObjectWrapper obj) {
                        if (obj.Object is JsonNode jn) return jn;
                        if (obj.Object is not FileHandleBase) {
                            try {
                                return JsonSerializer.SerializeToNode(obj.Object, options);
                            } catch (Exception) {
                                // ignore
                            }
                        }
                        return obj.Object.ToString();
                    }
                    return value.ToString();
                }
        }

        return null;
    }
}