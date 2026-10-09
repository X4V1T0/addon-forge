using AddonForge.Core.Generation;
using Xunit;

namespace AddonForge.Core.Tests.Generation;

public class EntrypointWrapperGeneratorTests
{
    [Fact]
    public void ExtractPlaceholderNames_FindsAllDistinctNames()
    {
        const string template = "weather:\n  location: ${WEATHER_LOCATION}\n  key: ${WEATHER_API_KEY}\n  again: ${WEATHER_LOCATION}\n";

        var names = EntrypointWrapperGenerator.ExtractPlaceholderNames(template);

        Assert.Equal(["WEATHER_LOCATION", "WEATHER_API_KEY"], names);
    }

    [Fact]
    public void ExtractPlaceholderNames_NoPlaceholders_ReturnsEmpty()
    {
        Assert.Empty(EntrypointWrapperGenerator.ExtractPlaceholderNames("static: content\nno placeholders here\n"));
    }

    [Fact]
    public void FinalStageHasNoShell_LastFromIsScratch_ReturnsTrue()
    {
        const string dockerfile = "FROM golang:1 AS builder\nRUN go build -o app .\nFROM scratch\nCOPY --from=builder /app /app\nENTRYPOINT [\"/app\"]\n";

        Assert.True(EntrypointWrapperGenerator.FinalStageHasNoShell(dockerfile));
    }

    [Fact]
    public void FinalStageHasNoShell_BuilderStageIsScratchButFinalIsNot_ReturnsFalse()
    {
        const string dockerfile = "FROM scratch AS export\nFROM alpine\nCOPY --from=export /x /x\n";

        Assert.False(EntrypointWrapperGenerator.FinalStageHasNoShell(dockerfile));
    }

    // Regression coverage for a real build failure: example-app's final
    // stage is `FROM gcr.io/distroless/static-debian13:nonroot` - not
    // literally "scratch", but just as shell-less. Wrapping it anyway
    // appended a RUN line that needs /bin/sh, which doesn't exist there, so
    // the Supervisor's build failed on that RUN instruction.
    [Fact]
    public void FinalStageHasNoShell_DistrolessImage_ReturnsTrue()
    {
        const string dockerfile =
            "FROM golang:1 AS build\nRUN go build -o /app .\n" +
            "FROM gcr.io/distroless/static-debian13:nonroot\nCOPY --from=build /app /app\nENTRYPOINT [\"/app\"]\n";

        Assert.True(EntrypointWrapperGenerator.FinalStageHasNoShell(dockerfile));
    }

    // The `--platform=...` flag on a multi-stage FROM must not be mistaken
    // for the image reference itself (it comes first, before the real image).
    [Fact]
    public void FinalStageHasNoShell_FromLineHasPlatformFlag_StillReadsRealImage()
    {
        const string dockerfile =
            "FROM --platform=$BUILDPLATFORM golang:1.27-bookworm AS build\nRUN go build -o /app .\n" +
            "FROM --platform=$BUILDPLATFORM alpine\nCOPY --from=build /app /app\nENTRYPOINT [\"/app\"]\n";

        Assert.False(EntrypointWrapperGenerator.FinalStageHasNoShell(dockerfile));
    }

    [Fact]
    public void ApplyDistrolessDebugVariant_NonrootTag_SwitchesToDebugNonroot()
    {
        const string dockerfile =
            "FROM golang:1 AS build\nRUN go build -o /app .\n" +
            "FROM gcr.io/distroless/static-debian13:nonroot\nCOPY --from=build /app /app\nENTRYPOINT [\"/app\"]\n";

        var rewritten = EntrypointWrapperGenerator.ApplyDistrolessDebugVariant(dockerfile);

        Assert.Contains("FROM gcr.io/distroless/static-debian13:debug-nonroot", rewritten);
        Assert.False(EntrypointWrapperGenerator.FinalStageHasNoShell(rewritten));
        // The earlier build stage must be untouched.
        Assert.Contains("FROM golang:1 AS build", rewritten);
    }

    [Fact]
    public void ApplyDistrolessDebugVariant_NoTag_SwitchesToPlainDebug()
    {
        const string dockerfile = "FROM gcr.io/distroless/static-debian13\nENTRYPOINT [\"/app\"]\n";

        var rewritten = EntrypointWrapperGenerator.ApplyDistrolessDebugVariant(dockerfile);

        Assert.Contains("FROM gcr.io/distroless/static-debian13:debug", rewritten);
        Assert.DoesNotContain(":debug-nonroot", rewritten);
    }

    [Fact]
    public void ApplyDistrolessDebugVariant_AlreadyOnDebugTag_LeavesUnchanged()
    {
        const string dockerfile = "FROM gcr.io/distroless/static-debian13:debug-nonroot\nENTRYPOINT [\"/app\"]\n";

        Assert.Equal(dockerfile, EntrypointWrapperGenerator.ApplyDistrolessDebugVariant(dockerfile));
    }

    [Fact]
    public void ApplyDistrolessDebugVariant_NotDistroless_LeavesUnchanged()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/app\"]\n";

        Assert.Equal(dockerfile, EntrypointWrapperGenerator.ApplyDistrolessDebugVariant(dockerfile));
    }

    [Fact]
    public void ApplyDistrolessDebugVariant_ScratchImage_LeavesUnchanged()
    {
        // scratch has no debug variant to switch to at all - unlike
        // distroless, there's nothing safe to rewrite it to.
        const string dockerfile = "FROM scratch\nENTRYPOINT [\"/app\"]\n";

        Assert.Equal(dockerfile, EntrypointWrapperGenerator.ApplyDistrolessDebugVariant(dockerfile));
        Assert.True(EntrypointWrapperGenerator.FinalStageHasNoShell(dockerfile));
    }

    [Fact]
    public void ApplyDistrolessDebugVariant_PinnedByDigest_LeavesUnchanged()
    {
        // Can't safely guess an equivalent debug variant's digest.
        const string dockerfile =
            "FROM gcr.io/distroless/static-debian13@sha256:1111111111111111111111111111111111111111111111111111111111111111\nENTRYPOINT [\"/app\"]\n";

        Assert.Equal(dockerfile, EntrypointWrapperGenerator.ApplyDistrolessDebugVariant(dockerfile));
    }

    [Fact]
    public void IsDistrolessDebugFinalStage_DebugTag_ReturnsTrue()
    {
        const string dockerfile = "FROM gcr.io/distroless/static-debian13:debug-nonroot\nENTRYPOINT [\"/app\"]\n";

        Assert.True(EntrypointWrapperGenerator.IsDistrolessDebugFinalStage(dockerfile));
    }

    [Fact]
    public void IsDistrolessDebugFinalStage_NonDebugDistroless_ReturnsFalse()
    {
        const string dockerfile = "FROM gcr.io/distroless/static-debian13:nonroot\nENTRYPOINT [\"/app\"]\n";

        Assert.False(EntrypointWrapperGenerator.IsDistrolessDebugFinalStage(dockerfile));
    }

    [Fact]
    public void IsDistrolessDebugFinalStage_NotDistroless_ReturnsFalse()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/app\"]\n";

        Assert.False(EntrypointWrapperGenerator.IsDistrolessDebugFinalStage(dockerfile));
    }

    // Regression coverage for a real build failure: distroless's "debug" tag
    // ships busybox, not /bin/sh - Docker's shell-form RUN always invokes
    // /bin/sh literally (never resolved via PATH), so a RUN-based wrapper
    // install failed with "/bin/sh": stat /bin/sh: no such file or
    // directory. Fixed by never using RUN at all (see
    // BuildDockerfileAppendix_NeverUsesRunOrSwitchesUser below) and having
    // the script itself exec via /busybox/sh instead of /bin/sh.
    [Fact]
    public void BuildWrapperScript_DistrolessDebugTools_UsesBusyboxShellAndCopiedJq()
    {
        const string dockerfile = "FROM gcr.io/distroless/static-debian13:debug-nonroot\nENTRYPOINT [\"/app\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile, useDistrolessDebugTools: true);

        Assert.NotNull(script);
        Assert.StartsWith("#!/busybox/sh\n", script);
        Assert.Contains("/addon-forge-jq -r 'to_entries[]", script);
        // Must not depend on anything a package manager would have installed.
        Assert.DoesNotContain("command -v jq", script);
        Assert.DoesNotContain("apk add", script);
        Assert.DoesNotContain("python", script);
    }

    // Regression coverage for a whole line of real failures, found and fixed
    // one after another (each confirmed directly against a real container):
    //   1. Reading /data/options.json (mode 600, owned by Home Assistant's
    //      own Supervisor - confirmed directly against its source) as this
    //      addon's own non-root user failed with "Permission denied".
    //   2. Becoming root first, then dropping back to the original user
    //      before the final exec, broke a real Alpine-based addon: the RUN
    //      that chmod +x's the copied wrapper script ran as that non-root
    //      user and couldn't (COPY's default ownership doesn't follow the
    //      active USER) - "Operation not permitted", left non-executable,
    //      container never started.
    //   3. Fixing *that* (COPY --chmod= instead of RUN chmod) and still
    //      dropping back via `su` broke a *different* real addon on Debian:
    //      `su -c` forks a child instead of exec-replacing, leaving itself
    //      as PID 1 running as root, so its non-root child (a cron job
    //      writing to its own parent's /proc/1/fd/1, a common trick to make
    //      cron output show up in `docker logs`) got "Permission denied".
    //   4. Switching the privilege-drop tool to `setpriv` (which *does*
    //      exec-replace correctly on Debian) didn't help either - and
    //      neither did `chpst` or `gosu`, tried afterwards: *every*
    //      userspace privilege-drop tool leaves the dropped process
    //      non-dumpable, a Linux kernel security measure triggered by the
    //      credential transition itself, and a non-dumpable process's
    //      /proc/PID/fd/* can't be opened by anyone else - even a same-UID
    //      child - without CAP_SYS_PTRACE (which no non-root container
    //      process has). This is inherent to dropping privileges at all, not
    //      a bug in any one tool.
    // The only way out found: never change *this script's own* privilege
    // level at all. jq itself is made setuid-root instead (works
    // identically on every base image, confirmed directly including on
    // distroless) and used to read whatever needs root - the file it reads
    // needs root, not the process asking it to. Confirmed end-to-end that
    // the wrapped app's own user, and its children's ability to touch its
    // own PID 1's fds, are both completely unaffected.
    [Fact]
    public void BuildWrapperScript_NeverChangesUser_RegardlessOfDeclaredUser()
    {
        const string namedUserDockerfile = "FROM alpine\nUSER someuser\nENTRYPOINT [\"/app\"]\n";
        const string numericUserDockerfile = "FROM alpine\nUSER 1000:1000\nENTRYPOINT [\"/app\"]\n";
        const string noUserDockerfile = "FROM alpine\nENTRYPOINT [\"/app\"]\n";

        foreach (var dockerfile in new[] { namedUserDockerfile, numericUserDockerfile, noUserDockerfile })
        {
            var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile);

            Assert.NotNull(script);
            Assert.DoesNotContain("chpst", script!);
            Assert.DoesNotContain(" su ", script!);
            Assert.DoesNotContain("setpriv", script!);
            Assert.DoesNotContain("/etc/passwd", script!);
            Assert.Contains("exec '/app'", script);
        }
    }

    [Fact]
    public void BuildWrapperScript_UsesSetuidJqForOptionsRegardlessOfDistroless()
    {
        var standard = EntrypointWrapperGenerator.BuildWrapperScript("FROM alpine\nENTRYPOINT [\"/app\"]\n");
        var distroless = EntrypointWrapperGenerator.BuildWrapperScript(
            "FROM gcr.io/distroless/static-debian13:debug-nonroot\nENTRYPOINT [\"/app\"]\n", useDistrolessDebugTools: true);

        foreach (var script in new[] { standard, distroless })
        {
            Assert.Contains("/addon-forge-jq -r 'to_entries[]", script);
            Assert.DoesNotContain("command -v jq", script!);
            Assert.DoesNotContain("python", script!);
        }
    }

    [Fact]
    public void BuildDockerfileAppendix_NeverUsesRunOrSwitchesUser()
    {
        var appendix = EntrypointWrapperGenerator.BuildDockerfileAppendix(includeConfigTemplate: true);

        Assert.DoesNotContain("RUN", appendix);
        Assert.DoesNotContain("USER", appendix);
        Assert.Contains("COPY --chmod=755 addon-forge-entrypoint.sh /addon-forge-entrypoint.sh", appendix);
        // setuid-root (4755, not just 755) - see BuildWrapperScript_NeverChangesUser... for why.
        Assert.Contains("COPY --from=ghcr.io/jqlang/jq:latest --chmod=4755 /jq /addon-forge-jq", appendix);
        Assert.Contains($"COPY {EntrypointWrapperGenerator.ConfigTemplateFileName} /{EntrypointWrapperGenerator.ConfigTemplateFileName}", appendix);
        Assert.Contains("ENTRYPOINT [\"/addon-forge-entrypoint.sh\"]", appendix);
    }

    [Theory]
    [InlineData("*\n!src/\n")]
    [InlineData("*\n!src/")] // no trailing newline - must not glue onto "!src/"
    public void AllowGeneratedFilesInDockerignore_AppendsNegationLast(string dockerignore)
    {
        var patched = EntrypointWrapperGenerator.AllowGeneratedFilesInDockerignore(dockerignore);

        Assert.StartsWith(dockerignore, patched);
        Assert.Contains("\n!src/\n", patched);
        Assert.EndsWith("\n!addon-forge-*\n", patched);
        // Every file the appendix COPYs in has to match that one pattern.
        Assert.StartsWith("addon-forge-", EntrypointWrapperGenerator.ScriptFileName);
        Assert.StartsWith("addon-forge-", EntrypointWrapperGenerator.ConfigTemplateFileName);
        Assert.StartsWith("addon-forge-", EntrypointWrapperGenerator.SeedFileName);
    }

    [Fact]
    public void BuildWrapperScript_DistrolessDebugTools_ShellFormEntrypointUsesBusyboxShDashC()
    {
        const string dockerfile = "FROM gcr.io/distroless/static-debian13:debug-nonroot\nENTRYPOINT /app --flag\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile, useDistrolessDebugTools: true);

        Assert.Contains("exec /busybox/sh -c '/app --flag'", script);
    }

    [Fact]
    public void BuildWrapperScript_ExecFormEntrypointOnly_ExecsIt()
    {
        const string dockerfile = "FROM debian:bookworm-slim\nENTRYPOINT [\"/usr/local/bin/subwave-supervisor\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile);

        Assert.NotNull(script);
        Assert.Contains("exec '/usr/local/bin/subwave-supervisor'", script);
    }

    [Fact]
    public void BuildWrapperScript_ExecFormEntrypointPlusCmd_AppendsCmdAsArgs()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/entrypoint.sh\"]\nCMD [\"serve\", \"--port\", \"8080\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile);

        Assert.Contains("exec '/entrypoint.sh' 'serve' '--port' '8080'", script);
    }

    [Fact]
    public void BuildWrapperScript_CmdOnlyExecForm_ExecsCmd()
    {
        const string dockerfile = "FROM node:20-alpine\nCMD [\"node\", \"server.js\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile);

        Assert.Contains("exec 'node' 'server.js'", script);
    }

    [Fact]
    public void BuildWrapperScript_ShellFormEntrypoint_WrapsInShDashC()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT /entrypoint.sh --flag\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile);

        Assert.Contains("exec /bin/sh -c '/entrypoint.sh --flag'", script);
    }

    [Fact]
    public void BuildWrapperScript_NoEntrypointOrCmdFound_ReturnsNull()
    {
        const string dockerfile = "FROM alpine\nRUN echo hi\n";

        Assert.Null(EntrypointWrapperGenerator.BuildWrapperScript(dockerfile));
    }

    [Fact]
    public void BuildWrapperScript_UsesLastEntrypointWhenMultipleStagesDeclareOne()
    {
        // Only the final stage's ENTRYPOINT is what actually runs.
        const string dockerfile =
            "FROM alpine AS builder\nENTRYPOINT [\"/builder-only.sh\"]\n" +
            "FROM alpine\nENTRYPOINT [\"/real-app\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile);

        Assert.Contains("exec '/real-app'", script);
        Assert.DoesNotContain("/builder-only.sh", script!);
    }

    [Fact]
    public void BuildDockerfileAppendix_OverridesEntrypointWithWrapperScript()
    {
        var appendix = EntrypointWrapperGenerator.BuildDockerfileAppendix();

        Assert.Contains($"COPY --chmod=755 {EntrypointWrapperGenerator.ScriptFileName} /addon-forge-entrypoint.sh", appendix);
        Assert.Contains("ENTRYPOINT [\"/addon-forge-entrypoint.sh\"]", appendix);
    }

    [Fact]
    public void BuildWrapperScript_WithConfigFileDestPath_RendersItBeforeExec()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/app\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile, "/config/config.yaml");

        Assert.NotNull(script);
        Assert.Contains($"if [ -f /{EntrypointWrapperGenerator.ConfigTemplateFileName} ]", script);
        Assert.Contains("/addon-forge-jq -r --rawfile tmpl", script);
        Assert.Contains("gsub(", script);
        Assert.Contains("'/config/config.yaml'", script);
        Assert.DoesNotContain("envsubst", script!);
        // The config file has to be rendered before the app actually starts.
        Assert.True(script.IndexOf("/addon-forge-jq", StringComparison.Ordinal) < script.LastIndexOf("exec '/app'", StringComparison.Ordinal));
    }

    // Regression coverage for a real crash-loop found against a live addon:
    // example-app's distroless nonroot image can't write anywhere under `/`
    // at all (confirmed directly), and its own config.yaml is genuinely
    // optional per its own docs - but the render-or-copy fallback, run under
    // `set -e`, had no third fallback, so when BOTH failed the whole script
    // aborted before ever reaching `exec` - the app never started at all,
    // not even with its own defaults. The chain must always end in something
    // that can't itself fail.
    [Fact]
    public void BuildWrapperScript_ConfigFileWriteFails_NeverAbortsBeforeExec()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/app\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile, "/config.yaml")!;

        Assert.Contains("could not write config file", script);
        // The final fallback (a logged warning) must be a command that can
        // never itself fail, or `set -e` would still abort the script.
        Assert.Contains("continuing without it\" >&2", script);
        Assert.True(
            script.IndexOf("could not write config file", StringComparison.Ordinal) <
            script.IndexOf("exec '/app'", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildWrapperScript_NoConfigFileDestPath_OmitsConfigFileSection()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/app\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile);

        Assert.DoesNotContain(EntrypointWrapperGenerator.ConfigTemplateFileName, script!);
    }

    [Fact]
    public void BuildDockerfileAppendix_IncludeConfigTemplate_CopiesTemplateFile()
    {
        var appendix = EntrypointWrapperGenerator.BuildDockerfileAppendix(includeConfigTemplate: true);

        Assert.Contains($"COPY {EntrypointWrapperGenerator.ConfigTemplateFileName} /{EntrypointWrapperGenerator.ConfigTemplateFileName}", appendix);
    }

    [Fact]
    public void BuildDockerfileAppendix_WithoutConfigTemplate_DoesNotCopyTemplateFile()
    {
        var appendix = EntrypointWrapperGenerator.BuildDockerfileAppendix(includeConfigTemplate: false);

        Assert.DoesNotContain(EntrypointWrapperGenerator.ConfigTemplateFileName, appendix);
    }

    // Regression coverage for a real failure found against a live addon
    // (example-app): jq's setuid bit only helps *read* /data/options.json -
    // writing configFileDestPath is done by the calling shell's own `>`,
    // never by jq itself, so it fails under a non-root image exactly like
    // any other unprivileged write would. Confirmed directly (docker run,
    // gcr.io/distroless/static-debian13:debug-nonroot as uid 65532): a
    // world-writable file pre-created at build time - which COPY --chmod
    // can do regardless of the active USER, the same trick used for jq's
    // own setuid bit - lets the runtime write succeed where it otherwise
    // can't at all.
    [Fact]
    public void BuildDockerfileAppendix_ConfigFileAtRoot_PreSeedsWorldWritableDestination()
    {
        var appendix = EntrypointWrapperGenerator.BuildDockerfileAppendix(
            includeConfigTemplate: true, configFileDestPath: "/config.yaml");

        Assert.Contains($"COPY --chmod=666 {EntrypointWrapperGenerator.ConfigTemplateFileName} /config.yaml", appendix);
        // The parent directory ("/") always already exists, so the
        // directory-permission marker copy is harmless but still present.
        Assert.Contains($"COPY --chmod=777 {EntrypointWrapperGenerator.SeedFileName} /.addon-forge-keep", appendix);
    }

    // A `--chmod` on a COPY that has to auto-create a NEW parent directory
    // applies that same mode to the directory too (confirmed directly) - and
    // a directory with no execute bit can't be traversed by anyone, not even
    // root. The marker copy (777, so the directory it force-creates is
    // actually usable) has to be a *separate* instruction from the real
    // destination file's own copy (666 - a config file has no business
    // being executable).
    [Fact]
    public void BuildDockerfileAppendix_ConfigFileInNewSubdirectory_SeedsParentDirWithExecuteBit()
    {
        var appendix = EntrypointWrapperGenerator.BuildDockerfileAppendix(
            includeConfigTemplate: true, configFileDestPath: "/config/settings.yaml");

        Assert.Contains($"COPY --chmod=777 {EntrypointWrapperGenerator.SeedFileName} /config/.addon-forge-keep", appendix);
        Assert.Contains($"COPY --chmod=666 {EntrypointWrapperGenerator.ConfigTemplateFileName} /config/settings.yaml", appendix);
        // The directory marker must land before the real file's own copy -
        // Docker creates a COPY's target directory as needed but never
        // retroactively fixes an already-existing one's mode.
        Assert.True(
            appendix.IndexOf(".addon-forge-keep", StringComparison.Ordinal) <
            appendix.IndexOf("/config/settings.yaml", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildDockerfileAppendix_NoConfigFileDestPath_OmitsSeedCopies()
    {
        var appendix = EntrypointWrapperGenerator.BuildDockerfileAppendix(includeConfigTemplate: false, configFileDestPath: null);

        Assert.DoesNotContain(EntrypointWrapperGenerator.SeedFileName, appendix);
        Assert.DoesNotContain(".addon-forge-keep", appendix);
    }

    [Fact]
    public void BuildWrapperScript_WithPersistentPaths_SymlinksThemIntoDataBeforeHandingOff()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/run.sh\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile, persistentPaths: ["/var/sub-wave"])!;

        Assert.Contains("addon_forge_persist '/var/sub-wave' '/data/persist/var_sub-wave'", script);
        Assert.Contains("ln -s \"$2\" \"$1\"", script);
        Assert.True(
            script.IndexOf("addon_forge_persist '/var/sub-wave'", StringComparison.Ordinal) <
            script.IndexOf("exec '/run.sh'", StringComparison.Ordinal));
    }

    // Regression (example-app): data copied into /share over
    // Samba is root-owned, and the app's own `chown -R` on the symlink doesn't
    // follow it - so the target must get the image directory's owner.
    [Fact]
    public void BuildWrapperScript_WithPersistentPaths_ChownsTargetToOriginalOwner()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/run.sh\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(dockerfile, persistentPaths: ["/home/app/.config"])!;

        Assert.Contains("owner=\"$(stat -c '%u:%g' \"$1\" 2>/dev/null || true)\"", script);
        Assert.Contains("chown -R \"$owner\" \"$2\" || true", script);
        // A directory the app's root phase creates later must stay writable for its own user.
        Assert.Contains("chmod g+ws", script);
        Assert.True(
            script.IndexOf("umask 0002", StringComparison.Ordinal) <
            script.IndexOf("exec '/run.sh'", StringComparison.Ordinal));
        Assert.True(
            script.IndexOf("stat -c", StringComparison.Ordinal) <
            script.IndexOf("rm -rf \"$1\"", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildWrapperScript_WithoutPersistentPaths_HasNoPersistenceSection()
    {
        var script = EntrypointWrapperGenerator.BuildWrapperScript("FROM alpine\nENTRYPOINT [\"/run.sh\"]\n")!;

        Assert.DoesNotContain("addon_forge_persist", script);
    }

    [Fact]
    public void StripVolumeDeclarations_RemovesOnlyPersistedPaths()
    {
        const string dockerfile = "FROM alpine\nVOLUME [\"/config\", \"/var/lib/app\"]\nVOLUME /cache\nVOLUME /var/lib/app/sub\n";

        var result = EntrypointWrapperGenerator.StripVolumeDeclarations(dockerfile, ["/var/lib/app"]);

        Assert.Contains("VOLUME [\"/config\"]", result);
        Assert.Contains("VOLUME /cache", result);
        Assert.Contains("# addon-forge: removed", result);
        Assert.DoesNotContain("\"/var/lib/app\"", result);
    }

    [Fact]
    public void StripVolumeDeclarations_NoPersistedPaths_LeavesDockerfileUntouched()
    {
        const string dockerfile = "FROM alpine\nVOLUME /data\n";

        Assert.Equal(dockerfile, EntrypointWrapperGenerator.StripVolumeDeclarations(dockerfile, []));
    }

    [Fact]
    public void BuildWrapperScript_WithSharedPaths_SymlinksThemIntoShareNamespacedBySlug()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/run.sh\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(
            dockerfile, sharedPaths: ["/data/nginx/logs"], slug: "proxy")!;

        Assert.Contains("addon_forge_persist '/data/nginx/logs' '/share/proxy/data_nginx_logs'", script);
        Assert.True(
            script.IndexOf("addon_forge_persist '/data/nginx/logs'", StringComparison.Ordinal) <
            script.IndexOf("exec '/run.sh'", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildWrapperScript_WithPersistentAndSharedPaths_EmitsBothSymlinkCalls()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/run.sh\"]\n";

        var script = EntrypointWrapperGenerator.BuildWrapperScript(
            dockerfile, persistentPaths: ["/var/lib/app"], sharedPaths: ["/var/log/app"], slug: "myapp")!;

        Assert.Contains("addon_forge_persist '/var/lib/app' '/data/persist/var_lib_app'", script);
        Assert.Contains("addon_forge_persist '/var/log/app' '/share/myapp/var_log_app'", script);
    }

    [Fact]
    public void BuildWrapperScript_SharedPathsWithoutSlug_Throws()
    {
        const string dockerfile = "FROM alpine\nENTRYPOINT [\"/run.sh\"]\n";

        Assert.Throws<ArgumentException>(() =>
            EntrypointWrapperGenerator.BuildWrapperScript(dockerfile, sharedPaths: ["/data/nginx/logs"]));
    }
}
