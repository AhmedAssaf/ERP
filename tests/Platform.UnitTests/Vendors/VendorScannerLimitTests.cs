using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Platform.Modules.Vendors;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared;

namespace Platform.UnitTests.Vendors;

/// <summary>
/// V-10: a vendor document of the largest allowed size must fit into one scan, so a host that wires vendor documents
/// refuses to start when <c>ClamAv:MaxStreamBytes</c> is below <see cref="VendorDocumentLimits.MaxBytes"/>.
/// </summary>
public sealed class VendorScannerLimitTests
{
    [Fact]
    public void A_scanner_limit_below_the_largest_document_fails_start_validation()
    {
        using var services = Services(VendorDocumentLimits.MaxBytes - 1);

        var refused = Should.Throw<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        refused.Message.ShouldContain("ClamAv:MaxStreamBytes");
    }

    [Theory]
    [InlineData(VendorDocumentLimits.MaxBytes)]
    [InlineData(null)]
    public void A_scanner_limit_that_holds_the_largest_document_passes(int? maxStreamBytes)
    {
        using var services = Services(maxStreamBytes);

        Should.NotThrow(() => services.GetRequiredService<IStartupValidator>().Validate());
    }

    private static ServiceProvider Services(int? maxStreamBytes)
    {
        var settings = new Dictionary<string, string?> { ["ClamAv:Host"] = "127.0.0.1" };
        if (maxStreamBytes is { } max)
        {
            settings["ClamAv:MaxStreamBytes"] = max.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformShared();
        services.AddVirusScanner(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddVendorJobs();
        return services.BuildServiceProvider();
    }
}
