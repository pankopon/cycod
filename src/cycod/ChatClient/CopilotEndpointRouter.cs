using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Decides which GitHub Copilot API endpoint a given model must be called through.
/// </summary>
/// <remarks>
/// Copilot serves different models through different APIs, and a model rejects
/// requests to an endpoint it does not support:
///
///   "The requested model is not supported. ... model \"gpt-5.5\" is not
///    accessible via the /chat/completions endpoint"
///
/// Older OpenAI models and all Gemini models are reachable via /chat/completions.
/// Newer OpenAI models (gpt-5.5 and later) and the Grok models are /responses
/// only. Claude models are /chat/completions plus /v1/messages, never /responses.
/// Because that mapping changes as models ship, it is read from the service's own
/// /models metadata rather than hard-coded here.
/// </remarks>
public static class CopilotEndpointRouter
{
    public const string ChatCompletionsEndpoint = "/chat/completions";
    public const string ResponsesEndpoint = "/responses";

    private static readonly Dictionary<string, Dictionary<string, List<string>>> _cachedEndpointsByService = new(StringComparer.Ordinal);
    private static readonly object _cacheLock = new();

    /// <summary>
    /// Returns true when the model must be driven through the Responses API.
    /// </summary>
    /// <remarks>
    /// Deliberately biased toward the historical behaviour: /chat/completions is
    /// chosen whenever it is supported, and also whenever the model's capabilities
    /// cannot be determined. Only a model that positively reports /responses
    /// support *and* no /chat/completions support is routed the new way, so models
    /// that work today keep taking exactly the path they take today.
    /// </remarks>
    public static bool ShouldUseResponsesApi(string modelName, string copilotToken, string editorVersion, string endpoint = "https://api.githubcopilot.com")
    {
        var mode = EnvironmentHelpers.FindEnvVar("COPILOT_API_MODE");
        if (!string.IsNullOrEmpty(mode))
        {
            if (mode.Equals("responses", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleHelpers.WriteDebugLine("COPILOT_API_MODE=responses; forcing the Responses API");
                return true;
            }
            if (mode.Equals("chat", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleHelpers.WriteDebugLine("COPILOT_API_MODE=chat; forcing the Chat Completions API");
                return false;
            }
            ConsoleHelpers.WriteDebugLine($"COPILOT_API_MODE='{mode}' not recognized (expected 'chat', 'responses', or 'auto'); detecting automatically");
        }

        var endpoints = GetSupportedEndpoints(modelName, copilotToken, editorVersion, endpoint);
        if (endpoints == null)
        {
            // Either the metadata could not be fetched or this model does not
            // publish the field. Both mean "unknown", so keep today's behaviour.
            ConsoleHelpers.WriteDebugLine($"No endpoint metadata for model '{modelName}'; using {ChatCompletionsEndpoint}");
            return false;
        }

        var supportsChatCompletions = endpoints.Any(e => e.Equals(ChatCompletionsEndpoint, StringComparison.OrdinalIgnoreCase));
        var supportsResponses = endpoints.Any(e => e.Equals(ResponsesEndpoint, StringComparison.OrdinalIgnoreCase));

        var useResponses = supportsResponses && !supportsChatCompletions;
        var chosen = useResponses ? ResponsesEndpoint : ChatCompletionsEndpoint;
        ConsoleHelpers.WriteDebugLine($"Model '{modelName}' supports [{string.Join(", ", endpoints)}]; using {chosen}");

        return useResponses;
    }

    /// <summary>
    /// Endpoints the model reports support for, or null when that is unknown.
    /// </summary>
    private static List<string>? GetSupportedEndpoints(string modelName, string copilotToken, string editorVersion, string endpoint)
    {
        var map = GetEndpointMap(copilotToken, editorVersion, endpoint);
        if (map == null) return null;

        return map.TryGetValue(modelName, out var endpoints) ? endpoints : null;
    }

    /// <summary>
    /// Fetches and caches the model-to-endpoints map for the life of the process.
    /// </summary>
    private static Dictionary<string, List<string>>? GetEndpointMap(string copilotToken, string editorVersion, string endpoint)
    {
        lock (_cacheLock)
        {
            var cacheKey = endpoint.TrimEnd('/');
            if (_cachedEndpointsByService.TryGetValue(cacheKey, out var cachedMap)) return cachedMap;

            try
            {
                var helper = new GitHubCopilotModelsHelpers();
                var models = helper.GetModelsSync(copilotToken, editorVersion, endpoint);

                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var model in models.Data ?? new List<GitHubCopilotModelsHelpers.ModelInfo>())
                {
                    if (string.IsNullOrEmpty(model.Id)) continue;
                    if (model.SupportedEndpoints == null) continue;
                    map[model.Id!] = model.SupportedEndpoints;
                }

                _cachedEndpointsByService[cacheKey] = map;
                ConsoleHelpers.WriteDebugLine($"Loaded Copilot endpoint metadata for {map.Count} model(s)");
                return map;
            }
            catch (Exception ex)
            {
                // Never let metadata lookup stop a chat from starting; the caller
                // falls back to the endpoint that has always been used.
                ConsoleHelpers.WriteDebugLine($"Could not load Copilot model metadata ({ex.GetType().Name}: {ex.Message}); assuming {ChatCompletionsEndpoint}");

                // Cache the failure so a broken/slow endpoint is not retried on
                // every client creation within this process.
                var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                _cachedEndpointsByService[cacheKey] = map;
                return map;
            }
        }
    }
}
