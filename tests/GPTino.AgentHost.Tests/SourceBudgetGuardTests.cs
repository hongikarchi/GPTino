using GPTino.AgentHost.Runtime;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// Layer 2 backstop: the conservative infinite-loop detector rejects a script ONLY when an unbounded
/// loop header has no exit or budget-guard mechanism anywhere. Anything with any exit path passes.
/// </summary>
public sealed class SourceBudgetGuardTests
{
    [Fact]
    public void CSharpUnboundedLoopWithNoExitIsRejected()
    {
        const string src = "int x = 0;\nwhile (true)\n{\n    x = x + 1;\n}";
        Assert.True(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: true));
    }

    [Fact]
    public void CSharpForEverWithNoExitIsRejected()
    {
        const string src = "for (;;)\n{\n    DoWork();\n}";
        Assert.True(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: true));
    }

    [Fact]
    public void CSharpUnboundedLoopWithBreakPasses()
    {
        const string src = "while (true)\n{\n    if (done) break;\n    Step();\n}";
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: true));
    }

    [Fact]
    public void CSharpUnboundedLoopWithBudgetGuardPasses()
    {
        const string src =
            "var __sw = System.Diagnostics.Stopwatch.StartNew(); long __i = 0;\n" +
            "while (true)\n{\n    if (__sw.ElapsedMilliseconds > 8000 || ++__i > 20000000) " +
            "throw new System.TimeoutException(\"solve budget\");\n    Step();\n}";
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: true));
    }

    [Fact]
    public void CSharpBoundedForLoopIsNeverBlocked()
    {
        const string src = "for (int i = 0; i < n; i++)\n{\n    panels.Add(Build(i));\n}";
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: true));
    }

    [Fact]
    public void CSharpCommentedOutInfiniteLoopIsNotTripped()
    {
        const string src = "// while (true) { }  legacy note\nvar a = Build();";
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: true));
    }

    [Fact]
    public void PythonWhileTrueWithNoExitIsRejected()
    {
        const string src = "x = 0\nwhile True:\n    x += 1";
        Assert.True(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: false));
    }

    [Fact]
    public void PythonWhileTrueWithBreakPasses()
    {
        const string src = "while True:\n    if done:\n        break\n    step()";
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: false));
    }

    [Fact]
    public void PythonWhileTrueWithBudgetGuardPasses()
    {
        const string src =
            "import time\n__t0 = time.time(); __i = 0\n" +
            "while True:\n    if time.time() - __t0 > 8 or (__i := __i + 1) > 20000000:\n" +
            "        raise TimeoutError('solve budget')\n    step()";
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: false));
    }

    [Fact]
    public void PythonBoundedRangeLoopIsNeverBlocked()
    {
        const string src = "for i in range(n):\n    panels.append(build(i))";
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape(src, isCSharp: false));
    }

    [Fact]
    public void EmptySourceIsNeverBlocked()
    {
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape("", isCSharp: true));
        Assert.False(LiveDocumentBackend.HasUnboundedLoopWithoutEscape("", isCSharp: false));
    }
}
