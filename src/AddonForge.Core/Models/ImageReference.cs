namespace AddonForge.Core.Models;

/// <summary>
/// A parsed Docker/OCI image reference, e.g. "docker.io/example/agent:v1.6.3"
/// or a bare Docker Hub name like "redis:7" (implicitly "library/redis:7") -
/// the same shapes `docker pull` itself accepts. This is the image-source
/// equivalent of a git repo URL: what the user pastes in when adding an addon
/// from a pre-built image instead of a source repo (see SourceKind).
/// </summary>
public sealed record ImageReference(string Registry, string Repository, string? Tag, string? Digest)
{
    private const string DockerHubAlias = "docker.io";

    /// <summary>
    /// docker.io itself doesn't serve the Registry HTTP API v2 - actual pulls
    /// (and our own inspection calls) go to registry-1.docker.io instead.
    /// Confirmed against how `docker pull` itself resolves the alias.
    /// </summary>
    public string ApiHost => Registry.Equals(DockerHubAlias, StringComparison.OrdinalIgnoreCase)
        ? "registry-1.docker.io"
        : Registry;

    /// <summary>What to actually request from the registry - a pinned digest wins over a tag; "latest" only as a last resort when neither was given.</summary>
    public string TagOrDigest => Digest ?? Tag ?? "latest";

    public ImageReference WithTag(string tag) => this with { Tag = tag, Digest = null };

    /// <summary>Full "[registry/]repository[:tag|@digest]" form, as it belongs in a Dockerfile's FROM line or config.yaml's image: key.</summary>
    public override string ToString()
    {
        var registryPrefix = Registry.Equals(DockerHubAlias, StringComparison.OrdinalIgnoreCase) ? "" : $"{Registry}/";
        var suffix = Digest is not null ? $"@{Digest}" : Tag is not null ? $":{Tag}" : "";
        return $"{registryPrefix}{Repository}{suffix}";
    }

    /// <summary>
    /// Parses a user-typed image reference. A registry host is distinguished
    /// from a Docker Hub namespace (e.g. "example") by Docker's own
    /// reference grammar: it contains a dot, a port (colon), or is literally
    /// "localhost" - anything else is assumed to be a Docker Hub repository,
    /// with the implicit "library/" namespace added for an unqualified name
    /// (just "redis", not "someuser/redis").
    /// </summary>
    public static ImageReference Parse(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            throw new FormatException("Image reference is empty.");
        }

        string? digest = null;
        var atIndex = value.IndexOf('@');
        if (atIndex >= 0)
        {
            digest = value[(atIndex + 1)..];
            value = value[..atIndex];
        }

        var firstSlash = value.IndexOf('/');
        var firstSegment = firstSlash >= 0 ? value[..firstSlash] : null;
        var hasRegistry = firstSegment is not null &&
            (firstSegment.Contains('.') || firstSegment.Contains(':') || firstSegment == "localhost");

        var registry = hasRegistry ? firstSegment! : DockerHubAlias;
        var rest = hasRegistry ? value[(firstSlash + 1)..] : value;

        string? tag = null;
        var lastSlash = rest.LastIndexOf('/');
        var colonIndex = rest.LastIndexOf(':');
        if (colonIndex > lastSlash)
        {
            tag = rest[(colonIndex + 1)..];
            rest = rest[..colonIndex];
        }

        var repository = !hasRegistry && !rest.Contains('/') ? $"library/{rest}" : rest;
        if (repository.Length == 0)
        {
            throw new FormatException($"Could not parse an image repository out of '{raw}'.");
        }

        return new ImageReference(registry, repository, tag, digest);
    }
}
