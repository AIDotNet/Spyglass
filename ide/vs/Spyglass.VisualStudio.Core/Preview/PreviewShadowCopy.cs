using System.Runtime.InteropServices;

namespace Spyglass.VisualStudio.Core.Preview;

/// <summary>
/// A private copy of the app's build output for the previewer to run from. On Windows a running process locks the assemblies
/// it loaded, so a previewer running from bin/ would make the next build fail and could never see the rebuild. One copy per
/// preview window; a restart copies only what changed. Other platforms' runtimes/ folders are left out: they are most of a
/// typical Avalonia output (552 of 565 MB for the sample) and the previewer never loads them.
/// </summary>
public sealed class PreviewShadowCopy
{
    const int Attempts = 10;
    static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(300);
    static readonly TimeSpan OrphanAge = TimeSpan.FromDays(7);
    static readonly HashSet<string> Architectures = new(StringComparer.OrdinalIgnoreCase) { "x64", "x86", "arm64", "arm", "armel", "s390x", "ppc64le", "loongarch64", "riscv64" };

    readonly string _root;
    readonly string _directory;
    bool _purged;

    /// <param name="root">The parent of all copies; by default spyglass-preview in the temp directory.</param>
    public PreviewShadowCopy(string? root = null)
    {
        _root = root ?? Path.Combine(Path.GetTempPath(), "spyglass-preview");
        _directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Brings the copy up to date with the project's output and returns the project with its paths in the copy. The build may
    /// still be writing (and a stopped previewer may still hold its files), so locked files are retried for a few seconds.
    /// </summary>
    public async Task<PreviewProject> SyncAsync(PreviewProject project, CancellationToken ct = default)
    {
        var source = Path.GetDirectoryName(Path.GetFullPath(project.TargetPath))!;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await Task.Run(() => Sync(source, ct), ct).ConfigureAwait(false);
                break;
            }
            catch (Exception ex) when (attempt < Attempts && ex is (IOException and not DirectoryNotFoundException) or UnauthorizedAccessException)
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
        return project with
        {
            TargetPath = InCopy(source, project.TargetPath),
            RuntimeConfigPath = InCopy(source, project.RuntimeConfigPath),
            DepsPath = InCopy(source, project.DepsPath),
        };
    }

    /// <summary>Removes the copy. Best effort: a previewer that is still exiting may hold files, and a later copy removes the orphan.</summary>
    public async Task DeleteAsync()
    {
        for (var attempt = 1; attempt <= Attempts && Directory.Exists(_directory); attempt++)
        {
            try { Directory.Delete(_directory, true); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(RetryDelay).ConfigureAwait(false); }
        }
    }

    /// <summary>Whether a runtimes/&lt;rid&gt; folder can be used on this OS family ("win", "osx", "linux") and architecture.</summary>
    public static bool IsRuntimeFor(string rid, string os, string architecture)
    {
        var family = rid.Split('-', '.')[0];
        if (!family.StartsWith(os, StringComparison.OrdinalIgnoreCase) && !(os != "win" && family.Equals("unix", StringComparison.OrdinalIgnoreCase))) return false;
        var last = rid.Split('-')[^1];
        return !Architectures.Contains(last) || last.Equals(architecture, StringComparison.OrdinalIgnoreCase);
    }

    static bool IsRuntimeForThisProcess(string rid)
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        return IsRuntimeFor(rid, os, RuntimeInformation.ProcessArchitecture.ToString());
    }

    string InCopy(string source, string path)
    {
        var relative = Path.GetRelativePath(source, Path.GetFullPath(path));
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? path : Path.Combine(_directory, relative);
    }

    void Sync(string source, CancellationToken ct)
    {
        PurgeOrphans();
        var expected = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        Copy(source, _directory, isRoot: true, inRuntimes: false, expected, ct);
        foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
            if (!expected.Contains(file)) File.Delete(file);   // gone from the output (another project, a removed reference)
    }

    // Not SearchOption.AllDirectories: that does not follow links to directories.
    static void Copy(string from, string to, bool isRoot, bool inRuntimes, HashSet<string> expected, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(to, Path.GetFileName(file));
            expected.Add(target);
            var source = new FileInfo(file);
            var existing = new FileInfo(target);
            if (existing.Exists && existing.Length == source.Length && existing.LastWriteTimeUtc == source.LastWriteTimeUtc) continue;
            File.Copy(file, target, true);
            File.SetLastWriteTimeUtc(target, source.LastWriteTimeUtc);
        }
        foreach (var directory in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(directory);
            if (inRuntimes && !IsRuntimeForThisProcess(name)) continue;
            Copy(directory, Path.Combine(to, name), isRoot: false, inRuntimes: isRoot && name.Equals("runtimes", StringComparison.OrdinalIgnoreCase), expected, ct);
        }
    }

    /// <summary>Removes copies left by a crash, once per window. A week old: a copy in use by a running preview is not touched.</summary>
    void PurgeOrphans()
    {
        if (_purged) return;
        _purged = true;
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var directory in Directory.GetDirectories(_root))
                if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > OrphanAge)
                    try { Directory.Delete(directory, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
