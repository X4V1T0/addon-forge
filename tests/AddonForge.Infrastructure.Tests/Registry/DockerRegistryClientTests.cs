using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AddonForge.Core.Models;
using AddonForge.Infrastructure.Registry;
using Xunit;

namespace AddonForge.Infrastructure.Tests.Registry;

public class DockerRegistryClientTests
{
    // Every route is keyed by path+query (never host) - tags/list pagination
    // reuses the same path with a different query string per page, so query
    // has to be part of the key; nothing here needs to inspect the Accept
    // header too, since every manifest/blob/tags-list URL is already distinct.
    private sealed class FakeRegistryHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> jsonRoutes = new();
        private readonly Dictionary<string, string> nextPageByPath = new();

        public List<string> RequestedPaths { get; } = [];

        public bool ChallengeFirstRequest { get; set; }

        private bool challenged;

        public void MapJson(string path, string json) => jsonRoutes[path] = json;

        public void MapNextPage(string path, string nextPath) => nextPageByPath[path] = nextPath;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            // Keyed by path+query, not just path - tags/list pagination is
            // the same path with a different query string each page.
            var key = uri.PathAndQuery;
            RequestedPaths.Add(key);

            if (uri.AbsolutePath == "/token")
            {
                return Task.FromResult(JsonResponse("""{"token":"fake-token"}"""));
            }

            if (ChallengeFirstRequest && !challenged && request.Headers.Authorization is null)
            {
                challenged = true;
                var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
                response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue(
                    "Bearer",
                    "realm=\"https://registry.example.org/token\",service=\"registry.example.org\"," +
                    "scope=\"repository:example/agent:pull\""));
                return Task.FromResult(response);
            }

            if (!jsonRoutes.TryGetValue(key, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            var result = JsonResponse(body);
            if (nextPageByPath.TryGetValue(key, out var next))
            {
                result.Headers.Add("Link", $"<https://registry.example.org{next}>; rel=\"next\"");
            }

            return Task.FromResult(result);
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    private static (DockerRegistryClient Client, FakeRegistryHandler Handler) CreateClient()
    {
        var handler = new FakeRegistryHandler();
        return (new DockerRegistryClient(new HttpClient(handler)), handler);
    }

    [Fact]
    public async Task InspectAsync_ManifestList_ResolvesAmd64EntryAndParsesConfig()
    {
        var (client, handler) = CreateClient();
        handler.ChallengeFirstRequest = true;

        handler.MapJson("/v2/example/agent/manifests/v1.6.3", """
            {
              "mediaType": "application/vnd.docker.distribution.manifest.list.v2+json",
              "manifests": [
                { "digest": "sha256:amd64digest", "platform": { "architecture": "amd64", "os": "linux" } },
                { "digest": "sha256:arm64digest", "platform": { "architecture": "arm64", "os": "linux" } },
                { "digest": "sha256:attdigest", "platform": { "architecture": "unknown", "os": "unknown" } }
              ]
            }
            """);
        handler.MapJson("/v2/example/agent/manifests/sha256:amd64digest", """
            { "config": { "digest": "sha256:configdigest" } }
            """);
        handler.MapJson("/v2/example/agent/blobs/sha256:configdigest", """
            {
              "architecture": "amd64",
              "config": {
                "Env": ["TZ=UTC", "PATH=/usr/bin"],
                "Entrypoint": ["/bin/bash", "/docker_start.sh"],
                "ExposedPorts": { "8080/tcp": {} },
                "Volumes": { "/etc/agent": {} }
              }
            }
            """);

        var reference = ImageReference.Parse("registry.example.org/example/agent:v1.6.3");
        var result = await client.InspectAsync(reference, CancellationToken.None);

        Assert.Equal("sha256:configdigest", result.Digest);
        // Home Assistant's own arch naming, not Docker's raw platform names
        // (its "arm64" is HA's "aarch64") - see HomeAssistantArch.
        Assert.Equal(["aarch64", "amd64"], result.Architectures);
        Assert.Equal(8080, Assert.Single(result.Ports).ContainerPort);
        Assert.Equal("/etc/agent", Assert.Single(result.Volumes).ContainerPath);
        Assert.Contains("TZ=UTC", result.RawEnv);
        Assert.Equal(["/bin/bash", "/docker_start.sh"], result.Entrypoint!);

        // The 401 challenge only fires once - the cached token must cover
        // every later request against the same repository.
        Assert.Equal(4, handler.RequestedPaths.Count(p => !p.StartsWith("/token", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task InspectAsync_SingleManifestNotAList_UsesConfigBlobsOwnArchitecture()
    {
        var (client, handler) = CreateClient();

        handler.MapJson("/v2/alpine/manifests/3.20", """
            { "config": { "digest": "sha256:configdigest" } }
            """);
        handler.MapJson("/v2/alpine/blobs/sha256:configdigest", """
            { "architecture": "amd64", "config": { "Cmd": ["/bin/sh"] } }
            """);

        var reference = ImageReference.Parse("registry.example.org/alpine:3.20");
        var result = await client.InspectAsync(reference, CancellationToken.None);

        Assert.Equal(["amd64"], result.Architectures);
        Assert.Equal(["/bin/sh"], result.Cmd!);
        Assert.Empty(result.Ports);
        Assert.Empty(result.Volumes);
    }

    [Fact]
    public async Task InspectAsync_ManifestListWithNoAmd64Entry_Throws()
    {
        var (client, handler) = CreateClient();
        handler.MapJson("/v2/alpine/manifests/3.20", """
            {
              "manifests": [
                { "digest": "sha256:armdigest", "platform": { "architecture": "arm64", "os": "linux" } }
              ]
            }
            """);

        var reference = ImageReference.Parse("registry.example.org/alpine:3.20");
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InspectAsync(reference, CancellationToken.None));
    }

    [Fact]
    public async Task ListTagsAsync_FollowsLinkHeaderPagination()
    {
        var (client, handler) = CreateClient();
        handler.MapJson("/v2/alpine/tags/list?n=100", """{"tags":["1.0.0","1.1.0"]}""");
        handler.MapNextPage("/v2/alpine/tags/list?n=100", "/v2/alpine/tags/list?last=1.1.0");
        handler.MapJson("/v2/alpine/tags/list?last=1.1.0", """{"tags":["1.2.0"]}""");

        var reference = ImageReference.Parse("registry.example.org/alpine");
        var tags = await client.ListTagsAsync(reference, CancellationToken.None);

        Assert.Equal(["1.0.0", "1.1.0", "1.2.0"], tags);
    }

    [Fact]
    public async Task GetLatestTagAsync_PicksHighestVersionTag()
    {
        var (client, handler) = CreateClient();
        handler.MapJson("/v2/alpine/tags/list?n=100", """{"tags":["latest","1.0.0","2.1.0","edge"]}""");

        var reference = ImageReference.Parse("registry.example.org/alpine");
        var latest = await client.GetLatestTagAsync(reference, CancellationToken.None);

        Assert.Equal("2.1.0", latest);
    }
}
