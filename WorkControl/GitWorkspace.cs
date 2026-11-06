using System.IO;

namespace WorkControl;

internal sealed record GitWorkspace(string Root, string BaseCommit, string? SourceRepository = null)
{
    private static readonly HashSet<string> Generated = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".venv", "venv", "__pycache__",
        ".pytest_cache", ".next", "dist", "build", "coverage"
    };

    public static async Task<GitWorkspace> Create(string repository, string root, CancellationToken token,
        Action<string>? log = null)
    {
        var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repository));
        var target = Path.GetFullPath(root);
        if (IsUnder(target, source) || Directory.Exists(target))
            throw new IOException("원본 밖의 새 작업 폴더를 선택해야 합니다.");

        string? baseline = null;
        if (Directory.Exists(Path.Combine(source, ".git")) || File.Exists(Path.Combine(source, ".git")))
            baseline = (await CommandRunner.Run("git.exe", source, ["rev-parse", "--verify", "--quiet", "HEAD"],
                token, allowExitOne: true)).Trim();

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!string.IsNullOrEmpty(baseline))
        {
            try
            {
                await Git(source, token, "-c", "core.hooksPath=NUL", "worktree", "add", "--detach", target, baseline);
                return new(target, baseline, source);
            }
            catch
            {
                await TryCleanup(target, source, log);
                throw;
            }
        }

        try
        {
            Directory.CreateDirectory(target);
            await Git(target, token, "init", "--quiet");
            await CopyFolder(source, target, token, log);
            await Git(target, token, "-c", "core.safecrlf=false", "add", "--all");
            await Git(target, token, "-c", "core.hooksPath=NUL", "-c", "commit.gpgsign=false",
                "-c", "user.name=Assi", "-c", "user.email=assi@localhost",
                "commit", "--quiet", "--allow-empty", "-m", "Initial folder snapshot");
            baseline = (await Git(target, token, "rev-parse", "--verify", "HEAD")).Trim();
            return new(target, baseline);
        }
        catch
        {
            await TryCleanup(target, null, log);
            throw;
        }
    }

    private static async Task CopyFolder(string source, string target, CancellationToken token, Action<string>? log)
    {
        var files = await Task.Run(() => EnumerateFiles(new DirectoryInfo(source), token)
            .Select(file => Path.GetRelativePath(source, file.FullName).Replace('\\', '/')).ToArray(), token);
        // The scratch repository must see each nested .gitignore before checking paths.
        var rules = files.Where(path => Path.GetFileName(path).Equals(".gitignore", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in rules)
            await CopyFile(source, target, path, token, log);

        var input = files.Length == 0 ? "" : string.Join('\0', files) + '\0';
        var output = await CommandRunner.Run("git.exe", target,
            ["check-ignore", "--no-index", "-z", "--stdin"], token, input, allowExitOne: true);
        var ignored = output.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        foreach (var path in files)
        {
            token.ThrowIfCancellationRequested();
            if (ignored.Contains(path))
            {
                var copiedRule = Path.Combine(target, path);
                if (rules.Contains(path) && File.Exists(copiedRule))
                    File.Delete(copiedRule);
                continue;
            }
            if (!rules.Contains(path))
                await CopyFile(source, target, path, token, log);
        }
    }

    private static IEnumerable<FileInfo> EnumerateFiles(DirectoryInfo folder, CancellationToken token)
    {
        foreach (var entry in folder.EnumerateFileSystemInfos())
        {
            token.ThrowIfCancellationRequested();
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                continue;
            if (entry is DirectoryInfo subfolder && !Generated.Contains(entry.Name))
            {
                foreach (var file in EnumerateFiles(subfolder, token))
                    yield return file;
            }
            else if (entry is FileInfo file && !entry.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                yield return file;
        }
    }

    private static async Task CopyFile(string source, string target, string relative, CancellationToken token, Action<string>? log)
    {
        var destination = Path.Combine(target, relative);
        try
        {
            await using var input = new FileStream(Path.Combine(source, relative), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(output, token);
        }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
        {
            log?.Invoke($"잠긴 파일 건너뜀 · {relative}");
        }
    }

    public Task Delete(string managedRoot) => DeleteManaged(Root, managedRoot, SourceRepository);

    private static async Task TryCleanup(string target, string? repository, Action<string>? log)
    {
        try { await DeleteManaged(target, Path.GetDirectoryName(target)!, repository); }
        catch (Exception error) { log?.Invoke($"작업 폴더 정리 실패 · {target} · {error.Message}"); }
    }

    private static async Task DeleteManaged(string target, string managedRoot, string? repository)
    {
        if (!Directory.Exists(target))
            return;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(managedRoot));
        if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _) ||
            File.GetAttributes(full).HasFlag(FileAttributes.ReparsePoint) ||
            File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint))
            throw new IOException("어시에서 생성한 작업 폴더만 정리할 수 있습니다.");
        if (repository is not null)
        {
            try
            {
                await Git(repository, CancellationToken.None, "worktree", "remove", "--force", full);
                return;
            }
            catch (IOException) when (!File.Exists(Path.Combine(full, ".git")))
            {
                // Worktree creation may have stopped before writing its registration link.
            }
        }
        await Task.Run(() => DeleteFolder(new DirectoryInfo(full)));
    }

    private static void DeleteFolder(DirectoryInfo folder)
    {
        foreach (var item in folder.EnumerateFileSystemInfos())
        {
            if (item is DirectoryInfo child && !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                DeleteFolder(child);
            else
            {
                if (item.Attributes.HasFlag(FileAttributes.ReadOnly) && !item.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    item.Attributes &= ~FileAttributes.ReadOnly;
                item.Delete();
            }
        }
        folder.Delete();
    }

    private static bool IsUnder(string path, string directory) =>
        path.StartsWith(Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase) || string.Equals(path, directory, StringComparison.OrdinalIgnoreCase);

    public async Task<string> Snapshot(CancellationToken token)
    {
        var index = Path.Combine(Path.GetTempPath(), $"assi-{Guid.NewGuid():N}.index");
        try
        {
            await CommandRunner.Run("git.exe", Root, ["read-tree", BaseCommit], token, gitIndex: index);
            await CommandRunner.Run("git.exe", Root,
                ["-c", "core.safecrlf=false", "add", "--all", "--", "."], token, gitIndex: index);
            return (await CommandRunner.Run("git.exe", Root, ["write-tree"], token, gitIndex: index)).Trim();
        }
        finally { File.Delete(index); }
    }

    public async Task<byte[]> ReadPatch(CancellationToken token, string? baseline = null)
    {
        baseline ??= BaseCommit;
        var index = Path.Combine(Path.GetTempPath(), $"assi-{Guid.NewGuid():N}.index");
        var patch = index + ".patch";
        try
        {
            await CommandRunner.Run("git.exe", Root, ["read-tree", baseline], token, gitIndex: index);
            await CommandRunner.Run("git.exe", Root,
                ["-c", "core.safecrlf=false", "add", "-N", "--all", "--", "."], token, gitIndex: index);
            await CommandRunner.Run("git.exe", Root,
                ["-c", "core.quotePath=false", "diff", "--no-color", "--no-ext-diff", "--no-textconv",
                 "--binary", "--full-index", $"--output={patch}", baseline, "--"], token, gitIndex: index);
            return await File.ReadAllBytesAsync(patch, token);
        }
        finally
        {
            File.Delete(index);
            File.Delete(patch);
        }
    }

    private static Task<string> Git(string folder, CancellationToken token, params string[] args) =>
        CommandRunner.Run("git.exe", folder, args, token);
}
