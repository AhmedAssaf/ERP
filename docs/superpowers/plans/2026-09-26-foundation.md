# Foundation Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the WaslaBid foundation: solution skeleton, tenant isolation with PostgreSQL row-level security, Keycloak login mapped to tenants, the Tailwind build, Arabic-first localisation, and the configurable approval workflow (W-02, W-03, W-04, W-05, W-07, F-56).

**Architecture:** A .NET 10 modular monolith. Each module is two projects (`.Contracts` public, implementation internal) with its own PostgreSQL schema and SQL migrations. A host-name middleware resolves the tenant, a connection interceptor sets `app.tenant_id`, and forced RLS policies filter every tenant-owned table. The workflow executor is our own state machine over a per-tender snapshot (ADR-0004).

**Tech Stack:** .NET 10 (SDK 10.0.401), ASP.NET Core Blazor Web App (Interactive Server), EF Core 10 + Npgsql, PostgreSQL 16 (pgvector image), Keycloak 26.3 Organizations, Tailwind CSS v4.3.3 standalone CLI, xUnit v3 + Shouldly + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-09-26-foundation-design.md` (section 7 lists corrections the plan applies).

**Conventions for every task**

- Work on branch `foundation`. Commit after each task with the message given. No AI attribution in commit messages (CLAUDE.md).
- Every test method takes its cancellation token from `TestContext.Current.CancellationToken` (xUnit v3 analyzer xUnit1051 is an error under `TreatWarningsAsErrors`).
- If the analyzer flags a rule this plan does not mention, fix the code. Add a suppression only in `.editorconfig` or with `#pragma` plus a one-line reason.
- UI markup uses logical utilities only (`ms-`, `pe-`, `text-start`); Task 10 adds the lint that enforces it.
- Docker Desktop must be running for integration tests (Testcontainers).

## File map

```
global.json, Directory.Build.props, Directory.Packages.props, .editorconfig, WaslaBid.slnx   Task 1
spikes/Directory.Build.props, spikes/Directory.Packages.props                              Task 1
src/Platform.Shared/                          Results, Tenancy, Data (SqlMigrator, interceptor, DbContext registration), SharedModule
src/Modules/<M>/Platform.Modules.<M>.Contracts/   public interfaces and records
src/Modules/<M>/Platform.Modules.<M>/             <M>Module entry class, internal implementation, Migrations/*.sql
    M = Audit (Task 6), Tenancy (Task 7), Identity (Task 9), Workflow (Tasks 12 to 14)
src/Platform.Migrator/                        MigrationRunner, DevSeed, Program
src/Platform.Web/                             Program, Components/, Tenancy/, Localization/
src/UI/Platform.UI/                           Styles/app.css, wwwroot/fonts, Resources/*.resx, PlatformLocalization, TenantTheme
tests/Platform.UnitTests/                     Shared, Architecture, Identity, Styling, Localization, Workflow
tests/Platform.IntegrationTests/              Infrastructure/ fixtures, Data, Tenancy, Web, Identity, Workflow
infra/compose/keycloak/import/waslabid-realm.json, docker-compose.yml, .env.example, caddy/Caddyfile
```

---

### Task 1: Repository build scaffolding

**Files:**
- Create: `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `spikes/Directory.Build.props`, `spikes/Directory.Packages.props`, `WaslaBid.slnx`
- Modify: `.gitignore`

- [ ] **Step 1: Create the branch and confirm the SDK**

```bash
git checkout -b foundation
dotnet --list-sdks
```
Expected: a line starting `10.0.401`.

- [ ] **Step 2: Write `global.json`**

```json
{
  "sdk": {
    "version": "10.0.401",
    "rollForward": "latestFeature"
  },
  "test": {
    "runner": "Microsoft.Testing.Platform"
  }
}
```

- [ ] **Step 3: Write `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AnalysisLevel>latest-recommended</AnalysisLevel>
    <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

- [ ] **Step 4: Write `Directory.Packages.props`**

```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
  </PropertyGroup>
  <ItemGroup>
    <PackageVersion Include="EFCore.NamingConventions" Version="10.0.1" />
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" Version="10.0.12" />
    <PackageVersion Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore" Version="10.0.12" />
    <PackageVersion Include="Microsoft.EntityFrameworkCore.Relational" Version="10.0.12" />
    <PackageVersion Include="Microsoft.NET.Test.Sdk" Version="18.10.1" />
    <PackageVersion Include="Npgsql" Version="10.0.3" />
    <PackageVersion Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.3" />
    <PackageVersion Include="Shouldly" Version="4.3.0" />
    <PackageVersion Include="Testcontainers.Keycloak" Version="4.15.0" />
    <PackageVersion Include="Testcontainers.PostgreSql" Version="4.15.0" />
    <PackageVersion Include="xunit.runner.visualstudio" Version="4.0.0" />
    <PackageVersion Include="xunit.v3" Version="4.0.1" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5: Write `.editorconfig`**

```ini
root = true

[*]
charset = utf-8
insert_final_newline = true
indent_style = space

[*.{cs,razor}]
indent_size = 4

[*.{csproj,props,targets,json,slnx,xml,resx}]
indent_size = 2

[*.cs]
csharp_style_namespace_declarations = file_scoped:warning
# ASP.NET Core has no synchronization context; ConfigureAwait adds noise without benefit.
dotnet_diagnostic.CA2007.severity = none
# LoggerMessage source generation is adopted when logging grows; the foundation logs little.
dotnet_diagnostic.CA1848.severity = none
# Result<T>.Success and Failure are deliberate static factories on a generic type.
dotnet_diagnostic.CA1000.severity = none
# Internal types are created by dependency injection, EF Core or Blazor, which the analyzer cannot see.
dotnet_diagnostic.CA1812.severity = none
# Components, minimal-API types and Program are discovered by the framework.
dotnet_diagnostic.CA1515.severity = none
# C#-only code base; keyword clashes with other .NET languages (for example "Shared") do not apply.
dotnet_diagnostic.CA1716.severity = none

[tests/**.cs]
# Test names read as sentences with underscores.
dotnet_diagnostic.CA1707.severity = none
# xUnit collection definitions end in "Collection" by convention.
dotnet_diagnostic.CA1711.severity = none
```

- [ ] **Step 6: Opt the spikes out of the product build rules**

`spikes/Directory.Build.props`:
```xml
<Project>
  <!-- Spikes are throwaway (docs/07); they must not inherit the product's build rules. -->
</Project>
```

`spikes/Directory.Packages.props`:
```xml
<Project>
  <PropertyGroup>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

- [ ] **Step 7: Extend `.gitignore`**

Append:
```
# Build tools downloaded by MSBuild (Tailwind standalone CLI)
.tools/
# Generated by the Tailwind build in Platform.UI
src/UI/Platform.UI/wwwroot/css/app.css
TestResults/
```

- [ ] **Step 8: Create the solution**

```bash
dotnet new sln -n WaslaBid
ls WaslaBid.slnx
```
Expected: `WaslaBid.slnx` exists (the .NET 10 SDK creates the XML solution format by default).

- [ ] **Step 9: Prove the spikes still build**

```bash
dotnet build spikes/ElsaWorkflowSpike/ElsaWorkflowSpike.csproj
```
Expected: `Build succeeded.`

- [ ] **Step 10: Commit**

```bash
git add global.json Directory.Build.props Directory.Packages.props .editorconfig spikes/Directory.Build.props spikes/Directory.Packages.props WaslaBid.slnx .gitignore
git commit -m "Foundation: SDK pin, build rules, central packages, solution"
```

---

### Task 2: Platform.Shared with Result and tenant context

**Files:**
- Create: `src/Platform.Shared/Platform.Shared.csproj`, `src/Platform.Shared/Results/Error.cs`, `src/Platform.Shared/Results/Result.cs`, `src/Platform.Shared/Tenancy/TenantContext.cs`
- Create: `tests/Platform.UnitTests/Platform.UnitTests.csproj`, `tests/Platform.UnitTests/Shared/ResultTests.cs`

- [ ] **Step 1: Create the projects**

```bash
dotnet new classlib -n Platform.Shared -o src/Platform.Shared
rm src/Platform.Shared/Class1.cs
mkdir -p tests/Platform.UnitTests/Shared
```

Replace `src/Platform.Shared/Platform.Shared.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <PackageReference Include="EFCore.NamingConventions" />
    <PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Platform.UnitTests" />
    <InternalsVisibleTo Include="Platform.IntegrationTests" />
  </ItemGroup>
</Project>
```

Create `tests/Platform.UnitTests/Platform.UnitTests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Shouldly" />
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Platform.Shared\Platform.Shared.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln WaslaBid.slnx add src/Platform.Shared/Platform.Shared.csproj tests/Platform.UnitTests/Platform.UnitTests.csproj
```

- [ ] **Step 2: Write the failing test** — `tests/Platform.UnitTests/Shared/ResultTests.cs`

```csharp
using Platform.Shared.Results;

namespace Platform.UnitTests.Shared;

public class ResultTests
{
    [Fact]
    public void Success_carries_the_value_and_no_error()
    {
        var result = Result.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void Failure_carries_the_error_and_reading_the_value_throws()
    {
        var result = Result.Failure<int>(Error.NotFound("tender.not_found", "The tender was not found."));

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
        result.Error.Code.ShouldBe("tender.not_found");
        Should.Throw<InvalidOperationException>(() => result.Value);
    }
}
```

- [ ] **Step 3: Run it to verify it fails**

Run: `dotnet test tests/Platform.UnitTests`
Expected: build error `The type or namespace name 'Results' does not exist in the namespace 'Platform.Shared'`.

- [ ] **Step 4: Implement** — `src/Platform.Shared/Results/Error.cs`

```csharp
namespace Platform.Shared.Results;

public enum ErrorKind
{
    Validation,
    NotFound,
    Refused,
    Conflict,
    InvariantViolated,
}

/// <summary>An expected failure. Code is stable and machine-readable; Message is a full sentence for people.</summary>
public sealed record Error(ErrorKind Kind, string Code, string Message)
{
    public static Error Validation(string code, string message) => new(ErrorKind.Validation, code, message);

    public static Error NotFound(string code, string message) => new(ErrorKind.NotFound, code, message);

    public static Error Refused(string code, string message) => new(ErrorKind.Refused, code, message);

    public static Error Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);

    public static Error Invariant(string code, string message) => new(ErrorKind.InvariantViolated, code, message);
}
```

`src/Platform.Shared/Results/Result.cs`:
```csharp
using System.Diagnostics.CodeAnalysis;

namespace Platform.Shared.Results;

/// <summary>The outcome of an operation that can fail in an expected way. Exceptions are for defects only.</summary>
public sealed class Result<T>
{
    private readonly T? _value;

    private Result(T? value, Error? error)
    {
        _value = value;
        Error = error;
    }

    public Error? Error { get; }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public T Value => IsSuccess ? _value! : throw new InvalidOperationException($"The result is a failure: {Error.Code}.");

    public static Result<T> Success(T value) => new(value, null);

    public static Result<T> Failure(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(default, error);
    }
}

public static class Result
{
    public static Result<T> Success<T>(T value) => Result<T>.Success(value);

    public static Result<T> Failure<T>(Error error) => Result<T>.Failure(error);
}
```

`src/Platform.Shared/Tenancy/TenantContext.cs`:
```csharp
namespace Platform.Shared.Tenancy;

public sealed record TenantBranding(string PortalName, string PrimaryColor, string? LogoUrl);

/// <summary>The tenant a request or circuit belongs to, resolved from the host name (spec section 2.1).</summary>
public sealed record TenantContext(
    Guid TenantId,
    string Slug,
    string KeycloakOrgAlias,
    string DefaultCulture,
    TenantBranding Branding);

public interface ITenantAccessor
{
    TenantContext? Current { get; }
}

/// <summary>Scoped holder set once per request or circuit. Only infrastructure code sets it.</summary>
public sealed class TenantAccessor : ITenantAccessor
{
    public TenantContext? Current { get; set; }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Platform.UnitTests`
Expected: `total: 2` and `failed: 0`.

- [ ] **Step 6: Commit**

```bash
git add src/Platform.Shared tests/Platform.UnitTests WaslaBid.slnx
git commit -m "Platform.Shared: Result, Error, tenant context"
```

---

### Task 3: Module projects and the architecture test (W-02)

**Files:**
- Create for each M in Audit, Tenancy, Identity, Workflow: `src/Modules/M/Platform.Modules.M.Contracts/Platform.Modules.M.Contracts.csproj`, `src/Modules/M/Platform.Modules.M/Platform.Modules.M.csproj`, `src/Modules/M/Platform.Modules.M/MModule.cs`
- Create: `tests/Platform.UnitTests/Architecture/ModuleBoundaryTests.cs`, `tests/Platform.UnitTests/Modules/ModuleRegistrationTests.cs`
- Modify: `tests/Platform.UnitTests/Platform.UnitTests.csproj`

- [ ] **Step 1: Create the eight projects**

```bash
for M in Audit Tenancy Identity Workflow; do
  dotnet new classlib -n Platform.Modules.$M.Contracts -o src/Modules/$M/Platform.Modules.$M.Contracts
  dotnet new classlib -n Platform.Modules.$M -o src/Modules/$M/Platform.Modules.$M
  rm src/Modules/$M/Platform.Modules.$M.Contracts/Class1.cs src/Modules/$M/Platform.Modules.$M/Class1.cs
  dotnet sln WaslaBid.slnx add src/Modules/$M/Platform.Modules.$M.Contracts/Platform.Modules.$M.Contracts.csproj src/Modules/$M/Platform.Modules.$M/Platform.Modules.$M.csproj
done
```

Replace each `Platform.Modules.M.Contracts.csproj` (substitute M):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\..\Platform.Shared\Platform.Shared.csproj" />
  </ItemGroup>
</Project>
```

Replace each `Platform.Modules.M.csproj` (substitute M). Tenancy and Identity also get `<FrameworkReference Include="Microsoft.AspNetCore.App" />` inside the first `ItemGroup`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\..\Platform.Shared\Platform.Shared.csproj" />
    <ProjectReference Include="..\Platform.Modules.M.Contracts\Platform.Modules.M.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="Migrations\*.sql" LogicalName="Migrations.%(Filename)%(Extension)" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Platform.UnitTests" />
    <InternalsVisibleTo Include="Platform.IntegrationTests" />
  </ItemGroup>
</Project>
```

Add to `tests/Platform.UnitTests/Platform.UnitTests.csproj` inside the `ProjectReference` group:
```xml
    <ProjectReference Include="..\..\src\Modules\Audit\Platform.Modules.Audit\Platform.Modules.Audit.csproj" />
    <ProjectReference Include="..\..\src\Modules\Tenancy\Platform.Modules.Tenancy\Platform.Modules.Tenancy.csproj" />
    <ProjectReference Include="..\..\src\Modules\Identity\Platform.Modules.Identity\Platform.Modules.Identity.csproj" />
    <ProjectReference Include="..\..\src\Modules\Workflow\Platform.Modules.Workflow\Platform.Modules.Workflow.csproj" />
```

- [ ] **Step 2: Write the failing tests**

`tests/Platform.UnitTests/Architecture/ModuleBoundaryTests.cs`:
```csharp
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
```

`tests/Platform.UnitTests/Modules/ModuleRegistrationTests.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy;
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
        new ServiceCollection().AddIdentityModule().ShouldNotBeEmpty();

    [Fact]
    public void Workflow_module_registers_into_the_collection() =>
        new ServiceCollection().AddWorkflowModule(AnyConnectionString).ShouldNotBeEmpty();
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test tests/Platform.UnitTests`
Expected: build errors, `The type or namespace name 'AuditModule' could not be found`.

- [ ] **Step 4: Write the four entry classes**

Each registers a marker so the collection is not empty until real services arrive in later tasks. Create `src/Modules/Audit/Platform.Modules.Audit/AuditModule.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Audit;

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton<AuditModuleMarker>();
        return services;
    }
}

internal sealed class AuditModuleMarker;
```

`src/Modules/Tenancy/Platform.Modules.Tenancy/TenancyModule.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Tenancy;

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton<TenancyModuleMarker>();
        return services;
    }
}

internal sealed class TenancyModuleMarker;
```

`src/Modules/Identity/Platform.Modules.Identity/IdentityModule.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IdentityModuleMarker>();
        return services;
    }
}

internal sealed class IdentityModuleMarker;
```

`src/Modules/Workflow/Platform.Modules.Workflow/WorkflowModule.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Workflow;

public static class WorkflowModule
{
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddSingleton<WorkflowModuleMarker>();
        return services;
    }
}

internal sealed class WorkflowModuleMarker;
```

Create an empty `Migrations` folder in each implementation project so the embedded-resource glob has a home: add `src/Modules/M/Platform.Modules.M/Migrations/.gitkeep` for all four.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test tests/Platform.UnitTests`
Expected: `Build succeeded.` then `total: 14` and `failed: 0` (2 result + 8 architecture + 4 registration).

- [ ] **Step 6: Commit**

```bash
git add src/Modules tests/Platform.UnitTests WaslaBid.slnx
git commit -m "Modules: Audit, Tenancy, Identity, Workflow projects with boundary tests"
```

---

### Task 4: Web host, integration test project, and W-02 close-out

**Files:**
- Create: `src/Platform.Web/Platform.Web.csproj`, `src/Platform.Web/Program.cs`, `src/Platform.Web/appsettings.json`, `src/Platform.Web/appsettings.Development.json`, `src/Platform.Web/Properties/launchSettings.json`, `src/Platform.Web/Components/App.razor`, `src/Platform.Web/Components/Routes.razor`, `src/Platform.Web/Components/_Imports.razor`, `src/Platform.Web/Components/Layout/MainLayout.razor`, `src/Platform.Web/Components/Pages/Home.razor`
- Create: `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj`, `tests/Platform.IntegrationTests/Web/HealthEndpointTests.cs`
- Modify: `docs/02-core-features-and-tech-stack.md` section 4.5, `docs/09-backlog.md` row W-02

- [ ] **Step 1: Create the host project**

```bash
dotnet new web -n Platform.Web -o src/Platform.Web
rm -f src/Platform.Web/appsettings.Development.json
mkdir -p src/Platform.Web/Components/Layout src/Platform.Web/Components/Pages
dotnet sln WaslaBid.slnx add src/Platform.Web/Platform.Web.csproj
```

Replace `src/Platform.Web/Platform.Web.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <UserSecretsId>waslabid-web</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Platform.Shared\Platform.Shared.csproj" />
    <ProjectReference Include="..\Modules\Audit\Platform.Modules.Audit\Platform.Modules.Audit.csproj" />
    <ProjectReference Include="..\Modules\Tenancy\Platform.Modules.Tenancy\Platform.Modules.Tenancy.csproj" />
    <ProjectReference Include="..\Modules\Identity\Platform.Modules.Identity\Platform.Modules.Identity.csproj" />
    <ProjectReference Include="..\Modules\Workflow\Platform.Modules.Workflow\Platform.Modules.Workflow.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Platform.IntegrationTests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Write the failing test**

Create `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" />
    <PackageReference Include="Shouldly" />
    <PackageReference Include="Testcontainers.Keycloak" />
    <PackageReference Include="Testcontainers.PostgreSql" />
    <PackageReference Include="xunit.runner.visualstudio" />
    <PackageReference Include="xunit.v3" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Shouldly" />
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\Platform.Web\Platform.Web.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln WaslaBid.slnx add tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj
mkdir -p tests/Platform.IntegrationTests/Web
```

`tests/Platform.IntegrationTests/Web/HealthEndpointTests.cs`:
```csharp
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Platform.IntegrationTests.Web;

public class HealthEndpointTests
{
    [Fact]
    public async Task Health_endpoint_answers_without_a_database_or_tenant()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b => b.UseSetting("ConnectionStrings:Platform", "Host=unused;Database=unused"));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
```

Run: `dotnet test tests/Platform.IntegrationTests`
Expected: build error, `Program` is inaccessible or the `/health` endpoint returns 404.

- [ ] **Step 3: Write the host**

`src/Platform.Web/Program.cs`:
```csharp
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;
using Platform.Shared.Tenancy;
using Platform.Web.Components;

var builder = WebApplication.CreateBuilder(args);
var platformDb = builder.Configuration.GetConnectionString("Platform");
if (string.IsNullOrWhiteSpace(platformDb))
{
    throw new InvalidOperationException("Connection string 'Platform' is not configured. Set it with dotnet user-secrets (see README).");
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<TenantAccessor>();
builder.Services.AddScoped<ITenantAccessor>(sp => sp.GetRequiredService<TenantAccessor>());
builder.Services.AddAuditModule(platformDb);
builder.Services.AddTenancyModule(platformDb);
builder.Services.AddIdentityModule();
builder.Services.AddWorkflowModule(platformDb);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
```

`src/Platform.Web/appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

`src/Platform.Web/appsettings.Development.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Information"
    }
  }
}
```

`src/Platform.Web/Properties/launchSettings.json`:
```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "Platform.Web": {
      "commandName": "Project",
      "launchBrowser": false,
      "applicationUrl": "http://localhost:5273",
      "environmentVariables": {
        "ASPNETCORE_ENVIRONMENT": "Development"
      }
    }
  }
}
```

`src/Platform.Web/Components/_Imports.razor`:
```razor
@using System.Net.Http
@using Microsoft.AspNetCore.Components.Authorization
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using static Microsoft.AspNetCore.Components.Web.RenderMode
@using Platform.Shared.Tenancy
@using Platform.Web
@using Platform.Web.Components
```

`src/Platform.Web/Components/App.razor`:
```razor
<!DOCTYPE html>
<html lang="ar" dir="rtl">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <HeadOutlet />
</head>
<body>
    <Routes />
    <script src="@Assets["_framework/blazor.web.js"]"></script>
</body>
</html>
```

`src/Platform.Web/Components/Routes.razor`:
```razor
<Router AppAssembly="typeof(Program).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)" />
    </Found>
</Router>
```

`src/Platform.Web/Components/Layout/MainLayout.razor`:
```razor
@inherits LayoutComponentBase

@Body
```

`src/Platform.Web/Components/Pages/Home.razor`:
```razor
@page "/"

<PageTitle>Home</PageTitle>
<h1>Home</h1>
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: `Build succeeded.`; unit tests 14 passed; integration tests 1 passed.

- [ ] **Step 5: Update docs/02 section 4.5**

In `docs/02-core-features-and-tech-stack.md`, in the section 4.5 code block, replace the line starting `      Evaluation/` with these two lines:
```
      Evaluation/                    compliance, scoring, comparison; calls Workflow for the approval chain
      Workflow/                      tenant workflow definitions, per-tender snapshots, executor (ADR-0004)
```
and replace the heading line `### 4.5 Repository layout (proposed)` with `### 4.5 Repository layout`, and add this paragraph directly after the code block:
```
Each module is two projects: `Platform.Modules.<Name>.Contracts` (public interfaces and records) and `Platform.Modules.<Name>` (everything else, internal, plus a `<Name>Module` entry class). Modules reference each other's `.Contracts` only, own one PostgreSQL schema each, and ship their schema as SQL scripts under `Migrations/` (design spec 2026-09-26). Modules are created by the first slice that needs them; the foundation created Audit, Tenancy, Identity and Workflow.
```

- [ ] **Step 6: Move W-02 to Done**

In `docs/09-backlog.md`, row W-02, replace `| P0 | M | Backlog | W-01 |` with `| P0 | M | Done | W-01 |`.

- [ ] **Step 7: Commit**

```bash
git add src/Platform.Web tests/Platform.IntegrationTests WaslaBid.slnx docs/02-core-features-and-tech-stack.md docs/09-backlog.md
git commit -m "Web host skeleton and integration test project; W-02 done"
```

---

### Task 5: SQL migration runner, platform script, and database fixture

**Files:**
- Create: `src/Platform.Shared/Data/SqlMigrator.cs`, `src/Platform.Shared/Data/Migrations/0001_platform.sql`, `src/Platform.Shared/SharedModule.cs`
- Create: `src/Platform.Migrator/Platform.Migrator.csproj`, `src/Platform.Migrator/MigrationRunner.cs`, `src/Platform.Migrator/Program.cs`
- Create: `tests/Platform.IntegrationTests/Infrastructure/RepoPaths.cs`, `tests/Platform.IntegrationTests/Infrastructure/DatabaseFixture.cs`, `tests/Platform.IntegrationTests/Data/MigrationTests.cs`
- Modify: `src/Platform.Shared/Platform.Shared.csproj`, `src/Platform.Web/Program.cs`, `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj`

- [ ] **Step 1: Write the failing test**

`tests/Platform.IntegrationTests/Infrastructure/RepoPaths.cs`:
```csharp
namespace Platform.IntegrationTests.Infrastructure;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string PostgresInitScript => Path.Combine(Root, "infra", "compose", "postgres", "init", "01-databases.sql");

    public static string KeycloakRealm => Path.Combine(Root, "infra", "compose", "keycloak", "import", "waslabid-realm.json");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WaslaBid.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("WaslaBid.slnx was not found above the test output folder.");
    }
}
```

`tests/Platform.IntegrationTests/Infrastructure/DatabaseFixture.cs`:
```csharp
using Npgsql;
using Platform.Migrator;
using Testcontainers.PostgreSql;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>One PostgreSQL container per test run, initialised exactly like infra/compose, with all migrations applied.</summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    // Development password of the app role, created by infra/compose/postgres/init/01-databases.sql.
    private const string AppRolePassword = "erp_app_dev_password";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("platform")
        .WithUsername("erp")
        .WithPassword("erp_test_owner")
        .WithResourceMapping(new FileInfo(RepoPaths.PostgresInitScript), "/docker-entrypoint-initdb.d/")
        .Build();

    public string OwnerConnectionString => _container.GetConnectionString();

    public string AppConnectionString =>
        new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = "erp_app", Password = AppRolePassword }.ConnectionString;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        await WaitForAppRoleAsync();
        await MigrationRunner.RunAsync(OwnerConnectionString);
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    // The entrypoint runs init scripts on a temporary server before the final start; wait until the app role can log in.
    private async Task WaitForAppRoleAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(AppConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "database";
}
```

`tests/Platform.IntegrationTests/Data/MigrationTests.cs`:
```csharp
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;

namespace Platform.IntegrationTests.Data;

[Collection(DatabaseCollection.Name)]
public class MigrationTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Migrations_are_journaled_and_a_second_run_applies_nothing()
    {
        var secondRun = await MigrationRunner.RunAsync(db.OwnerConnectionString, Ct);

        secondRun.ShouldBeEmpty();
        await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from platform.schema_migrations where module = 'platform'", connection);
        var count = (long)(await command.ExecuteScalarAsync(Ct))!;
        count.ShouldBe(1);
    }

    [Fact]
    public async Task Current_tenant_is_null_when_nothing_is_set()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select platform.current_tenant()", connection);

        var result = await command.ExecuteScalarAsync(Ct);

        result.ShouldBe(DBNull.Value);
    }
}
```

Add the migrator reference to `tests/Platform.IntegrationTests/Platform.IntegrationTests.csproj`:
```xml
    <ProjectReference Include="..\..\src\Platform.Migrator\Platform.Migrator.csproj" />
```

Run: `dotnet test tests/Platform.IntegrationTests`
Expected: build error, project `Platform.Migrator` not found.

- [ ] **Step 2: Add the runner to Platform.Shared**

Add to `src/Platform.Shared/Platform.Shared.csproj`:
```xml
  <ItemGroup>
    <EmbeddedResource Include="Data\Migrations\*.sql" LogicalName="Migrations.%(Filename)%(Extension)" />
  </ItemGroup>
```

`src/Platform.Shared/Data/SqlMigrator.cs`:
```csharp
using System.Reflection;
using Npgsql;

namespace Platform.Shared.Data;

/// <summary>
/// Applies a module's embedded SQL scripts (resources named "Migrations.*.sql") in ordinal name order, each once,
/// each in its own transaction, recorded in platform.schema_migrations. An advisory lock serialises concurrent runs.
/// </summary>
public static class SqlMigrator
{
    private const long AdvisoryLockKey = 0x5741534C4249; // "WASLBI"
    private const string ResourcePrefix = "Migrations.";

    public static async Task<IReadOnlyList<string>> ApplyAsync(
        NpgsqlConnection connection, string module, Assembly assembly, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(assembly);

        var resources = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        await ExecuteAsync(connection, null, $"select pg_advisory_lock({AdvisoryLockKey})", cancellationToken);
        try
        {
            await ExecuteAsync(connection, null, """
                create schema if not exists platform;
                create table if not exists platform.schema_migrations (
                    module     text        not null,
                    script     text        not null,
                    applied_at timestamptz not null default now(),
                    primary key (module, script));
                """, cancellationToken);

            var applied = new List<string>();
            foreach (var resource in resources)
            {
                var script = resource[ResourcePrefix.Length..];
                if (await IsAppliedAsync(connection, module, script, cancellationToken))
                {
                    continue;
                }

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await ExecuteAsync(connection, transaction, await ReadAsync(assembly, resource, cancellationToken), cancellationToken);
                await using (var record = new NpgsqlCommand(
                    "insert into platform.schema_migrations (module, script) values (@module, @script)", connection, transaction))
                {
                    record.Parameters.AddWithValue("module", module);
                    record.Parameters.AddWithValue("script", script);
                    await record.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                applied.Add(script);
            }

            return applied;
        }
        finally
        {
            await ExecuteAsync(connection, null, $"select pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
        }
    }

    private static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string module, string script, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "select exists (select 1 from platform.schema_migrations where module = @module and script = @script)", connection);
        command.Parameters.AddWithValue("module", module);
        command.Parameters.AddWithValue("script", script);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<string> ReadAsync(Assembly assembly, string resource, CancellationToken cancellationToken)
    {
        await using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded migration '{resource}' is missing.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

#pragma warning disable CA2100 // The SQL comes from scripts compiled into our own assemblies, never from user input.
    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
#pragma warning restore CA2100
}
```

`src/Platform.Shared/Data/Migrations/0001_platform.sql`:
```sql
-- Platform-wide helpers used by every module's migrations (spec section 2.2).
create schema if not exists platform;

-- The tenant of the current connection, or NULL when none is set. NULLIF turns the empty string that a reset
-- custom setting returns into NULL, so "no tenant" means zero rows instead of a uuid cast error.
create or replace function platform.current_tenant() returns uuid
    language sql
    stable
as $$ select nullif(current_setting('app.tenant_id', true), '')::uuid $$;

-- Forced row-level security with one isolation policy for a tenant-owned table.
create or replace function platform.enable_tenant_rls(p_schema text, p_table text) returns void
    language plpgsql
as $$
begin
    execute format('alter table %I.%I enable row level security', p_schema, p_table);
    execute format('alter table %I.%I force row level security', p_schema, p_table);
    execute format('drop policy if exists tenant_isolation on %I.%I', p_schema, p_table);
    execute format(
        'create policy tenant_isolation on %I.%I using (tenant_id = platform.current_tenant()) with check (tenant_id = platform.current_tenant())',
        p_schema, p_table);
end
$$;

grant usage on schema platform to erp_app;
grant execute on function platform.current_tenant() to erp_app;
```

`src/Platform.Shared/SharedModule.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.Shared.Data;
using Platform.Shared.Tenancy;

namespace Platform.Shared;

public static class SharedModule
{
    public static IServiceCollection AddPlatformShared(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<TenantAccessor>();
        services.AddScoped<ITenantAccessor>(sp => sp.GetRequiredService<TenantAccessor>());
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "platform", typeof(SharedModule).Assembly, cancellationToken);
}
```

In `src/Platform.Web/Program.cs`, replace the three lines that register `TimeProvider`, `TenantAccessor` and `ITenantAccessor` with:
```csharp
builder.Services.AddPlatformShared();
```
and add `using Platform.Shared;` at the top (remove `using Platform.Shared.Tenancy;` if it becomes unused).

- [ ] **Step 3: Create the migrator**

```bash
dotnet new console -n Platform.Migrator -o src/Platform.Migrator
dotnet sln WaslaBid.slnx add src/Platform.Migrator/Platform.Migrator.csproj
```

Replace `src/Platform.Migrator/Platform.Migrator.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <UserSecretsId>waslabid-migrator</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Platform.Shared\Platform.Shared.csproj" />
    <ProjectReference Include="..\Modules\Audit\Platform.Modules.Audit\Platform.Modules.Audit.csproj" />
    <ProjectReference Include="..\Modules\Tenancy\Platform.Modules.Tenancy\Platform.Modules.Tenancy.csproj" />
    <ProjectReference Include="..\Modules\Workflow\Platform.Modules.Workflow\Platform.Modules.Workflow.csproj" />
  </ItemGroup>
</Project>
```

`src/Platform.Migrator/MigrationRunner.cs`:
```csharp
using Npgsql;
using Platform.Shared;

namespace Platform.Migrator;

/// <summary>Applies every module's SQL migrations as the owner role, in dependency order.</summary>
public static class MigrationRunner
{
    public static async Task<IReadOnlyList<string>> RunAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);

        var applied = new List<string>();
        applied.AddRange(Named("platform", await SharedModule.MigrateAsync(connection, cancellationToken)));
        return applied;
    }

    private static IEnumerable<string> Named(string module, IEnumerable<string> scripts) => scripts.Select(s => $"{module}/{s}");
}
```

`src/Platform.Migrator/Program.cs`:
```csharp
using Microsoft.Extensions.Configuration;
using Platform.Migrator;

var configuration = new ConfigurationBuilder()
    .AddUserSecrets(typeof(MigrationRunner).Assembly, optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var owner = configuration.GetConnectionString("Owner");
if (string.IsNullOrWhiteSpace(owner))
{
    Console.Error.WriteLine("Connection string 'Owner' is not configured (user secrets or ConnectionStrings__Owner).");
    return 1;
}

var applied = await MigrationRunner.RunAsync(owner);
Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied {applied.Count} scripts: {string.Join(", ", applied)}");
return 0;
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test tests/Platform.IntegrationTests`
Expected: 3 passed (health, two migration tests). The first run pulls `pgvector/pgvector:pg16` if it is not cached.

- [ ] **Step 5: Commit**

```bash
git add src/Platform.Shared src/Platform.Migrator src/Platform.Web/Program.cs tests/Platform.IntegrationTests WaslaBid.slnx
git commit -m "SQL migration runner, platform RLS helpers, migrator, database fixture"
```

---
### Task 6: Tenant connection interceptor, Audit module, and row-level security (W-03)

**Files:**
- Create: `src/Platform.Shared/Data/TenantConnectionInterceptor.cs`, `src/Platform.Shared/Data/ModuleDbContext.cs`
- Create: `src/Modules/Audit/Platform.Modules.Audit.Contracts/IAuditWriter.cs`
- Create: `src/Modules/Audit/Platform.Modules.Audit/AuditEvent.cs`, `AuditDbContext.cs`, `AuditWriter.cs`, `Migrations/0001_audit.sql`
- Modify: `src/Modules/Audit/Platform.Modules.Audit/AuditModule.cs`, `src/Platform.Migrator/MigrationRunner.cs`
- Create: `tests/Platform.IntegrationTests/Infrastructure/ModuleHost.cs`, `tests/Platform.IntegrationTests/Infrastructure/TestTenants.cs`, `tests/Platform.IntegrationTests/Data/RowLevelSecurityTests.cs`

- [ ] **Step 1: Write the test helpers**

`tests/Platform.IntegrationTests/Infrastructure/TestTenants.cs` (Task 7 replaces these literals with the dev seed):
```csharp
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

internal static class TestTenants
{
    public static TenantContext Acme { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000ac01"), "acme", "acme", "ar-SA",
        new TenantBranding("Acme Contracting", "#0F766E", null));

    public static TenantContext Beta { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000be01"), "beta", "beta", "en-US",
        new TenantBranding("Beta Industries", "#9A3412", null));
}
```

`tests/Platform.IntegrationTests/Infrastructure/ModuleHost.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Platform.Modules.Audit;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>The modules wired as the web host wires them, without HTTP. One scope stands for one request.</summary>
internal sealed class ModuleHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;

    public ModuleHost(string appConnectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformShared();
        services.AddAuditModule(appConnectionString);
        services.AddTenancyModule(appConnectionString);
        services.AddWorkflowModule(appConnectionString);
        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public AsyncServiceScope ScopeFor(TenantContext? tenant)
    {
        var scope = _root.CreateAsyncScope();
        if (tenant is not null)
        {
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant);
        }

        return scope;
    }

    public ValueTask DisposeAsync() => _root.DisposeAsync();
}
```

- [ ] **Step 2: Write the failing W-03 tests** — `tests/Platform.IntegrationTests/Data/RowLevelSecurityTests.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit;
using Platform.Modules.Audit.Contracts;

namespace Platform.IntegrationTests.Data;

/// <summary>W-03 acceptance, run against audit.events as the first tenant-owned table.</summary>
[Collection(DatabaseCollection.Name)]
public class RowLevelSecurityTests(DatabaseFixture db) : IAsyncLifetime
{
    private const string SeedAction = "rls.seed";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ModuleHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = new ModuleHost(db.AppConnectionString);
        foreach (var tenant in new[] { TestTenants.Acme, TestTenants.Beta })
        {
            await using var scope = _host.ScopeFor(tenant);
            await scope.ServiceProvider.GetRequiredService<IAuditWriter>()
                .WriteAsync(new AuditEntry("rls-test", SeedAction, "tenant", tenant.Slug), Ct);
        }
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Ef_query_returns_only_the_current_tenants_rows()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        await using var context = await CreateContextAsync(scope);

        var tenants = await context.Events.IgnoreQueryFilters()
            .Where(e => e.Action == SeedAction).Select(e => e.TenantId).Distinct().ToListAsync(Ct);

        tenants.ShouldBe([TestTenants.Acme.TenantId]);
    }

    [Fact]
    public async Task Raw_sql_as_the_app_role_returns_only_the_current_tenants_rows()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        await using var context = await CreateContextAsync(scope);

        var tenants = await context.Database
            .SqlQueryRaw<Guid>("select tenant_id as \"Value\" from audit.events where action = 'rls.seed'")
            .Distinct().ToListAsync(Ct);

        tenants.ShouldBe([TestTenants.Acme.TenantId]);
    }

    [Fact]
    public async Task No_tenant_set_returns_zero_rows_and_an_insert_is_rejected()
    {
        await using var scope = _host.ScopeFor(null);
        await using var context = await CreateContextAsync(scope);

        var count = await context.Events.IgnoreQueryFilters().CountAsync(Ct);
        count.ShouldBe(0);

        await Should.ThrowAsync<PostgresException>(() => context.Database.ExecuteSqlRawAsync(
            "insert into audit.events (id, tenant_id, action, subject_type) values (gen_random_uuid(), '0f0e0d0c-0000-7000-8000-00000000ac01', 'x', 'y')",
            Ct));
    }

    [Fact]
    public async Task A_pooled_connection_reused_by_another_tenant_sees_none_of_the_first_tenants_rows()
    {
        // One connection in the pool forces both scopes onto the same PostgreSQL backend.
        var singleConnection = new NpgsqlConnectionStringBuilder(db.AppConnectionString) { MaxPoolSize = 1 }.ConnectionString;
        await using var host = new ModuleHost(singleConnection);

        int acmeBackend;
        await using (var acme = host.ScopeFor(TestTenants.Acme))
        {
            await using var context = await CreateContextAsync(acme);
            acmeBackend = await context.Database.SqlQueryRaw<int>("select pg_backend_pid() as \"Value\"").SingleAsync(Ct);
            (await context.Events.CountAsync(Ct)).ShouldBeGreaterThan(0);
        }

        await using var beta = host.ScopeFor(TestTenants.Beta);
        await using var betaContext = await CreateContextAsync(beta);
        var betaBackend = await betaContext.Database.SqlQueryRaw<int>("select pg_backend_pid() as \"Value\"").SingleAsync(Ct);
        var tenants = await betaContext.Events.IgnoreQueryFilters().Select(e => e.TenantId).Distinct().ToListAsync(Ct);

        betaBackend.ShouldBe(acmeBackend);
        tenants.ShouldBe([TestTenants.Beta.TenantId]);
    }

    private static Task<AuditDbContext> CreateContextAsync(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuditDbContext>>().CreateDbContextAsync(Ct);
}
```

The tests call `IgnoreQueryFilters()` on purpose: they prove the database enforces isolation, not the EF filter.

Run: `dotnet test tests/Platform.IntegrationTests --filter RowLevelSecurityTests`
Expected: build error, `IAuditWriter` not found.

- [ ] **Step 3: Interceptor and DbContext registration in Platform.Shared**

`src/Platform.Shared/Data/TenantConnectionInterceptor.cs`:
```csharp
using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Data;

/// <summary>
/// Sets app.tenant_id on every connection EF Core opens so PostgreSQL row-level security sees the current tenant.
/// With no tenant it sets the empty string, which platform.current_tenant() reads as NULL: zero rows, inserts rejected.
/// Npgsql resets session state when the connection goes back to the pool.
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantAccessor tenants) : DbConnectionInterceptor
{
    private const string SetTenantSql = "select set_config('app.tenant_id', @tenant, false)";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = SetTenantSql;
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenants.Current?.TenantId.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty;
        command.Parameters.Add(parameter);
        return command;
    }
}
```

`src/Platform.Shared/Data/ModuleDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Tenancy;

namespace Platform.Shared.Data;

public static class ModuleDbContextRegistration
{
    /// <summary>
    /// Registers a scoped IDbContextFactory for a module context: create one context per operation, never hold one for
    /// the life of a Blazor circuit. Every connection gets the tenant interceptor.
    /// </summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string connectionString)
        where TContext : DbContext
    {
        services.AddDbContextFactory<TContext>(
            (provider, options) => options
                .UseNpgsql(connectionString)
                .UseSnakeCaseNamingConvention()
                .AddInterceptors(new TenantConnectionInterceptor(provider.GetRequiredService<ITenantAccessor>())),
            ServiceLifetime.Scoped);
        return services;
    }
}
```

- [ ] **Step 4: Audit contracts** — `src/Modules/Audit/Platform.Modules.Audit.Contracts/IAuditWriter.cs`

```csharp
namespace Platform.Modules.Audit.Contracts;

/// <summary>One append-only audit event for the current tenant (F-41 basis).</summary>
public sealed record AuditEntry(
    string? ActorId,
    string Action,
    string SubjectType,
    string? SubjectId,
    IReadOnlyDictionary<string, string?>? Data = null);

public interface IAuditWriter
{
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
```

- [ ] **Step 5: Audit implementation**

`src/Modules/Audit/Platform.Modules.Audit/Migrations/0001_audit.sql`:
```sql
create schema if not exists audit;

create table audit.events (
    id           uuid        primary key,
    tenant_id    uuid        not null,
    occurred_at  timestamptz not null default now(),
    actor_id     text        null,
    action       text        not null,
    subject_type text        not null,
    subject_id   text        null,
    data         jsonb       not null default '{}'::jsonb
);

create index ix_events_tenant_occurred on audit.events (tenant_id, occurred_at desc);

select platform.enable_tenant_rls('audit', 'events');

grant usage on schema audit to erp_app;
-- Append-only at the database level: no UPDATE or DELETE for the application role.
grant select, insert on audit.events to erp_app;
```

`src/Modules/Audit/Platform.Modules.Audit/AuditEvent.cs`:
```csharp
namespace Platform.Modules.Audit;

internal sealed class AuditEvent
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public DateTimeOffset OccurredAt { get; set; }

    public string? ActorId { get; set; }

    public string Action { get; set; } = string.Empty;

    public string SubjectType { get; set; } = string.Empty;

    public string? SubjectId { get; set; }

    public string Data { get; set; } = "{}";
}
```

`src/Modules/Audit/Platform.Modules.Audit/AuditDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Audit;

internal sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ITenantAccessor tenants) : DbContext(options)
{
    public DbSet<AuditEvent> Events => Set<AuditEvent>();

    // The query filter mirrors the RLS policy for readable failures; PostgreSQL is the enforcing layer.
    private Guid? CurrentTenantId => tenants.Current?.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("audit");
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Data).HasColumnType("jsonb");
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }
}
```

`src/Modules/Audit/Platform.Modules.Audit/AuditWriter.cs`:
```csharp
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Audit.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Audit;

internal sealed class AuditWriter(IDbContextFactory<AuditDbContext> contexts, ITenantAccessor tenants, TimeProvider clock) : IAuditWriter
{
    private static readonly Dictionary<string, string?> NoData = [];

    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var tenant = tenants.Current ?? throw new InvalidOperationException("An audit event needs a current tenant.");

        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        db.Events.Add(new AuditEvent
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenant.TenantId,
            OccurredAt = clock.GetUtcNow(),
            ActorId = entry.ActorId,
            Action = entry.Action,
            SubjectType = entry.SubjectType,
            SubjectId = entry.SubjectId,
            Data = JsonSerializer.Serialize(entry.Data ?? NoData),
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
```

Replace `src/Modules/Audit/Platform.Modules.Audit/AuditModule.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Audit.Contracts;
using Platform.Shared.Data;

namespace Platform.Modules.Audit;

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddModuleDbContext<AuditDbContext>(connectionString);
        services.AddScoped<IAuditWriter, AuditWriter>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "audit", typeof(AuditModule).Assembly, cancellationToken);
}
```

Delete `src/Modules/Audit/Platform.Modules.Audit/Migrations/.gitkeep`.

In `src/Platform.Migrator/MigrationRunner.cs`, add after the platform line (and `using Platform.Modules.Audit;`):
```csharp
        applied.AddRange(Named("audit", await AuditModule.MigrateAsync(connection, cancellationToken)));
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass, including the four `RowLevelSecurityTests`.

If `No_tenant_set...` fails because rows come back: the policy is not forced; `select relforcerowsecurity from pg_class where relname = 'events'` must return `t`.

- [ ] **Step 7: Commit and move W-03 to Done**

In `docs/09-backlog.md`, row W-03, replace `| P0 | M | Backlog | W-02 |` with `| P0 | M | Done | W-02 |`.

```bash
git add src tests docs/09-backlog.md
git commit -m "Tenant interceptor, Audit module, row-level security tests; W-03 done"
```

---

### Task 7: Tenancy module and development tenants

**Files:**
- Create: `src/Modules/Tenancy/Platform.Modules.Tenancy.Contracts/ITenantDirectory.cs`
- Create: `src/Modules/Tenancy/Platform.Modules.Tenancy/TenantDirectory.cs`, `Migrations/0001_tenancy.sql`
- Modify: `src/Modules/Tenancy/Platform.Modules.Tenancy/TenancyModule.cs`
- Create: `src/Platform.Migrator/DevSeed.cs`
- Modify: `src/Platform.Migrator/MigrationRunner.cs`, `tests/Platform.IntegrationTests/Infrastructure/DatabaseFixture.cs`, `tests/Platform.IntegrationTests/Infrastructure/TestTenants.cs`
- Create: `tests/Platform.IntegrationTests/Tenancy/TenantDirectoryTests.cs`

- [ ] **Step 1: Write the failing tests** — `tests/Platform.IntegrationTests/Tenancy/TenantDirectoryTests.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.IntegrationTests.Tenancy;

[Collection(DatabaseCollection.Name)]
public class TenantDirectoryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Known_host_resolves_to_the_tenant_ignoring_case()
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(null);

        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByHostAsync("ACME.localhost", Ct);

        tenant.ShouldBe(TestTenants.Acme);
    }

    [Fact]
    public async Task Unknown_host_resolves_to_nothing()
    {
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(null);

        var tenant = await scope.ServiceProvider.GetRequiredService<ITenantDirectory>().FindByHostAsync("nobody.localhost", Ct);

        tenant.ShouldBeNull();
    }

    [Fact]
    public async Task The_app_role_cannot_read_the_tenant_tables_directly()
    {
        await using var connection = new NpgsqlConnection(db.AppConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("select count(*) from tenancy.tenants", connection);

        var error = await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}
```

Run: `dotnet test tests/Platform.IntegrationTests --filter TenantDirectoryTests`
Expected: build error, `ITenantDirectory` not found.

- [ ] **Step 2: Tenancy schema** — `src/Modules/Tenancy/Platform.Modules.Tenancy/Migrations/0001_tenancy.sql`

```sql
-- Platform-level tables: the one deliberate exception to tenant RLS (spec sections 2.1 and 7). They are read before a
-- tenant is known, so access is limited by grants: erp_app has no table rights, only the resolve_host function.
create schema if not exists tenancy;

create table tenancy.tenants (
    id                 uuid        primary key,
    slug               text        not null unique check (slug ~ '^[a-z0-9-]{2,40}$'),
    keycloak_org_alias text        not null unique,
    default_culture    text        not null default 'ar-SA' check (default_culture in ('ar-SA', 'en-US')),
    portal_name        text        not null,
    primary_color      text        not null check (primary_color ~ '^#[0-9A-Fa-f]{6}$'),
    logo_url           text        null,
    created_at         timestamptz not null default now()
);

create table tenancy.tenant_hosts (
    host      text primary key check (host = lower(host)),
    tenant_id uuid not null references tenancy.tenants (id)
);

create function tenancy.resolve_host(p_host text)
    returns table (
        tenant_id uuid, slug text, keycloak_org_alias text, default_culture text,
        portal_name text, primary_color text, logo_url text)
    language sql
    stable
    security definer
    set search_path = tenancy, pg_temp
as $$
    select t.id, t.slug, t.keycloak_org_alias, t.default_culture, t.portal_name, t.primary_color, t.logo_url
    from tenancy.tenant_hosts h
    join tenancy.tenants t on t.id = h.tenant_id
    where h.host = lower(p_host)
$$;

revoke all on function tenancy.resolve_host(text) from public;
grant usage on schema tenancy to erp_app;
grant execute on function tenancy.resolve_host(text) to erp_app;
```

Delete `src/Modules/Tenancy/Platform.Modules.Tenancy/Migrations/.gitkeep`.

- [ ] **Step 3: Contract and directory**

`src/Modules/Tenancy/Platform.Modules.Tenancy.Contracts/ITenantDirectory.cs`:
```csharp
using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy.Contracts;

public interface ITenantDirectory
{
    /// <summary>The tenant that owns the host name, or null. Results, including misses, are cached for 60 seconds.</summary>
    Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default);
}
```

`src/Modules/Tenancy/Platform.Modules.Tenancy/TenantDirectory.cs`:
```csharp
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Tenancy;

internal sealed class TenantDirectory(
    [FromKeyedServices(TenancyModule.DataSourceKey)] NpgsqlDataSource dataSource,
    IMemoryCache cache) : ITenantDirectory
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    public async Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
#pragma warning disable CA1308 // Host names compare in lower case (RFC 4343); the table stores them lower-cased.
        var normalized = host.ToLowerInvariant();
#pragma warning restore CA1308
        var key = "tenant-host:" + normalized;
        if (cache.TryGetValue(key, out TenantContext? cached))
        {
            return cached;
        }

        await using var command = dataSource.CreateCommand(
            "select tenant_id, slug, keycloak_org_alias, default_culture, portal_name, primary_color, logo_url from tenancy.resolve_host($1)");
        command.Parameters.Add(new NpgsqlParameter { Value = normalized });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        TenantContext? tenant = null;
        if (await reader.ReadAsync(cancellationToken))
        {
            tenant = new TenantContext(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                new TenantBranding(reader.GetString(4), reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        cache.Set(key, tenant, CacheFor);
        return tenant;
    }
}
```

Replace `src/Modules/Tenancy/Platform.Modules.Tenancy/TenancyModule.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Data;

namespace Platform.Modules.Tenancy;

public static class TenancyModule
{
    internal const string DataSourceKey = "tenancy";

    public static IServiceCollection AddTenancyModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddMemoryCache();
        services.AddKeyedSingleton(DataSourceKey, (_, _) => NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<ITenantDirectory, TenantDirectory>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "tenancy", typeof(TenancyModule).Assembly, cancellationToken);
}
```

In `MigrationRunner.RunAsync`, add after the audit line (and `using Platform.Modules.Tenancy;`):
```csharp
        applied.AddRange(Named("tenancy", await TenancyModule.MigrateAsync(connection, cancellationToken)));
```

- [ ] **Step 4: Development tenants** — `src/Platform.Migrator/DevSeed.cs`

```csharp
using Npgsql;
using Platform.Shared.Tenancy;

namespace Platform.Migrator;

public sealed record DevTenant(Guid Id, string Slug, string OrgAlias, string Culture, string PortalName, string PrimaryColor, string Host)
{
    public TenantContext ToContext() => new(Id, Slug, OrgAlias, Culture, new TenantBranding(PortalName, PrimaryColor, null));
}

/// <summary>Development and test data only. Tenant provisioning for real customers is F-01.</summary>
public static class DevSeed
{
    public static DevTenant Acme { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000ac01"), "acme", "acme", "ar-SA", "Acme Contracting", "#0F766E", "acme.localhost");

    public static DevTenant Beta { get; } = new(
        Guid.Parse("0f0e0d0c-0000-7000-8000-00000000be01"), "beta", "beta", "en-US", "Beta Industries", "#9A3412", "beta.localhost");

    public static IReadOnlyList<DevTenant> Tenants { get; } = [Acme, Beta];

    public static async Task SeedTenantsAsync(string ownerConnectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync(cancellationToken);
        foreach (var tenant in Tenants)
        {
            await using var command = new NpgsqlCommand("""
                insert into tenancy.tenants (id, slug, keycloak_org_alias, default_culture, portal_name, primary_color)
                values (@id, @slug, @alias, @culture, @portal, @color)
                on conflict do nothing;
                insert into tenancy.tenant_hosts (host, tenant_id) values (@host, @id) on conflict do nothing;
                """, connection);
            command.Parameters.AddWithValue("id", tenant.Id);
            command.Parameters.AddWithValue("slug", tenant.Slug);
            command.Parameters.AddWithValue("alias", tenant.OrgAlias);
            command.Parameters.AddWithValue("culture", tenant.Culture);
            command.Parameters.AddWithValue("portal", tenant.PortalName);
            command.Parameters.AddWithValue("color", tenant.PrimaryColor);
            command.Parameters.AddWithValue("host", tenant.Host);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
```

Replace `tests/Platform.IntegrationTests/Infrastructure/TestTenants.cs`:
```csharp
using Platform.Migrator;
using Platform.Shared.Tenancy;

namespace Platform.IntegrationTests.Infrastructure;

internal static class TestTenants
{
    public static TenantContext Acme { get; } = DevSeed.Acme.ToContext();

    public static TenantContext Beta { get; } = DevSeed.Beta.ToContext();
}
```

In `DatabaseFixture.InitializeAsync`, after `await MigrationRunner.RunAsync(OwnerConnectionString);` add:
```csharp
        await DevSeed.SeedTenantsAsync(OwnerConnectionString);
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass, including three `TenantDirectoryTests`.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "Tenancy module: host resolution through a security-definer function, dev tenants"
```

---

### Task 8: Tenant middleware and circuit handler

**Files:**
- Create: `src/Platform.Web/Tenancy/TenantMiddleware.cs`, `src/Platform.Web/Tenancy/TenantCircuitHandler.cs`
- Modify: `src/Platform.Web/Program.cs`
- Create: `tests/Platform.IntegrationTests/Infrastructure/PlatformWebFactory.cs`, `tests/Platform.IntegrationTests/Web/TenantResolutionTests.cs`, `tests/Platform.IntegrationTests/Web/TenantCircuitHandlerTests.cs`, `tests/Platform.IntegrationTests/Web/TenantMiddlewareTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Platform.IntegrationTests/Infrastructure/PlatformWebFactory.cs`:
```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Platform.IntegrationTests.Infrastructure;

internal sealed class PlatformWebFactory(string appConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", appConnectionString);
    }

    public HttpClient ClientFor(string host) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });
}
```

`tests/Platform.IntegrationTests/Web/TenantResolutionTests.cs`:
```csharp
using System.Net;
using Microsoft.AspNetCore.Http;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

[Collection(DatabaseCollection.Name)]
public class TenantResolutionTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Unknown_host_gets_404_before_anything_else()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("nobody.localhost");

        var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Known_host_is_served()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Health_bypass_covers_only_the_exact_health_path()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("nobody.localhost");

        var response = await client.GetAsync(new Uri("/health/x", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Empty_host_gets_404()
    {
        // HttpClient and TestServer's client handler both fill in a missing Host header, so set it on the context.
        await using var factory = new PlatformWebFactory(db.AppConnectionString);

        var context = await factory.Server.SendAsync(
            c =>
            {
                c.Request.Method = HttpMethods.Get;
                c.Request.Path = "/";
                c.Request.Host = default;
            },
            Ct);

        context.Request.Host.HasValue.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
    }
}
```

`tests/Platform.IntegrationTests/Web/TenantCircuitHandlerTests.cs`:
```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Tenancy;

namespace Platform.IntegrationTests.Web;

public class TenantCircuitHandlerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Circuit_start_sets_the_tenant_from_the_connection_host()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://acme.localhost:8443/"), ConnectionFrom("acme.localhost"), new TwoTenantDirectory(), accessor);

        await handler.OnCircuitOpenedAsync(null!, Ct);

        accessor.Current.ShouldBe(TestTenants.Acme);
    }

    [Fact]
    public async Task Circuit_start_accepts_a_base_uri_host_that_differs_only_in_case()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://ACME.localhost:8443/"), ConnectionFrom("acme.localhost"), new TwoTenantDirectory(), accessor);

        await handler.OnCircuitOpenedAsync(null!, Ct);

        accessor.Current.ShouldBe(TestTenants.Acme);
    }

    [Fact]
    public async Task Circuit_start_refuses_a_base_uri_for_another_host()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://beta.localhost:8443/"), ConnectionFrom("acme.localhost"), new TwoTenantDirectory(), accessor);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.OnCircuitOpenedAsync(null!, Ct));

        accessor.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Circuit_start_refuses_when_there_is_no_connection()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://acme.localhost:8443/"), new HttpContextAccessor(), new TwoTenantDirectory(), accessor);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.OnCircuitOpenedAsync(null!, Ct));

        accessor.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Circuit_start_refuses_a_host_no_tenant_owns()
    {
        var accessor = new TenantAccessor();
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://nobody.localhost:8443/"), ConnectionFrom("nobody.localhost"), new TwoTenantDirectory(), accessor);

        await Should.ThrowAsync<InvalidOperationException>(() => handler.OnCircuitOpenedAsync(null!, Ct));

        accessor.Current.ShouldBeNull();
    }

    [Fact]
    public void Runs_before_any_other_circuit_handler()
    {
        var handler = new TenantCircuitHandler(
            new FixedNavigation("https://acme.localhost:8443/"), new HttpContextAccessor(), new TwoTenantDirectory(), new TenantAccessor());

        handler.Order.ShouldBe(int.MinValue);
    }

    private static HttpContextAccessor ConnectionFrom(string host)
    {
        var context = new DefaultHttpContext();
        context.Request.Host = new HostString(host, 8443);
        return new HttpContextAccessor { HttpContext = context };
    }

    private sealed class FixedNavigation : NavigationManager
    {
        public FixedNavigation(string baseUri) => Initialize(baseUri, baseUri);
    }

    private sealed class TwoTenantDirectory : ITenantDirectory
    {
        public Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default) =>
            Task.FromResult(host switch { "acme.localhost" => TestTenants.Acme, "beta.localhost" => TestTenants.Beta, _ => null });

        public void Invalidate(string host)
        {
        }
    }
}
```

`tests/Platform.IntegrationTests/Web/TenantMiddlewareTests.cs`:
```csharp
using Microsoft.AspNetCore.Http;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;
using Platform.Web.Tenancy;

namespace Platform.IntegrationTests.Web;

public class TenantMiddlewareTests
{
    [Fact]
    public async Task Missing_host_is_404_without_a_lookup()
    {
        var nextCalled = false;
        var middleware = new TenantMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        });
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        var accessor = new TenantAccessor();

        await middleware.InvokeAsync(context, new ThrowingDirectory(), accessor);

        context.Response.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        nextCalled.ShouldBeFalse();
        accessor.Current.ShouldBeNull();
    }

    private sealed class ThrowingDirectory : ITenantDirectory
    {
        public Task<TenantContext?> FindByHostAsync(string host, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The directory must not be asked about an empty host.");

        public void Invalidate(string host)
        {
        }
    }
}
```

Run: `dotnet test tests/Platform.IntegrationTests --filter "TenantResolutionTests|TenantCircuitHandlerTests"`
Expected: build error, namespace `Platform.Web.Tenancy` does not exist.

- [ ] **Step 2: Implement**

`src/Platform.Web/Tenancy/TenantMiddleware.cs`:
```csharp
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Tenancy;

/// <summary>Resolves the tenant from the host name before authentication; an unknown host is a 404 (spec 2.1).</summary>
internal sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantDirectory directory, TenantAccessor accessor)
    {
        // Only the exact health path skips tenant resolution; /health/anything is an ordinary tenant path.
        if (context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context);
            return;
        }

        var host = context.Request.Host.Host;
        var tenant = string.IsNullOrWhiteSpace(host) ? null : await directory.FindByHostAsync(host, context.RequestAborted);
        if (tenant is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        accessor.Set(tenant);
        await next(context);
    }
}
```

`src/Platform.Web/Tenancy/TenantCircuitHandler.cs`:
```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Platform.Modules.Tenancy.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Web.Tenancy;

/// <summary>
/// A Blazor circuit has its own DI scope, separate from the HTTP request that started it. This handler sets the
/// circuit's tenant when the circuit opens, before any other circuit handler runs and before any component renders.
/// The host comes from the <c>/_blazor</c> connection request, which already passed <see cref="TenantMiddleware"/> and
/// authorization. <see cref="NavigationManager.BaseUri"/> is supplied by the browser in the circuit start message, so
/// it is only checked against the connection host, never trusted on its own.
/// </summary>
internal sealed class TenantCircuitHandler(
    NavigationManager navigation,
    IHttpContextAccessor httpContextAccessor,
    ITenantDirectory directory,
    TenantAccessor accessor) : CircuitHandler
{
    public override int Order => int.MinValue;

    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        var connectionHost = httpContextAccessor.HttpContext?.Request.Host.Host;
        if (string.IsNullOrEmpty(connectionHost))
        {
            throw new InvalidOperationException("The circuit has no connection request to take the tenant host from.");
        }

        var baseUriHost = new Uri(navigation.BaseUri).Host;
        if (!string.Equals(baseUriHost, connectionHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The circuit base URI host '{baseUriHost}' does not match the connection host '{connectionHost}'.");
        }

        var tenant = await directory.FindByHostAsync(connectionHost, cancellationToken)
            ?? throw new InvalidOperationException($"No tenant owns the host '{connectionHost}'.");
        accessor.Set(tenant);
    }
}
```

`src/Platform.Web/Program.cs` becomes:
```csharp
using Microsoft.AspNetCore.Components.Server.Circuits;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Web.Components;
using Platform.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);
var platformDb = builder.Configuration.GetConnectionString("Platform");
if (string.IsNullOrWhiteSpace(platformDb))
{
    throw new InvalidOperationException("Connection string 'Platform' is not configured. Set it with dotnet user-secrets (see README).");
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddPlatformShared();
builder.Services.AddAuditModule(platformDb);
builder.Services.AddTenancyModule(platformDb);
builder.Services.AddIdentityModule();
builder.Services.AddWorkflowModule(platformDb);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CircuitHandler, TenantCircuitHandler>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseMiddleware<TenantMiddleware>();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapHealthChecks("/health");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
```

- [ ] **Step 3: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/Platform.Web tests
git commit -m "Web: host-name tenant middleware and circuit tenant handler"
```

---
### Task 9: Keycloak realm, Identity module, OIDC login (W-04)

**Files:**
- Create: `infra/compose/keycloak/import/waslabid-realm.json`
- Modify: `infra/compose/docker-compose.yml`, `infra/compose/.env.example`, `infra/compose/keycloak/import/README.md`
- Create: `src/Modules/Identity/Platform.Modules.Identity.Contracts/IdentityClaims.cs`
- Create: `src/Modules/Identity/Platform.Modules.Identity/OrganizationClaims.cs`, `SameTenantAuthorization.cs`
- Modify: `src/Modules/Identity/Platform.Modules.Identity/IdentityModule.cs`, `Platform.Modules.Identity.csproj`, `src/Platform.Web/Platform.Web.csproj`, `src/Platform.Web/Program.cs`, `src/Platform.Web/appsettings.json`, `src/Platform.Web/appsettings.Development.json`
- Create: `tests/Platform.UnitTests/Identity/OrganizationClaimsTests.cs`, `tests/Platform.IntegrationTests/Infrastructure/TestAuthentication.cs`, `tests/Platform.IntegrationTests/Infrastructure/KeycloakFixture.cs`, `tests/Platform.IntegrationTests/Identity/KeycloakTokenTests.cs`, `tests/Platform.IntegrationTests/Web/SameTenantTests.cs`
- Modify: `tests/Platform.IntegrationTests/Infrastructure/PlatformWebFactory.cs`, `tests/Platform.IntegrationTests/Web/TenantResolutionTests.cs`

- [ ] **Step 1: Write the failing unit tests** — `tests/Platform.UnitTests/Identity/OrganizationClaimsTests.cs`

```csharp
using System.Security.Claims;
using Platform.Modules.Identity;
using Platform.Shared.Tenancy;

namespace Platform.UnitTests.Identity;

public class OrganizationClaimsTests
{
    private static readonly TenantContext Acme = new(Guid.NewGuid(), "acme", "acme", "ar-SA", new TenantBranding("Acme", "#0F766E", null));

    [Fact]
    public void One_claim_per_alias_is_read_as_membership()
    {
        var user = Principal(new Claim("organization", "acme"), new Claim("organization", "beta"));

        OrganizationClaims.Aliases(user).ShouldBe(["acme", "beta"], ignoreOrder: true);
        OrganizationClaims.BelongsTo(user, Acme).ShouldBeTrue();
    }

    [Fact]
    public void A_json_array_claim_is_read_as_membership() =>
        OrganizationClaims.BelongsTo(Principal(new Claim("organization", "[\"acme\"]")), Acme).ShouldBeTrue();

    [Fact]
    public void A_json_object_claim_with_ids_is_read_by_alias()
    {
        var user = Principal(new Claim("organization", "{\"acme\":{\"id\":\"d9df78c6-c1ac-404c-9462-d8aae572c603\"}}"));

        OrganizationClaims.BelongsTo(user, Acme).ShouldBeTrue();
    }

    [Fact]
    public void Another_organization_does_not_belong() =>
        OrganizationClaims.BelongsTo(Principal(new Claim("organization", "beta")), Acme).ShouldBeFalse();

    private static ClaimsPrincipal Principal(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));
}
```

Run: `dotnet test tests/Platform.UnitTests --filter OrganizationClaimsTests`
Expected: build error, `OrganizationClaims` not found.

- [ ] **Step 2: Identity module**

Replace `src/Modules/Identity/Platform.Modules.Identity/Platform.Modules.Identity.csproj` (Identity has no schema; delete its `Migrations/.gitkeep`):
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\..\Platform.Shared\Platform.Shared.csproj" />
    <ProjectReference Include="..\Platform.Modules.Identity.Contracts\Platform.Modules.Identity.Contracts.csproj" />
    <ProjectReference Include="..\..\Audit\Platform.Modules.Audit.Contracts\Platform.Modules.Audit.Contracts.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Platform.UnitTests" />
    <InternalsVisibleTo Include="Platform.IntegrationTests" />
  </ItemGroup>
</Project>
```

`src/Modules/Identity/Platform.Modules.Identity.Contracts/IdentityClaims.cs`:
```csharp
namespace Platform.Modules.Identity.Contracts;

/// <summary>Claim names as Keycloak issues them; inbound claim mapping is off, so they arrive unchanged.</summary>
public static class IdentityClaims
{
    public const string Subject = "sub";
    public const string Username = "preferred_username";
    public const string Locale = "locale";
    public const string Organization = "organization";
}
```

`src/Modules/Identity/Platform.Modules.Identity/OrganizationClaims.cs`:
```csharp
using System.Security.Claims;
using System.Text.Json;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

/// <summary>
/// Reads Keycloak organization membership. The built-in "organization" scope emits ["acme"], which arrives as one claim
/// per alias; with "add organization id" it emits {"acme":{"id":"..."}}. Tenants match on the alias because Keycloak
/// generates organization ids on import (spec section 7).
/// </summary>
internal static class OrganizationClaims
{
    public static IReadOnlySet<string> Aliases(ClaimsPrincipal principal)
    {
        var aliases = new HashSet<string>(StringComparer.Ordinal);
        foreach (var claim in principal.FindAll(IdentityClaims.Organization))
        {
            var value = claim.Value.Trim();
            if (!value.StartsWith('{') && !value.StartsWith('['))
            {
                aliases.Add(value);
                continue;
            }

            using var document = JsonDocument.Parse(value);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    aliases.Add(property.Name);
                }
            }
            else
            {
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    aliases.Add(item.GetString() ?? string.Empty);
                }
            }
        }

        aliases.Remove(string.Empty);
        return aliases;
    }

    public static bool BelongsTo(ClaimsPrincipal principal, TenantContext tenant) =>
        Aliases(principal).Contains(tenant.KeycloakOrgAlias);
}
```

`src/Modules/Identity/Platform.Modules.Identity/SameTenantAuthorization.cs`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Identity;

internal sealed class SameTenantRequirement : IAuthorizationRequirement;

/// <summary>The signed-in user must be a member of the Keycloak organization of the host's tenant (W-04).</summary>
internal sealed class SameTenantHandler(ITenantAccessor tenants, IAuditWriter audit) : AuthorizationHandler<SameTenantRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, SameTenantRequirement requirement)
    {
        var tenant = tenants.Current;
        if (tenant is null || context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (OrganizationClaims.BelongsTo(context.User, tenant))
        {
            context.Succeed(requirement);
            return;
        }

        await audit.WriteAsync(new AuditEntry(
            context.User.FindFirst(IdentityClaims.Subject)?.Value,
            "identity.cross_tenant_denied",
            "tenant",
            tenant.Slug,
            new Dictionary<string, string?> { ["organizations"] = string.Join(',', OrganizationClaims.Aliases(context.User)) }));
    }
}
```

Replace `src/Modules/Identity/Platform.Modules.Identity/IdentityModule.cs`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Platform.Modules.Identity;

public static class IdentityModule
{
    /// <summary>Authenticated and a member of the host tenant's organization. The host uses it as the fallback policy.</summary>
    public static AuthorizationPolicy SameTenantPolicy { get; } = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .AddRequirements(new SameTenantRequirement())
        .Build();

    public static IServiceCollection AddIdentityModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddScoped<IAuthorizationHandler, SameTenantHandler>();
        return services;
    }
}
```

Run: `dotnet test tests/Platform.UnitTests`
Expected: all pass, including four `OrganizationClaimsTests` and the architecture tests.

- [ ] **Step 3: Realm file** — `infra/compose/keycloak/import/waslabid-realm.json`

```json
{
  "realm": "waslabid",
  "displayName": "WaslaBid",
  "enabled": true,
  "sslRequired": "external",
  "organizationsEnabled": true,
  "internationalizationEnabled": true,
  "supportedLocales": ["ar", "en"],
  "defaultLocale": "ar",
  "clients": [
    {
      "clientId": "waslabid-web",
      "name": "WaslaBid web",
      "enabled": true,
      "publicClient": false,
      "clientAuthenticatorType": "client-secret",
      "secret": "${WASLABID_WEB_CLIENT_SECRET}",
      "standardFlowEnabled": true,
      "directAccessGrantsEnabled": false,
      "implicitFlowEnabled": false,
      "serviceAccountsEnabled": false,
      "redirectUris": [
        "https://acme.localhost:8443/signin-oidc",
        "https://beta.localhost:8443/signin-oidc",
        "https://acme.localhost/signin-oidc",
        "https://beta.localhost/signin-oidc"
      ],
      "webOrigins": ["+"],
      "attributes": {
        "pkce.code.challenge.method": "S256",
        "post.logout.redirect.uris": "https://acme.localhost:8443/*##https://beta.localhost:8443/*##https://acme.localhost/*##https://beta.localhost/*"
      },
      "defaultClientScopes": ["basic", "profile", "email", "roles", "web-origins", "acr"],
      "optionalClientScopes": ["organization"]
    },
    {
      "clientId": "waslabid-tests",
      "name": "Automated tests only; password grant; must not exist in staging or production realms",
      "enabled": true,
      "publicClient": true,
      "standardFlowEnabled": false,
      "directAccessGrantsEnabled": true,
      "defaultClientScopes": ["basic", "profile", "email", "roles", "web-origins", "acr"],
      "optionalClientScopes": ["organization"]
    }
  ],
  "users": [
    {
      "username": "acme.admin",
      "enabled": true,
      "email": "admin@acme.waslabid.test",
      "emailVerified": true,
      "firstName": "Acme",
      "lastName": "Admin",
      "attributes": { "locale": ["ar"] },
      "credentials": [{ "type": "password", "value": "${WASLABID_DEV_USER_PASSWORD}", "temporary": false }]
    },
    {
      "username": "beta.admin",
      "enabled": true,
      "email": "admin@beta.waslabid.test",
      "emailVerified": true,
      "firstName": "Beta",
      "lastName": "Admin",
      "attributes": { "locale": ["en"] },
      "credentials": [{ "type": "password", "value": "${WASLABID_DEV_USER_PASSWORD}", "temporary": false }]
    }
  ],
  "organizations": [
    {
      "name": "Acme Contracting",
      "alias": "acme",
      "enabled": true,
      "domains": [{ "name": "acme.waslabid.test", "verified": false }],
      "members": [{ "username": "acme.admin", "membershipType": "MANAGED" }]
    },
    {
      "name": "Beta Industries",
      "alias": "beta",
      "enabled": true,
      "domains": [{ "name": "beta.waslabid.test", "verified": false }],
      "members": [{ "username": "beta.admin", "membershipType": "MANAGED" }]
    }
  ]
}
```

Do not add a `clientScopes` array: defining any client scope in an import stops Keycloak creating its built-in ones (`profile`, `organization`, ...), which drops `locale` and `organization` from tokens (verified 2026-09-26).

- [ ] **Step 4: Compose and env wiring**

In `infra/compose/docker-compose.yml`, in the `keycloak` service `environment` block, after `KC_HEALTH_ENABLED: "true"`, add:
```yaml
      # Substituted into keycloak/import/waslabid-realm.json at import. Values live in .env, never in the repository (N-10).
      WASLABID_WEB_CLIENT_SECRET: ${WASLABID_WEB_CLIENT_SECRET:?Set WASLABID_WEB_CLIENT_SECRET in infra/compose/.env}
      WASLABID_DEV_USER_PASSWORD: ${WASLABID_DEV_USER_PASSWORD:?Set WASLABID_DEV_USER_PASSWORD in infra/compose/.env}
```

Append to `infra/compose/.env.example`:
```
# Keycloak realm import placeholders (keycloak/import/waslabid-realm.json). Generate your own values (README step 1).
WASLABID_WEB_CLIENT_SECRET=
WASLABID_DEV_USER_PASSWORD=
```

Append to `infra/compose/keycloak/import/README.md`:
```
## waslabid-realm.json

Realm `waslabid` with Organizations: clients `waslabid-web` (code flow with PKCE, confidential) and `waslabid-tests`
(password grant, tests only), organizations `acme` and `beta` with one admin user each. The client secret and the
users' password are `${...}` placeholders filled from `infra/compose/.env` at import. Keycloak imports a realm only
when it does not exist yet; after editing this file, delete the realm in the admin console and restart Keycloak.
```

Generate local values without printing them (once per machine; `.env` is git-ignored), then restart Keycloak so it imports the realm:
```bash
grep -q '^WASLABID_WEB_CLIENT_SECRET=.' infra/compose/.env || printf 'WASLABID_WEB_CLIENT_SECRET=%s\nWASLABID_DEV_USER_PASSWORD=Dev-%s\n' "$(openssl rand -hex 24)" "$(openssl rand -hex 8)" >> infra/compose/.env
docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env up -d keycloak
```
Expected: `Container erp-keycloak Started`; healthy within about a minute.

- [ ] **Step 5: Write the failing integration tests**

In `tests/Platform.IntegrationTests/Web/TenantResolutionTests.cs`, `Known_host_is_served` asserts `HttpStatusCode.OK` since Task 8. Once the fallback policy requires an authenticated user, an anonymous request to a known host is challenged, so change its assertion to:
```csharp
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
```

`tests/Platform.IntegrationTests/Infrastructure/TestAuthentication.cs`:
```csharp
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>A signed-in user for web tests, sent as a header and turned into the claims Keycloak issues.</summary>
internal sealed record TestUser(string Subject, IReadOnlyList<string> Organizations, string? Locale = null)
{
    public const string Header = "X-Test-User";

    public static TestUser AcmeAdmin { get; } = new("acme.admin", ["acme"], "ar");

    public static TestUser BetaAdmin { get; } = new("beta.admin", ["beta"], "en");
}

internal sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Scheme = "Test";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TestUser.Header, out var raw))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var user = JsonSerializer.Deserialize<TestUser>(raw.ToString())!;
        var claims = new List<Claim> { new("sub", user.Subject), new("preferred_username", user.Subject) };
        claims.AddRange(user.Organizations.Select(o => new Claim("organization", o)));
        if (user.Locale is not null)
        {
            claims.Add(new Claim("locale", user.Locale));
        }

        var identity = new ClaimsIdentity(claims, Scheme, "preferred_username", null);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme)));
    }
}

internal static class TestAuthenticationExtensions
{
    public static IServiceCollection AddTestAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.Scheme, _ => { });
        services.Configure<AuthenticationOptions>(o =>
        {
            o.DefaultScheme = TestAuthHandler.Scheme;
            o.DefaultAuthenticateScheme = TestAuthHandler.Scheme;
            o.DefaultChallengeScheme = TestAuthHandler.Scheme;
            o.DefaultForbidScheme = TestAuthHandler.Scheme;
        });
        return services;
    }

    public static HttpRequestMessage As(this HttpRequestMessage request, TestUser user)
    {
        request.Headers.Add(TestUser.Header, JsonSerializer.Serialize(user));
        return request;
    }
}
```

Replace `tests/Platform.IntegrationTests/Infrastructure/PlatformWebFactory.cs`:
```csharp
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace Platform.IntegrationTests.Infrastructure;

internal sealed class PlatformWebFactory(string appConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Platform", appConnectionString);
        builder.UseSetting("Oidc:Authority", "http://keycloak.invalid/realms/waslabid");
        builder.UseSetting("Oidc:ClientSecret", "unused-in-tests");
        builder.ConfigureTestServices(services => services.AddTestAuthentication());
    }

    public HttpClient ClientFor(string host) =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{host}"), AllowAutoRedirect = false });
}
```

`tests/Platform.IntegrationTests/Web/SameTenantTests.cs`:
```csharp
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit;

namespace Platform.IntegrationTests.Web;

[Collection(DatabaseCollection.Name)]
public class SameTenantTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Anonymous_request_is_challenged()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        var response = await client.GetAsync(new Uri("/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Member_of_the_hosts_organization_gets_the_page()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/").As(TestUser.AcmeAdmin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Token_for_another_tenant_is_forbidden_and_audited()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/").As(TestUser.BetaAdmin), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme);
        await using var audit = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuditDbContext>>().CreateDbContextAsync(Ct);
        var denied = await audit.Events.CountAsync(e => e.Action == "identity.cross_tenant_denied" && e.ActorId == "beta.admin", Ct);
        denied.ShouldBeGreaterThan(0);
    }
}
```

`tests/Platform.IntegrationTests/Infrastructure/KeycloakFixture.cs`:
```csharp
using System.Text.Json;
using Testcontainers.Keycloak;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>Keycloak 26.3 with the repository's realm file, the same image as infra/compose.</summary>
public sealed class KeycloakFixture : IAsyncLifetime
{
    private const string UserPassword = "Test-Passw0rd-1";

    private readonly KeycloakContainer _container = new KeycloakBuilder("quay.io/keycloak/keycloak:26.3")
        .WithRealm(RepoPaths.KeycloakRealm)
        .WithEnvironment("KC_FEATURES", "organization")
        .WithEnvironment("WASLABID_WEB_CLIENT_SECRET", "test-client-secret")
        .WithEnvironment("WASLABID_DEV_USER_PASSWORD", UserPassword)
        .Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    /// <summary>Signs in through the tests-only client and returns the id token.</summary>
    public async Task<string> SignInAsync(string username, CancellationToken cancellationToken)
    {
        using var http = new HttpClient();
        var tokenEndpoint = new Uri(new Uri(_container.GetBaseAddress()), "realms/waslabid/protocol/openid-connect/token");
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "waslabid-tests",
            ["grant_type"] = "password",
            ["username"] = username,
            ["password"] = UserPassword,
            ["scope"] = "openid organization",
        });
        using var response = await http.PostAsync(tokenEndpoint, form, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("id_token").GetString()!;
    }
}
```

`tests/Platform.IntegrationTests/Identity/KeycloakTokenTests.cs`:
```csharp
using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity;
using Platform.Modules.Tenancy.Contracts;

namespace Platform.IntegrationTests.Identity;

/// <summary>W-04: a login through Keycloak carries the organization, and the host maps it to the TenantContext.</summary>
[Collection(DatabaseCollection.Name)]
public class KeycloakTokenTests(DatabaseFixture db, KeycloakFixture keycloak) : IClassFixture<KeycloakFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Login_token_carries_the_organization_and_maps_to_the_hosts_tenant()
    {
        var token = new JsonWebToken(await keycloak.SignInAsync("acme.admin", Ct));
        var user = new ClaimsPrincipal(new ClaimsIdentity(token.Claims, "keycloak"));

        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(null);
        var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
        var acme = await directory.FindByHostAsync("acme.localhost", Ct);
        var beta = await directory.FindByHostAsync("beta.localhost", Ct);

        acme.ShouldNotBeNull();
        beta.ShouldNotBeNull();
        OrganizationClaims.BelongsTo(user, acme).ShouldBeTrue();
        OrganizationClaims.BelongsTo(user, beta).ShouldBeFalse();
        user.FindFirst("locale")?.Value.ShouldBe("ar");
    }
}
```

Run: `dotnet test tests/Platform.IntegrationTests --filter "SameTenantTests|KeycloakTokenTests"`
Expected: `SameTenantTests` fail (anonymous and cross-tenant both get 200); `KeycloakTokenTests` passes (it needs only the realm file and the Identity module).

- [ ] **Step 6: Wire authentication in the host**

Add to `src/Platform.Web/Platform.Web.csproj`:
```xml
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" />
  </ItemGroup>
```

Replace `src/Platform.Web/appsettings.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "Oidc": {
    "ClientId": "waslabid-web"
  }
}
```

Replace `src/Platform.Web/appsettings.Development.json`:
```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Information"
    }
  },
  "Oidc": {
    "Authority": "http://localhost:8080/realms/waslabid",
    "RequireHttpsMetadata": false
  }
}
```

`src/Platform.Web/Program.cs` becomes:
```csharp
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Platform.Modules.Audit;
using Platform.Modules.Identity;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Tenancy;
using Platform.Modules.Workflow;
using Platform.Shared;
using Platform.Web.Components;
using Platform.Web.Tenancy;

var builder = WebApplication.CreateBuilder(args);
var platformDb = builder.Configuration.GetConnectionString("Platform");
if (string.IsNullOrWhiteSpace(platformDb))
{
    throw new InvalidOperationException("Connection string 'Platform' is not configured. Set it with dotnet user-secrets (see README).");
}

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddPlatformShared();
builder.Services.AddAuditModule(platformDb);
builder.Services.AddTenancyModule(platformDb);
builder.Services.AddIdentityModule();
builder.Services.AddWorkflowModule(platformDb);
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CircuitHandler, TenantCircuitHandler>();

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
    })
    .AddCookie(options =>
    {
        options.Cookie.Name = "waslabid.auth";
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddOpenIdConnect(options =>
    {
        builder.Configuration.GetSection("Oidc").Bind(options);
        options.ResponseType = OpenIdConnectResponseType.Code;
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.SaveTokens = false;
        options.Scope.Clear();
        options.Scope.Add("openid");
        options.Scope.Add("profile");
        options.Scope.Add("organization");
        options.TokenValidationParameters.NameClaimType = IdentityClaims.Username;
    });
builder.Services.AddAuthorization(options => options.FallbackPolicy = IdentityModule.SameTenantPolicy);
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler();
    app.UseHsts();
}

app.UseMiddleware<TenantMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
```

Set the development client secret from `.env` without printing it:
```bash
dotnet user-secrets set "Oidc:ClientSecret" "$(grep '^WASLABID_WEB_CLIENT_SECRET=' infra/compose/.env | cut -d= -f2-)" --project src/Platform.Web
```

- [ ] **Step 7: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass. `/health` is anonymous; an unknown host is still a 404 before authentication; `Known_host_is_served` now asserts 401 (Step 5).

- [ ] **Step 8: Commit and move W-04 to Done**

In `docs/09-backlog.md`, row W-04, replace `| P0 | M | Backlog | W-01, W-02 |` with `| P0 | M | Done | W-01, W-02 |`.

```bash
git add infra/compose/keycloak infra/compose/docker-compose.yml infra/compose/.env.example src tests docs/09-backlog.md
git commit -m "Keycloak realm with organizations, OIDC login, same-tenant policy; W-04 done"
```

---

### Task 10: Platform.UI, Tailwind build, fonts, physical-utility lint (W-05)

**Files:**
- Create: `src/UI/Platform.UI/Platform.UI.csproj`, `src/UI/Platform.UI/_Imports.razor`, `src/UI/Platform.UI/Styles/app.css`, `src/UI/Platform.UI/wwwroot/fonts/*.woff2`, `src/UI/Platform.UI/wwwroot/fonts/OFL.txt`
- Modify: `src/Platform.Web/Platform.Web.csproj`, `src/Platform.Web/Components/App.razor`, `tests/Platform.UnitTests/Platform.UnitTests.csproj`
- Create: `tests/Platform.UnitTests/TestRepo.cs`, `tests/Platform.UnitTests/Styling/PhysicalUtilityLint.cs`, `tests/Platform.UnitTests/Styling/PhysicalUtilityLintTests.cs`, `tests/Platform.UnitTests/Styling/TailwindBuildTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Platform.UnitTests/TestRepo.cs`:
```csharp
namespace Platform.UnitTests;

internal static class TestRepo
{
    public static string Root { get; } = FindRoot();

    public static string Src => Path.Combine(Root, "src");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WaslaBid.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("WaslaBid.slnx was not found above the test output folder.");
    }
}
```

`tests/Platform.UnitTests/Styling/PhysicalUtilityLintTests.cs`:
```csharp
namespace Platform.UnitTests.Styling;

/// <summary>W-05 and docs/08 section 4: physical direction utilities are banned in favour of logical ones.</summary>
public class PhysicalUtilityLintTests
{
    [Fact]
    public void A_razor_file_with_ml_4_is_reported_with_file_and_line()
    {
        var folder = Directory.CreateTempSubdirectory("lint-");
        try
        {
            File.WriteAllLines(Path.Combine(folder.FullName, "Sample.razor"), ["<div class=\"ms-2\">", "<p class=\"ml-4 text-start\">x</p>", "</div>"]);

            var findings = PhysicalUtilityLint.Scan(folder.FullName).ToList();

            findings.ShouldBe(["Sample.razor:2: ml-4"]);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("pr-6")]
    [InlineData("text-right")]
    [InlineData("left-0")]
    [InlineData("rounded-l-md")]
    [InlineData("border-r-2")]
    [InlineData("rtl:mr-2")]
    public void Physical_utilities_are_detected(string utility) =>
        PhysicalUtilityLint.FindIn($"<div class=\"{utility}\"></div>").ShouldBe([utility.Split(':')[^1]]);

    [Theory]
    [InlineData("ms-4 me-2 ps-1 pe-3 start-0 end-0 text-start text-end rounded-s-md border-e-2")]
    [InlineData("px-6 mx-auto")]
    [InlineData("border-left-color copyright-notice html-body")]
    public void Logical_and_unrelated_text_is_not_reported(string text) =>
        PhysicalUtilityLint.FindIn(text).ShouldBeEmpty();

    [Fact]
    public void The_source_tree_has_no_physical_utilities() =>
        PhysicalUtilityLint.Scan(TestRepo.Src).ShouldBeEmpty();
}
```

`tests/Platform.UnitTests/Styling/TailwindBuildTests.cs`:
```csharp
namespace Platform.UnitTests.Styling;

public class TailwindBuildTests
{
    [Fact]
    public void Built_css_declares_the_primary_colour_token()
    {
        var css = Path.Combine(TestRepo.Src, "UI", "Platform.UI", "wwwroot", "css", "app.css");

        File.Exists(css).ShouldBeTrue("the Tailwind target in Platform.UI writes this file during the build");
        File.ReadAllText(css).ShouldContain("--color-primary:");
    }
}
```

Add to `tests/Platform.UnitTests/Platform.UnitTests.csproj` in the project-reference group, so building the tests builds the CSS first:
```xml
    <ProjectReference Include="..\..\src\UI\Platform.UI\Platform.UI.csproj" />
```

Run: `dotnet test tests/Platform.UnitTests --filter "PhysicalUtilityLintTests|TailwindBuildTests"`
Expected: build errors (`PhysicalUtilityLint` not found; `Platform.UI.csproj` missing).

- [ ] **Step 2: The lint** — `tests/Platform.UnitTests/Styling/PhysicalUtilityLint.cs`

```csharp
using System.Text.RegularExpressions;

namespace Platform.UnitTests.Styling;

/// <summary>
/// Finds physical direction utilities in markup, C# and CSS under a folder. A line containing
/// "lint-physical: allow" is skipped (for a rare legitimate case, with the reason on the same line).
/// </summary>
internal static partial class PhysicalUtilityLint
{
    private static readonly string[] Extensions = [".razor", ".cshtml", ".cs", ".css"];
    private static readonly string[] SkippedFolders = ["bin", "obj", "wwwroot", "node_modules"];

    public static IEnumerable<string> Scan(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar)
                .Any(part => SkippedFolders.Contains(part, StringComparer.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("lint-physical: allow", StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var utility in FindIn(lines[i]))
                {
                    yield return $"{Path.GetRelativePath(root, file).Replace('\\', '/')}:{i + 1}: {utility}";
                }
            }
        }
    }

    public static IReadOnlyList<string> FindIn(string text) =>
        PhysicalUtility().Matches(text).Select(m => m.Value).ToList();

    [GeneratedRegex(
        @"(?<![\w-])(?:(?:scroll-)?(?:ml|mr|pl|pr)-[\w\[\]./%-]+|(?:left|right)-[\w\[\]./%-]+|(?:border|rounded)-(?:l|r|tl|tr|bl|br)(?:-[\w\[\]./%-]+)?|text-(?:left|right)|float-(?:left|right)|clear-(?:left|right))(?![\w-])")]
    private static partial Regex PhysicalUtility();
}
```

- [ ] **Step 3: Create Platform.UI**

```bash
mkdir -p src/UI/Platform.UI/Styles src/UI/Platform.UI/wwwroot/fonts src/UI/Platform.UI/wwwroot/css src/UI/Platform.UI/Components
```

`src/UI/Platform.UI/Platform.UI.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TailwindVersion>v4.3.3</TailwindVersion>
    <TailwindDir>$(MSBuildThisFileDirectory)..\..\..\.tools\tailwind\$(TailwindVersion)\</TailwindDir>
  </PropertyGroup>
  <PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Windows'))">
    <TailwindAsset>tailwindcss-windows-x64.exe</TailwindAsset>
    <TailwindSha256>e0e260ce048014e9268f6237ff18f8ccf02cef521cbd0ae04e82c2cdf7aa3955</TailwindSha256>
  </PropertyGroup>
  <PropertyGroup Condition="$([MSBuild]::IsOSPlatform('Linux'))">
    <TailwindAsset>tailwindcss-linux-x64</TailwindAsset>
    <TailwindSha256>dc61b3ac6b8c9ca874c0cc4c57b2409791a64c5540404ca5f5367360babc313a</TailwindSha256>
  </PropertyGroup>
  <PropertyGroup>
    <TailwindExe>$(TailwindDir)$(TailwindAsset)</TailwindExe>
  </PropertyGroup>

  <ItemGroup>
    <SupportedPlatform Include="browser" />
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\Platform.Shared\Platform.Shared.csproj" />
  </ItemGroup>

  <!-- Downloads the pinned Tailwind standalone CLI once and verifies its SHA-256 (no Node.js in the build). -->
  <Target Name="TailwindDownload" Condition="!Exists('$(TailwindExe)')">
    <Error Condition="'$(TailwindAsset)' == ''" Text="Tailwind: no standalone binary is configured for this operating system." />
    <DownloadFile SourceUrl="https://github.com/tailwindlabs/tailwindcss/releases/download/$(TailwindVersion)/$(TailwindAsset)" DestinationFolder="$(TailwindDir)" />
    <GetFileHash Files="$(TailwindExe)" Algorithm="SHA256">
      <Output TaskParameter="Hash" PropertyName="TailwindActualSha256" />
    </GetFileHash>
    <Delete Files="$(TailwindExe)" Condition="'$(TailwindActualSha256)' != '$(TailwindSha256)'" />
    <Error Condition="'$(TailwindActualSha256)' != '$(TailwindSha256)'" Text="Tailwind: SHA-256 mismatch for $(TailwindAsset); the download was deleted." />
    <Exec Condition="!$([MSBuild]::IsOSPlatform('Windows'))" Command="chmod +x &quot;$(TailwindExe)&quot;" />
  </Target>

  <!-- Builds wwwroot/css/app.css from Styles/app.css before compilation and registers it as a static web asset. -->
  <Target Name="TailwindBuild" BeforeTargets="BeforeBuild" DependsOnTargets="TailwindDownload">
    <Exec Command="&quot;$(TailwindExe)&quot; --input Styles/app.css --output wwwroot/css/app.css --minify" WorkingDirectory="$(MSBuildProjectDirectory)" />
    <ItemGroup>
      <Content Remove="wwwroot/css/app.css" />
      <Content Include="wwwroot/css/app.css" />
    </ItemGroup>
  </Target>
</Project>
```

```bash
dotnet sln WaslaBid.slnx add src/UI/Platform.UI/Platform.UI.csproj
```

`src/UI/Platform.UI/_Imports.razor`:
```razor
@using Microsoft.AspNetCore.Components.Web
@using Platform.Shared.Tenancy
```

- [ ] **Step 4: Fonts**

```bash
BASE=https://cdn.jsdelivr.net/npm/@fontsource/ibm-plex-sans-arabic@5.2.8
for f in arabic-400 arabic-600 latin-400 latin-600; do
  curl -fsSL -o src/UI/Platform.UI/wwwroot/fonts/ibm-plex-sans-arabic-$f-normal.woff2 $BASE/files/ibm-plex-sans-arabic-$f-normal.woff2
done
curl -fsSL -o src/UI/Platform.UI/wwwroot/fonts/OFL.txt $BASE/LICENSE
ls src/UI/Platform.UI/wwwroot/fonts
```
Expected: four `.woff2` files and `OFL.txt`.

- [ ] **Step 5: Tokens** — `src/UI/Platform.UI/Styles/app.css`

```css
@import "tailwindcss";

/* Scan the host's components as well as this library's. */
@source "../../../Platform.Web";

/* docs/08 section 3. "static" emits every token even when no utility uses it yet, so tenant overrides always apply. */
@theme static {
  --color-primary: #1E4E79;
  --color-on-primary: #FFFFFF;
  --color-ink: #1F2933;
  --color-ink-muted: #52606D;
  --color-canvas: #F5F7FA;
  --color-surface: #FFFFFF;
  --color-line: #D9E2EC;
  --color-sealed: #5B3A8C;
  --color-success: #0F7B4F;
  --color-warning: #B45309;
  --color-danger: #B42318;
  --color-action: #1D5FD1;

  --font-sans: "IBM Plex Sans Arabic", system-ui, sans-serif;

  --text-display: 30px;
  --text-display--line-height: 1.2;
  --text-heading: 22px;
  --text-heading--line-height: 1.3;
  --text-subheading: 17px;
  --text-subheading--line-height: 1.4;
  --text-body: 15px;
  --text-body--line-height: 1.6;
  --text-small: 13px;
  --text-small--line-height: 1.5;
  --text-micro: 12px;
  --text-micro--line-height: 1.4;

  --radius-control: 6px;
  --radius-card: 10px;
}

@font-face {
  font-family: "IBM Plex Sans Arabic";
  font-style: normal;
  font-weight: 400;
  font-display: swap;
  src: url("../fonts/ibm-plex-sans-arabic-arabic-400-normal.woff2") format("woff2");
  unicode-range: U+0600-06FF, U+0750-077F, U+0870-088E, U+0890-0891, U+0898-08E1, U+08E3-08FF, U+200C-200E, U+2010-2011, U+204F, U+2E41, U+FB50-FDFF, U+FE70-FE74, U+FE76-FEFC;
}

@font-face {
  font-family: "IBM Plex Sans Arabic";
  font-style: normal;
  font-weight: 600;
  font-display: swap;
  src: url("../fonts/ibm-plex-sans-arabic-arabic-600-normal.woff2") format("woff2");
  unicode-range: U+0600-06FF, U+0750-077F, U+0870-088E, U+0890-0891, U+0898-08E1, U+08E3-08FF, U+200C-200E, U+2010-2011, U+204F, U+2E41, U+FB50-FDFF, U+FE70-FE74, U+FE76-FEFC;
}

@font-face {
  font-family: "IBM Plex Sans Arabic";
  font-style: normal;
  font-weight: 400;
  font-display: swap;
  src: url("../fonts/ibm-plex-sans-arabic-latin-400-normal.woff2") format("woff2");
  unicode-range: U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+0304, U+0308, U+0329, U+2000-206F, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD;
}

@font-face {
  font-family: "IBM Plex Sans Arabic";
  font-style: normal;
  font-weight: 600;
  font-display: swap;
  src: url("../fonts/ibm-plex-sans-arabic-latin-600-normal.woff2") format("woff2");
  unicode-range: U+0000-00FF, U+0131, U+0152-0153, U+02BB-02BC, U+02C6, U+02DA, U+02DC, U+0304, U+0308, U+0329, U+2000-206F, U+20AC, U+2122, U+2191, U+2193, U+2212, U+2215, U+FEFF, U+FFFD;
}

@layer base {
  html {
    font-family: var(--font-sans);
    font-size: var(--text-body);
    line-height: var(--text-body--line-height);
    color: var(--color-ink);
    background-color: var(--color-canvas);
  }

  table {
    font-variant-numeric: tabular-nums;
  }

  :focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
  }
}
```

- [ ] **Step 6: Use it from the host**

Add to `src/Platform.Web/Platform.Web.csproj` project references:
```xml
    <ProjectReference Include="..\UI\Platform.UI\Platform.UI.csproj" />
```

In `src/Platform.Web/Components/App.razor`, add inside `<head>` after `<base href="/" />`:
```razor
    <link rel="stylesheet" href="@Assets["_content/Platform.UI/css/app.css"]" />
```

- [ ] **Step 7: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: the first build downloads the Tailwind CLI into `.tools/`; all tests pass, including the lint tests and `Built_css_declares_the_primary_colour_token`.

- [ ] **Step 8: Commit and move W-05 to Done**

In `docs/09-backlog.md`, row W-05, replace `| P0 | S | Backlog | W-02 |` with `| P0 | S | Done | W-02 |`.

```bash
git add src/UI src/Platform.Web tests/Platform.UnitTests WaslaBid.slnx docs/09-backlog.md
git commit -m "Platform.UI: Tailwind standalone build, tokens, self-hosted fonts, physical-utility lint; W-05 done"
```

---

### Task 11: Localisation, direction, and tenant colour (W-07)

**Files:**
- Create: `src/UI/Platform.UI/SharedResource.cs`, `src/UI/Platform.UI/Resources/SharedResource.ar-SA.resx`, `src/UI/Platform.UI/Resources/SharedResource.en-US.resx`, `src/UI/Platform.UI/PlatformLocalization.cs`, `src/UI/Platform.UI/Components/TenantTheme.razor`
- Create: `src/Platform.Web/Localization/CultureEndpoints.cs`
- Modify: `src/Platform.Web/Program.cs`, `src/Platform.Web/Components/App.razor`, `src/Platform.Web/Components/_Imports.razor`, `src/Platform.Web/Components/Pages/Home.razor`
- Create: `tests/Platform.UnitTests/Localization/ResourceParityTests.cs`, `tests/Platform.IntegrationTests/Web/LocalizationTests.cs`

- [ ] **Step 1: Write the failing tests**

`tests/Platform.UnitTests/Localization/ResourceParityTests.cs`:
```csharp
using System.Xml.Linq;

namespace Platform.UnitTests.Localization;

/// <summary>docs/07 principle 5: Arabic and English ship together; no key exists in one language only.</summary>
public class ResourceParityTests
{
    private static readonly string Resources = Path.Combine(TestRepo.Src, "UI", "Platform.UI", "Resources");

    [Fact]
    public void Arabic_and_English_resources_have_the_same_keys_and_no_empty_values()
    {
        var arabic = Read("SharedResource.ar-SA.resx");
        var english = Read("SharedResource.en-US.resx");

        arabic.Keys.Except(english.Keys).ShouldBeEmpty("keys only in Arabic");
        english.Keys.Except(arabic.Keys).ShouldBeEmpty("keys only in English");
        arabic.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ShouldBeEmpty("empty Arabic values");
        english.Where(p => string.IsNullOrWhiteSpace(p.Value)).Select(p => p.Key).ShouldBeEmpty("empty English values");
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(Path.Combine(Resources, file)).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? string.Empty);
}
```

`tests/Platform.IntegrationTests/Web/LocalizationTests.cs`:
```csharp
using System.Net;
using Platform.IntegrationTests.Infrastructure;

namespace Platform.IntegrationTests.Web;

/// <summary>W-07 acceptance and the culture order: cookie, locale claim, tenant default, ar-SA.</summary>
[Collection(DatabaseCollection.Name)]
public class LocalizationTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Arabic_profile_renders_an_arabic_right_to_left_page()
    {
        var html = await GetHomeAsync("acme.localhost", TestUser.AcmeAdmin);

        html.ShouldContain("<html lang=\"ar\" dir=\"rtl\">");
        html.ShouldContain("مرحباً بك في Acme Contracting");
        html.ShouldNotContain("Home.Welcome");
    }

    [Fact]
    public async Task English_profile_renders_an_english_left_to_right_page()
    {
        var html = await GetHomeAsync("beta.localhost", TestUser.BetaAdmin);

        html.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
        html.ShouldContain("Welcome to Beta Industries");
    }

    [Fact]
    public async Task The_culture_cookie_overrides_the_profile()
    {
        var html = await GetHomeAsync("acme.localhost", TestUser.AcmeAdmin, cookie: ".AspNetCore.Culture=c%3Den-US%7Cuic%3Den-US");

        html.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
    }

    [Fact]
    public async Task Without_a_profile_locale_the_tenant_default_applies()
    {
        var html = await GetHomeAsync("beta.localhost", TestUser.BetaAdmin with { Locale = null });

        html.ShouldContain("<html lang=\"en\" dir=\"ltr\">");
    }

    [Fact]
    public async Task The_tenant_colour_overrides_the_primary_token()
    {
        var html = await GetHomeAsync("acme.localhost", TestUser.AcmeAdmin);

        html.ShouldContain("--color-primary: #0F766E");
    }

    [Fact]
    public async Task Culture_switch_sets_the_cookie_and_refuses_an_external_return_url()
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor("acme.localhost");

        using var response = await client.GetAsync(new Uri("/culture/set?culture=en-US&returnUrl=https://evil.example/", UriKind.Relative), Ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/");
        response.Headers.GetValues("Set-Cookie").ShouldContain(c => c.StartsWith(".AspNetCore.Culture=c%3Den-US", StringComparison.Ordinal));
    }

    private async Task<string> GetHomeAsync(string host, TestUser user, string? cookie = null)
    {
        await using var factory = new PlatformWebFactory(db.AppConnectionString);
        using var client = factory.ClientFor(host);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/").As(user);
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        // Razor may encode non-ASCII characters as entities; decode so assertions read as the user sees the page.
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(Ct));
    }
}
```

Run: `dotnet test WaslaBid.slnx --filter "ResourceParityTests|LocalizationTests"`
Expected: `ResourceParityTests` fails (files missing); `LocalizationTests` fail (`lang="ar"` is hard-coded and there is no welcome text).

- [ ] **Step 2: Resources**

`src/UI/Platform.UI/SharedResource.cs`:
```csharp
namespace Platform.UI;

/// <summary>Marker for IStringLocalizer&lt;SharedResource&gt;; strings live in Resources/SharedResource.{culture}.resx.</summary>
public sealed class SharedResource;
```

`src/UI/Platform.UI/Resources/SharedResource.ar-SA.resx`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="Home.Title" xml:space="preserve"><value>الصفحة الرئيسية</value></data>
  <data name="Home.Welcome" xml:space="preserve"><value>مرحباً بك في {0}</value></data>
  <data name="Home.SignedInAs" xml:space="preserve"><value>دخلت باسم {0}</value></data>
  <data name="Language.Other" xml:space="preserve"><value>English</value></data>
  <data name="Language.OtherCulture" xml:space="preserve"><value>en-US</value></data>
</root>
```

`src/UI/Platform.UI/Resources/SharedResource.en-US.resx`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<root>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <data name="Home.Title" xml:space="preserve"><value>Home</value></data>
  <data name="Home.Welcome" xml:space="preserve"><value>Welcome to {0}</value></data>
  <data name="Home.SignedInAs" xml:space="preserve"><value>Signed in as {0}</value></data>
  <data name="Language.Other" xml:space="preserve"><value>العربية</value></data>
  <data name="Language.OtherCulture" xml:space="preserve"><value>ar-SA</value></data>
</root>
```

(The Arabic copy is a first draft; a native speaker reviews it before the pilot, docs/08 section 9.)

- [ ] **Step 3: Localisation setup** — `src/UI/Platform.UI/PlatformLocalization.cs`

```csharp
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.DependencyInjection;
using Platform.Shared.Tenancy;

namespace Platform.UI;

public static class PlatformLocalization
{
    public const string DefaultCulture = "ar-SA";

    public static IReadOnlyList<string> SupportedCultures { get; } = ["ar-SA", "en-US"];

    /// <summary>Culture order (spec section 7): the culture cookie, the "locale" claim, the tenant default, then ar-SA.</summary>
    public static IServiceCollection AddPlatformLocalization(this IServiceCollection services)
    {
        services.AddLocalization(o => o.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(o =>
        {
            o.SetDefaultCulture(DefaultCulture)
                .AddSupportedCultures([.. SupportedCultures])
                .AddSupportedUICultures([.. SupportedCultures]);
            o.RequestCultureProviders =
            [
                new CookieRequestCultureProvider(),
                new LocaleClaimCultureProvider(),
                new TenantDefaultCultureProvider(),
            ];
        });
        return services;
    }

    /// <summary>Maps "ar", "ar-SA", "en", "en-US" in any case to a supported culture; anything else to null.</summary>
    public static string? Normalize(string? culture)
    {
        if (culture is null)
        {
            return null;
        }

        if (culture.Equals("ar", StringComparison.OrdinalIgnoreCase) || culture.Equals("ar-SA", StringComparison.OrdinalIgnoreCase))
        {
            return "ar-SA";
        }

        if (culture.Equals("en", StringComparison.OrdinalIgnoreCase) || culture.Equals("en-US", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return null;
    }

    private static Task<ProviderCultureResult?> ResultFor(string? culture) =>
        Task.FromResult(Normalize(culture) is { } c ? new ProviderCultureResult(c) : null);

    /// <summary>Keycloak's "locale" claim, from the user's profile.</summary>
    private sealed class LocaleClaimCultureProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext) =>
            ResultFor(httpContext.User.FindFirst("locale")?.Value);
    }

    private sealed class TenantDefaultCultureProvider : RequestCultureProvider
    {
        public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext) =>
            ResultFor(httpContext.RequestServices.GetService<ITenantAccessor>()?.Current?.DefaultCulture);
    }
}
```

`src/UI/Platform.UI/Components/TenantTheme.razor`:
```razor
@* Overrides the primary token with the tenant's colour (F-02). A database check constraint validates the colour. *@
@inject ITenantAccessor Tenants

@if (Tenants.Current is { } tenant)
{
    <style>:root { --color-primary: @tenant.Branding.PrimaryColor; }</style>
}
```

- [ ] **Step 4: Culture switch endpoint** — `src/Platform.Web/Localization/CultureEndpoints.cs`

```csharp
using Microsoft.AspNetCore.Localization;
using Platform.UI;

namespace Platform.Web.Localization;

internal static class CultureEndpoints
{
    public static IEndpointRouteBuilder MapCultureEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/culture/set", (string culture, string? returnUrl, HttpContext context) =>
        {
            var normalized = PlatformLocalization.Normalize(culture);
            if (normalized is null)
            {
                return Results.BadRequest();
            }

            context.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(normalized)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    HttpOnly = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = context.Request.IsHttps,
                });
            return Results.Redirect(IsLocal(returnUrl) ? returnUrl! : "/");
        }).AllowAnonymous();
        return app;
    }

    // Same-site paths only: "/x" yes; "//evil" and "/\evil" no (open-redirect protection).
    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
```

- [ ] **Step 5: Host and components**

In `src/Platform.Web/Program.cs`:
- add `using Platform.UI;` and `using Platform.Web.Localization;`;
- after `builder.Services.AddCascadingAuthenticationState();` add `builder.Services.AddPlatformLocalization();`;
- after `app.UseAuthentication();` add `app.UseRequestLocalization();`;
- after `app.MapHealthChecks("/health").AllowAnonymous();` add `app.MapCultureEndpoints();`.

Add to `src/Platform.Web/Components/_Imports.razor`:
```razor
@using Microsoft.Extensions.Localization
@using Platform.UI
@using Platform.UI.Components
```

Replace `src/Platform.Web/Components/App.razor`:
```razor
@using System.Globalization

<!DOCTYPE html>
<html lang="@Culture.TwoLetterISOLanguageName" dir="@(Culture.TextInfo.IsRightToLeft ? "rtl" : "ltr")">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <base href="/" />
    <link rel="stylesheet" href="@Assets["_content/Platform.UI/css/app.css"]" />
    <TenantTheme />
    <HeadOutlet />
</head>
<body>
    <Routes />
    <script src="@Assets["_framework/blazor.web.js"]"></script>
</body>
</html>

@code {
    private static CultureInfo Culture => CultureInfo.CurrentUICulture;
}
```

Replace `src/Platform.Web/Components/Pages/Home.razor`:
```razor
@page "/"
@inject IStringLocalizer<SharedResource> T
@inject ITenantAccessor Tenants

<PageTitle>@T["Home.Title"]</PageTitle>

<header class="bg-primary text-on-primary px-6 py-4">
    <p class="text-heading font-semibold">@PortalName</p>
</header>

<main class="mx-auto max-w-3xl px-6 py-8">
    <h1 class="text-display font-semibold">@T["Home.Welcome", PortalName]</h1>
    <AuthorizeView>
        <Authorized>
            <p class="mt-4 text-ink-muted">@T["Home.SignedInAs", context.User.Identity?.Name ?? string.Empty]</p>
        </Authorized>
    </AuthorizeView>
    <p class="mt-6">
        <a class="text-action underline" href="@($"/culture/set?culture={T["Language.OtherCulture"]}&returnUrl=/")">@T["Language.Other"]</a>
    </p>
</main>

@code {
    private string PortalName => Tenants.Current?.Branding.PortalName ?? string.Empty;
}
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass, including `ResourceParityTests` and six `LocalizationTests`; the lint still finds nothing in `src/`.

- [ ] **Step 7: Commit and move W-07 to Done**

In `docs/09-backlog.md`, row W-07, replace `| P0 | S | Backlog | W-02 |` with `| P0 | S | Done | W-02 |`.

```bash
git add src tests docs/09-backlog.md
git commit -m "Localisation: Arabic and English resources, culture order, lang and dir, tenant colour; W-07 done"
```

---
### Task 12: Workflow contracts and the definition validator (F-56)

**Files:**
- Create: `src/Modules/Workflow/Platform.Modules.Workflow.Contracts/WorkflowTypes.cs`, `StepDefinition.cs`, `WorkflowStatus.cs`, `IWorkflowDefinitions.cs`, `IWorkflowService.cs`, `DefaultTemplate.cs`
- Create: `src/Modules/Workflow/Platform.Modules.Workflow/DefinitionValidator.cs`
- Create: `tests/Platform.UnitTests/Workflow/DefinitionValidatorTests.cs`

- [ ] **Step 1: Write the failing tests** — `tests/Platform.UnitTests/Workflow/DefinitionValidatorTests.cs`

```csharp
using Platform.Modules.Workflow;
using Platform.Modules.Workflow.Contracts;

namespace Platform.UnitTests.Workflow;

public class DefinitionValidatorTests
{
    private static StepDefinition Human(Stage stage, string role, StepRule rule = StepRule.AnyOf) => new(stage, "Department", rule, [role]);

    private static StepDefinition System(Stage stage) => new(stage, "System", StepRule.AnyOf, []);

    [Fact]
    public void The_default_template_is_valid() =>
        DefinitionValidator.Validate(DefaultTemplate.Steps).ShouldBeEmpty();

    [Fact]
    public void Financial_opening_before_score_locking_is_rejected_with_the_rule_named()
    {
        var errors = DefinitionValidator.Validate(
            [Human(Stage.Screening, "contracts"), System(Stage.FinancialOpening), System(Stage.LockScores), Human(Stage.Approval, "finance")]);

        errors.Select(e => e.Code).ShouldBe(["workflow.opening_before_lock"]);
        errors[0].Message.ShouldBe("Financial opening must follow score locking (F-30).");
    }

    [Fact]
    public void Removing_a_fixed_point_is_rejected()
    {
        var errors = DefinitionValidator.Validate([Human(Stage.Screening, "contracts"), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldContain("workflow.lock_missing");
    }

    [Fact]
    public void A_human_stage_out_of_system_order_is_rejected()
    {
        var errors = DefinitionValidator.Validate(
            [Human(Stage.Approval, "finance"), Human(Stage.Screening, "contracts"), System(Stage.LockScores), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldContain("workflow.stage_order");
    }

    [Fact]
    public void A_human_step_without_actors_is_rejected()
    {
        var errors = DefinitionValidator.Validate(
            [new StepDefinition(Stage.Screening, "Contracts", StepRule.AnyOf, []), System(Stage.LockScores), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldBe(["workflow.step_without_actors"]);
    }

    [Fact]
    public void A_system_step_with_actors_is_rejected()
    {
        var errors = DefinitionValidator.Validate(
            [Human(Stage.Screening, "contracts"), new StepDefinition(Stage.LockScores, "System", StepRule.AnyOf, ["someone"]), System(Stage.FinancialOpening)]);

        errors.Select(e => e.Code).ShouldBe(["workflow.system_step_actors"]);
    }

    [Fact]
    public void Check_combines_every_broken_rule_into_one_error()
    {
        var error = DefinitionValidator.Check([]);

        error.ShouldNotBeNull();
        error.Code.ShouldBe("workflow.empty");
    }
}
```

Add the Workflow contracts reference to the unit test project only if the build asks for it (it arrives transitively through `Platform.Modules.Workflow`).

Run: `dotnet test tests/Platform.UnitTests --filter DefinitionValidatorTests`
Expected: build error, `StepDefinition` not found.

- [ ] **Step 2: Contracts**

`src/Modules/Workflow/Platform.Modules.Workflow.Contracts/WorkflowTypes.cs`:
```csharp
namespace Platform.Modules.Workflow.Contracts;

/// <summary>Stages in system order (ADR-0003 point 3). A definition decides who acts; it cannot reorder stages.</summary>
public enum Stage
{
    Screening = 1,
    TechnicalScoring = 2,
    LockScores = 3,
    FinancialOpening = 4,
    Approval = 5,
    Award = 6,
}

public enum StepRule
{
    AnyOf,
    AllOf,
}

public enum Decision
{
    Approve,
    Reject,
}

public enum WorkflowState
{
    Running,
    Completed,
    Rejected,
}

public enum StepState
{
    Pending,
    Open,
    Approved,
    Rejected,
    Done,
}

public static class StageExtensions
{
    /// <summary>Fixed points completed by the owning domain module, never by a person's decision.</summary>
    public static bool IsSystem(this Stage stage) => stage is Stage.LockScores or Stage.FinancialOpening;
}
```

`src/Modules/Workflow/Platform.Modules.Workflow.Contracts/StepDefinition.cs`:
```csharp
namespace Platform.Modules.Workflow.Contracts;

/// <summary>One step of a tenant's definition. Threshold is stored now and evaluated by F-09 later.</summary>
public sealed record StepDefinition(
    Stage Stage,
    string Department,
    StepRule Rule,
    IReadOnlyList<string> ActorRoles,
    decimal? Threshold = null);

/// <summary>Create (DefinitionId null) or replace a definition. Replacing increments its version.</summary>
public sealed record SaveDefinition(Guid? DefinitionId, string Name, bool IsDefault, IReadOnlyList<StepDefinition> Steps);
```

`src/Modules/Workflow/Platform.Modules.Workflow.Contracts/WorkflowStatus.cs`:
```csharp
namespace Platform.Modules.Workflow.Contracts;

public sealed record StepStatus(
    int Position,
    Stage Stage,
    string Department,
    StepRule Rule,
    StepState State,
    IReadOnlyList<string> AssignedUsers,
    IReadOnlyList<string> ApprovedBy);

public sealed record WorkflowStatus(
    Guid TenderId,
    WorkflowState State,
    int? CurrentPosition,
    Stage? CurrentStage,
    IReadOnlyList<string> PendingUsers,
    IReadOnlyList<StepStatus> Steps);
```

`src/Modules/Workflow/Platform.Modules.Workflow.Contracts/IWorkflowDefinitions.cs`:
```csharp
using Platform.Shared.Results;

namespace Platform.Modules.Workflow.Contracts;

/// <summary>A tenant's workflow definitions. Changing one never affects a running tender (ADR-0003 point 2).</summary>
public interface IWorkflowDefinitions
{
    Task<Result<Guid>> SaveAsync(SaveDefinition command, CancellationToken cancellationToken = default);

    Task<Guid?> FindDefaultAsync(CancellationToken cancellationToken = default);
}
```

`src/Modules/Workflow/Platform.Modules.Workflow.Contracts/IWorkflowService.cs`:
```csharp
using Platform.Shared.Results;

namespace Platform.Modules.Workflow.Contracts;

/// <summary>The executor (ADR-0004): runs a tender's snapshot of a definition. It never reads tender data.</summary>
public interface IWorkflowService
{
    /// <summary>Snapshots the definition onto the tender, assigns users per role, and opens the first step.</summary>
    Task<Result<WorkflowStatus>> StartAsync(
        Guid tenderId,
        Guid definitionId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> assignmentsByRole,
        CancellationToken cancellationToken = default);

    /// <summary>A decision by an assigned user on the open human step. Others are refused and audited.</summary>
    Task<Result<WorkflowStatus>> DecideAsync(Guid tenderId, string userId, Decision decision, CancellationToken cancellationToken = default);

    /// <summary>Called by the owning domain module when scores are locked or envelopes opened.</summary>
    Task<Result<WorkflowStatus>> CompleteSystemStageAsync(Guid tenderId, Stage stage, CancellationToken cancellationToken = default);

    Task<Result<WorkflowStatus>> GetStatusAsync(Guid tenderId, CancellationToken cancellationToken = default);
}
```

`src/Modules/Workflow/Platform.Modules.Workflow.Contracts/DefaultTemplate.cs`:
```csharp
namespace Platform.Modules.Workflow.Contracts;

/// <summary>The pilot's default chain (docs/05 row 16): contracts, technical evaluators, locking, opening, finance.</summary>
public static class DefaultTemplate
{
    public const string Name = "Default approval chain";

    public static IReadOnlyList<StepDefinition> Steps { get; } =
    [
        new(Stage.Screening, "Contracts", StepRule.AnyOf, ["contracts"]),
        new(Stage.TechnicalScoring, "Technical committee", StepRule.AllOf, ["evaluator"]),
        new(Stage.LockScores, "System", StepRule.AnyOf, []),
        new(Stage.FinancialOpening, "System", StepRule.AnyOf, []),
        new(Stage.Approval, "Finance", StepRule.AnyOf, ["finance"]),
    ];
}
```

- [ ] **Step 3: Validator** — `src/Modules/Workflow/Platform.Modules.Workflow/DefinitionValidator.cs`

```csharp
using Platform.Modules.Workflow.Contracts;
using Platform.Shared.Results;

namespace Platform.Modules.Workflow;

/// <summary>Checks a definition on save and on start; returns every broken rule, each named (F-56 acceptance).</summary>
internal static class DefinitionValidator
{
    public static IReadOnlyList<Error> Validate(IReadOnlyList<StepDefinition> steps)
    {
        var errors = new List<Error>();
        if (steps.Count == 0)
        {
            errors.Add(Error.Validation("workflow.empty", "A workflow needs at least one step."));
            return errors;
        }

        var lockAt = IndexOf(steps, Stage.LockScores);
        var openAt = IndexOf(steps, Stage.FinancialOpening);
        if (lockAt < 0)
        {
            errors.Add(Error.Validation("workflow.lock_missing", "Score locking is a fixed point and cannot be removed (F-30)."));
        }

        if (openAt < 0)
        {
            errors.Add(Error.Validation("workflow.opening_missing", "Financial opening is a fixed point and cannot be removed (F-23)."));
        }

        if (steps.Count(s => s.Stage == Stage.LockScores) > 1 || steps.Count(s => s.Stage == Stage.FinancialOpening) > 1)
        {
            errors.Add(Error.Validation("workflow.fixed_point_repeated", "Score locking and financial opening each appear exactly once."));
        }

        if (lockAt >= 0 && openAt >= 0 && openAt < lockAt)
        {
            errors.Add(Error.Validation("workflow.opening_before_lock", "Financial opening must follow score locking (F-30)."));
        }

        for (var i = 1; i < steps.Count; i++)
        {
            var previous = steps[i - 1].Stage;
            var current = steps[i].Stage;
            var bothFixed = previous.IsSystem() && current.IsSystem();
            if (current < previous && !bothFixed)
            {
                errors.Add(Error.Validation("workflow.stage_order", $"Step {i + 1} ({current}) cannot come after {previous}; stages follow the system order."));
            }
        }

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (string.IsNullOrWhiteSpace(step.Department))
            {
                errors.Add(Error.Validation("workflow.department_missing", $"Step {i + 1} needs a department."));
            }

            if (step.Stage.IsSystem() && step.ActorRoles.Count > 0)
            {
                errors.Add(Error.Validation("workflow.system_step_actors", $"Step {i + 1} ({step.Stage}) is run by the system and cannot have actors."));
            }

            if (!step.Stage.IsSystem() && step.ActorRoles.Count == 0)
            {
                errors.Add(Error.Validation("workflow.step_without_actors", $"Step {i + 1} ({step.Department}) needs at least one actor role."));
            }
        }

        return errors;
    }

    /// <summary>Null when valid; otherwise one validation error carrying the first code and every message.</summary>
    public static Error? Check(IReadOnlyList<StepDefinition> steps)
    {
        var errors = Validate(steps);
        return errors.Count == 0 ? null : Error.Validation(errors[0].Code, string.Join(" ", errors.Select(e => e.Message)));
    }

    private static int IndexOf(IReadOnlyList<StepDefinition> steps, Stage stage)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i].Stage == stage)
            {
                return i;
            }
        }

        return -1;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test tests/Platform.UnitTests`
Expected: all pass, including seven `DefinitionValidatorTests`. The architecture test still passes: `DefinitionValidator` is internal.

- [ ] **Step 5: Commit**

```bash
git add src/Modules/Workflow tests/Platform.UnitTests
git commit -m "Workflow contracts, default template and definition validator"
```

---

### Task 13: Workflow persistence and definitions

**Files:**
- Create: `src/Modules/Workflow/Platform.Modules.Workflow/Migrations/0001_workflow.sql`, `Persistence/Rows.cs`, `Persistence/WorkflowDbContext.cs`, `WorkflowDefinitions.cs`
- Modify: `src/Modules/Workflow/Platform.Modules.Workflow/Platform.Modules.Workflow.csproj`, `src/Modules/Workflow/Platform.Modules.Workflow/WorkflowModule.cs`, `src/Platform.Migrator/MigrationRunner.cs`
- Create: `tests/Platform.IntegrationTests/Workflow/WorkflowDefinitionsTests.cs`

- [ ] **Step 1: Write the failing tests** — `tests/Platform.IntegrationTests/Workflow/WorkflowDefinitionsTests.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Workflow;

[Collection(DatabaseCollection.Name)]
public class WorkflowDefinitionsTests(DatabaseFixture db) : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ModuleHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ModuleHost(db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Saving_stores_the_steps_in_order()
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);
        var id = (await Definitions(scope).SaveAsync(new SaveDefinition(null, "Chain A", false, DefaultTemplate.Steps), Ct)).Value;

        await using var context = await Context(scope);
        var stages = await context.DefinitionSteps.Where(s => s.DefinitionId == id).OrderBy(s => s.Position).Select(s => s.Stage).ToListAsync(Ct);

        stages.ShouldBe(DefaultTemplate.Steps.Select(s => s.Stage));
    }

    [Fact]
    public async Task Saving_again_increments_the_version_and_replaces_the_steps()
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);
        var definitions = Definitions(scope);
        var id = (await definitions.SaveAsync(new SaveDefinition(null, "Chain B", false, DefaultTemplate.Steps), Ct)).Value;
        List<StepDefinition> edited = [DefaultTemplate.Steps[0], .. DefaultTemplate.Steps.Skip(2)];

        var again = await definitions.SaveAsync(new SaveDefinition(id, "Chain B", false, edited), Ct);

        again.Value.ShouldBe(id);
        await using var context = await Context(scope);
        var row = await context.Definitions.Include(d => d.Steps).SingleAsync(d => d.Id == id, Ct);
        row.Version.ShouldBe(2);
        row.Steps.Count.ShouldBe(edited.Count);
    }

    [Fact]
    public async Task A_tenant_has_at_most_one_default()
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);
        var definitions = Definitions(scope);
        await definitions.SaveAsync(new SaveDefinition(null, "Default 1", true, DefaultTemplate.Steps), Ct);
        var second = (await definitions.SaveAsync(new SaveDefinition(null, "Default 2", true, DefaultTemplate.Steps), Ct)).Value;

        (await definitions.FindDefaultAsync(Ct)).ShouldBe(second);
        await using var context = await Context(scope);
        (await context.Definitions.CountAsync(d => d.IsDefault, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_definition_with_financial_opening_before_locking_is_rejected_with_the_rule_named()
    {
        await using var scope = _host.ScopeFor(TestTenants.Beta);
        List<StepDefinition> broken = [DefaultTemplate.Steps[0], DefaultTemplate.Steps[1], DefaultTemplate.Steps[3], DefaultTemplate.Steps[2], DefaultTemplate.Steps[4]];

        var result = await Definitions(scope).SaveAsync(new SaveDefinition(null, "Broken", false, broken), Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Validation);
        result.Error.Message.ShouldContain("Financial opening must follow score locking (F-30).");
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_definition()
    {
        Guid id;
        await using (var beta = _host.ScopeFor(TestTenants.Beta))
        {
            id = (await Definitions(beta).SaveAsync(new SaveDefinition(null, "Private", false, DefaultTemplate.Steps), Ct)).Value;
        }

        await using var acme = _host.ScopeFor(TestTenants.Acme);
        await using var context = await Context(acme);
        (await context.Definitions.IgnoreQueryFilters().AnyAsync(d => d.Id == id, Ct)).ShouldBeFalse();
    }

    private static IWorkflowDefinitions Definitions(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWorkflowDefinitions>();

    private static Task<WorkflowDbContext> Context(AsyncServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkflowDbContext>>().CreateDbContextAsync(Ct);
}
```

Run: `dotnet test tests/Platform.IntegrationTests --filter WorkflowDefinitionsTests`
Expected: build error, `Platform.Modules.Workflow.Persistence` does not exist.

- [ ] **Step 2: Project reference to Audit contracts**

In `src/Modules/Workflow/Platform.Modules.Workflow/Platform.Modules.Workflow.csproj`, add to the project-reference group:
```xml
    <ProjectReference Include="..\..\Audit\Platform.Modules.Audit.Contracts\Platform.Modules.Audit.Contracts.csproj" />
```

- [ ] **Step 3: Schema** — `src/Modules/Workflow/Platform.Modules.Workflow/Migrations/0001_workflow.sql`

```sql
create schema if not exists workflow;

create table workflow.workflow_definition (
    id         uuid        primary key,
    tenant_id  uuid        not null,
    name       text        not null,
    version    integer     not null,
    is_default boolean     not null,
    created_at timestamptz not null
);
create index ix_workflow_definition_tenant on workflow.workflow_definition (tenant_id);
create unique index ux_workflow_definition_one_default on workflow.workflow_definition (tenant_id) where is_default;

create table workflow.workflow_step (
    id            uuid          primary key,
    tenant_id     uuid          not null,
    definition_id uuid          not null references workflow.workflow_definition (id) on delete cascade,
    position      integer       not null,
    stage         text          not null,
    department    text          not null,
    rule          text          not null,
    actor_roles   text[]        not null,
    threshold     numeric(18,2) null,
    unique (definition_id, position)
);
create index ix_workflow_step_tenant on workflow.workflow_step (tenant_id, definition_id);

create table workflow.tender_workflow (
    id               uuid        primary key,
    tenant_id        uuid        not null,
    tender_id        uuid        not null,
    definition_id    uuid        not null,
    snapshot         jsonb       not null,
    snapshot_version integer     not null,
    state            text        not null,
    created_at       timestamptz not null,
    updated_at       timestamptz not null,
    unique (tenant_id, tender_id)
);

create table workflow.tender_workflow_step (
    id                 uuid    primary key,
    tenant_id          uuid    not null,
    tender_workflow_id uuid    not null references workflow.tender_workflow (id) on delete cascade,
    position           integer not null,
    stage              text    not null,
    department         text    not null,
    rule               text    not null,
    assigned_users     text[]  not null,
    status             text    not null,
    unique (tender_workflow_id, position)
);
create index ix_tender_workflow_step_tenant on workflow.tender_workflow_step (tenant_id, tender_workflow_id);

create table workflow.step_decision (
    id         uuid        primary key,
    tenant_id  uuid        not null,
    step_id    uuid        not null references workflow.tender_workflow_step (id) on delete cascade,
    user_id    text        not null,
    decision   text        not null,
    decided_at timestamptz not null,
    unique (step_id, user_id)
);
create index ix_step_decision_tenant on workflow.step_decision (tenant_id, step_id);

select platform.enable_tenant_rls('workflow', 'workflow_definition');
select platform.enable_tenant_rls('workflow', 'workflow_step');
select platform.enable_tenant_rls('workflow', 'tender_workflow');
select platform.enable_tenant_rls('workflow', 'tender_workflow_step');
select platform.enable_tenant_rls('workflow', 'step_decision');

grant usage on schema workflow to erp_app;
grant select, insert, update, delete on all tables in schema workflow to erp_app;
```

Delete `src/Modules/Workflow/Platform.Modules.Workflow/Migrations/.gitkeep`.

- [ ] **Step 4: Rows and context**

`src/Modules/Workflow/Platform.Modules.Workflow/Persistence/Rows.cs`:
```csharp
using Platform.Modules.Workflow.Contracts;

namespace Platform.Modules.Workflow.Persistence;

internal sealed class DefinitionRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Version { get; set; }

    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public List<DefinitionStepRow> Steps { get; set; } = [];
}

internal sealed class DefinitionStepRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DefinitionId { get; set; }

    public int Position { get; set; }

    public Stage Stage { get; set; }

    public string Department { get; set; } = string.Empty;

    public StepRule Rule { get; set; }

    public List<string> ActorRoles { get; set; } = [];

    public decimal? Threshold { get; set; }
}

internal sealed class TenderWorkflowRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TenderId { get; set; }

    public Guid DefinitionId { get; set; }

    public string Snapshot { get; set; } = "{}";

    public int SnapshotVersion { get; set; }

    public WorkflowState State { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>PostgreSQL xmin, the optimistic concurrency token.</summary>
    public uint Version { get; set; }

    public List<TenderStepRow> Steps { get; set; } = [];
}

internal sealed class TenderStepRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid TenderWorkflowId { get; set; }

    public int Position { get; set; }

    public Stage Stage { get; set; }

    public string Department { get; set; } = string.Empty;

    public StepRule Rule { get; set; }

    public List<string> AssignedUsers { get; set; } = [];

    public StepState Status { get; set; }

    public List<StepDecisionRow> Decisions { get; set; } = [];
}

internal sealed class StepDecisionRow
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid StepId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public Decision Decision { get; set; }

    public DateTimeOffset DecidedAt { get; set; }
}
```

`src/Modules/Workflow/Platform.Modules.Workflow/Persistence/WorkflowDbContext.cs`:
```csharp
using Microsoft.EntityFrameworkCore;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Workflow.Persistence;

internal sealed class WorkflowDbContext(DbContextOptions<WorkflowDbContext> options, ITenantAccessor tenants) : DbContext(options)
{
    public DbSet<DefinitionRow> Definitions => Set<DefinitionRow>();

    public DbSet<DefinitionStepRow> DefinitionSteps => Set<DefinitionStepRow>();

    public DbSet<TenderWorkflowRow> TenderWorkflows => Set<TenderWorkflowRow>();

    public DbSet<TenderStepRow> TenderSteps => Set<TenderStepRow>();

    public DbSet<StepDecisionRow> Decisions => Set<StepDecisionRow>();

    // Query filters mirror the RLS policies for readable failures; PostgreSQL is the enforcing layer.
    private Guid? CurrentTenantId => tenants.Current?.TenantId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("workflow");

        modelBuilder.Entity<DefinitionRow>(e =>
        {
            e.ToTable("workflow_definition");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(s => s.DefinitionId);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<DefinitionStepRow>(e =>
        {
            e.ToTable("workflow_step");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Stage).HasConversion<string>();
            e.Property(x => x.Rule).HasConversion<string>();
            e.Property(x => x.Threshold).HasPrecision(18, 2);
            e.HasIndex(x => new { x.DefinitionId, x.Position }).IsUnique();
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TenderWorkflowRow>(e =>
        {
            e.ToTable("tender_workflow");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Snapshot).HasColumnType("jsonb");
            e.Property(x => x.State).HasConversion<string>();
            e.Property(x => x.Version).HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
            e.HasMany(x => x.Steps).WithOne().HasForeignKey(s => s.TenderWorkflowId);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<TenderStepRow>(e =>
        {
            e.ToTable("tender_workflow_step");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Stage).HasConversion<string>();
            e.Property(x => x.Rule).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.HasIndex(x => new { x.TenderWorkflowId, x.Position }).IsUnique();
            e.HasMany(x => x.Decisions).WithOne().HasForeignKey(d => d.StepId);
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });

        modelBuilder.Entity<StepDecisionRow>(e =>
        {
            e.ToTable("step_decision");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Decision).HasConversion<string>();
            e.HasQueryFilter(x => x.TenantId == CurrentTenantId);
        });
    }
}
```

`ValueGeneratedNever` on every key matters: the code assigns `Guid.CreateVersion7()` ids, and without it EF treats a new child found through a navigation as an existing row and issues an UPDATE.

- [ ] **Step 5: Definitions service** — `src/Modules/Workflow/Platform.Modules.Workflow/WorkflowDefinitions.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Workflow;

internal sealed class WorkflowDefinitions(IDbContextFactory<WorkflowDbContext> contexts, ITenantAccessor tenants, TimeProvider clock) : IWorkflowDefinitions
{
    public async Task<Result<Guid>> SaveAsync(SaveDefinition command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (DefinitionValidator.Check(command.Steps) is { } invalid)
        {
            return Result.Failure<Guid>(invalid);
        }

        var tenantId = tenants.Current?.TenantId ?? throw new InvalidOperationException("Workflow operations need a current tenant.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        DefinitionRow row;
        if (command.DefinitionId is { } id)
        {
            var existing = await db.Definitions.Include(d => d.Steps).SingleOrDefaultAsync(d => d.Id == id, cancellationToken);
            if (existing is null)
            {
                return Result.Failure<Guid>(Error.NotFound("workflow.definition_not_found", "The workflow definition was not found."));
            }

            db.DefinitionSteps.RemoveRange(existing.Steps);
            existing.Steps.Clear();
            existing.Name = command.Name;
            existing.IsDefault = command.IsDefault;
            existing.Version += 1;
            row = existing;
        }
        else
        {
            row = new DefinitionRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                Name = command.Name,
                Version = 1,
                IsDefault = command.IsDefault,
                CreatedAt = clock.GetUtcNow(),
            };
            db.Definitions.Add(row);
        }

        if (command.IsDefault)
        {
            await db.Definitions.Where(d => d.IsDefault && d.Id != row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.IsDefault, false), cancellationToken);
        }

        row.Steps.AddRange(command.Steps.Select((step, index) => new DefinitionStepRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DefinitionId = row.Id,
            Position = index + 1,
            Stage = step.Stage,
            Department = step.Department,
            Rule = step.Rule,
            ActorRoles = [.. step.ActorRoles],
            Threshold = step.Threshold,
        }));

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Result.Success(row.Id);
    }

    public async Task<Guid?> FindDefaultAsync(CancellationToken cancellationToken = default)
    {
        _ = tenants.Current ?? throw new InvalidOperationException("Workflow operations need a current tenant.");
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        return await db.Definitions.Where(d => d.IsDefault).Select(d => (Guid?)d.Id).SingleOrDefaultAsync(cancellationToken);
    }
}
```

Replace `src/Modules/Workflow/Platform.Modules.Workflow/WorkflowModule.cs`:
```csharp
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Data;

namespace Platform.Modules.Workflow;

public static class WorkflowModule
{
    public static IServiceCollection AddWorkflowModule(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddModuleDbContext<WorkflowDbContext>(connectionString);
        services.AddScoped<IWorkflowDefinitions, WorkflowDefinitions>();
        return services;
    }

    public static Task<IReadOnlyList<string>> MigrateAsync(NpgsqlConnection connection, CancellationToken cancellationToken = default) =>
        SqlMigrator.ApplyAsync(connection, "workflow", typeof(WorkflowModule).Assembly, cancellationToken);
}
```

In `MigrationRunner.RunAsync`, add after the tenancy line (and `using Platform.Modules.Workflow;`):
```csharp
        applied.AddRange(Named("workflow", await WorkflowModule.MigrateAsync(connection, cancellationToken)));
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass, including five `WorkflowDefinitionsTests`.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "Workflow persistence: schema with RLS, definitions with versioning and one default per tenant"
```

---

### Task 14: Workflow executor (F-56)

**Files:**
- Create: `src/Modules/Workflow/Platform.Modules.Workflow/SnapshotDocument.cs`, `src/Modules/Workflow/Platform.Modules.Workflow/WorkflowService.cs`
- Modify: `src/Modules/Workflow/Platform.Modules.Workflow/WorkflowModule.cs`
- Create: `tests/Platform.IntegrationTests/Workflow/WorkflowExecutorTests.cs`

- [ ] **Step 1: Write the failing tests** — `tests/Platform.IntegrationTests/Workflow/WorkflowExecutorTests.cs`

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Results;

namespace Platform.IntegrationTests.Workflow;

/// <summary>F-56 acceptance, run against real PostgreSQL through the module's public contracts.</summary>
[Collection(DatabaseCollection.Name)]
public class WorkflowExecutorTests(DatabaseFixture db) : IAsyncLifetime
{
    private static readonly Dictionary<string, IReadOnlyList<string>> Committee = new()
    {
        ["contracts"] = ["u.legal"],
        ["evaluator"] = ["u.tech1", "u.tech2"],
        ["finance"] = ["u.cfo"],
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private ModuleHost _host = null!;

    public ValueTask InitializeAsync()
    {
        _host = new ModuleHost(db.AppConnectionString);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    [Fact]
    public async Task Publishing_snapshots_the_definition_and_the_committee_fills_the_roles()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var (tenderId, _) = await StartAsync(scope);

        var status = (await Service(scope).GetStatusAsync(tenderId, Ct)).Value;

        status.CurrentStage.ShouldBe(Stage.Screening);
        status.PendingUsers.ShouldBe(["u.legal"]);
        status.Steps[1].AssignedUsers.ShouldBe(["u.tech1", "u.tech2"]);
        await using var context = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<WorkflowDbContext>>().CreateDbContextAsync(Ct);
        var snapshot = await context.TenderWorkflows.Where(w => w.TenderId == tenderId).Select(w => w.Snapshot).SingleAsync(Ct);
        snapshot.ShouldContain("Technical committee");
    }

    [Fact]
    public async Task Editing_the_definition_afterwards_leaves_the_running_tender_unchanged()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var (tenderId, definitionId) = await StartAsync(scope);

        List<StepDefinition> edited =
        [
            DefaultTemplate.Steps[0],
            new(Stage.TechnicalScoring, "Senior committee", StepRule.AnyOf, ["senior-evaluator"]),
            new(Stage.TechnicalScoring, "Procurement head", StepRule.AnyOf, ["head"]),
            .. DefaultTemplate.Steps.Skip(2),
        ];
        (await Definitions(scope).SaveAsync(new SaveDefinition(definitionId, "Edited", false, edited), Ct)).IsSuccess.ShouldBeTrue();

        var status = (await Service(scope).GetStatusAsync(tenderId, Ct)).Value;
        status.Steps.Count.ShouldBe(DefaultTemplate.Steps.Count);
        status.Steps[1].Department.ShouldBe("Technical committee");
        status.Steps[1].AssignedUsers.ShouldBe(["u.tech1", "u.tech2"]);
    }

    [Fact]
    public async Task An_all_of_step_stays_open_until_every_approver_decides_and_names_who_is_pending()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);
        await service.DecideAsync(tenderId, "u.legal", Decision.Approve, Ct);

        var afterOne = (await service.DecideAsync(tenderId, "u.tech1", Decision.Approve, Ct)).Value;

        afterOne.CurrentStage.ShouldBe(Stage.TechnicalScoring);
        afterOne.PendingUsers.ShouldBe(["u.tech2"]);

        var afterBoth = (await service.DecideAsync(tenderId, "u.tech2", Decision.Approve, Ct)).Value;
        afterBoth.CurrentStage.ShouldBe(Stage.LockScores);
    }

    [Fact]
    public async Task A_decision_from_an_unassigned_user_is_refused_audited_and_changes_nothing()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);

        var result = await service.DecideAsync(tenderId, "u.intruder", Decision.Approve, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Kind.ShouldBe(ErrorKind.Refused);
        (await service.GetStatusAsync(tenderId, Ct)).Value.PendingUsers.ShouldBe(["u.legal"]);
        await using var audit = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AuditDbContext>>().CreateDbContextAsync(Ct);
        var tender = tenderId.ToString("D", System.Globalization.CultureInfo.InvariantCulture);
        (await audit.Events.CountAsync(e => e.Action == "workflow.decision_refused" && e.ActorId == "u.intruder" && e.SubjectId == tender, Ct)).ShouldBe(1);
    }

    [Fact]
    public async Task The_chain_completes_and_financial_opening_cannot_run_before_locking()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);
        await service.DecideAsync(tenderId, "u.legal", Decision.Approve, Ct);
        await service.DecideAsync(tenderId, "u.tech1", Decision.Approve, Ct);
        await service.DecideAsync(tenderId, "u.tech2", Decision.Approve, Ct);

        var early = await service.CompleteSystemStageAsync(tenderId, Stage.FinancialOpening, Ct);
        early.IsSuccess.ShouldBeFalse();
        early.Error.Kind.ShouldBe(ErrorKind.InvariantViolated);
        early.Error.Message.ShouldBe("Financial opening must follow score locking (F-30).");

        (await service.CompleteSystemStageAsync(tenderId, Stage.LockScores, Ct)).IsSuccess.ShouldBeTrue();
        (await service.CompleteSystemStageAsync(tenderId, Stage.FinancialOpening, Ct)).IsSuccess.ShouldBeTrue();
        var done = (await service.DecideAsync(tenderId, "u.cfo", Decision.Approve, Ct)).Value;

        done.State.ShouldBe(WorkflowState.Completed);
        done.CurrentStage.ShouldBeNull();
    }

    [Fact]
    public async Task A_rejection_ends_the_workflow()
    {
        await using var scope = _host.ScopeFor(TestTenants.Acme);
        var service = Service(scope);
        var (tenderId, _) = await StartAsync(scope);

        var rejected = (await service.DecideAsync(tenderId, "u.legal", Decision.Reject, Ct)).Value;

        rejected.State.ShouldBe(WorkflowState.Rejected);
        (await service.DecideAsync(tenderId, "u.legal", Decision.Approve, Ct)).Error!.Kind.ShouldBe(ErrorKind.InvariantViolated);
    }

    [Fact]
    public async Task Another_tenant_cannot_see_the_tenders_workflow()
    {
        Guid tenderId;
        await using (var acme = _host.ScopeFor(TestTenants.Acme))
        {
            (tenderId, _) = await StartAsync(acme);
        }

        await using var beta = _host.ScopeFor(TestTenants.Beta);
        var result = await Service(beta).GetStatusAsync(tenderId, Ct);

        result.Error!.Kind.ShouldBe(ErrorKind.NotFound);
    }

    private static async Task<(Guid TenderId, Guid DefinitionId)> StartAsync(AsyncServiceScope scope)
    {
        var definitionId = (await Definitions(scope).SaveAsync(new SaveDefinition(null, "Test chain", false, DefaultTemplate.Steps), Ct)).Value;
        var tenderId = Guid.CreateVersion7();
        var started = await Service(scope).StartAsync(tenderId, definitionId, Committee, Ct);
        started.IsSuccess.ShouldBeTrue(started.Error?.Message);
        return (tenderId, definitionId);
    }

    private static IWorkflowService Service(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWorkflowService>();

    private static IWorkflowDefinitions Definitions(AsyncServiceScope scope) => scope.ServiceProvider.GetRequiredService<IWorkflowDefinitions>();
}
```

Run: `dotnet test tests/Platform.IntegrationTests --filter WorkflowExecutorTests`
Expected: tests fail with `No service for type 'IWorkflowService' has been registered`.

- [ ] **Step 2: Snapshot document** — `src/Modules/Workflow/Platform.Modules.Workflow/SnapshotDocument.cs`

```csharp
using Platform.Modules.Workflow.Contracts;

namespace Platform.Modules.Workflow;

/// <summary>Our own versioned snapshot format (ADR-0004 point 2). Bump CurrentVersion when the shape changes.</summary>
internal sealed record SnapshotDocument(int Version, Guid DefinitionId, int DefinitionVersion, string Name, IReadOnlyList<StepDefinition> Steps)
{
    public const int CurrentVersion = 1;
}
```

- [ ] **Step 3: Executor** — `src/Modules/Workflow/Platform.Modules.Workflow/WorkflowService.cs`

```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Workflow.Contracts;
using Platform.Modules.Workflow.Persistence;
using Platform.Shared.Results;
using Platform.Shared.Tenancy;

namespace Platform.Modules.Workflow;

/// <summary>
/// Our own state machine (ADR-0004): walks a tender's snapshot between the fixed points. A refused action never faults
/// the tender; it is audited and the step keeps waiting (docs/06 section 6.3).
/// </summary>
internal sealed class WorkflowService(
    IDbContextFactory<WorkflowDbContext> contexts,
    ITenantAccessor tenants,
    IAuditWriter audit,
    TimeProvider clock) : IWorkflowService
{
    private static readonly JsonSerializerOptions SnapshotJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public async Task<Result<WorkflowStatus>> StartAsync(
        Guid tenderId,
        Guid definitionId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> assignmentsByRole,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(assignmentsByRole);
        var tenantId = RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);

        var definition = await db.Definitions.AsNoTracking().Include(d => d.Steps)
            .SingleOrDefaultAsync(d => d.Id == definitionId, cancellationToken);
        if (definition is null)
        {
            return Result.Failure<WorkflowStatus>(Error.NotFound("workflow.definition_not_found", "The workflow definition was not found."));
        }

        var steps = definition.Steps.OrderBy(s => s.Position)
            .Select(s => new StepDefinition(s.Stage, s.Department, s.Rule, s.ActorRoles, s.Threshold))
            .ToList();
        if (DefinitionValidator.Check(steps) is { } invalid)
        {
            return Result.Failure<WorkflowStatus>(invalid);
        }

        if (await db.TenderWorkflows.AnyAsync(w => w.TenderId == tenderId, cancellationToken))
        {
            return Result.Failure<WorkflowStatus>(Error.Conflict("workflow.already_started", "A workflow is already running for this tender."));
        }

        var now = clock.GetUtcNow();
        var workflow = new TenderWorkflowRow
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            TenderId = tenderId,
            DefinitionId = definition.Id,
            SnapshotVersion = SnapshotDocument.CurrentVersion,
            Snapshot = JsonSerializer.Serialize(
                new SnapshotDocument(SnapshotDocument.CurrentVersion, definition.Id, definition.Version, definition.Name, steps), SnapshotJson),
            State = WorkflowState.Running,
            CreatedAt = now,
            UpdatedAt = now,
        };

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            List<string> users = step.Stage.IsSystem()
                ? []
                : [.. step.ActorRoles
                    .SelectMany(role => assignmentsByRole.TryGetValue(role, out var assigned) ? assigned : Array.Empty<string>())
                    .Distinct(StringComparer.Ordinal)];
            if (!step.Stage.IsSystem() && users.Count == 0)
            {
                return Result.Failure<WorkflowStatus>(Error.Validation(
                    "workflow.step_unassigned",
                    $"Step {i + 1} ({step.Department}) has nobody assigned for the roles {string.Join(", ", step.ActorRoles)}."));
            }

            workflow.Steps.Add(new TenderStepRow
            {
                Id = Guid.CreateVersion7(),
                TenantId = tenantId,
                TenderWorkflowId = workflow.Id,
                Position = i + 1,
                Stage = step.Stage,
                Department = step.Department,
                Rule = step.Rule,
                AssignedUsers = users,
                Status = i == 0 ? StepState.Open : StepState.Pending,
            });
        }

        db.TenderWorkflows.Add(workflow);
        await db.SaveChangesAsync(cancellationToken);
        await audit.WriteAsync(
            new AuditEntry(null, "workflow.started", "tender", Format(tenderId), new Dictionary<string, string?>
            {
                ["definition"] = Format(definition.Id),
                ["definitionVersion"] = Format(definition.Version),
            }),
            // the change is committed; do not let a cancelled request skip its audit row
            CancellationToken.None);
        return Result.Success(ToStatus(workflow));
    }

    public async Task<Result<WorkflowStatus>> DecideAsync(Guid tenderId, string userId, Decision decision, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var tenantId = RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var workflow = await LoadAsync(db, tenderId, cancellationToken);
        if (workflow is null)
        {
            return NotFound();
        }

        if (workflow.State != WorkflowState.Running)
        {
            return NotRunning(workflow);
        }

        var step = OpenStep(workflow);
        if (step.Stage.IsSystem())
        {
            return Result.Failure<WorkflowStatus>(Error.Refused(
                "workflow.system_step", $"The current step ({Name(step.Stage)}) is completed by the system, not by a decision."));
        }

        if (!step.AssignedUsers.Contains(userId, StringComparer.Ordinal))
        {
            await audit.WriteAsync(
                new AuditEntry(userId, "workflow.decision_refused", "tender", Format(tenderId), new Dictionary<string, string?>
                {
                    ["step"] = Format(step.Position),
                    ["reason"] = "not_assigned",
                }),
                cancellationToken);
            return Result.Failure<WorkflowStatus>(Error.Refused(
                "workflow.not_assigned", $"You are not assigned to step {step.Position} ({step.Department})."));
        }

        if (step.Decisions.Any(d => d.UserId == userId))
        {
            return Result.Failure<WorkflowStatus>(Error.Conflict("workflow.already_decided", "You have already decided on this step."));
        }

        var now = clock.GetUtcNow();
        var row = new StepDecisionRow { Id = Guid.CreateVersion7(), TenantId = tenantId, StepId = step.Id, UserId = userId, Decision = decision, DecidedAt = now };
        db.Decisions.Add(row);
        step.Decisions.Add(row);

        if (decision == Decision.Reject)
        {
            step.Status = StepState.Rejected;
            workflow.State = WorkflowState.Rejected;
        }
        else if (step.Rule == StepRule.AnyOf || step.AssignedUsers.All(u => step.Decisions.Any(d => d.UserId == u && d.Decision == Decision.Approve)))
        {
            step.Status = StepState.Approved;
            OpenNextStep(workflow);
        }

        // Touch the workflow row on every decision so its xmin guards against two decisions racing (spec section 7).
        workflow.UpdatedAt = now;
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return Result.Failure<WorkflowStatus>(conflict);
        }

        await audit.WriteAsync(
            new AuditEntry(userId, "workflow.decided", "tender", Format(tenderId), new Dictionary<string, string?>
            {
                ["step"] = Format(step.Position),
                ["decision"] = Name(decision),
                ["stepState"] = Name(step.Status),
            }),
            // the change is committed; do not let a cancelled request skip its audit row
            CancellationToken.None);
        return Result.Success(ToStatus(workflow));
    }

    public async Task<Result<WorkflowStatus>> CompleteSystemStageAsync(Guid tenderId, Stage stage, CancellationToken cancellationToken = default)
    {
        if (!stage.IsSystem())
        {
            return Result.Failure<WorkflowStatus>(Error.Validation(
                "workflow.not_system_stage", $"{Name(stage)} is decided by people, not completed by the system."));
        }

        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var workflow = await LoadAsync(db, tenderId, cancellationToken);
        if (workflow is null)
        {
            return NotFound();
        }

        if (workflow.State != WorkflowState.Running)
        {
            return NotRunning(workflow);
        }

        var step = OpenStep(workflow);
        if (step.Stage != stage)
        {
            var message = stage == Stage.FinancialOpening && step.Stage <= Stage.LockScores
                ? "Financial opening must follow score locking (F-30)."
                : $"{Name(stage)} is not the current step; the workflow is at {Name(step.Stage)}.";
            return Result.Failure<WorkflowStatus>(Error.Invariant("workflow.stage_not_current", message));
        }

        step.Status = StepState.Done;
        OpenNextStep(workflow);
        workflow.UpdatedAt = clock.GetUtcNow();
        if (await TrySaveAsync(db, cancellationToken) is { } conflict)
        {
            return Result.Failure<WorkflowStatus>(conflict);
        }

        await audit.WriteAsync(
            new AuditEntry(null, "workflow.system_stage_completed", "tender", Format(tenderId), new Dictionary<string, string?>
            {
                ["stage"] = Name(stage),
            }),
            // the change is committed; do not let a cancelled request skip its audit row
            CancellationToken.None);
        return Result.Success(ToStatus(workflow));
    }

    public async Task<Result<WorkflowStatus>> GetStatusAsync(Guid tenderId, CancellationToken cancellationToken = default)
    {
        RequireTenant();
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var workflow = await LoadAsync(db, tenderId, cancellationToken);
        return workflow is null ? NotFound() : Result.Success(ToStatus(workflow));
    }

    private Guid RequireTenant() =>
        tenants.Current?.TenantId ?? throw new InvalidOperationException("Workflow operations need a current tenant.");

    private static Task<TenderWorkflowRow?> LoadAsync(WorkflowDbContext db, Guid tenderId, CancellationToken cancellationToken) =>
        db.TenderWorkflows
            .Include(w => w.Steps).ThenInclude(s => s.Decisions)
            .AsSplitQuery()
            .SingleOrDefaultAsync(w => w.TenderId == tenderId, cancellationToken);

    private static async Task<Error?> TrySaveAsync(WorkflowDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("workflow.concurrent_update", "Another action was saved at the same moment. Reload and try again.");
        }
    }

    private static TenderStepRow OpenStep(TenderWorkflowRow workflow) =>
        workflow.Steps.OrderBy(s => s.Position).First(s => s.Status == StepState.Open);

    private static void OpenNextStep(TenderWorkflowRow workflow)
    {
        var next = workflow.Steps.OrderBy(s => s.Position).FirstOrDefault(s => s.Status == StepState.Pending);
        if (next is null)
        {
            workflow.State = WorkflowState.Completed;
        }
        else
        {
            next.Status = StepState.Open;
        }
    }

    private static WorkflowStatus ToStatus(TenderWorkflowRow workflow)
    {
        var steps = workflow.Steps.OrderBy(s => s.Position)
            .Select(s => new StepStatus(
                s.Position,
                s.Stage,
                s.Department,
                s.Rule,
                s.Status,
                s.AssignedUsers,
                s.Decisions.Where(d => d.Decision == Decision.Approve).OrderBy(d => d.DecidedAt).Select(d => d.UserId).ToList()))
            .ToList();
        var open = steps.FirstOrDefault(s => s.State == StepState.Open);
        IReadOnlyList<string> pending = open is null || open.Stage.IsSystem()
            ? []
            : open.AssignedUsers.Except(open.ApprovedBy, StringComparer.Ordinal).ToList();
        return new WorkflowStatus(workflow.TenderId, workflow.State, open?.Position, open?.Stage, pending, steps);
    }

    private static Result<WorkflowStatus> NotFound() =>
        Result.Failure<WorkflowStatus>(Error.NotFound("workflow.not_found", "No workflow exists for this tender."));

    private static Result<WorkflowStatus> NotRunning(TenderWorkflowRow workflow) =>
        Result.Failure<WorkflowStatus>(Error.Invariant(
            "workflow.not_running", $"The workflow is {Name(workflow.State)}; no further actions are accepted."));

    private static string Format(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Name<TEnum>(TEnum value)
        where TEnum : struct, Enum => Enum.GetName(value) ?? string.Empty;
}
```

Known limitation, recorded here and in the pull request: the workflow change and its audit row are two transactions, so an audit write failure after a committed transition surfaces as an exception without rolling the transition back. The decision rows are the source of truth for the workflow; an outbox for audit is part of F-41.

In `WorkflowModule.AddWorkflowModule`, after the `IWorkflowDefinitions` registration add:
```csharp
        services.AddScoped<IWorkflowService, WorkflowService>();
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass, including seven `WorkflowExecutorTests`.

- [ ] **Step 5: Commit and move F-56 to Done**

In `docs/09-backlog.md`, row F-56, replace `| P0 | L | Backlog | W-03, W-20 |` with `| P0 | L | Done | W-03, W-20 |`.

```bash
git add src tests docs/09-backlog.md
git commit -m "Workflow executor: snapshot, any-of and all-of steps, fixed points, refusals audited; F-56 done"
```

---

### Task 15: Development seed for tenants and default chains

**Files:**
- Modify: `src/Platform.Migrator/DevSeed.cs`, `src/Platform.Migrator/Program.cs`
- Create: `tests/Platform.IntegrationTests/Data/DevSeedTests.cs`

- [ ] **Step 1: Write the failing test** — `tests/Platform.IntegrationTests/Data/DevSeedTests.cs`

```csharp
using Microsoft.Extensions.DependencyInjection;
using Platform.IntegrationTests.Infrastructure;
using Platform.Migrator;
using Platform.Modules.Workflow.Contracts;

namespace Platform.IntegrationTests.Data;

[Collection(DatabaseCollection.Name)]
public class DevSeedTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Seeding_twice_leaves_each_dev_tenant_with_one_default_chain()
    {
        await DevSeed.SeedTenantsAsync(db.OwnerConnectionString, Ct);
        await DevSeed.SeedWorkflowsAsync(db.AppConnectionString, Ct);
        await DevSeed.SeedWorkflowsAsync(db.AppConnectionString, Ct);

        await using var host = new ModuleHost(db.AppConnectionString);
        foreach (var tenant in DevSeed.Tenants)
        {
            await using var scope = host.ScopeFor(tenant.ToContext());
            (await scope.ServiceProvider.GetRequiredService<IWorkflowDefinitions>().FindDefaultAsync(Ct)).ShouldNotBeNull();
        }
    }
}
```

Run: `dotnet test tests/Platform.IntegrationTests --filter DevSeedTests`
Expected: build error, `SeedWorkflowsAsync` not found.

- [ ] **Step 2: Implement**

Add to `src/Platform.Migrator/DevSeed.cs` (with `using Microsoft.Extensions.DependencyInjection;`, `using Platform.Modules.Audit;`, `using Platform.Modules.Workflow;`, `using Platform.Modules.Workflow.Contracts;`, `using Platform.Shared;`):
```csharp
    /// <summary>Gives each dev tenant the default approval chain, through the module's own service as the app role.</summary>
    public static async Task SeedWorkflowsAsync(string appConnectionString, CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddPlatformShared();
        services.AddAuditModule(appConnectionString);
        services.AddWorkflowModule(appConnectionString);
        await using var provider = services.BuildServiceProvider();

        foreach (var tenant in Tenants)
        {
            await using var scope = provider.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantAccessor>().Set(tenant.ToContext());
            var definitions = scope.ServiceProvider.GetRequiredService<IWorkflowDefinitions>();
            if (await definitions.FindDefaultAsync(cancellationToken) is not null)
            {
                continue;
            }

            var saved = await definitions.SaveAsync(new SaveDefinition(null, DefaultTemplate.Name, true, DefaultTemplate.Steps), cancellationToken);
            if (!saved.IsSuccess)
            {
                throw new InvalidOperationException($"Seeding the default chain for {tenant.Slug} failed: {saved.Error.Message}");
            }
        }
    }
```

Replace `src/Platform.Migrator/Program.cs`. Keep the `internal static class EntryPoint` shape from Task 5 (top-level statements synthesize a type named `Program`, which collides with `Platform.Web`'s `Program` once `Platform.IntegrationTests` references both — see spec section 7):
```csharp
using Microsoft.Extensions.Configuration;

namespace Platform.Migrator;

internal static class EntryPoint
{
    public static async Task<int> Main(string[] args)
    {
        var seedDev = args.Contains("--seed-dev", StringComparer.Ordinal);
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(MigrationRunner).Assembly, optional: true)
            .AddEnvironmentVariables()
            .AddCommandLine(args.Where(a => a != "--seed-dev").ToArray())
            .Build();

        var owner = configuration.GetConnectionString("Owner");
        if (string.IsNullOrWhiteSpace(owner))
        {
            Console.Error.WriteLine("Connection string 'Owner' is not configured (user secrets or ConnectionStrings__Owner).");
            return 1;
        }

        var applied = await MigrationRunner.RunAsync(owner);
        Console.WriteLine(applied.Count == 0 ? "Database is up to date." : $"Applied {applied.Count} scripts: {string.Join(", ", applied)}");

        if (seedDev)
        {
            var app = configuration.GetConnectionString("Platform");
            if (string.IsNullOrWhiteSpace(app))
            {
                Console.Error.WriteLine("--seed-dev needs connection string 'Platform' (the erp_app role).");
                return 1;
            }

            await DevSeed.SeedTenantsAsync(owner);
            await DevSeed.SeedWorkflowsAsync(app);
            Console.WriteLine("Seeded development tenants acme and beta with the default approval chain.");
        }

        return 0;
    }
}
```

- [ ] **Step 3: Run to verify pass**

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass.

- [ ] **Step 4: Commit**

```bash
git add src/Platform.Migrator tests
git commit -m "Migrator: --seed-dev seeds dev tenants and their default approval chain"
```

---

### Task 16: End-to-end through Caddy, and documentation

**Files:**
- Modify: `infra/compose/caddy/Caddyfile`, `src/Platform.Web/Program.cs`, `README.md`, `CLAUDE.md`

- [ ] **Step 1: Forwarded headers for the local edge**

In `infra/compose/caddy/Caddyfile`, replace `header_up Host {host}` with:
```
		header_up Host {hostport}
```
(so the app sees `acme.localhost:8443` and builds the OIDC redirect URI with the port).

In `src/Platform.Web/Program.cs`, add `using Microsoft.AspNetCore.HttpOverrides;`, then before `var app = builder.Build();` add:
```csharp
if (builder.Environment.IsDevelopment())
{
    // Caddy on the local Compose stack terminates TLS and forwards the scheme. Production trusts only its own proxy (W-11).
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}
```
and as the first middleware after `var app = builder.Build();`:
```csharp
if (app.Environment.IsDevelopment())
{
    app.UseForwardedHeaders();
}
```

Run: `dotnet build WaslaBid.slnx -warnaserror && dotnet test WaslaBid.slnx`
Expected: all pass.

- [ ] **Step 2: Local secrets, migrations and seed**

```bash
PGPW=$(grep '^POSTGRES_PASSWORD=' infra/compose/.env | cut -d= -f2-)
dotnet user-secrets set "ConnectionStrings:Owner" "Host=localhost;Port=5432;Database=platform;Username=erp;Password=$PGPW" --project src/Platform.Migrator
dotnet user-secrets set "ConnectionStrings:Platform" "Host=localhost;Port=5432;Database=platform;Username=erp_app;Password=erp_app_dev_password" --project src/Platform.Migrator
dotnet user-secrets set "ConnectionStrings:Platform" "Host=localhost;Port=5432;Database=platform;Username=erp_app;Password=erp_app_dev_password" --project src/Platform.Web
docker compose -f infra/compose/docker-compose.yml --env-file infra/compose/.env restart caddy
dotnet run --project src/Platform.Migrator -- --seed-dev
```
Expected: `Applied 4 scripts: platform/0001_platform.sql, audit/0001_audit.sql, tenancy/0001_tenancy.sql, workflow/0001_workflow.sql` and `Seeded development tenants acme and beta with the default approval chain.`

- [ ] **Step 3: Run the app and check the edge**

Start `dotnet run --project src/Platform.Web` in the background, then:
```bash
curl -sk -o /dev/null -w "%{http_code}\n" https://acme.localhost:8443/health
curl -sk -o /dev/null -w "%{http_code} %{redirect_url}\n" https://acme.localhost:8443/
curl -sk -o /dev/null -w "%{http_code}\n" https://nobody.localhost:8443/
```
Expected: `200`; `302` to `http://localhost:8080/realms/waslabid/protocol/openid-connect/auth?...` whose `redirect_uri` is `https%3A%2F%2Facme.localhost%3A8443%2Fsignin-oidc`; `404`.

If Caddy answers `502`, Kestrel is not reachable from the container on `localhost`; set `"applicationUrl": "http://0.0.0.0:5273"` in `launchSettings.json` for local use only and retry.

- [ ] **Step 4: Browser check (qa-engineer)**

In a browser (Playwright), open `https://acme.localhost:8443/`, sign in as `acme.admin` with `WASLABID_DEV_USER_PASSWORD` from `infra/compose/.env`. Expected: an Arabic page, right to left, header in teal `#0F766E`, "مرحباً بك في Acme Contracting", "دخلت باسم acme.admin". Click "English": the page switches to English left to right. Sign in as `beta.admin` at `https://acme.localhost:8443/` in a private window: `403`.

- [ ] **Step 5: Documentation**

In `CLAUDE.md`, replace the paragraph starting `There is no application code yet.` with:
```
The foundation slice is in `src/` (design spec `docs/superpowers/specs/2026-09-26-foundation-design.md`, plan `docs/superpowers/plans/2026-09-26-foundation.md`): solution `WaslaBid.slnx` on .NET 10, modules Audit, Tenancy, Identity and Workflow, the Blazor host `Platform.Web`, `Platform.Migrator`, and `Platform.UI` with the Tailwind build. Each module ships SQL migrations under `Migrations/` and owns one schema with forced row-level security. The two projects under `spikes/` remain throwaway evidence; do not build on them.
```

In `README.md`, replace the numbered onboarding step 1 (`**Machine.** ...`) with:
```
1. **Machine.** Install the .NET 10 SDK and start Docker Desktop. `cd infra/compose && cp .env.example .env`, fill the two `WASLABID_*` values (any strong random strings), then `docker compose up -d`. On Windows machines where port 443 is reserved, set `CADDY_HTTP_PORT=8081` and `CADDY_HTTPS_PORT=8443` in `.env`. Then from the repository root: set the user secrets listed in the foundation plan (Task 16 step 2), run `dotnet run --project src/Platform.Migrator -- --seed-dev`, run `dotnet run --project src/Platform.Web`, and open `https://acme.localhost:8443`. Ports and credentials are in `docs/07-ways-of-working.md` section 4.
```

- [ ] **Step 6: Final verification and commit**

```bash
dotnet build WaslaBid.slnx -warnaserror
dotnet test WaslaBid.slnx
dotnet format WaslaBid.slnx --verify-no-changes
```
Expected: build succeeds with no warnings; every test passes; `dotnet format` reports no changes (if it does, run `dotnet format WaslaBid.slnx` and include the result).

```bash
git add infra/compose/caddy/Caddyfile src/Platform.Web README.md CLAUDE.md
git commit -m "Foundation end to end through Caddy; README and CLAUDE.md describe the running system"
```

---

## Acceptance map

| Backlog acceptance (docs/09) | Test |
|---|---|
| W-02 build and test green, one test per module, no cross-module internals | Task 3 `ModuleBoundaryTests`, `ModuleRegistrationTests`; Task 16 step 6 |
| W-03 EF only tenant A; raw SQL only tenant A; no tenant zero rows | Task 6 `RowLevelSecurityTests` (plus pool reuse) |
| W-04 realm import, token carries organization, host maps to TenantContext | Task 9 `KeycloakTokenTests`, `SameTenantTests` |
| W-05 `ml-4` fails naming file and line; `--color-primary` in output | Task 10 `PhysicalUtilityLintTests`, `TailwindBuildTests` |
| W-07 Arabic profile gives `lang="ar" dir="rtl"` and Arabic strings only | Task 11 `LocalizationTests`, `ResourceParityTests` |
| F-56 snapshot and committee; edit leaves running tender; financial before locking rejected with rule named; all-of stays open naming pending | Task 13 and 14 `WorkflowDefinitionsTests`, `WorkflowExecutorTests`; Task 12 `DefinitionValidatorTests` |
