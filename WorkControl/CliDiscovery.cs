using System.IO;

namespace WorkControl;

internal static class CliDiscovery
{
    private static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string Aliases = Path.Combine(Local, "Microsoft", "WindowsApps");
    private static readonly string Store = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");

    public static string? Resolve(string value, string name)
    {
        var path = value.Trim().Trim('"');
        return path.Length == 0 ? Find(name) : IsCli(path) ? Path.GetFullPath(path) : null;
    }

    public static string? Find(string name)
    {
        var configured = Environment.GetEnvironmentVariable($"WORKCONTROL_{name.ToUpperInvariant()}_EXE");
        if (IsCli(configured))
            return Path.GetFullPath(configured!);

        var exe = $"{name}.exe";
        var winget = Path.Combine(Local, "Microsoft", "WinGet");
        string[] standard =
        [
            Path.Combine(Home, ".local", "bin", exe),
            Path.Combine(winget, "Links", exe),
            Path.Combine(Local, "Programs", name, exe)
        ];
        var pathEntries = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path.Trim().Trim('"'), exe));
        return standard.Concat(pathEntries).FirstOrDefault(IsCli);
    }

    public static async Task<string?> FindPackagedCodex()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try
        {
            var path = await CommandRunner.Run(powershell, Local,
                ["-NoProfile", "-NonInteractive", "-Command", """
                [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
                Get-AppxPackage -Name OpenAI.Codex |
                    Sort-Object Version -Descending |
                    ForEach-Object { Join-Path $_.InstallLocation 'app\resources\codex.exe' } |
                    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
                    Select-Object -First 1
                """], timeout.Token);
            return IsCli(path.Trim()) ? path.Trim() : null;
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    private static bool IsCli(string? path)
    {
        if (path is null || !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var fullPath = Path.GetFullPath(path);
        // App execution aliases and the packaged GUI can share the CLI executable name.
        if (fullPath.StartsWith(Aliases + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        return !fullPath.StartsWith(Store + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(Path.GetDirectoryName(fullPath)), "app", StringComparison.OrdinalIgnoreCase);
    }
}
