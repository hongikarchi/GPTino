// SPDX-License-Identifier: Apache-2.0
// Deterministic routing design informed by SkillMeld; no SkillMeld runtime code is linked.

using System.Collections.Concurrent;
using GPTino.Contracts;

namespace GPTino.AgentHost.Codex;

public sealed record MessageRoute(
    TaskClass TaskClass,
    ModelProfile ClassifiedProfile,
    ModelProfile EffectiveProfile,
    string PersistedPreference,
    bool Escalated,
    string Rationale);

public sealed class MessageRoutingPolicy
{
    private const int LargeContextCharacters = 4_000;
    private const int LargeContextLines = 80;

    private static readonly string[] RecoveryTerms =
    [
        "recover", "recovery", "rollback", "restore", "error", "exception", "failed", "failure",
        "crash", "broken", "debug", "diagnose", "cannot connect", "won't connect", "401", "복구",
        "롤백", "오류", "에러", "실패", "크래시", "디버그", "진단", "작동하지", "안 됨", "안됨"
    ];

    private static readonly string[] HighAssuranceTerms =
    [
        "python", "ghpython", "script", "schema", "socket type", "input type", "output type", "i/o",
        "geometry", "topology", "brep", "mesh", "surface", "curve", "solid", "boolean", "loft",
        "sweep", "delete", "remove", "bake", "solver", "global state", "non-reversible", "external reference",
        "complex", "parametric", "파이썬", "스크립트", "코드", "스키마", "입출력", "소켓 타입", "형상",
        "지오메트리", "토폴로지", "브렙", "메시", "서피스", "커브", "솔리드", "불리언", "로프트",
        "스윕", "삭제", "제거", "베이크", "솔버", "전역", "복잡", "파라메트릭"
    ];

    private static readonly string[] AmbiguityTerms =
    [
        "something", "anything", "whichever", "either one", "some component", "somewhere", "roughly",
        "that thing", "those things", "not sure which", "ambiguous", "그거", "저거", "아무거나", "무언가",
        "어딘가", "적당히", "어느 것", "어느것", "모호", "뭔가"
    ];

    private static readonly string[] LargeScopeTerms =
    [
        "all components", "every component", "entire definition", "whole definition", "multiple documents",
        "across documents", "everything", "모든 컴포넌트", "전체 정의", "정의 전체", "문서 전체", "전부",
        "여러 문서"
    ];

    private static readonly string[] ReadTerms =
    [
        "read", "inspect", "show", "list", "status", "check", "find", "describe", "what", "which",
        "snapshot", "analyze", "analyse", "읽", "확인", "조회", "보여", "목록", "상태", "찾", "설명",
        "무엇", "어떤", "분석", "스냅샷"
    ];

    private static readonly string[] SimpleWriteTerms =
    [
        "move", "wire", "connect", "disconnect", "이동", "옮겨", "와이어", "연결", "연결 해제"
    ];

    private static readonly string[] StructuralBuildTerms =
    [
        "create", "add", "build", "make", "generate", "construct",
        "생성", "추가", "만들", "구성", "작성"
    ];

    private static readonly string[] ParametricBuildSubjectTerms =
    [
        "grasshopper", "definition", "component graph", "parametric", "adjustable",
        "cylinder", "cone", "sphere", "surface", "solid", "diameter", "radius", "height",
        "그래스호퍼", "파라메트릭", "조절 가능한", "조정할 수 있는", "컴포넌트 구성",
        "실린더", "원기둥", "원뿔", "구체", "곡면", "솔리드", "지름", "반지름", "높이"
    ];

    private static readonly string[] AnyWriteTerms =
    [
        .. SimpleWriteTerms,
        "change", "edit", "modify", "create", "add", "replace", "delete", "remove", "bake", "execute",
        "run", "변경", "수정", "생성", "추가", "교체", "삭제", "제거", "베이크", "실행"
    ];

    private static readonly string[] ContextContinuationTerms =
    [
        "continue", "keep going", "do it", "proceed", "try again", "same", "as discussed",
        "계속", "진행", "해줘", "해주세요", "다시", "그대로", "앞에서", "방금", "아까"
    ];

    public MessageRoute Route(
        string content,
        string persistedPreference,
        ModelProfile? previousContextFloor = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var preference = NormalizePreference(persistedPreference);
        var (taskClass, classified, reason) = Classify(content);
        var preferenceFloor = PreferenceFloor(preference);
        var contextFloor = previousContextFloor is not null && IsContextDependentContinuation(content)
            ? previousContextFloor
            : null;
        var effective = classified;
        if (preferenceFloor is not null)
        {
            effective = Max(effective, preferenceFloor.Value);
        }
        if (contextFloor is not null)
        {
            effective = Max(effective, contextFloor.Value);
        }
        var escalated =
            (preferenceFloor is not null && preferenceFloor.Value != classified) ||
            (contextFloor is not null && Rank(contextFloor.Value) > Rank(classified));
        var rationale = contextFloor is not null && Rank(contextFloor.Value) > Rank(classified)
            ? $"{reason} This context-dependent follow-up retains the prior {contextFloor.Value} capability floor."
            : preferenceFloor is null
                ? $"{reason} Persisted preference 'auto' accepts the deterministic {effective} floor."
                : Rank(classified) > Rank(preferenceFloor.Value)
                    ? $"{reason} The deterministic task floor escalates preference '{preference}' to {effective}."
                    : Rank(preferenceFloor.Value) > Rank(classified)
                        ? $"{reason} Persisted preference '{preference}' raises the floor from {classified} to {effective}."
                        : $"{reason} Persisted preference '{preference}' matches the deterministic {effective} floor.";

        return new MessageRoute(taskClass, classified, effective, preference, escalated, rationale);
    }

    private static (TaskClass TaskClass, ModelProfile Profile, string Reason) Classify(string content)
    {
        if (ContainsAny(content, RecoveryTerms))
        {
            return (TaskClass.Recovery, ModelProfile.Recovery,
                "Recovery or runtime-failure language requires the recovery route.");
        }

        var lineCount = content.Count(character => character == '\n') + 1;
        var largeContext = content.Length >= LargeContextCharacters || lineCount >= LargeContextLines;
        var structuralParametricBuild =
            ContainsAny(content, StructuralBuildTerms) &&
            ContainsAny(content, ParametricBuildSubjectTerms);
        if (largeContext ||
            structuralParametricBuild ||
            ContainsAny(content, LargeScopeTerms) ||
            ContainsAny(content, AmbiguityTerms) ||
            ContainsAny(content, HighAssuranceTerms))
        {
            var reason = largeContext
                ? "Large message context requires high-assurance reasoning."
                : structuralParametricBuild
                    ? "A structural parametric or geometric build requires high-assurance reasoning."
                : ContainsAny(content, AmbiguityTerms)
                    ? "Ambiguous target language requires high-assurance reasoning."
                    : ContainsAny(content, LargeScopeTerms)
                        ? "Large-scope work requires high-assurance reasoning."
                        : "Complex modeling or schema language requires high-assurance reasoning.";
            return (TaskClass.ComplexWrite, ModelProfile.HighAssurance, reason);
        }

        var hasWrite = ContainsAny(content, AnyWriteTerms);
        if (!hasWrite && ContainsAny(content, ReadTerms))
        {
            return (TaskClass.ReadOnly, ModelProfile.ReadFast,
                "The message requests inspection without a recognized mutation.");
        }

        if (ContainsAny(content, SimpleWriteTerms) &&
            !ContainsAny(content, HighAssuranceTerms))
        {
            return (TaskClass.SimpleDeterministicWrite, ModelProfile.FastSafe,
                "The message is limited to deterministic move, wire, rename, or value work.");
        }

        return (TaskClass.StandardWrite, ModelProfile.Standard,
            "The message is neither read-only, fast-safe, nor high-risk, so the standard floor applies.");
    }

    private static string NormalizePreference(string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value)
            ? "auto"
            : value.Trim().Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        return normalized switch
        {
            "auto" => "auto",
            "read-fast" => "read-fast",
            "fast" or "fast-safe" => "fast-safe",
            "standard" => "standard",
            "deep" or "high-assurance" => "high-assurance",
            "recovery" => "recovery",
            _ => throw new ArgumentException($"Unknown persisted model preference '{value}'.", nameof(value))
        };
    }

    private static ModelProfile? PreferenceFloor(string preference) => preference switch
    {
        "auto" => null,
        "read-fast" => ModelProfile.ReadFast,
        "fast-safe" => ModelProfile.FastSafe,
        "standard" => ModelProfile.Standard,
        "high-assurance" => ModelProfile.HighAssurance,
        "recovery" => ModelProfile.Recovery,
        _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, null)
    };

    private static ModelProfile Max(ModelProfile left, ModelProfile right) =>
        Rank(left) >= Rank(right) ? left : right;

    private static int Rank(ModelProfile profile) => profile switch
    {
        ModelProfile.ReadFast => 0,
        ModelProfile.FastSafe => 1,
        ModelProfile.Standard => 2,
        ModelProfile.HighAssurance => 3,
        ModelProfile.Recovery => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, null)
    };

    private static bool ContainsAny(string content, IEnumerable<string> terms) =>
        terms.Any(term => ContainsTerm(content, term));

    private static bool IsContextDependentContinuation(string content) =>
        content.Length <= 240 && ContainsAny(content, ContextContinuationTerms);

    private static bool ContainsTerm(string content, string term)
    {
        var start = 0;
        while (true)
        {
            var index = content.IndexOf(term, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return false;
            }

            if (!IsAsciiWord(term))
            {
                return true;
            }

            var beforeIsWord = index > 0 && IsAsciiWordCharacter(content[index - 1]);
            var after = index + term.Length;
            var afterIsWord = after < content.Length && IsAsciiWordCharacter(content[after]);
            if (!beforeIsWord && !afterIsWord)
            {
                return true;
            }
            start = index + term.Length;
        }
    }

    private static bool IsAsciiWord(string value) =>
        value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9');

    private static bool IsAsciiWordCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
}

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

    public void RecordSuccess(Guid sessionId, MessageRoute route, ModelSelection selection) =>
        _states[sessionId] = new EffectiveModelSnapshot(
            sessionId,
            route.TaskClass,
            route.EffectiveProfile,
            selection.Model,
            selection.Effort,
            selection.Rationale,
            null,
            DateTimeOffset.UtcNow);

    public void RecordFailure(Guid sessionId, MessageRoute route, Exception exception) =>
        _states[sessionId] = new EffectiveModelSnapshot(
            sessionId,
            route.TaskClass,
            route.EffectiveProfile,
            null,
            null,
            route.Rationale,
            exception.Message,
            DateTimeOffset.UtcNow);

    public bool TryGet(Guid sessionId, out EffectiveModelSnapshot snapshot) =>
        _states.TryGetValue(sessionId, out snapshot!);
}
