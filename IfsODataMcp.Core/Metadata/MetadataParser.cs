using System.Xml;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;
using Microsoft.OData.Edm.Validation;

namespace IfsODataMcp.Core.Metadata;

public static class MetadataParser
{
    public static ServiceMetadata Parse(string csdlXml)
    {
        // $metadata comes from a remote server, so DTDs and external entities stay off.
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };

        using var stringReader = new StringReader(csdlXml);
        using var xmlReader = XmlReader.Create(stringReader, settings);

        if (!CsdlReader.TryParse(xmlReader, out IEdmModel? model, out IEnumerable<EdmError> errors))
        {
            var first = errors.FirstOrDefault()?.ErrorMessage ?? "unknown error";
            throw new QueryValidationException($"The service metadata could not be parsed: {first}");
        }

        var types = new Dictionary<string, EntityTypeInfo>(StringComparer.Ordinal);
        foreach (var entityType in model.SchemaElements.OfType<IEdmEntityType>())
        {
            types[entityType.Name] = ToTypeInfo(entityType);
        }

        var sets = model.EntityContainer?.Elements.OfType<IEdmEntitySet>()
            .Select(s => (s.Name, s.EntityType.Name))
            .ToList() ?? [];

        return new ServiceMetadata(types.Values, sets);
    }

    private static EntityTypeInfo ToTypeInfo(IEdmEntityType type)
    {
        var keyNames = type.Key().Select(k => k.Name).ToHashSet(StringComparer.Ordinal);

        var properties = type.StructuralProperties()
            .Select(p => new PropertyInfo(p.Name, p.Type.Definition.FullTypeName(), p.Type.IsNullable, keyNames.Contains(p.Name)))
            .ToList();

        var navigations = type.NavigationProperties()
            .Select(n => new NavigationInfo(n.Name, n.ToEntityType().Name, n.Type.IsCollection()))
            .ToList();

        return new EntityTypeInfo(type.Name, properties, navigations);
    }
}
