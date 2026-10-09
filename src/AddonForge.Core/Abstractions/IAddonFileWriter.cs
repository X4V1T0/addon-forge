namespace AddonForge.Core.Abstractions;

/// <summary>
/// Writes generated addon files to disk. "Staging" is addon-forge's own
/// working area; "published" is the real local addon store folder (mounted
/// via the `addons:rw` map) that Home Assistant's Supervisor scans. Nothing
/// here calls the Supervisor API — the user triggers "check for updates" in
/// HA themselves once files land in the published folder.
///
/// Staging holds a full addon build context (the source repo's tree, since
/// its Dockerfile usually needs more than just itself), not just the handful
/// of text files addon-forge generates — hence paths/listings here are
/// relative and can be nested, and content isn't assumed to be text.
/// </summary>
public interface IAddonFileWriter
{
    /// <summary>Clears and (re)creates the staging directory for this addon, ready to receive a fresh build context. Returns its absolute path.</summary>
    Task<string> PrepareStagingAsync(string slug, CancellationToken ct);

    /// <summary>Writes/overwrites specific text files (relative path -> content) into staging — used for what addon-forge itself generates.</summary>
    Task WriteToStagingAsync(string slug, IReadOnlyDictionary<string, string> files, CancellationToken ct);

    /// <summary>Replaces the published folder's contents with whatever is currently staged.</summary>
    Task PublishFromStagingAsync(string slug, CancellationToken ct);

    /// <summary>Relative paths (forward-slash separated) of every file currently staged.</summary>
    Task<IReadOnlyList<string>> ListStagedFilesAsync(string slug, CancellationToken ct);

    /// <summary>Relative paths (forward-slash separated) of every file currently published.</summary>
    Task<IReadOnlyList<string>> ListPublishedFilesAsync(string slug, CancellationToken ct);

    /// <summary>Text content of one staged file, or null if it doesn't exist or doesn't look like text.</summary>
    Task<string?> ReadStagedFileAsync(string slug, string relativePath, CancellationToken ct);

    /// <summary>Text content of one published file, or null if it doesn't exist or doesn't look like text.</summary>
    Task<string?> ReadPublishedFileAsync(string slug, string relativePath, CancellationToken ct);

    /// <summary>
    /// Best-effort: if the staged addon doesn't already have an icon.png/logo.png
    /// at its root, looks for a real PNG icon/logo elsewhere in the staged tree
    /// (common paths, then a filename scan) and copies it into place. A no-op,
    /// not a failure, when nothing suitable is found - the addon just falls
    /// back to Home Assistant's default icon, same as before this existed.
    /// </summary>
    Task EnsureIconAsync(string slug, CancellationToken ct);

    /// <summary>
    /// Saves a user-uploaded icon for this addon, stored outside staging/published
    /// (both get wiped and rebuilt from scratch on every regeneration) so it
    /// survives every future one. Throws if the bytes don't look like a real
    /// PNG/JPEG. Takes effect once <see cref="ApplyManualIconAsync"/> next runs
    /// as part of generation - callers that want it live immediately (e.g. on
    /// an already-published addon) need to follow up with a regeneration.
    /// </summary>
    Task SetManualIconAsync(string slug, byte[] imageBytes, CancellationToken ct);

    /// <summary>Whether a manual icon (see <see cref="SetManualIconAsync"/>) is set for this addon.</summary>
    Task<bool> HasManualIconAsync(string slug, CancellationToken ct);

    /// <summary>
    /// If a manual icon is set for this addon, overwrites the staged
    /// icon.png/logo.png with it. Always wins over whatever
    /// <see cref="EnsureIconAsync"/> auto-detected - called after it in the
    /// generation pipeline for exactly that reason. A no-op otherwise.
    /// </summary>
    Task ApplyManualIconAsync(string slug, CancellationToken ct);

    /// <summary>
    /// Raw bytes of one published file, with no text-content assumption
    /// (unlike <see cref="ReadPublishedFileAsync"/>) - for images like icon.png.
    /// Null if it doesn't exist.
    /// </summary>
    Task<byte[]?> ReadPublishedBinaryFileAsync(string slug, string relativePath, CancellationToken ct);

    /// <summary>
    /// Permanently deletes both the staged and published folders for this
    /// addon — unlike just forgetting it (which only stops addon-forge's own
    /// tracking), this actually removes /addons/&lt;slug&gt; from disk. A no-op if
    /// neither exists. Irreversible; callers are responsible for confirming
    /// with the user first.
    /// </summary>
    Task DeleteAsync(string slug, CancellationToken ct);
}
