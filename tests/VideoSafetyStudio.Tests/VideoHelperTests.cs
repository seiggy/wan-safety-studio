using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using VideoSafetyStudio.Infrastructure.Agent;

namespace VideoSafetyStudio.Tests;

public class VideoHelperTests
{
    private static AIFunction Tool(string name) => VideoHelperTools.Create().Single(tool => tool.Name == name);

    private static async Task<string> Call(string name, params (string Key, object Value)[] args) =>
        (await Tool(name).InvokeAsync(new AIFunctionArguments(args.ToDictionary(a => a.Key, a => (object?)a.Value)))) is JsonElement json
            ? json.GetString()!
            : "";

    [Fact]
    public void Exposes_only_the_form_controls()
    {
        Assert.Equal(
            ["add_scene", "remove_scene", "set_aspect_ratio", "set_duration", "set_negative_prompt", "set_quality", "set_scene_prompt", "set_takes"],
            VideoHelperTools.Create().Select(tool => tool.Name).Order());
    }

    [Fact]
    public async Task Tools_apply_valid_arguments_and_explain_rejections()
    {
        Assert.StartsWith("Applied", await Call("set_duration", ("seconds", 7)));
        Assert.StartsWith("Rejected", await Call("set_duration", ("seconds", 12)));
        Assert.StartsWith("Rejected", await Call("set_takes", ("count", 2)));
        Assert.StartsWith("Rejected", await Call("remove_scene", ("scene", 1)));
        Assert.StartsWith("Applied", await Call("set_aspect_ratio", ("value", "4:3")));
    }

    [Fact]
    public void Transcript_labels_earlier_turns_and_keeps_only_the_latest_form_state()
    {
        var single = VideoHelperAgent.Transcript([new ChatMessage(ChatRole.User, "hi")]);
        Assert.Equal("hi", single);

        var transcript = VideoHelperAgent.Transcript(
        [
            new ChatMessage(ChatRole.User, "<form_state>{\"old\":1}</form_state>\n\nfirst"),
            new ChatMessage(ChatRole.Assistant, "answer"),
            new ChatMessage(ChatRole.User, "<form_state>{\"new\":1}</form_state>\n\nsecond"),
        ]);
        Assert.Contains("User: first", transcript);
        Assert.Contains("Assistant: answer", transcript);
        Assert.DoesNotContain("\"old\"", transcript);
        Assert.Contains("\"new\"", transcript);
    }

    private const string Foundry = "https://res.openai.azure.com/openai/v1/";

    [Theory]
    [InlineData(Foundry, null, null, null, VideoHelperAuth.AzureCli)]
    [InlineData(Foundry, "k", null, null, VideoHelperAuth.ApiKey)]
    [InlineData("http://localhost:11434/v1", null, null, null, VideoHelperAuth.ApiKey)]
    [InlineData(Foundry, null, "AzureCli", null, VideoHelperAuth.AzureCli)]
    [InlineData(Foundry, null, "SystemAssignedManagedIdentity", null, VideoHelperAuth.SystemAssignedManagedIdentity)]
    [InlineData(Foundry, null, "UserAssignedManagedIdentity", "id", VideoHelperAuth.UserAssignedManagedIdentity)]
    public void Auth_resolves_from_configuration(string endpoint, string? key, string? auth, string? clientId, VideoHelperAuth expected)
    {
        var options = new VideoHelperOptions { Endpoint = endpoint, Model = "m", ApiKey = key, Auth = auth, ManagedIdentityClientId = clientId };
        Assert.Equal(expected, options.ResolvedAuth);
        Assert.Equal(expected == VideoHelperAuth.ApiKey, options.CreateCredential() is null);
        Assert.Equal(expected != VideoHelperAuth.ApiKey || key is not null, options.Enabled);
    }

    [Theory]
    [InlineData(null, "m", null, null, "Agent:Endpoint")]
    [InlineData(Foundry, "m", "Bogus", null, "not recognised")]
    [InlineData(Foundry, "m", "ApiKey", null, "Agent:ApiKey")]
    [InlineData(Foundry, "m", "UserAssignedManagedIdentity", null, "ManagedIdentityClientId")]
    public void Incomplete_configuration_explains_what_is_missing(string? endpoint, string? model, string? auth, string? clientId, string expected)
    {
        var options = new VideoHelperOptions { Endpoint = endpoint, Model = model, Auth = auth, ManagedIdentityClientId = clientId };
        Assert.False(options.Enabled);
        Assert.Contains(expected, options.ConfigurationError);
    }
    [Fact]
    public async Task Status_reports_the_helper_as_unconfigured_by_default()
    {
        await using var factory = new ApiTests.Factory();
        using var client = factory.CreateClient();
        var status = await client.GetFromJsonAsync<JsonElement>("/api/agent/status");
        Assert.False(status.GetProperty("enabled").GetBoolean());
        Assert.Contains("Agent:Endpoint", status.GetProperty("hint").GetString());
    }
}
