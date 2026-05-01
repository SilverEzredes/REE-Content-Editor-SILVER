using ReeLib;

namespace ContentPatcher;

public static partial class RszFieldCache
{
    /// <summary>
    /// via.dynamics.GpuCloth
    /// </summary>
    [RszAccessor("via.dynamics.GpuCloth", [nameof(PreDD2)], GamesExclude = true)]
    public static class GpuCloth
    {
        public static readonly RszFieldAccessorFirstFallbacks<string> Resource =
            First<string>([
                f => f.name == "Resource",
                f => f.type is RszFieldType.Resource or RszFieldType.String
            ])
            .Resource("via.dynamics.GpuClothResourceHolder")
            .Rename();
    }
}
