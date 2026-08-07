using GPTino.AgentHost.Runtime;

namespace GPTino.AgentHost.Tests;

/// <summary>
/// The SDK-source detector rejects a C# source ONLY when it is an SDK-class component (a class
/// inheriting GH_ScriptInstance/GH_Component, or a RunScript method signature). Real Rhino 8
/// script-mode sources — plain top-level statements — always pass, even when they merely mention
/// RunScript in a comment or string.
/// </summary>
public sealed class SdkComponentSourceDetectorTests
{
    [Fact]
    public void FullScriptInstanceClassIsDetected()
    {
        const string src =
            "using System;\n" +
            "using Rhino.Geometry;\n" +
            "public class Script_Instance : GH_ScriptInstance\n" +
            "{\n" +
            "    private void RunScript(double x, ref object a)\n" +
            "    {\n" +
            "        a = x * 2;\n" +
            "    }\n" +
            "}";
        Assert.True(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void GhComponentSubclassIsDetected()
    {
        const string src =
            "public class MyComponent : GH_Component\n" +
            "{\n" +
            "    public MyComponent() : base(\"My\", \"M\", \"desc\", \"cat\", \"sub\") { }\n" +
            "}";
        Assert.True(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void QualifiedBaseTypeAcrossLinesIsDetected()
    {
        const string src =
            "class Script_Instance :\n" +
            "    Grasshopper.Kernel.GH_ScriptInstance\n" +
            "{\n}";
        Assert.True(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void PrivateRunScriptSignatureIsDetected()
    {
        const string src = "private void RunScript(Curve crv, int count, ref object result)\n{\n}";
        Assert.True(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void PublicOverrideRunScriptSignatureIsDetected()
    {
        const string src = "public override void RunScript(double x, ref object a)\n{\n}";
        Assert.True(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void PrivateStaticRunScriptSignatureIsDetected()
    {
        const string src = "private static void RunScript(double x, ref object a)\n{\n}";
        Assert.True(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void TopLevelScriptModeSourcePasses()
    {
        const string src =
            "// #! csharp\n" +
            "using Rhino.Geometry;\n" +
            "var pts = new List<Point3d>();\n" +
            "for (int i = 0; i < count; i++)\n" +
            "{\n" +
            "    pts.Add(new Point3d(i, 0, 0));\n" +
            "}\n" +
            "a = pts;";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void RunScriptMentionedInACommentPasses()
    {
        const string src =
            "// #! csharp\n" +
            "// converted from an old private void RunScript(...) body\n" +
            "a = x * 2;";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void RunScriptSignatureInsideAStringLiteralPasses()
    {
        // The literal carries the EXACT SDK signature shape — only string stripping saves it.
        const string src =
            "// #! csharp\n" +
            "var s = \"private void RunScript(\";\n" +
            "a = s.Length;";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void SdkWrapperInsideAVerbatimStringPasses()
    {
        const string src =
            "// #! csharp\n" +
            "var doc = @\"class Old : GH_Component\n" +
            "private void RunScript(double x)\";\n" +
            "a = doc.Length;";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void HelperClassWithoutSdkBasePasses()
    {
        const string src =
            "// #! csharp\n" +
            "a = new Helper().Build(x);\n" +
            "class Helper\n" +
            "{\n" +
            "    public double Build(double x) => x * 2;\n" +
            "}";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void CommentedOutSdkWrapperPasses()
    {
        const string src =
            "// #! csharp\n" +
            "// public class Script_Instance : GH_ScriptInstance { }\n" +
            "a = x;";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void BlockCommentedSdkWrapperPasses()
    {
        // Only line comments used to be stripped; a /* */ wrapper must not trip the detector.
        const string src =
            "// #! csharp\n" +
            "/*\n" +
            "public class Script_Instance : GH_ScriptInstance\n" +
            "{\n" +
            "    private void RunScript(double x, ref object a) { }\n" +
            "}\n" +
            "*/\n" +
            "a = x;";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void SdkTypeAsGenericArgumentPasses()
    {
        // Legal script-mode helper: GH_Component appears only as a generic ARGUMENT, never as
        // the base type — the class pattern must not reach into the angle brackets.
        const string src =
            "// #! csharp\n" +
            "a = comps;\n" +
            "class Sorter : System.Collections.Generic.IComparer<GH_Component>\n" +
            "{\n" +
            "    public int Compare(GH_Component left, GH_Component right) => 0;\n" +
            "}";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void LocalRunScriptFunctionPasses()
    {
        // A plain top-level local function named RunScript is legal script-mode C#: without an
        // access modifier it is not the SDK signature.
        const string src =
            "// #! csharp\n" +
            "void RunScript() { a = x * 2; }\n" +
            "RunScript();";
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(src));
    }

    [Fact]
    public void EmptySourcePasses()
    {
        Assert.False(LiveDocumentBackend.LooksLikeSdkComponentSource(""));
    }
}
