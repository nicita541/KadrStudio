using System.Text.Json;
using System.Text.Json.Nodes;
using KadrStudio.AiServer.Api;
using KadrStudio.AiServer.Configuration;
using KadrStudio.AiServer.Inference;

namespace KadrStudio.AiServer.Tests;

public sealed class StructuredInferencePipelineTests
{
    private static readonly JsonElement AnswerSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        properties = new
        {
            answer = new { type = "string", minLength = 1 }
        },
        required = new[] { "answer" },
        additionalProperties = false
    });

    [Fact]
    public async Task ThinkingOnlyLengthResponseIsFinalizedWithoutExposingThinking()
    {
        var runtime = new FakeRuntime(
            Response(content: "", thinking: "private chain of thought", doneReason: "length", evalCount: 1280),
            Response(content: "{\"answer\":\"ready\"}", thinking: "", doneReason: "stop", evalCount: 22));
        var pipeline = new StructuredInferencePipeline(runtime);

        var result = await pipeline.RunAsync(Request(think: true), Planner(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("{\"answer\":\"ready\"}", result.Content);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(1280, result.ReasoningEvalCount);
        Assert.DoesNotContain("private chain", result.Content, StringComparison.Ordinal);
        Assert.Equal("low", runtime.Requests[0]["think"]?.GetValue<string>());
        Assert.False(runtime.Requests[1]["think"]?.GetValue<bool>());
        Assert.Equal(
            1792,
            runtime.Requests[0]["options"]?["num_predict"]?.GetValue<int>());
        Assert.Equal(
            512,
            runtime.Requests[1]["options"]?["num_predict"]?.GetValue<int>());
        Assert.Equal(
            "Return an answer.",
            runtime.Requests[1]["messages"]?[1]?["content"]?.GetValue<string>());
        Assert.DoesNotContain(
            "private chain of thought",
            runtime.Requests[1].ToJsonString(),
            StringComparison.Ordinal);
        Assert.Equal("object", runtime.Requests[0]["format"]?["type"]?.GetValue<string>());
        Assert.Contains("additionalProperties", runtime.Requests[0].ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThinkingAndAnswerShareCombinedFirstAttemptBudget()
    {
        var runtime = new FakeRuntime(
            Response(
                content: "{\"answer\":\"ready after reasoning\"}",
                thinking: "private analysis",
                doneReason: "stop",
                evalCount: 42));

        var result = await new StructuredInferencePipeline(runtime)
            .RunAsync(Request(think: true), Planner(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.AttemptCount);
        Assert.Single(runtime.Requests);
        Assert.Equal(
            1792,
            runtime.Requests[0]["options"]?["num_predict"]?.GetValue<int>());
        Assert.DoesNotContain("private analysis", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidJsonIsRepairedOnce()
    {
        var runtime = new FakeRuntime(
            Response("not json", "", "stop", 10),
            Response("{\"answer\":\"fixed\"}", "", "stop", 12));
        var result = await new StructuredInferencePipeline(runtime)
            .RunAsync(Request(think: false), Planner(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, runtime.Requests.Count);
    }

    [Fact]
    public async Task SchemaViolationAfterRepairReturnsTypedFailure()
    {
        var runtime = new FakeRuntime(
            Response("{\"wrong\":true}", "", "stop", 10),
            Response("{\"answer\":\"ok\",\"extra\":1}", "", "stop", 11));
        var result = await new StructuredInferencePipeline(runtime)
            .RunAsync(Request(think: false), Planner(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("schema_validation_failed", result.ErrorCode);
        Assert.Equal(2, result.AttemptCount);
        Assert.DoesNotContain("thinking", result.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExhaustedThinkingBudgetReturnsTypedFailure()
    {
        var runtime = new FakeRuntime(
            Response("", "secret", "length", 1280),
            Response("", "", "length", 64));
        var result = await new StructuredInferencePipeline(runtime)
            .RunAsync(Request(think: true), Planner(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("reasoning_budget_exhausted", result.ErrorCode);
        Assert.Null(result.Content);
    }

    [Fact]
    public async Task InvalidNonEmptyFinalizerOutputIsClassifiedAsSchemaFailure()
    {
        var runtime = new FakeRuntime(
            Response("", "secret", "length", 1280),
            Response("{\"answer\":", "", "stop", 32));
        var result = await new StructuredInferencePipeline(runtime)
            .RunAsync(Request(think: true), Planner(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("schema_validation_failed", result.ErrorCode);
        Assert.Equal(2, result.AttemptCount);
    }

    [Fact]
    public async Task CompletedValuesWithOnlyMissingContainersAreClosedDeterministically()
    {
        var runtime = new FakeRuntime(
            Response("not json", "", "stop", 10),
            Response("{\"answer\":\"fixed\"", "", "stop", 12));
        var result = await new StructuredInferencePipeline(runtime)
            .RunAsync(Request(think: false), Planner(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal("{\"answer\":\"fixed\"}", result.Content);
        Assert.Equal(2, result.AttemptCount);
    }

    [Fact]
    public async Task ContextWindowGrowsToFitPromptAndReservedOutputBudgets()
    {
        var runtime = new FakeRuntime(
            Response("{\"answer\":\"grown\"}", "", "stop", 10));
        var request = Request(think: true) with
        {
            UserPrompt = new string('x', 30_000),
            ContextTokens = 8_192,
            MaxTokens = 2_048,
            ReasoningTokens = 1_280
        };

        var result = await new StructuredInferencePipeline(runtime)
            .RunAsync(request, Planner(), CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            16_384,
            runtime.Requests[0]["options"]?["num_ctx"]?.GetValue<int>());
    }

    [Fact]
    public async Task PromptBeyondMaximumDynamicWindowIsRejectedBeforeInference()
    {
        var runtime = new FakeRuntime();
        var request = Request(think: true) with
        {
            UserPrompt = new string('x', 100_000),
            ContextTokens = 8_192,
            MaxTokens = 2_048,
            ReasoningTokens = 1_280
        };

        var result = await new StructuredInferencePipeline(runtime)
            .RunAsync(request, Planner(), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid_context_budget", result.ErrorCode);
        Assert.Empty(runtime.Requests);
    }

    [Fact]
    public void ValidatorRejectsBoundsUuidAndAdditionalProperties()
    {
        var schema = JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new
            {
                id = new { type = "string", format = "uuid" },
                values = new
                {
                    type = "array",
                    minItems = 1,
                    maxItems = 2,
                    items = new { type = "integer", minimum = 1, maximum = 3 }
                }
            },
            required = new[] { "id", "values" },
            additionalProperties = false
        });

        var valid = StructuredOutputValidator.TryValidate(
            "{\"id\":\"bad\",\"values\":[0,4],\"extra\":true}",
            schema,
            out _,
            out var errors);

        Assert.False(valid);
        Assert.Contains(errors, error => error.Contains("UUID", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("minimum", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("maximum", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("additional property", StringComparison.Ordinal));
    }

    private static StructuredInferenceRequest Request(bool think)
        => new(
            AnswerSchema,
            "Understand the task.",
            "Return an answer.",
            ContextTokens: 8192,
            MaxTokens: 512,
            Model: AiServerOptions.DefaultPlannerPublicModelAlias,
            Think: think,
            ReasoningTokens: 1280);

    private static AiServerModelRoute Planner()
        => new(
            AiServerOptions.DefaultPlannerPublicModelAlias,
            AiServerOptions.DefaultPlannerBackendModel,
            RequiresVision: false,
            Role: "planner");

    private static JsonObject Response(
        string content,
        string thinking,
        string doneReason,
        int evalCount)
        => new()
        {
            ["message"] = new JsonObject
            {
                ["content"] = content,
                ["thinking"] = thinking
            },
            ["done_reason"] = doneReason,
            ["eval_count"] = evalCount
        };

    private sealed class FakeRuntime : IInferenceChatRuntime
    {
        private readonly Queue<JsonObject> _responses;

        public FakeRuntime(params JsonObject[] responses)
        {
            _responses = new Queue<JsonObject>(responses);
        }

        public List<JsonObject> Requests { get; } = [];

        public Task<JsonObject> ChatAsync(
            AiServerModelRoute model,
            JsonObject publicRequest,
            CancellationToken cancellationToken)
        {
            Requests.Add((JsonObject)publicRequest.DeepClone());
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
