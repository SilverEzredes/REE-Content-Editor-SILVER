namespace ContentPatcher;

using System.Text.Json;
using System.Text.Json.Nodes;
using ContentEditor;
using ContentEditor.Core;
using ContentEditor.Editor;
using ContentPatcher.StringFormatting;
using ReeLib;
using ReeLib.Il2cpp;
using SmartFormat;
using SmartFormat.Extensions;
using VYaml.Annotations;
using VYaml.Serialization;

public class PatchDataContainer(string filepath)
{
    private readonly Dictionary<string, ClassConfig> configs = new();
    private readonly Dictionary<string, EntityConfig> entities = new();
    private readonly Dictionary<string, CustomTypeConfig> customTypes = new();

    private string DefinitionFilepath { get; } = Path.Combine(filepath, "definitions");
    private string EnumFilepath { get; } = Path.Combine(filepath, "enums");

    public IEnumerable<KeyValuePair<string, ClassConfig>> Classes => configs;
    public IEnumerable<KeyValuePair<string, EntityConfig>> Entities => entities;
    public IEnumerable<KeyValuePair<string, CustomTypeConfig>> CustomTypes => customTypes;

    public bool IsLoaded { get; private set; }

    public EntityTypeList EntityHierarchy { get; } = new("");

    public ClassConfig? Get(string classname) => configs.GetValueOrDefault(classname);
    public FieldConfig? Get(string classname, string fieldName) => configs.GetValueOrDefault(classname)?.Fields?.GetValueOrDefault(fieldName);
    public EntityConfig? GetEntityConfig(string entityType) => entities.GetValueOrDefault(entityType);

    private static readonly Dictionary<string, Func<JsonObject, ResourceHandler>> patchers = new();
    public static void RegisterResourcePatcher(string type, Func<JsonObject, ResourceHandler> factory)
    {
        patchers[type] = factory;
    }

    public void Load(ContentWorkspace workspace)
    {
        IsLoaded = true;
        configs.Clear();
        AddDefaultConfigs();
        LoadPatchConfigs(workspace);
        if (!string.IsNullOrEmpty(workspace.Env.Config.GamePath)) {
            var runtimeEnumConfigsPath = Path.Combine(workspace.Env.Config.GamePath, "reframework/data/usercontent/enums");
            LoadEnums(workspace.Env, runtimeEnumConfigsPath);
        }
        LoadEnums(workspace.Env, EnumFilepath);
    }

    private void AddDefaultConfigs()
    {
        configs["via.GameObject"] = new ClassConfig() {
            StringFormatter = new StringFormatter("{Name}", FormatterSettings.DefaultFormatter)
        };
    }

    private static void LoadEnums(Workspace env, string filepath)
    {
        if (!Directory.Exists(filepath)) return;

        foreach (var file in Directory.EnumerateFiles(filepath, "*.json")) {
            var fs = File.OpenRead(file);
            EnumConfig data;
            try {
                data = JsonSerializer.Deserialize<EnumConfig>(fs, JsonConfig.jsonOptions)!;
                if (data == null || string.IsNullOrEmpty(data.EnumName)) continue;
            } catch (Exception) {
                continue;
            } finally {
                fs.Dispose();
            }

            var desc = env.TypeCache.GetEnumDescriptor(data.EnumName);
            if (desc == EnumDescriptor<int>.Default) {
                // TODO unknown / virtual enum
                continue;
            }

            desc.SetDisplayLabels(data.DisplayLabels);
        }
    }

    public void LoadPatchConfigs(ContentWorkspace workspace)
    {
        LoadConfigsFromDir(workspace, DefinitionFilepath);
        var globalPath = Path.Combine(Path.GetDirectoryName(filepath)!, "global/definitions");
        LoadConfigsFromDir(workspace, globalPath);
    }

    private void LoadConfigsFromDir(ContentWorkspace workspace, string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml")) {
            var fs = File.OpenRead(file).ToMemoryStream();
            var memory = fs.GetBuffer().AsMemory(0, (int)fs.Length);
            var newDict = YamlSerializer.Deserialize<SerializedPatchConfigRoot>(memory, yamlOptions);
            if (newDict == null) continue;

            if (newDict.Types != null) foreach (var (name, customType) in newDict.Types) {
                if (customType.Fields == null || customType.Fields.Count == 0) {
                    throw new Exception($"Unsupported user-defined object config {name}. Must have at least one field");
                }

                var config = customType.ToRuntimeConfig();
                customTypes.Add(name, config);
            }

            if (newDict.Entities != null) foreach (var (name, entity) in newDict.Entities) {
                if (entity.Fields == null || entity.Fields.Count == 0) {
                    throw new Exception($"Unsupported user-defined object config {name}. Must have at least one field");
                }

                var config = entity.ToRuntimeConfig(workspace);
                var shortname = EntityHierarchy.Add(name, config);
                entities.Add(shortname, config);
            }

            if (newDict.Classes != null) foreach (var (cls, config) in newDict.Classes) {
                var rszClass = workspace.Env.RszParser.GetRSZClass(cls);
                if (rszClass == null) {
                    Logger.Debug($"Unknown RSZ class {cls} for game {workspace.Env.Config.Game}");
                    continue;
                }

                if (!configs.TryGetValue(cls, out var runtimeConfig)) {
                    configs[cls] = runtimeConfig = new();
                }

                config.MergeIntoRuntimeConfig(rszClass, runtimeConfig);

                if (config.To_String != null) {
                    var fmt = FormatterSettings.CreateWorkspaceFormatter(workspace);
                    runtimeConfig.StringFormatter = new StringFormatter(config.To_String, fmt);
                }

                if (config.Subclasses != null) {
                    foreach (var (subcls, sub) in config.Subclasses) {
                        if (!configs.TryGetValue(subcls, out var subConfig)) {
                            configs[subcls] = subConfig = new();
                        }
                        runtimeConfig.MergeIntoSubclass(subcls, subConfig);
                        subConfig.StringFormatter ??= runtimeConfig.StringFormatter;
                    }
                }
            }
        }
    }

    private static readonly YamlSerializerOptions yamlOptions = new YamlSerializerOptions {
        NamingConvention = NamingConvention.LowerCamelCase,
    };
}
