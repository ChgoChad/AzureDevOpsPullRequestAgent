using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ADOPullRequestAgent
{
    public class PullRequestAgent
    {
        private readonly IFileSystem _fileSystem;
        private readonly string _adoMcpAuthenticationToken;
        private readonly AgentOptions _agentOptions;

        /// <summary>
        /// Timeout for the Claude Code CLI process. PR reviews with MCP tool calls can be slow.
        /// </summary>
        private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(30);

        public PullRequestAgent(IFileSystem fileSystem, string adoMcpAuthenticationToken, AgentOptions agentOptions)
        {
            _fileSystem = fileSystem;
            _adoMcpAuthenticationToken = adoMcpAuthenticationToken ??
                                         throw new ArgumentNullException(nameof(adoMcpAuthenticationToken), "ADO_MCP_AUTH_TOKEN cannot be null");
            _agentOptions = agentOptions;
        }

        /// <summary>
        /// Runs an AI-powered code review on the specified pull request using Claude Code CLI.
        /// </summary>
        /// <param name="pullRequestId">The ID of the pull request to review.</param>
        /// <param name="organizationName">The name of the Azure DevOps organization.</param>
        /// <param name="projectName">The name of the Azure DevOps project.</param>
        /// <param name="repositoryName">The name of the Azure DevOps repository.</param>
        /// <returns>The review output as a string.</returns>
        public async Task<string> RunAsync(int pullRequestId, string organizationName, string projectName, string repositoryName)
        {
            // Load the system prompt and inject the sources directory and output directory
            var systemInstructions = await _fileSystem.File.ReadAllTextAsync("pullreview.prompt");
            systemInstructions = systemInstructions.Replace("{{SOURCES_DIRECTORY}}", _agentOptions.SourcesDirectory);
            var outputDir = !string.IsNullOrWhiteSpace(_agentOptions.OutputDirectory)
                ? _agentOptions.OutputDirectory
                : _agentOptions.SourcesDirectory;
            systemInstructions = systemInstructions.Replace("{{OUTPUT_DIRECTORY}}", outputDir);

            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddConsole();
            });
            var logger = loggerFactory.CreateLogger<PullRequestAgent>();

            // Write system prompt to a temp file for --system-prompt-file
            var tempDir = _fileSystem.Path.GetTempPath();
            var systemPromptPath = _fileSystem.Path.Combine(tempDir, _fileSystem.Path.GetRandomFileName());
            await _fileSystem.File.WriteAllTextAsync(systemPromptPath, systemInstructions);

            // Build and write MCP config to a temp file for --mcp-config
            var mcpConfigPath = _fileSystem.Path.Combine(tempDir, _fileSystem.Path.GetRandomFileName());
            var mcpConfig = BuildMcpConfig(organizationName);
            await _fileSystem.File.WriteAllTextAsync(mcpConfigPath, mcpConfig);

            try
            {
                var userPrompt = $"Review the pull request number {pullRequestId} in Azure DevOps project {projectName} for the repository {repositoryName}. The repository source code is cloned locally at: {_agentOptions.SourcesDirectory}";

                var arguments = BuildClaudeArguments(systemPromptPath, mcpConfigPath);

                logger.LogInformation("Starting Claude Code review for PR #{PullRequestId} in {Project}/{Repository}", pullRequestId, projectName, repositoryName);
                logger.LogInformation("Using model: {Model}", _agentOptions.Model);

                var reviewStopwatch = Stopwatch.StartNew();
                var (exitCode, stdout, stderr) = await RunClaudeProcessAsync(arguments, userPrompt, logger);
                reviewStopwatch.Stop();

                logger.LogInformation("[Metrics] Pull request review: {Elapsed}", reviewStopwatch.Elapsed);

                if (exitCode != 0)
                {
                    throw new Exception($"Claude Code CLI exited with code {exitCode}.{(string.IsNullOrWhiteSpace(stderr) ? string.Empty : $"{Environment.NewLine}stderr: {stderr}")}");
                }

                return stdout;
            }
            finally
            {
                // Clean up temp files
                TryDeleteFile(systemPromptPath);
                TryDeleteFile(mcpConfigPath);
            }
        }

        /// <summary>
        /// Builds the MCP server configuration JSON for Claude Code CLI.
        /// </summary>
        private string BuildMcpConfig(string organizationName)
        {
            var config = new
            {
                mcpServers = new Dictionary<string, object>
                {
                    ["azure-devops"] = new
                    {
                        command = "npx",
                        args = new[]
                        {
                            "-y", "@azure-devops/mcp", organizationName,
                            "--domains", "core", "repositories", "search", "work", "work-items",
                            "--authentication", "envvar"
                        },
                        env = new Dictionary<string, string>
                        {
                            ["ADO_MCP_AUTH_TOKEN"] = _adoMcpAuthenticationToken
                        }
                    },
                    ["microsoft-learn"] = new
                    {
                        type = "sse",
                        url = "https://learn.microsoft.com/api/mcp"
                    }
                }
            };

            return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        }

        /// <summary>
        /// Builds the command-line arguments for the claude CLI invocation.
        /// </summary>
        private List<string> BuildClaudeArguments(string systemPromptPath, string mcpConfigPath)
        {
            var args = new List<string>
            {
                "-p",
                "--output-format", "stream-json",
                "--verbose",
                "--model", _agentOptions.Model,
                "--system-prompt-file", systemPromptPath,
                "--mcp-config", mcpConfigPath,
                "--no-session-persistence",
                "--allowedTools", "mcp__azure-devops__*,mcp__microsoft-learn__*,Bash(git *),Bash(cat *),Read,Write,Glob,Grep,Edit"
            };

            if (_agentOptions.MaxTurns.HasValue)
            {
                args.Add("--max-turns");
                args.Add(_agentOptions.MaxTurns.Value.ToString());
            }

            if (_agentOptions.MaxBudgetUsd.HasValue)
            {
                args.Add("--max-budget-usd");
                args.Add(_agentOptions.MaxBudgetUsd.Value.ToString("F2"));
            }

            return args;
        }

        /// <summary>
        /// Executes the claude CLI process, piping the user prompt via stdin and capturing output.
        /// </summary>
        private async Task<(int ExitCode, string Stdout, string Stderr)> RunClaudeProcessAsync(
            List<string> arguments, string userPrompt, ILogger logger)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "claude",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _agentOptions.SourcesDirectory
            };

            // Configure the Claude Code provider (direct Anthropic API or Microsoft Foundry)
            ConfigureProviderEnvironment(startInfo, logger);

            // Pass ADO token for the MCP server (provider-independent)
            startInfo.Environment["ADO_MCP_AUTH_TOKEN"] = _adoMcpAuthenticationToken;

            foreach (var arg in arguments)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = new Process { StartInfo = startInfo };

            var resultBuilder = new StringBuilder();
            var stderrBuilder = new StringBuilder();

            process.Start();

            // Read stdout JSONL events line-by-line, parse each event, and stream
            // human-readable summaries to the console (visible in pipeline logs).
            // The final review text is extracted from the "result" event.
            var stdoutTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) != null)
                {
                    ProcessStreamEvent(line, resultBuilder, logger);
                }
            });

            var stderrTask = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) != null)
                {
                    Console.Error.WriteLine(line);
                    stderrBuilder.AppendLine(line);
                }
            });

            // Pipe the user prompt to stdin and close it
            await process.StandardInput.WriteLineAsync(userPrompt);
            process.StandardInput.Close();

            // Wait for process completion with timeout
            using var cts = new CancellationTokenSource(ProcessTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Process may have already exited; ignore to keep timeout handling reliable
                }
                throw new TimeoutException($"Claude Code CLI did not complete within {ProcessTimeout.TotalMinutes} minutes. The process was terminated.");
            }

            // Ensure all buffered output has been fully read before returning
            await stdoutTask;
            await stderrTask;

            return (process.ExitCode, resultBuilder.ToString(), stderrBuilder.ToString());
        }

        /// <summary>
        /// Parses a single JSONL line from the Claude Code stream-json output, logs a human-readable
        /// summary to the pipeline logs, and extracts the final review text from result events.
        /// </summary>
        /// <param name="jsonLine">A single line of JSONL output from Claude Code CLI.</param>
        /// <param name="resultBuilder">StringBuilder that accumulates the final review text.</param>
        /// <param name="logger">Logger for streaming event summaries to the pipeline logs.</param>
        private static void ProcessStreamEvent(string jsonLine, StringBuilder resultBuilder, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(jsonLine))
            {
                return;
            }

            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                {
                    return;
                }

                var eventType = typeElement.GetString();

                switch (eventType)
                {
                    case "assistant":
                        ProcessAssistantEvent(root, logger);
                        break;

                    case "result":
                        ProcessResultEvent(root, resultBuilder, logger);
                        break;

                    case "system":
                        if (root.TryGetProperty("subtype", out var subtype))
                        {
                            logger.LogInformation("[Claude] System: {Subtype}", subtype.GetString());
                        }
                        break;
                }
            }
            catch (JsonException)
            {
                // Malformed JSON line — log it raw and continue
                logger.LogWarning("[Claude] Non-JSON output: {Line}", jsonLine.Length > 200 ? jsonLine[..200] + "..." : jsonLine);
            }
        }

        /// <summary>
        /// Processes an "assistant" event, logging text content and tool calls to the pipeline logs.
        /// </summary>
        private static void ProcessAssistantEvent(JsonElement root, ILogger logger)
        {
            if (!root.TryGetProperty("message", out var message))
            {
                return;
            }

            if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var blockType))
                {
                    continue;
                }

                var blockTypeStr = blockType.GetString();

                if (blockTypeStr == "text" && block.TryGetProperty("text", out var text))
                {
                    var textStr = text.GetString();
                    if (!string.IsNullOrWhiteSpace(textStr))
                    {
                        // Log each line of the assistant's text so multi-line output is readable
                        foreach (var line in textStr.Split('\n'))
                        {
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                logger.LogInformation("[Claude] {Line}", line.TrimEnd('\r'));
                            }
                        }
                    }
                }
                else if (blockTypeStr == "tool_use")
                {
                    var toolName = block.TryGetProperty("name", out var name) ? name.GetString() : "unknown";
                    logger.LogInformation("[Claude] Calling tool: {Tool}", toolName);
                }
            }
        }

        /// <summary>
        /// Processes a "result" event, extracting the final review text and logging session metrics.
        /// </summary>
        private static void ProcessResultEvent(JsonElement root, StringBuilder resultBuilder, ILogger logger)
        {
            // Extract the final result text
            if (root.TryGetProperty("result", out var result))
            {
                resultBuilder.Append(result.GetString());
            }

            // Log cost and usage metrics if available
            if (root.TryGetProperty("cost_usd", out var cost))
            {
                logger.LogInformation("[Claude] Session cost: ${Cost:F4}", cost.GetDouble());
            }

            if (root.TryGetProperty("duration_ms", out var duration))
            {
                var elapsed = TimeSpan.FromMilliseconds(duration.GetDouble());
                logger.LogInformation("[Claude] Session duration: {Duration}", elapsed);
            }

            if (root.TryGetProperty("total_turns", out var turns))
            {
                logger.LogInformation("[Claude] Total turns: {Turns}", turns.GetInt32());
            }

            logger.LogInformation("[Claude] Session complete");
        }

        /// <summary>
        /// Detects the configured Claude Code provider and sets the appropriate environment variables
        /// on the process start info. Supports direct Anthropic API and Microsoft Foundry.
        /// </summary>
        /// <param name="startInfo">The process start info to configure with provider environment variables.</param>
        /// <param name="logger">Logger for recording which provider is selected.</param>
        /// <exception cref="InvalidOperationException">
        /// Thrown when no provider is configured (neither ANTHROPIC_API_KEY nor CLAUDE_CODE_USE_FOUNDRY is set).
        /// </exception>
        private static void ConfigureProviderEnvironment(ProcessStartInfo startInfo, ILogger logger)
        {
            var useFoundry = Environment.GetEnvironmentVariable("CLAUDE_CODE_USE_FOUNDRY");
            var anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

            if (string.Equals(useFoundry, "1", StringComparison.Ordinal))
            {
                // Microsoft Foundry mode — forward all relevant Foundry env vars
                startInfo.Environment["CLAUDE_CODE_USE_FOUNDRY"] = "1";

                string[] foundryVars =
                [
                    "ANTHROPIC_FOUNDRY_RESOURCE",
                    "ANTHROPIC_FOUNDRY_BASE_URL",
                    "ANTHROPIC_FOUNDRY_API_KEY",
                    "ANTHROPIC_DEFAULT_SONNET_MODEL",
                    "ANTHROPIC_DEFAULT_HAIKU_MODEL",
                    "ANTHROPIC_DEFAULT_OPUS_MODEL"
                ];

                foreach (var name in foundryVars)
                {
                    var value = Environment.GetEnvironmentVariable(name);
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        startInfo.Environment[name] = value;
                    }
                }

                var resource = Environment.GetEnvironmentVariable("ANTHROPIC_FOUNDRY_RESOURCE") ?? "(not set)";
                logger.LogInformation("Using Microsoft Foundry provider (resource: {Resource})", resource);
            }
            else if (!string.IsNullOrWhiteSpace(anthropicApiKey))
            {
                // Direct Anthropic API mode
                startInfo.Environment["ANTHROPIC_API_KEY"] = anthropicApiKey;
                logger.LogInformation("Using direct Anthropic API provider");
            }
            else
            {
                throw new InvalidOperationException(
                    "No Claude Code provider configured. Set one of the following:" + Environment.NewLine +
                    "  - ANTHROPIC_API_KEY for direct Anthropic API access" + Environment.NewLine +
                    "  - CLAUDE_CODE_USE_FOUNDRY=1 with ANTHROPIC_FOUNDRY_RESOURCE for Microsoft Foundry access" + Environment.NewLine +
                    "See README.md for details.");
            }
        }

        /// <summary>
        /// Attempts to delete a file, suppressing any exceptions.
        /// </summary>
        private void TryDeleteFile(string path)
        {
            try
            {
                if (_fileSystem.File.Exists(path))
                {
                    _fileSystem.File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup; ignore failures
            }
        }
    }
}
