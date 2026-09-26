using System.Reflection;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;

namespace Platform.UnitTests.Architecture;

/// <summary>W-02: no module reaches into another module's internals; a module's only public type is its entry class.</summary>
public class ModuleBoundaryTests
{
    private static readonly Assembly[] Modules =
    [
        typeof(AuditModule).Assembly,
        typeof(TenancyModule).Assembly,
        typeof(IdentityModule).Assembly,
        typeof(WorkflowModule).Assembly,
    ];

    public static TheoryData<string> ModuleNames => new(Modules.Select(a => a.GetName().Name!));

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Module_does_not_reference_another_module_implementation(string moduleName)
    {
        var module = Modules.Single(a => a.GetName().Name == moduleName);
        var others = Modules.Where(a => a != module).Select(a => a.GetName().Name).ToHashSet();

        var offending = module.GetReferencedAssemblies().Where(r => others.Contains(r.Name)).Select(r => r.Name);

        offending.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ModuleNames))]
    public void Module_exposes_only_its_entry_class(string moduleName)
    {
        var module = Modules.Single(a => a.GetName().Name == moduleName);
        var entryClass = moduleName.Replace("Platform.Modules.", string.Empty, StringComparison.Ordinal) + "Module";

        var publicTypes = module.GetExportedTypes().Where(t => t.Name != entryClass).Select(t => t.FullName);

        publicTypes.ShouldBeEmpty();
    }
}
