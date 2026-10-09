using System.Text.RegularExpressions;
using AddonForge.Core.Models;

namespace AddonForge.Core.Analysis;

/// <summary>
/// Parses a .env.example file's active (non-commented) KEY=value lines into
/// env var candidates. Many repos document their whole configurable surface
/// here rather than in Dockerfile ENV / compose `environment:` — especially
/// when a container loads its env via `env_file: ./.env` instead of listing
/// each key explicitly in compose, which leaves compose with nothing to see.
/// Commented-out "optional, uncomment to override" lines are deliberately
/// skipped: whether those are meant as real candidates or just documentation
/// is far more ambiguous than an active line.
/// </summary>
public static partial class EnvExampleParser
{
    [GeneratedRegex(@"^([A-Za-z_][A-Za-z0-9_]*)=(.*)$")]
    private static partial Regex AssignmentLine();

    public static List<EnvVarCandidate> Parse(string envExampleContent, string source = ".env.example")
    {
        var vars = new List<EnvVarCandidate>();

        foreach (var rawLine in envExampleContent.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var match = AssignmentLine().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups[1].Value;
            if (!DockerfileParser.IsUserConfigurable(key))
            {
                continue;
            }

            var value = StripInlineComment(match.Groups[2].Value);
            vars.Add(DockerfileParser.BuildCandidate(key, value, source));
        }

        return vars;
    }

    private static string StripInlineComment(string raw)
    {
        var value = raw.Trim();

        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\''))
        {
            var quote = value[0];
            var closingIndex = value.IndexOf(quote, 1);
            if (closingIndex > 0)
            {
                return value[1..closingIndex];
            }
        }

        // Unquoted: a `#` starts a trailing comment, whether or not there was
        // any value before it once the surrounding whitespace is gone (e.g.
        // `ADMIN_PASS=                       # generate one: ...` trims down
        // to just "# generate one: ..." with nothing left before the `#`).
        if (value.StartsWith('#'))
        {
            return string.Empty;
        }

        var hashIndex = value.IndexOf(" #", StringComparison.Ordinal);
        return (hashIndex >= 0 ? value[..hashIndex] : value).Trim();
    }
}
