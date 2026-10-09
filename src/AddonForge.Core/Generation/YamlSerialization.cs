using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AddonForge.Core.Generation;

/// <summary>
/// Home Assistant addon config.yaml uses snake_case keys (ingress_port,
/// panel_icon...). Dictionary-valued fields (options/schema/ports) are
/// left untouched by the naming convention since their keys come from the
/// source repo, not from reflected C# property names.
/// </summary>
internal static class YamlSerialization
{
    public static string ToYaml(object value)
    {
        var serializer = new SerializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            // A string default like "true" or "8080" must stay a string:
            // unquoted it re-parses as a bool/int and HA's schema check
            // ("expected str. Got ...") rejects the whole options block.
            .WithQuotingNecessaryStrings()
            .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
            .Build();

        return serializer.Serialize(value);
    }
}
