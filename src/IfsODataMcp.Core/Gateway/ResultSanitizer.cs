using System.Text.Json.Nodes;
using IfsODataMcp.Core.Metadata;
using IfsODataMcp.Core.Policy;

namespace IfsODataMcp.Core.Gateway;

/// <summary>
/// Defence in depth for results. The URL already selects only allowed properties; this pass removes
/// denied properties and OData control annotations from whatever the server actually returned.
/// </summary>
public sealed class ResultSanitizer(AccessPolicy policy, ServiceMetadata metadata)
{
    public JsonObject Sanitize(JsonObject entity, EntityTypeInfo type)
    {
        Clean(entity, type);
        return entity;
    }

    private void Clean(JsonObject obj, EntityTypeInfo? type)
    {
        foreach (var key in obj.Select(p => p.Key).ToList())
        {
            if (key.StartsWith('@') || key.Contains("@odata", StringComparison.Ordinal))
            {
                obj.Remove(key);
                continue;
            }

            var denied = type is not null ? policy.IsDenied(type, key) : policy.IsDeniedEverywhere(key);
            if (denied)
            {
                obj.Remove(key);
                continue;
            }

            var nested = type?.FindNavigation(key) is { } navigation ? metadata.FindType(navigation.TargetType) : null;
            switch (obj[key])
            {
                case JsonObject child:
                    Clean(child, nested);
                    break;
                case JsonArray children:
                    foreach (var item in children.OfType<JsonObject>())
                    {
                        Clean(item, nested);
                    }

                    break;
            }
        }
    }
}
