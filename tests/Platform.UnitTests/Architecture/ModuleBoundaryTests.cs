using System.Reflection;

namespace Platform.UnitTests.Architecture;

/// <summary>W-02: no module reaches into another module's internals; a module's only public type is its entry class.</summary>
public class ModuleBoundaryTests
{
    private const string ModuleAssemblyPrefix = "Platform.Modules.";
    private const string ContractsSuffix = ".Contracts";

    private static readonly Assembly[] AllModuleAssemblies = DiscoverModuleAssemblies();

    private static readonly Assembly[] Implementations =
        [.. AllModuleAssemblies.Where(a => !a.GetName().Name!.EndsWith(ContractsSuffix, StringComparison.Ordinal))];

    private static readonly Assembly[] Contracts =
        [.. AllModuleAssemblies.Where(a => a.GetName().Name!.EndsWith(ContractsSuffix, StringComparison.Ordinal))];

    public static TheoryData<string> ImplementationNames => new(Implementations.Select(a => a.GetName().Name!));

    public static TheoryData<string> ContractsNames => new(Contracts.Select(a => a.GetName().Name!));

    [Fact]
    public void At_least_four_module_implementations_are_discovered() =>
        Implementations.Length.ShouldBeGreaterThanOrEqualTo(4);

    [Theory]
    [MemberData(nameof(ImplementationNames))]
    public void Module_does_not_reference_another_module_implementation(string moduleName)
    {
        var module = Implementations.Single(a => a.GetName().Name == moduleName);
        var others = Implementations.Where(a => a != module).Select(a => a.GetName().Name).ToHashSet();

        var offending = module.GetReferencedAssemblies().Where(r => others.Contains(r.Name)).Select(r => r.Name);

        offending.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ImplementationNames))]
    public void Module_exposes_only_its_entry_class(string moduleName)
    {
        var module = Implementations.Single(a => a.GetName().Name == moduleName);
        var entryClass = moduleName.Replace(ModuleAssemblyPrefix, string.Empty, StringComparison.Ordinal) + "Module";

        var publicTypes = module.GetExportedTypes().Where(t => t.Name != entryClass).Select(t => t.FullName);

        publicTypes.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(ContractsNames))]
    public void Contracts_assembly_does_not_reference_a_module_implementation(string contractsName)
    {
        var contracts = Contracts.Single(a => a.GetName().Name == contractsName);
        var implementationNames = Implementations.Select(a => a.GetName().Name).ToHashSet();

        var offending = contracts.GetReferencedAssemblies().Where(r => implementationNames.Contains(r.Name)).Select(r => r.Name);

        offending.ShouldBeEmpty();
    }

    private static Assembly[] DiscoverModuleAssemblies() =>
        [.. Directory.EnumerateFiles(AppContext.BaseDirectory, $"{ModuleAssemblyPrefix}*.dll").Select(Assembly.LoadFrom)];
}
