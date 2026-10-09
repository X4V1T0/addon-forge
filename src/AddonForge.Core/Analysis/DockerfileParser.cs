using System.Text.RegularExpressions;
using AddonForge.Core.Models;

namespace AddonForge.Core.Analysis;

/// <summary>
/// Deterministic, line-based extraction of EXPOSE/VOLUME/ENV from a
/// Dockerfile. Deliberately simple: it isn't stage-aware (an ENV/ARG from an
/// earlier stage can bleed into a later one it shouldn't), but it does
/// resolve `${VAR}`/`$VAR` references in EXPOSE against preceding ENV/ARG
/// declarations, since `EXPOSE ${PORT}` (paired with an env var that also
/// configures the app's actual listening port) is a common pattern. Good
/// enough as a first signal, refined by ComposeParser when a compose file is
/// also present.
/// </summary>
public static partial class DockerfileParser
{
    [GeneratedRegex(@"^\s*EXPOSE\s+(.+?)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ExposeLine();

    [GeneratedRegex(@"^\s*VOLUME\s+(.+?)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VolumeLine();

    [GeneratedRegex(@"^\s*ENV\s+(.+?)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EnvLine();

    [GeneratedRegex(@"^\s*(?:ENV|ARG)\s+(.+?)\s*(#.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex EnvOrArgLine();

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?::-[^}]*)?\}|\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex VariableReference();

    public static List<PortMapping> ParsePorts(string dockerfileContent)
    {
        var ports = new List<PortMapping>();

        foreach (Match match in ExposeLine().Matches(dockerfileContent))
        {
            // `EXPOSE ${PORT}` is common when the port is also runtime-configurable
            // via an env var - Docker resolves it at build time from a preceding
            // ENV/ARG declaration in the same stage, so we do too.
            var declaredVars = CollectDeclaredVariables(dockerfileContent.AsSpan(0, match.Index));

            foreach (var rawToken in SplitTokens(match.Groups[1].Value))
            {
                var token = ResolveVariables(rawToken, declaredVars);
                var protocol = PortProtocol.Tcp;
                var portPart = token;
                var slashIndex = token.IndexOf('/');
                if (slashIndex >= 0)
                {
                    portPart = token[..slashIndex];
                    if (string.Equals(token[(slashIndex + 1)..], "udp", StringComparison.OrdinalIgnoreCase))
                    {
                        protocol = PortProtocol.Udp;
                    }
                }

                if (int.TryParse(portPart, out var port))
                {
                    ports.Add(new PortMapping { ContainerPort = port, Protocol = protocol });
                }
            }
        }

        return ports;
    }

    /// <summary>Every ENV/ARG NAME=value declared before the given point in the Dockerfile text, last one wins.</summary>
    private static Dictionary<string, string> CollectDeclaredVariables(ReadOnlySpan<char> textBeforePosition)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in EnvOrArgLine().Matches(textBeforePosition.ToString()))
        {
            var raw = match.Groups[1].Value.Trim();
            if (raw.Contains('='))
            {
                foreach (var pair in SplitKeyValueTokens(raw))
                {
                    if (pair.Value is not null)
                    {
                        result[pair.Key] = pair.Value;
                    }
                }
            }
        }

        return result;
    }

    private static string ResolveVariables(string token, Dictionary<string, string> declaredVars) =>
        VariableReference().Replace(token, m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            return declaredVars.TryGetValue(name, out var value) ? value : m.Value;
        });

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)(?:(:?[-+?])([^}]*))?\}|\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex InterpolationReference();

    /// <summary>
    /// Resolves `${VAR}`, `${VAR:-default}` (and `-`/`+`/`?` variants) the way
    /// Docker/compose would, for a value that becomes an option's *default*.
    /// Unlike <see cref="ResolveVariables"/>, an unresolvable reference turns
    /// into its fallback or nothing - never the literal `${VAR}` text, which
    /// would otherwise end up exported verbatim over the image's own value
    /// (e.g. `ENV APP_SERIES=${APP_SERIES}` became a "${APP_SERIES}" default).
    /// </summary>
    public static string ResolveInterpolation(string value, IReadOnlyDictionary<string, string>? declaredVars = null) =>
        InterpolationReference().Replace(value, m =>
        {
            var name = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
            var op = m.Groups[2].Value;
            var word = m.Groups[3].Value;
            var isSet = declaredVars is not null && declaredVars.TryGetValue(name, out var declared) &&
                        (declared.Length > 0 || !op.StartsWith(':'));
            var current = isSet ? declaredVars![name] : "";

            return op.TrimStart(':') switch
            {
                "-" => isSet ? current : word,
                "+" => isSet ? word : "",
                _ => current,
            };
        });

    public static List<VolumeMapping> ParseVolumes(string dockerfileContent)
    {
        var volumes = new List<VolumeMapping>();

        foreach (Match match in VolumeLine().Matches(dockerfileContent))
        {
            var raw = match.Groups[1].Value.Trim();

            // JSON-array form: VOLUME ["/data", "/config"]
            if (raw.StartsWith('['))
            {
                foreach (var path in Regex.Matches(raw, "\"([^\"]+)\"").Select(m => m.Groups[1].Value))
                {
                    volumes.Add(new VolumeMapping { ContainerPath = path });
                }

                continue;
            }

            foreach (var token in SplitTokens(raw))
            {
                volumes.Add(new VolumeMapping { ContainerPath = token.Trim('"') });
            }
        }

        return volumes;
    }

    public static List<EnvVarCandidate> ParseEnvVars(string dockerfileContent)
    {
        var vars = new List<EnvVarCandidate>();

        foreach (Match match in EnvLine().Matches(dockerfileContent))
        {
            var raw = match.Groups[1].Value.Trim();
            var declaredVars = CollectDeclaredVariables(dockerfileContent.AsSpan(0, match.Index));

            // ENV KEY=value [KEY2=value2 ...]
            if (raw.Contains('='))
            {
                foreach (var pair in SplitKeyValueTokens(raw))
                {
                    if (IsUserConfigurable(pair.Key))
                    {
                        var value = pair.Value is null ? null : ResolveInterpolation(pair.Value, declaredVars);
                        vars.Add(BuildCandidate(pair.Key, value, "Dockerfile ENV"));
                    }
                }
            }
            else
            {
                // ENV KEY value  (legacy single-pair form)
                var spaceIndex = raw.IndexOf(' ');
                if (spaceIndex > 0)
                {
                    var key = raw[..spaceIndex].Trim();
                    var value = ResolveInterpolation(raw[(spaceIndex + 1)..].Trim(), declaredVars);
                    if (IsUserConfigurable(key))
                    {
                        vars.Add(BuildCandidate(key, value, "Dockerfile ENV"));
                    }
                }
            }
        }

        return vars;
    }

    /// <summary>
    /// Build/runtime plumbing vars that show up in almost every Dockerfile's
    /// ENV but are never meant for a user to edit (PATH, locale, Python
    /// buffering flags...). Not exhaustive — just the ones common enough to
    /// be worth not cluttering the generated addon's options with.
    /// </summary>
    private static readonly HashSet<string> SystemEnvVarNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PYTHONUNBUFFERED", "PYTHONDONTWRITEBYTECODE", "PYTHONPATH", "DEBIAN_FRONTEND",
        "LANG", "LANGUAGE", "LC_ALL", "HOME", "TERM", "LD_LIBRARY_PATH",
    };

    internal static bool IsUserConfigurable(string name) => !SystemEnvVarNames.Contains(name);

    internal static EnvVarCandidate BuildCandidate(string name, string? defaultValue, string source)
    {
        defaultValue = string.IsNullOrWhiteSpace(defaultValue) ? null : defaultValue.Trim().Trim('"');

        return new EnvVarCandidate
        {
            Name = name,
            DefaultValue = defaultValue,
            InferredType = InferType(name, defaultValue),
            IsAmbiguous = defaultValue is null && !LooksSelfDescribing(name),
            Source = source,
        };
    }

    private static InferredOptionType InferType(string name, string? defaultValue)
    {
        var upperName = name.ToUpperInvariant();

        if (LooksSecret(upperName))
        {
            return InferredOptionType.Password;
        }

        if (upperName.Contains("URL") || upperName.Contains("URI") ||
            (defaultValue is not null && (defaultValue.StartsWith("http://") || defaultValue.StartsWith("https://"))))
        {
            return InferredOptionType.Url;
        }

        if (defaultValue is not null)
        {
            if (bool.TryParse(defaultValue, out _) ||
                defaultValue.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                defaultValue.Equals("false", StringComparison.OrdinalIgnoreCase))
            {
                return InferredOptionType.Bool;
            }

            if (int.TryParse(defaultValue, out _))
            {
                return InferredOptionType.Int;
            }
        }
        else if (upperName.Contains("PORT") || upperName.StartsWith("ENABLE_") || upperName.StartsWith("USE_"))
        {
            // No default but the name still signals a type - not ambiguous.
            return upperName.Contains("PORT") ? InferredOptionType.Int : InferredOptionType.Bool;
        }

        return InferredOptionType.Str;
    }

    private static bool LooksSelfDescribing(string name)
    {
        var upperName = name.ToUpperInvariant();
        return upperName.Contains("PORT") || upperName.StartsWith("ENABLE_") || upperName.StartsWith("USE_") ||
               LooksSecret(upperName) || upperName.Contains("URL") || upperName.Contains("URI");
    }

    /// <summary>
    /// "PASS" has to be a whole word (or part of PASSWORD/PASSWD/PASSPHRASE),
    /// not any substring: AGENT_BYPASS_DB_VOLUME_CHECK is a boolean flag,
    /// not a secret - a plain Contains("PASS") turned it into a password
    /// option, which HA then rejected for its `true` default.
    /// </summary>
    private static bool LooksSecret(string upperName) =>
        upperName.Split('_').Contains("PASS") ||
        upperName.Contains("PASSWORD") || upperName.Contains("PASSWD") || upperName.Contains("PASSPHRASE") ||
        upperName.Contains("SECRET") || upperName.Contains("TOKEN") ||
        upperName.EndsWith("_KEY") || upperName.Contains("APIKEY");

    private static IEnumerable<string> SplitTokens(string value) =>
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<KeyValuePair<string, string?>> SplitKeyValueTokens(string value)
    {
        // Handles `KEY=value KEY2="value with spaces" KEY3=value3`
        foreach (Match m in Regex.Matches(value, "([A-Za-z_][A-Za-z0-9_]*)=(\"[^\"]*\"|'[^']*'|\\S*)"))
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Value.Trim('"', '\'');
            yield return new KeyValuePair<string, string?>(key, string.IsNullOrEmpty(val) ? null : val);
        }
    }
}
