using System.Reflection;

namespace GPTino.Rhino;

/// <summary>
/// Reflection access to Rhino's advanced option "HideFloatingWindowsOnDeactivate" (Options &gt;
/// Advanced). Rhino hides all floating panel containers while another application is active by
/// default; setting the option to false keeps a floated GPTino panel visible when the user works
/// in other apps. RhinoCommon exposes no public accessor — only the public
/// <c>Rhino.Runtime.AdvancedSetting</c> enum plus internal
/// <c>UnsafeNativeMethods.CRhAdvancedSettings_GetBool/SetBool</c> — so resolution is by
/// reflection, split from invocation so a headless test can pin the member shapes without
/// touching native Rhino.
/// </summary>
internal static class RhinoAdvancedSettings
{
    // internal (not private): the canary test pins these exact member names against the pinned
    // RhinoCommon reference assembly, so resolution and test share a single source of truth.
    internal const string EnumTypeName = "Rhino.Runtime.AdvancedSetting";
    internal const string EnumValueName = "HideFloatingWindowsOnDeactivate";
    internal const string AccessorTypeName = "UnsafeNativeMethods";
    internal const string GetMethodName = "CRhAdvancedSettings_GetBool";
    internal const string SetMethodName = "CRhAdvancedSettings_SetBool";

    internal sealed record Resolved(MethodInfo Get, MethodInfo Set, object HideFloatingSetting);

    /// <summary>
    /// Locates the enum value and both internal accessors in the loaded RhinoCommon. Pure managed
    /// reflection — never calls into native Rhino — so it is safe anywhere, including tests.
    /// Returns null when any member is missing (a future Rhino renamed the internals).
    /// </summary>
    internal static Resolved? TryResolve()
    {
        try
        {
            // Resolve RhinoCommon by assembly NAME rather than typeof(Rhino.RhinoApp): inside the
            // plugin the host has it loaded already, and the test project compiles this file
            // against a fake RhinoApp (FakeRhinoApp.cs) whose assembly would be the wrong target.
            var rhinoCommon =
                AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, "RhinoCommon", StringComparison.OrdinalIgnoreCase))
                ?? Assembly.Load("RhinoCommon");
            var enumType = rhinoCommon.GetType(EnumTypeName);
            if (enumType is null || !enumType.IsEnum || !Enum.TryParse(enumType, EnumValueName, out var setting))
            {
                return null;
            }
            var accessor = rhinoCommon.GetType(AccessorTypeName);
            var get = accessor?.GetMethod(
                GetMethodName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                new[] { enumType },
                modifiers: null);
            var set = accessor?.GetMethod(
                SetMethodName,
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public,
                binder: null,
                new[] { enumType, typeof(bool) },
                modifiers: null);
            if (get is null || set is null || get.ReturnType != typeof(bool) || setting is null)
            {
                return null;
            }
            return new Resolved(get, set, setting);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Reads the option's live value; null when the accessors cannot be resolved or fail.</summary>
    internal static bool? TryGetHideFloatingWindowsOnDeactivate()
    {
        if (TryResolve() is not { } resolved)
        {
            return null;
        }
        try
        {
            return (bool?)resolved.Get.Invoke(null, new[] { resolved.HideFloatingSetting });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the option and confirms by reading it back. False means the caller should fall back
    /// to telling the user the manual path (Options &gt; Advanced).
    /// </summary>
    internal static bool TrySetHideFloatingWindowsOnDeactivate(bool value)
    {
        if (TryResolve() is not { } resolved)
        {
            return false;
        }
        try
        {
            resolved.Set.Invoke(null, new object[] { resolved.HideFloatingSetting, value });
            return (bool?)resolved.Get.Invoke(null, new[] { resolved.HideFloatingSetting }) == value;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
