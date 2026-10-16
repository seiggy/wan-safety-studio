using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Azure.Core;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VideoSafetyStudio.Infrastructure.Agent;

/// <summary>
/// The Video Production Helper: a Microsoft Agent Framework agent running on the GitHub Copilot harness against a
/// bring-your-own-key OpenAI v1 endpoint. Every run is stateless on the server (the browser owns the transcript and
/// resends it with the current form state), so each run builds a fresh harness session restricted to the form tools.
/// </summary>
public sealed class VideoHelperAgent(
    IOptions<VideoHelperOptions> options,
    ILoggerFactory loggers) : AIAgent, IAsyncDisposable
{
    private static readonly HashSet<string> ToolNames = VideoHelperTools.Create().Select(tool => tool.Name).ToHashSet();
    private readonly VideoHelperOptions _options = options.Value;
    private readonly TokenCredential? _credential = options.Value.CreateCredential();
    private readonly Lazy<CopilotClient> _client = new(() => CreateClient(options.Value, loggers), LazyThreadSafetyMode.ExecutionAndPublication);

    public override string Name => "Video Production Helper";

    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
        new(new VideoHelperSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(JsonSerializer.SerializeToElement(new { }));

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
        new(new VideoHelperSession());

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCoreStreamingAsync(messages, session, options, cancellationToken).ToAgentResponseAsync(cancellationToken);

    protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? session = null,
        AgentRunOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled) throw new InvalidOperationException($"The Video Production Helper is not configured. {_options.ConfigurationError}");

        var logger = loggers.CreateLogger<VideoHelperAgent>();
        IAsyncEnumerator<AgentResponseUpdate>? updates = null;
        string? failure = null;
        try
        {
            // Starting the Copilot runtime happens here, so setup failures are reported too.
            var inner = new GitHubCopilotAgent(_client.Value, CreateSessionConfig(_options, _credential, cancellationToken), ownsClient: false, name: Name, loggerFactory: loggers);
            var turns = messages.ToList();
            var prompt = new ChatMessage(ChatRole.User, [new TextContent(Transcript(turns)), .. LatestImages(turns)]);
            updates = inner.RunStreamingAsync(prompt, session: null, options: null, cancellationToken).GetAsyncEnumerator(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Video Production Helper could not start");
            failure = Summarize(exception);
        }

        if (updates is null)
        {
            yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextContent($"⚠️ The helper could not start: {failure}")]);
            yield break;
        }

        await using var _ = updates;
        while (true)
        {
            failure = null;
            try
            {
                if (!await updates.MoveNextAsync()) break;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The AG-UI host reduces failures to a generic message, so keep the cause in the log and show it in the chat.
                logger.LogError(exception, "Video Production Helper run failed");
                failure = Summarize(exception);
            }

            if (failure is not null)
            {
                yield return new AgentResponseUpdate(ChatRole.Assistant, [new TextContent($"⚠️ The helper could not complete this request: {failure}")]);
                yield break;
            }

            yield return updates.Current;
        }
    }

    private static string Summarize(Exception exception)
    {
        var message = (exception.InnerException ?? exception).Message.ReplaceLineEndings(" ");
        return message.Length > 400 ? message[..400] + "…" : message;
    }

    /// <summary>Only the newest user turn carries images; the browser sends each reference image once.</summary>
    private IEnumerable<DataContent> LatestImages(IReadOnlyList<ChatMessage> turns) =>
        _options.Vision && turns.LastOrDefault(turn => turn.Role == ChatRole.User) is { } latest
            ? latest.Contents.OfType<DataContent>().Where(content => content.HasTopLevelMediaType("image")).Take(VideoHelperTools.MaxScenes)
            : [];

    /// <summary>The harness takes one prompt per run, so earlier turns are replayed as a labelled transcript.</summary>
    public static string Transcript(IEnumerable<ChatMessage> messages)
    {
        var turns = messages.Where(message => message.Role != ChatRole.System && !string.IsNullOrWhiteSpace(message.Text)).ToList();
        if (turns.Count == 1) return turns[0].Text;

        var builder = new StringBuilder("Conversation so far (the form state is only current in the final user message):\n\n");
        foreach (var turn in turns.Take(turns.Count - 1))
        {
            builder.Append(turn.Role == ChatRole.User ? "User: " : "Assistant: ").AppendLine(StripFormState(turn.Text)).AppendLine();
        }
        builder.AppendLine("Now answer this final user message:").AppendLine().Append(turns[^1].Text);
        return builder.ToString();
    }

    private static string StripFormState(string text)
    {
        var start = text.IndexOf("<form_state>", StringComparison.Ordinal);
        var end = text.IndexOf("</form_state>", StringComparison.Ordinal);
        return start >= 0 && end > start ? (text[..start] + text[(end + "</form_state>".Length)..]).Trim() : text;
    }

    private static SessionConfig CreateSessionConfig(VideoHelperOptions options, TokenCredential? credential, CancellationToken cancellationToken) => new()
    {
        Model = options.Model,
        Streaming = true,
        Provider = CreateProvider(options, credential, cancellationToken),
        Tools = VideoHelperTools.Create().ToList<AIFunctionDeclaration>(),
        AvailableTools = [.. ToolNames],
        SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Replace, Content = VideoHelperInstructions.Text },
        ModelCapabilities = options.Vision ? new ModelCapabilitiesOverride { Supports = new() { Vision = true } } : null,
        EnableConfigDiscovery = false,
        EnableSkills = false,
        SkipCustomInstructions = true,
        OnPermissionRequest = (request, _) => Task.FromResult(
            request is PermissionRequestCustomTool tool && ToolNames.Contains(tool.ToolName)
                ? PermissionDecision.ApproveOnce()
                : PermissionDecision.Reject("The Video Production Helper may only use the Create page form tools.")),
    };

    private static GitHub.Copilot.ProviderConfig CreateProvider(VideoHelperOptions options, TokenCredential? credential, CancellationToken cancellationToken)
    {
        var provider = new GitHub.Copilot.ProviderConfig
        {
            Type = "openai",
            BaseUrl = options.Endpoint ?? throw new InvalidOperationException("Agent:Endpoint is required."),
            WireApi = options.ResolvedWireApi,
        };
        if (credential is null)
        {
            provider.ApiKey = options.ApiKey;
        }
        else
        {
            // The credential caches and refreshes tokens, so one instance serves every run.
            provider.BearerTokenProvider = async _ =>
                (await credential.GetTokenAsync(new TokenRequestContext([VideoHelperOptions.TokenScope]), cancellationToken)).Token;
        }
        return provider;
    }

    private static CopilotClient CreateClient(VideoHelperOptions options, ILoggerFactory loggers)
    {
        var state = WritableStateDirectory(options);
        var environment = new Dictionary<string, string>();
        if (!OperatingSystem.IsWindows())
        {
            // The runtime also writes under HOME, which is read-only for the container user on App Service.
            environment["HOME"] = state;
        }

        return new CopilotClient(new CopilotClientOptions
        {
            BaseDirectory = state,
            UseLoggedInUser = false,
            Environment = environment,
            Logger = loggers.CreateLogger<CopilotClient>(),
        });
    }

    /// <summary>The configured folder, else the user's data folder, else the temp folder: the first one that can be created and written.</summary>
    private static string WritableStateDirectory(VideoHelperOptions options)
    {
        var candidates = new[]
        {
            options.StateDirectory,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoSafetyStudio", "copilot"),
            Path.Combine(Path.GetTempPath(), "VideoSafetyStudio", "copilot"),
        };
        foreach (var path in candidates.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                Directory.CreateDirectory(path!);
                var probe = Path.Combine(path!, $".write-{Guid.NewGuid():N}");
                File.WriteAllBytes(probe, []);
                File.Delete(probe);
                return path!;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
            }
        }

        throw new InvalidOperationException("No writable folder is available for the Copilot runtime state.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsValueCreated) await _client.Value.DisposeAsync();
    }

    private sealed class VideoHelperSession : AgentSession;
}
