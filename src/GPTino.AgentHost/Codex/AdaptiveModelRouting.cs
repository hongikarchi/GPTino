// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using GPTino.Contracts;

namespace GPTino.AgentHost.Codex;

// Adaptive per-message routing (MessageRoutingPolicy / MessageRoute) was removed when reasoning
// effort became a direct manual setting — see ModelSelector.ResolveDirectAsync. This file now only
// records the effective model/effort chosen for a session so the projector can display it.

public sealed record EffectiveModelSnapshot(
    Guid SessionId,
    TaskClass TaskClass,
    ModelProfile EffectiveProfile,
    string? Model,
    string? Reasoning,
    string Rationale,
    string? Error,
    DateTimeOffset UpdatedAt);

public sealed class EffectiveModelState
{
    private readonly ConcurrentDictionary<Guid, EffectiveModelSnapshot> _states = new();

    public void RecordDirect(Guid sessionId, ModelSelection selection) =>
        _states[sessionId] = new EffectiveModelSnapshot(
            sessionId,
            TaskClass.StandardWrite,
            selection.EffectiveProfile,
            selection.Model,
            selection.Effort,
            selection.Rationale,
            null,
            DateTimeOffset.UtcNow);

    public void RecordDirectFailure(Guid sessionId, Exception exception) =>
        _states[sessionId] = new EffectiveModelSnapshot(
            sessionId,
            TaskClass.StandardWrite,
            ModelProfile.Standard,
            null,
            null,
            exception.Message,
            exception.Message,
            DateTimeOffset.UtcNow);

    public bool TryGet(Guid sessionId, out EffectiveModelSnapshot snapshot) =>
        _states.TryGetValue(sessionId, out snapshot!);
}
