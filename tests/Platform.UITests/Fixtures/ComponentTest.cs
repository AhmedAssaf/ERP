using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Platform.UI;

namespace Platform.UITests.Fixtures;

/// <summary>
/// A bUnit context with the real shared resources (a missing key renders as the key, so text assertions also prove
/// the resource exists), loose JavaScript interop, and English as the default culture.
/// </summary>
public abstract class ComponentTest : BunitContext
{
    static ComponentTest()
    {
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
    }

    protected ComponentTest()
    {
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        Services.AddPlatformUI();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>Switches this test's culture (it flows with the test's own async context only).</summary>
    protected static void UseCulture(string name)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
    }
}
