using System.IO;
using System.Text.Json;

namespace WorkControl;

internal enum AgentKind { Claude, Codex }

internal interface IAssistantAgent
{
    string Name { get; }
    Task<string> Run(string directory, string prompt, bool readOnly, Action<string> log, CancellationToken token);
}

internal sealed class AgentCli(AgentKind kind, string executable) : IAssistantAgent
{
    public string Name => kind.ToString();
    public string? ReadSession { get; set; }
    public string? WriteSession { get; set; }

    public async Task<string> Run(
        string directory, string prompt, bool readOnly, Action<string> log, CancellationToken token)
    {
        var session = readOnly ? ReadSession : WriteSession;
        var args = kind == AgentKind.Codex ? CodexArguments(readOnly, session) : ClaudeArguments(directory, readOnly, session);
        string? result = null;
        string? failure = null;
        try { await CommandRunner.Run(executable, directory, args, token, prompt, Parse, log); }
        catch (IOException) when (failure is not null) { throw new IOException(failure); }
        if (failure is not null)
            throw new IOException(failure);
        if (readOnly) ReadSession = session;
        else WriteSession = session;
        return string.IsNullOrWhiteSpace(result)
            ? throw new IOException($"{kind} 응답에서 최종 메시지를 찾지 못했습니다. 진행 로그를 확인하세요.")
            : result;

        void Parse(string line)
        {
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException)
            {
                log(line);
                return;
            }

            using (document)
            {
                var root = document.RootElement;
                var type = String(root, "type");
                switch (type)
                {
                    case "thread.started":
                        session = String(root, "thread_id") ?? session;
                        break;
                    case "item.started" when root.TryGetProperty("item", out var started):
                        if (String(started, "type") == "command_execution")
                            log($"명령 실행 · {String(started, "command")}");
                        break;
                    case "item.completed" when root.TryGetProperty("item", out var item):
                        if (String(item, "type") == "agent_message")
                        {
                            result = String(item, "text");
                            if (result is not null)
                                log(result);
                        }
                        break;
                    case "turn.failed":
                        failure = root.GetRawText();
                        break;
                    case "error":
                        log(String(root, "message") ?? "CLI 오류 이벤트");
                        break;
                    case "system" when String(root, "subtype") == "init":
                        session = String(root, "session_id") ?? session;
                        break;
                    case "assistant" when root.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array:
                        foreach (var block in content.EnumerateArray())
                            if (String(block, "type") == "tool_use")
                                log($"도구 실행 · {String(block, "name")}");
                            else if (String(block, "type") == "text" && String(block, "text") is { } text)
                                log(text);
                        break;
                    case "result":
                        session = String(root, "session_id") ?? session;
                        result = String(root, "result");
                        if (root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True)
                            failure = result ?? root.GetRawText();
                        break;
                }
            }
        }
    }

    private static List<string> CodexArguments(bool readOnly, string? session)
    {
        var sandbox = readOnly ? "read-only" : "workspace-write";
        var args = new List<string> { "-a", "never", "exec" };
        if (session is null)
            args.AddRange(["--sandbox", sandbox, "--json", "--ignore-user-config", "--skip-git-repo-check", "-"]);
        else
            args.AddRange(["resume", "-c", $"sandbox_mode=\"{sandbox}\"", "--json", "--ignore-user-config",
                "--skip-git-repo-check", session, "-"]);
        return args;
    }

    private static List<string> ClaudeArguments(string directory, bool readOnly, string? session)
    {
        var args = new List<string>
        {
            "-p", "--output-format", "stream-json", "--verbose", "--safe-mode",
            "--strict-mcp-config", "--mcp-config", """{"mcpServers":{}}"""
        };
        if (readOnly)
            args.AddRange(["--tools", "Read,Glob,Grep", "--allowedTools", "Read,Glob,Grep",
                "--disallowedTools", "mcp__*"]);
        else
        {
            var tools = new List<string> { "Read", "Glob", "Grep", "Bash(git status *)", "Bash(git diff *)",
                "Bash(dotnet build *)", "Bash(dotnet test *)" };
            if (File.Exists(Path.Combine(directory, "package.json")))
                tools.AddRange(["Bash(npm test *)", "Bash(npm run test *)", "Bash(npm run build *)"]);
            if (new[] { "pyproject.toml", "requirements.txt", "setup.py", "pytest.ini" }
                .Any(file => File.Exists(Path.Combine(directory, file))))
                tools.AddRange(["Bash(python -m pytest *)", "Bash(py -m pytest *)",
                    "Bash(python -m unittest *)", "Bash(py -m unittest *)"]);
            args.AddRange(["--permission-mode", "acceptEdits",
                "--tools", "Read,Glob,Grep,Edit,Write,Bash",
                "--allowedTools", .. tools]);
        }
        if (session is not null)
            args.AddRange(["--resume", session]);
        return args;
    }

    private static string? String(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
