namespace AddonForge.Infrastructure.Git;

/// <summary>Bound from this addon's own config.yaml `options.github` group.</summary>
public class GitOptions
{
    /// <summary>
    /// Optional token for private repos. Only applied to github.com HTTPS
    /// URLs; public repos work anonymously via `git ls-remote`/`git clone`,
    /// which isn't subject to the GitHub REST API's anonymous rate limit.
    /// </summary>
    public string Token { get; set; } = string.Empty;
}
