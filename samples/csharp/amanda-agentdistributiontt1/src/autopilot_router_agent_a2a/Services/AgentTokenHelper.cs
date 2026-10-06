namespace WorkstreamManager.Services;

using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using System.Text.Json;

public class AgentTokenHelper(ILogger<AgentTokenHelper> logger)
{
    private const int BlueprintTokenMaxAttempts = 5;
    private static readonly TimeSpan BlueprintTokenInitialDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan BlueprintTokenMaxDelay = TimeSpan.FromSeconds(4);

    /// <summary>
    /// Performs the three-step agentic user identity token acquisition process using managed identity.
    /// </summary>
    public async Task<string> GetAgenticUserTokenAsync(string agentAppId, string agentAppInstanceId, string userUpn, string tenantId, string[] scopes)
    {
        try
        {
            // FIRST: Get blueprint token via managed identity
            var blueprintToken = await GetBlueprintToken(agentAppId);

            // SECOND: Get AAD token for AgentAppInstanceId
            var instanceApp = ConfidentialClientApplicationBuilder
                .Create(agentAppInstanceId)
                .WithClientAssertion((AssertionRequestOptions _) => Task.FromResult(blueprintToken.Token))
                .WithAuthority(new Uri($"https://login.microsoftonline.com/{tenantId}"))
                .Build();

            var instanceTokenResult = await instanceApp
                .AcquireTokenForClient(["api://AzureAdTokenExchange/.default"])
                .ExecuteAsync();

            // THIRD: Get combined user token
            var userToken = await GetUserFederatedIdentityTokenAsync(
                agentAppInstanceId,
                tenantId,
                blueprintToken.Token,
                instanceTokenResult.AccessToken,
                userUpn,
                scopes);

            return userToken;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error acquiring agentic user token");
            throw;
        }
    }

    private async Task<AccessToken> GetBlueprintToken(string clientId)
    {
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = clientId,
        });
        var context = new TokenRequestContext(["api://AzureADTokenExchange/.default"]);

        // The Foundry hosted-agent identity endpoint can reject token requests with
        // "Invalid foundation token" (HTTP 401) for roughly the first second after the
        // container starts, while its managed identity is still being provisioned. The
        // container is routed traffic before that completes, so the first activity on a
        // cold start can arrive inside that window. Without a retry a single 401 aborts
        // the whole turn and the user gets no reply at all, because the error-reporting
        // path needs a token too. 1:1 chats hit this most often: the direct-message
        // access-control gate needs a Graph token earlier in the turn than group chats do.
        var delay = BlueprintTokenInitialDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await credential.GetTokenAsync(context);
            }
            catch (Exception ex) when (attempt < BlueprintTokenMaxAttempts && ex is not OperationCanceledException)
            {
                logger.LogWarning(
                    ex,
                    "Blueprint token acquisition failed for client {ClientId} (attempt {Attempt}/{MaxAttempts}). Retrying in {DelayMs}ms.",
                    clientId,
                    attempt,
                    BlueprintTokenMaxAttempts,
                    delay.TotalMilliseconds);

                await Task.Delay(delay);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, BlueprintTokenMaxDelay.Ticks));
            }
        }
    }

    private async Task<string> GetUserFederatedIdentityTokenAsync(
        string clientId,
        string tenantId,
        string clientAssertion,
        string userFederatedIdentityCredential,
        string username,
        string[] scopes)
    {
        using var httpClient = new HttpClient();

        var tokenEndpoint = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

        var parameters = new Dictionary<string, string>
        {
            { "client_id", clientId },
            { "scope", string.Join(" ", scopes) },
            { "client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer" },
            { "client_assertion", clientAssertion },
            { "user_federated_identity_credential", userFederatedIdentityCredential },
            { "grant_type", "user_fic" }
        };

        if (username.Contains('@'))
        {
            parameters["username"] = username;
        }
        else
        {
            parameters["user_id"] = username;
        }

        var content = new FormUrlEncodedContent(parameters);
        var response = await httpClient.PostAsync(tokenEndpoint, content);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Failed to acquire user federated identity token: {errorContent}");
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        var tokenResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(responseContent);

        if (tokenResponse != null && tokenResponse.TryGetValue("access_token", out var accessToken))
        {
            var token = accessToken?.ToString();
            return token ?? throw new InvalidOperationException("Access token is null");
        }

        throw new InvalidOperationException("Failed to parse access token from response");
    }
}
