namespace AddonForge.Infrastructure.Publishing;

public class FileSystemAddonFileWriterOptions
{
    /// <summary>Root folder for addon-forge's own working files (e.g. /data/staging inside the container).</summary>
    public string StagingRoot { get; set; } = "/data/staging";

    /// <summary>Root of the real local addon store, mounted read-write via the `addons:rw` map (typically /addons).</summary>
    public string PublishedRoot { get; set; } = "/addons";

    /// <summary>
    /// Root for manually-uploaded icons, kept outside both staging and published
    /// (both get wiped and rebuilt from scratch on every regeneration) so a
    /// user's icon choice survives every future one.
    /// </summary>
    public string IconsRoot { get; set; } = "/data/icons";
}
