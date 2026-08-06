namespace GPTino.Core;

/// <summary>
/// Raised when a turn cannot resolve a usable model. The adaptive router that originally threw this
/// was removed with per-message routing (#48); the type remains as the orchestrator's defensive
/// catch surface for model-resolution failures.
/// </summary>
public sealed class ModelRoutingException(string message) : InvalidOperationException(message);
