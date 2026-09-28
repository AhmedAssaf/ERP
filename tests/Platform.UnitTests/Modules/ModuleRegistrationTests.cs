using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy;
using Platform.Modules.Vendors;
using Platform.Modules.Workflow;

namespace Platform.UnitTests.Modules;

public class ModuleRegistrationTests
{
    private const string AnyConnectionString = "Host=localhost;Database=platform;Username=erp_app";

    [Fact]
    public void Audit_module_registers_into_the_collection() =>
        new ServiceCollection().AddAuditModule(AnyConnectionString).ShouldNotBeEmpty();

    [Fact]
    public void Tenancy_module_registers_into_the_collection() =>
        new ServiceCollection().AddTenancyModule(AnyConnectionString).ShouldNotBeEmpty();

    [Fact]
    public void Identity_module_registers_into_the_collection() =>
        new ServiceCollection().AddIdentityModule(AnyConnectionString).ShouldNotBeEmpty();

    [Fact]
    public void Workflow_module_registers_into_the_collection() =>
        new ServiceCollection().AddWorkflowModule(AnyConnectionString).ShouldNotBeEmpty();

    [Fact]
    public void Vendors_module_registers_into_the_collection() =>
        new ServiceCollection().AddVendorsModule(AnyConnectionString).ShouldNotBeEmpty();
}
