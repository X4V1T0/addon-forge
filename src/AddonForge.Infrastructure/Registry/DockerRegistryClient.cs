using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AddonForge.Core.Abstractions;
using AddonForge.Core.Generation;
using AddonForge.Core.Models;
using AddonForge.Core.Versioning;

namespace AddonForge.Infrastructure.Registry;

/// <summary>
/// Talks to a Docker/OCI registry's plain HTTP Registry API v2 directly - no
/// docker daemon, no pull, no local image cache. Public images only: the auth
/// flow implemented here is the standard, registry-agnostic Bearer
/// challenge/response every public registry (Docker Hub, ghcr.io, quay.io...)
/// answers a 401 with, requesting an anonymous pull-scoped token from
/// whichever realm the registry itself names - there's nothing Docker
/// Hub-specific or ghcr.io-specific hardcoded here at all.
/// </summary>
public partial class DockerRegistryClient(HttpClient httpClient) : IContainerRegistryClient
{
    private const string ManifestAcceptHeader =
        "application/vnd.docker.distribution.manifest.list.v2+json, " +
        "application/vnd.docker.distribution.manifest.v2+json, " +
        "application/vnd.oci.image.index.v1+json, " +
        "application/vnd.oci.image.manifest.v1+json";

    private const string ConfigAcceptHeader =
        "application/vnd.docker.container.image.v1+json, application/vnd.oci.image.config.v1+json";

    /// <summary>Per-repository pull tokens, so a single Inspect (manifest, maybe a sub-manifest, then a config blob) doesn't re-authenticate on every request. Not expiry-aware - this client is only ever used for a short-lived operation, never held across a token's lifetime.</summary>
    private readonly ConcurrentDictionary<string, string> tokenCache = new();

    public async Task<ImageInspectionResult> InspectAsync(ImageReference reference, CancellationToken ct)
    {
        var topManifest = await GetJsonWithAuthAsync<ManifestDto>(
            ManifestUrl(reference, reference.TagOrDigest), ManifestAcceptHeader, reference.Repository, ct);

        List<string> architectures;
        ManifestConfigRefDto configRef;

        if (topManifest.Manifests is { Count: > 0 })
        {
            // A manifest list/OCI index - one entry per platform this image
            // actually publishes. Record all of them, translated to Home
            // Assistant's own arch naming (its `arch:` is set from this - see
            // AddonConfigGenerator) - Docker's "arm64" is HA's "aarch64", and
            // a platform with no HA equivalent at all is dropped rather than
            // passed through raw (the Supervisor's schema rejects anything
            // outside its own five names outright). Then resolve down to the
            // one this addon is actually built from: linux/amd64 only, for
            // v1 (same scope as the git-sourced build path).
            architectures = [.. topManifest.Manifests
                .Where(m => m.Platform is { Os: "linux" })
                .Select(m => HomeAssistantArch.Map(m.Platform!.Architecture, m.Platform.Variant))
                .Where(a => a is not null)
                .Select(a => a!)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(a => a, StringComparer.Ordinal)];

            var amd64Entry = topManifest.Manifests.FirstOrDefault(m => m.Platform is { Os: "linux", Architecture: "amd64" })
                ?? throw new InvalidOperationException($"'{reference}' has no linux/amd64 build.");

            var subManifest = await GetJsonWithAuthAsync<ManifestDto>(
                ManifestUrl(reference, amd64Entry.Digest), ManifestAcceptHeader, reference.Repository, ct);
            configRef = subManifest.Config
                ?? throw new InvalidOperationException($"'{reference}' (linux/amd64) has no image config.");
        }
        else
        {
            configRef = topManifest.Config
                ?? throw new InvalidOperationException($"'{reference}' has no image config - not a container image?");
            architectures = []; // filled in below, from the config blob's own "architecture" field.
        }

        var config = await GetJsonWithAuthAsync<ImageConfigDto>(
            BlobUrl(reference, configRef.Digest), ConfigAcceptHeader, reference.Repository, ct);

        if (architectures.Count == 0 && !string.IsNullOrEmpty(config.Architecture) &&
            HomeAssistantArch.Map(config.Architecture, config.Variant) is { } mapped)
        {
            architectures = [mapped];
        }

        var inner = config.Config;
        return new ImageInspectionResult
        {
            Digest = configRef.Digest,
            Architectures = architectures,
            Ports = ParsePorts(inner?.ExposedPorts),
            Volumes = ParseVolumes(inner?.Volumes),
            RawEnv = inner?.Env ?? [],
            Entrypoint = inner?.Entrypoint,
            Cmd = inner?.Cmd,
        };
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(ImageReference reference, CancellationToken ct)
    {
        var tags = new List<string>();
        var url = $"https://{reference.ApiHost}/v2/{reference.Repository}/tags/list?n=100";

        // A generous cap against a pathological/looping Link header - no real
        // registry needs more pages than this for a single repository.
        for (var page = 0; url is not null && page < 20; page++)
        {
            using var response = await GetWithAuthAsync(new Uri(url), "application/json", reference.Repository, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"Listing tags for '{reference}' failed with {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<TagsListDto>(ct);
            if (payload?.Tags is { Count: > 0 })
            {
                tags.AddRange(payload.Tags);
            }

            url = NextPageUrl(response, url);
        }

        return tags;
    }

    public async Task<string?> GetLatestTagAsync(ImageReference reference, CancellationToken ct) =>
        ImageTagPicker.PickLatest(await ListTagsAsync(reference, ct));

    private static string ManifestUrl(ImageReference reference, string tagOrDigest) =>
        $"https://{reference.ApiHost}/v2/{reference.Repository}/manifests/{tagOrDigest}";

    private static string BlobUrl(ImageReference reference, string digest) =>
        $"https://{reference.ApiHost}/v2/{reference.Repository}/blobs/{digest}";

    private static List<PortMapping> ParsePorts(Dictionary<string, object>? exposedPorts)
    {
        if (exposedPorts is null)
        {
            return [];
        }

        var result = new List<PortMapping>();
        foreach (var key in exposedPorts.Keys)
        {
            var slash = key.IndexOf('/');
            var portPart = slash >= 0 ? key[..slash] : key;
            var protocol = slash >= 0 && key[(slash + 1)..].Equals("udp", StringComparison.OrdinalIgnoreCase)
                ? PortProtocol.Udp
                : PortProtocol.Tcp;

            if (int.TryParse(portPart, out var port))
            {
                result.Add(new PortMapping { ContainerPort = port, Protocol = protocol });
            }
        }

        return [.. result.OrderBy(p => p.ContainerPort)];
    }

    private static List<VolumeMapping> ParseVolumes(Dictionary<string, object>? volumes) =>
        volumes is null
            ? []
            : [.. volumes.Keys.Select(k => new VolumeMapping { ContainerPath = k }).OrderBy(v => v.ContainerPath, StringComparer.Ordinal)];

    /// <summary>RFC 5988 pagination: `Link: &lt;url&gt;; rel="next"`, resolved against the request it came from since registries may return a relative one.</summary>
    private static string? NextPageUrl(HttpResponseMessage response, string requestUrl)
    {
        if (!response.Headers.TryGetValues("Link", out var values))
        {
            return null;
        }

        var match = LinkNextPattern().Match(values.First());
        if (!match.Success)
        {
            return null;
        }

        return Uri.TryCreate(new Uri(requestUrl), match.Groups[1].Value, out var resolved) ? resolved.ToString() : null;
    }

    [GeneratedRegex("<([^>]+)>;\\s*rel=\"next\"")]
    private static partial Regex LinkNextPattern();

    private async Task<T> GetJsonWithAuthAsync<T>(string url, string accept, string repository, CancellationToken ct)
    {
        using var response = await GetWithAuthAsync(new Uri(url), accept, repository, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Registry request to {url} failed with {(int)response.StatusCode}: {Truncate(body)}");
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidOperationException($"Registry returned an empty response for {url}.");
    }

    /// <summary>
    /// Sends the request with whatever pull token is cached for this
    /// repository (none, the first time); on a 401, parses the registry's own
    /// `WWW-Authenticate: Bearer realm=...,service=...,scope=...` challenge,
    /// fetches an anonymous token from exactly the realm/service/scope it
    /// named, caches it, and retries once. This is the entire auth flow every
    /// public registry needs - none of it is specific to any one registry.
    /// </summary>
    private async Task<HttpResponseMessage> GetWithAuthAsync(Uri uri, string accept, string repository, CancellationToken ct)
    {
        HttpRequestMessage BuildRequest(string? token)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Accept.ParseAdd(accept);
            if (token is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            return request;
        }

        tokenCache.TryGetValue(repository, out var cachedToken);
        var response = await httpClient.SendAsync(BuildRequest(cachedToken), ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var challenge = response.Headers.WwwAuthenticate
            .FirstOrDefault(h => string.Equals(h.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase));
        response.Dispose();

        if (challenge?.Parameter is null)
        {
            throw new InvalidOperationException($"{uri.Host} returned 401 with no Bearer challenge to authenticate against.");
        }

        var token = await FetchTokenAsync(challenge.Parameter, ct);
        tokenCache[repository] = token;

        return await httpClient.SendAsync(BuildRequest(token), ct);
    }

    private async Task<string> FetchTokenAsync(string challengeParameter, CancellationToken ct)
    {
        var parameters = ChallengeParamPattern().Matches(challengeParameter)
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.OrdinalIgnoreCase);

        if (!parameters.TryGetValue("realm", out var realm))
        {
            throw new InvalidOperationException("Registry auth challenge had no realm to fetch a token from.");
        }

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service))
        {
            query.Add($"service={Uri.EscapeDataString(service)}");
        }

        if (parameters.TryGetValue("scope", out var scope))
        {
            query.Add($"scope={Uri.EscapeDataString(scope)}");
        }

        var tokenUrl = query.Count > 0 ? $"{realm}?{string.Join('&', query)}" : realm;

        using var response = await httpClient.GetAsync(tokenUrl, ct);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<TokenResponseDto>(ct);
        return payload?.Token ?? payload?.AccessToken
            ?? throw new InvalidOperationException($"Registry token endpoint {realm} returned no token.");
    }

    [GeneratedRegex("(\\w+)=\"([^\"]*)\"")]
    private static partial Regex ChallengeParamPattern();

    private static string Truncate(string value) => value.Length > 300 ? value[..300] : value;

    private sealed record ManifestDto(
        [property: JsonPropertyName("manifests")] List<ManifestListEntryDto>? Manifests,
        [property: JsonPropertyName("config")] ManifestConfigRefDto? Config);

    private sealed record ManifestListEntryDto(
        [property: JsonPropertyName("digest")] string Digest,
        [property: JsonPropertyName("platform")] PlatformDto? Platform);

    private sealed record PlatformDto(
        [property: JsonPropertyName("architecture")] string Architecture,
        [property: JsonPropertyName("os")] string Os,
        [property: JsonPropertyName("variant")] string? Variant);

    private sealed record ManifestConfigRefDto(
        [property: JsonPropertyName("digest")] string Digest);

    /// <summary>
    /// The image config blob's own shape (OCI Image Config spec) - note the
    /// PascalCase field names under `config`, unlike everything else in the
    /// registry API: this one really is capitalized that way on the wire
    /// (the same shape `docker inspect` surfaces as `.Config.Env` etc.).
    /// </summary>
    private sealed record ImageConfigDto(
        [property: JsonPropertyName("architecture")] string? Architecture,
        [property: JsonPropertyName("variant")] string? Variant,
        [property: JsonPropertyName("config")] ImageConfigInnerDto? Config);

    private sealed record ImageConfigInnerDto(
        [property: JsonPropertyName("Env")] List<string>? Env,
        [property: JsonPropertyName("Entrypoint")] List<string>? Entrypoint,
        [property: JsonPropertyName("Cmd")] List<string>? Cmd,
        [property: JsonPropertyName("Volumes")] Dictionary<string, object>? Volumes,
        [property: JsonPropertyName("ExposedPorts")] Dictionary<string, object>? ExposedPorts);

    private sealed record TagsListDto(
        [property: JsonPropertyName("tags")] List<string>? Tags);

    private sealed record TokenResponseDto(
        [property: JsonPropertyName("token")] string? Token,
        [property: JsonPropertyName("access_token")] string? AccessToken);
}
