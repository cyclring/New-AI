using System.Diagnostics;
using System.IO;
using System.Text;

namespace WorkControl;

internal static class CommandRunner
{
    public static async Task<string> Run(
        string executable, string directory, IEnumerable<string> arguments, CancellationToken token,
        string? input = null, Action<string>? output = null, Action<string>? diagnostic = null,
        string? gitIndex = null, bool allowExitOne = false)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        if (gitIndex is not null)
            start.Environment["GIT_INDEX_FILE"] = gitIndex;

        using var process = Process.Start(start) ?? throw new IOException("프로세스를 시작할 수 없습니다.");
        var stdout = output is null
            ? process.StandardOutput.ReadToEndAsync()
            : ReadLines(process.StandardOutput, output);
        var stderr = diagnostic is null
            ? process.StandardError.ReadToEndAsync()
            : ReadLines(process.StandardError, diagnostic);
        try
        {
            if (input is not null)
                await process.StandardInput.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.DisposeAsync().ConfigureAwait(false);
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (IOException) when (token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().ConfigureAwait(false);
            }
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        token.ThrowIfCancellationRequested();
        if (process.ExitCode != 0 && !(allowExitOne && process.ExitCode == 1))
        {
            var message = stderr.Result.Trim();
            throw new IOException($"{Path.GetFileName(executable)} 종료 코드 {process.ExitCode} · {message}");
        }
        return stdout.Result;
    }

    private static async Task<string> ReadLines(StreamReader reader, Action<string> onLine)
    {
        var tail = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            onLine(line);
            tail.AppendLine(line.Length > 2000 ? line[..2000] : line);
            if (tail.Length > 4000)
                tail.Remove(0, tail.Length - 4000);
        }
        return tail.ToString();
    }
}
