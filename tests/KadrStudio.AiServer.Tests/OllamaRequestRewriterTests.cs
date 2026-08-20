using Microsoft.AspNetCore.Http;
using System.Text.Json.Nodes;
using KadrStudio.AiServer.Inference;

namespace KadrStudio.AiServer.Tests;

public sealed class OllamaRequestRewriterTests
{
    [Fact]
    public void ReplacesClientModelWithServerManagedModel()
    {
        var request = new JsonObject
        {
            ["model"] = "client-selected-model",
            ["stream"] = false,
            ["messages"] = new JsonArray()
        };

        var rewritten = OllamaRequestRewriter.RewriteChatRequest(request, "server-model");

        Assert.Equal("server-model", rewritten["model"]!.GetValue<string>());
        Assert.False(rewritten["stream"]!.GetValue<bool>());
        Assert.Equal("client-selected-model", request["model"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsStreamingRequests()
    {
        var request = new JsonObject
        {
            ["model"] = "anything",
            ["stream"] = true
        };

        Assert.Throws<BadHttpRequestException>(() =>
            OllamaRequestRewriter.RewriteChatRequest(request, "server-model"));
    }

    [Fact]
    public void MasksBackendModelInResponse()
    {
        var response = new JsonObject
        {
            ["model"] = "private-server-model",
            ["message"] = new JsonObject { ["content"] = "{}" }
        };

        var masked = OllamaRequestRewriter.MaskChatResponse(response, "kadr-vision:latest");

        Assert.Equal("kadr-vision:latest", masked["model"]!.GetValue<string>());
        Assert.Equal("private-server-model", response["model"]!.GetValue<string>());
    }
}
