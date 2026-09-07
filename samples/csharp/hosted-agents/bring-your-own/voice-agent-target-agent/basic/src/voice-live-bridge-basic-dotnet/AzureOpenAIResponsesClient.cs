// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.ClientModel;
using System.ClientModel.Primitives;
using System.Runtime.CompilerServices;
using Azure.Identity;
using OpenAI;
using OpenAI.Responses;

#pragma warning disable OPENAI001

namespace VoiceHostedAgent;

internal sealed class AzureOpenAIResponsesClient : IStreamingModelClient
{
    private const string FoundryTokenScope = "https://ai.azure.com/.default";
    private const string DefaultSystemPrompt =
        "You are a concise voice assistant. Answer naturally in plain text without markdown.";
    private const int DefaultMaxOutputTokens = 512;

    private readonly ResponsesClient _client;
    private readonly string _systemPrompt;
    private readonly int _maxOutputTokens;

    private AzureOpenAIResponsesClient(
        ResponsesClient client,
        Uri endpoint,
        string modelName,
        string systemPrompt,
        int maxOutputTokens)
    {
        _client = client;
        Endpoint = endpoint;
        ModelName = modelName;
        ServerAddress = endpoint.Host;
        _systemPrompt = systemPrompt;
        _maxOutputTokens = maxOutputTokens;
    }

    public string ModelName { get; }

    public string ServerAddress { get; }

    internal Uri Endpoint { get; }

    internal static AzureOpenAIResponsesClient CreateFromEnvironment()
    {
        var endpoint = CreateOpenAIEndpoint(Require("FOUNDRY_PROJECT_ENDPOINT"));
        var model = Require("AZURE_AI_MODEL_DEPLOYMENT_NAME");
        var systemPrompt = Environment.GetEnvironmentVariable("AZURE_OPENAI_SYSTEM_PROMPT");
        var maxOutputTokens = ParseMaxOutputTokens(
            Environment.GetEnvironmentVariable("AZURE_OPENAI_MAX_OUTPUT_TOKENS"));
        var apiKey = Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY");

        return new AzureOpenAIResponsesClient(
            CreateClient(endpoint, apiKey),
            endpoint,
            model,
            string.IsNullOrWhiteSpace(systemPrompt) ? DefaultSystemPrompt : systemPrompt,
            maxOutputTokens);
    }

    public async IAsyncEnumerable<string> CompleteAsync(
        IReadOnlyList<ModelMessage> messages,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var options = new CreateResponseOptions
        {
            Model = ModelName,
            Instructions = _systemPrompt,
            MaxOutputTokenCount = _maxOutputTokens,
            StoredOutputEnabled = false,
            StreamingEnabled = true,
        };

        foreach (var message in messages)
        {
            options.InputItems.Add(message.Role switch
            {
                ModelMessageRole.User => ResponseItem.CreateUserMessageItem(message.Content),
                ModelMessageRole.Assistant => ResponseItem.CreateAssistantMessageItem(message.Content),
                _ => throw new InvalidOperationException($"Unsupported role {message.Role}."),
            });
        }

        var completed = false;
        await foreach (var update in _client
            .CreateResponseStreamingAsync(options, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            switch (update)
            {
                case StreamingResponseOutputTextDeltaUpdate delta
                    when !string.IsNullOrEmpty(delta.Delta):
                    yield return delta.Delta;
                    break;
                case StreamingResponseCompletedUpdate:
                    completed = true;
                    break;
                case StreamingResponseIncompleteUpdate:
                    throw new InvalidOperationException("The model response was incomplete.");
                case StreamingResponseFailedUpdate:
                case StreamingResponseErrorUpdate:
                    throw new InvalidOperationException("The model response failed.");
            }

            if (completed)
            {
                break;
            }
        }

        if (!completed)
        {
            throw new InvalidOperationException("The model stream ended without completion.");
        }
    }

    private static ResponsesClient CreateClient(Uri endpoint, string? apiKey)
    {
        var options = new OpenAIClientOptions { Endpoint = endpoint };
        return string.IsNullOrWhiteSpace(apiKey)
            ? new OpenAIClient(
                new BearerTokenPolicy(new DefaultAzureCredential(), FoundryTokenScope),
                options).GetResponsesClient()
            : new OpenAIClient(new ApiKeyCredential(apiKey), options).GetResponsesClient();
    }

    private static Uri CreateOpenAIEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var projectEndpoint) ||
            projectEndpoint.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(projectEndpoint.UserInfo) ||
            !string.IsNullOrEmpty(projectEndpoint.Query) ||
            !string.IsNullOrEmpty(projectEndpoint.Fragment))
        {
            throw new InvalidOperationException(
                "FOUNDRY_PROJECT_ENDPOINT must be an absolute HTTPS Foundry project endpoint.");
        }

        var segments = projectEndpoint.AbsolutePath
            .TrimEnd('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 3 ||
            !string.Equals(segments[0], "api", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "projects", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(segments[2]))
        {
            throw new InvalidOperationException(
                "FOUNDRY_PROJECT_ENDPOINT path must be /api/projects/<project>.");
        }

        return new Uri($"{projectEndpoint.AbsoluteUri.TrimEnd('/')}/openai/v1");
    }

    private static string Require(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"Required environment variable {name} is not set.");

    private static int ParseMaxOutputTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return DefaultMaxOutputTokens;
        }

        return int.TryParse(value, out var parsed) && parsed is >= 1 and <= 4096
            ? parsed
            : throw new InvalidOperationException(
                "AZURE_OPENAI_MAX_OUTPUT_TOKENS must be between 1 and 4096.");
    }
}

#pragma warning restore OPENAI001
