using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;

namespace Platform.IntegrationTests.Vendors;

/// <summary>
/// The tenant's view of its vendors and the second tenant (vendor plan task 5, F-10 as narrowed, V-7, V-11): staff read a
/// company's card and documents only while their tenant has a relationship with it (<see cref="IVendorDirectory"/>), a
/// contracts officer or tenant admin approves a pending one with an audit entry in the tenant's log, and a vendor of
/// another tenant joins through <see cref="IVendorJoin"/>, which relates it to that tenant alone.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class VendorDirectoryTests(DatabaseFixture db)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_officer_approves_a_pending_vendor_and_it_is_audited()
    {
        var (companyId, _) = await VendorAsync("Approved Supplies");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var host = new ModuleHost(db.AppConnectionString);

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();
            (await directory.GetRelatedAsync(companyId, Ct)).ShouldNotBeNull().Status.ShouldBe(VendorRelationshipStatus.Pending);

            var result = await directory.ApproveAsync(companyId, officer, Ct);

            result.IsSuccess.ShouldBeTrue(result.Error?.Message);
            var approved = (await directory.GetRelatedAsync(companyId, Ct)).ShouldNotBeNull();
            approved.Status.ShouldBe(VendorRelationshipStatus.Approved);
            approved.ApprovedBy.ShouldBe(officer);
        }

        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("approved");
        var audit = (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, officer, "vendor.approved", Ct)).ShouldHaveSingleItem();
        audit.SubjectId.ShouldBe(companyId.ToString());

        // Approving again is refused as already approved, and writes no second entry.
        await using (var again = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            var repeat = await again.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, officer, Ct);
            repeat.Error.ShouldNotBeNull().Code.ShouldBe(VendorDirectoryErrors.AlreadyApproved);
        }

        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, officer, "vendor.approved", Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_tenant_admin_approves_too_but_another_role_or_another_tenant_cannot()
    {
        var (companyId, _) = await VendorAsync("Role Checked Trading");
        var evaluator = await StaffAsync(TestTenants.Acme, TenantRoles.TechnicalEvaluator);
        var betaOfficer = await StaffAsync(TestTenants.Beta, TenantRoles.ContractsOfficer);
        var admin = await StaffAsync(TestTenants.Acme, TenantRoles.TenantAdmin);
        await using var host = new ModuleHost(db.AppConnectionString);

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: evaluator))
        {
            var refused = await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, evaluator, Ct);
            refused.Error.ShouldNotBeNull().Code.ShouldBe(VendorDirectoryErrors.NotAllowed);
        }

        // Beta has no relationship with the company: it is not found there, whatever the role.
        await using (var scope = host.ScopeFor(TestTenants.Beta, actingUserId: betaOfficer))
        {
            var missing = await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, betaOfficer, Ct);
            missing.Error.ShouldNotBeNull().Code.ShouldBe(VendorDirectoryErrors.NotFound);
        }

        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe(
            new Dictionary<Guid, string> { [TestTenants.Acme.TenantId] = "pending" });

        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: admin))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, admin, Ct)).IsSuccess.ShouldBeTrue();
        }

        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, evaluator, "vendor.approved", Ct)).ShouldBeEmpty();
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, admin, "vendor.approved", Ct)).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task A_staff_member_who_is_also_a_vendor_user_is_not_allowed_to_approve()
    {
        var (companyId, _) = await VendorAsync("Dual Role Target");
        // An officer of acme whose account also belongs to a vendor company.
        var (_, dualUser) = await VendorAsync("Dual Role Own Company");
        await MemberRows.InsertAsync(db.AppConnectionString, TestTenants.Acme.TenantId, dualUser, $"{dualUser}@acme.test", [TenantRoles.ContractsOfficer], "active", Ct);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: dualUser);

        var result = await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, dualUser, Ct);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorDirectoryErrors.NotAllowed);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task Approving_as_someone_other_than_the_acting_user_is_a_defect()
    {
        var (companyId, _) = await VendorAsync("Actor Mismatch Company");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: "someone-else");

        await Should.ThrowAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, officer, Ct));

        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task A_vendor_document_is_visible_to_a_tenant_only_while_a_relationship_exists()
    {
        var (companyId, _) = await VendorAsync("Visible Documents Company");
        var expiry = new DateOnly(2030, 1, 31);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.CrCertificate, expiry, "clean", isCurrent: true, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, companyId, VendorDocumentTypes.VatCertificate, expiry, "pending_scan", isCurrent: false, Ct);
        await using var host = new ModuleHost(db.AppConnectionString);

        (await RelatedInAsync(host, TestTenants.Beta, companyId)).ShouldBeNull();
        (await ListInAsync(host, TestTenants.Beta)).ShouldNotContain(v => v.Id == companyId);

        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);

        var related = (await RelatedInAsync(host, TestTenants.Beta, companyId)).ShouldNotBeNull();
        related.NameEn.ShouldBe("Visible Documents Company");
        related.Status.ShouldBe(VendorRelationshipStatus.Pending);
        // Only the file that scanned clean; the one waiting for its scan is never handed to a tenant.
        var document = related.Documents.ShouldHaveSingleItem();
        document.Type.ShouldBe(VendorDocumentTypes.CrCertificate);
        document.ExpiresOn.ShouldBe(expiry);
        document.IsCurrent.ShouldBeTrue();
        related.BlockingDocuments.ShouldHaveSingleItem().Type.ShouldBe(VendorDocumentTypes.VatCertificate);

        await VendorRows.UnrelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);

        (await RelatedInAsync(host, TestTenants.Beta, companyId)).ShouldBeNull();
        (await ListInAsync(host, TestTenants.Beta)).ShouldNotContain(v => v.Id == companyId);
        // Acme, where the company registered, still sees it.
        (await RelatedInAsync(host, TestTenants.Acme, companyId)).ShouldNotBeNull().Documents.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task The_list_shows_the_tenants_vendors_with_their_status_and_documents_state()
    {
        var (pendingId, _) = await VendorAsync("Listed Pending Company");
        var (approvedId, _) = await VendorAsync("Listed Approved Company");
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, approvedId, VendorDocumentTypes.CrCertificate, new DateOnly(2099, 1, 1), "clean", isCurrent: true, Ct);
        await VendorDocumentRows.InsertAsync(db.OwnerConnectionString, approvedId, VendorDocumentTypes.VatCertificate, new DateOnly(2001, 1, 1), "clean", isCurrent: true, Ct);
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var host = new ModuleHost(db.AppConnectionString);
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(approvedId, officer, Ct)).IsSuccess.ShouldBeTrue();
        }

        var list = await ListInAsync(host, TestTenants.Acme);

        var pending = list.Single(v => v.Id == pendingId);
        pending.NameEn.ShouldBe("Listed Pending Company");
        pending.Status.ShouldBe(VendorRelationshipStatus.Pending);
        pending.BlockingDocuments.Select(b => (b.Type, b.Reason)).ShouldBe(
            [(VendorDocumentTypes.CrCertificate, BlockingReason.Missing), (VendorDocumentTypes.VatCertificate, BlockingReason.Missing)]);
        var approved = list.Single(v => v.Id == approvedId);
        approved.Status.ShouldBe(VendorRelationshipStatus.Approved);
        var expired = approved.BlockingDocuments.ShouldHaveSingleItem();
        expired.Type.ShouldBe(VendorDocumentTypes.VatCertificate);
        expired.Reason.ShouldBe(BlockingReason.Expired);
        (await ListInAsync(host, TestTenants.Beta)).ShouldNotContain(v => v.Id == pendingId || v.Id == approvedId);
    }

    [Fact]
    public async Task Joining_a_second_tenant_creates_a_pending_relationship_there_and_nothing_at_the_first()
    {
        var (companyId, userId) = await VendorAsync("Second Tenant Joiner");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, officer, Ct)).IsSuccess.ShouldBeTrue();
        }

        var joined = await JoinAsync(host, TestTenants.Beta, companyId, userId);

        joined.IsSuccess.ShouldBeTrue(joined.Error?.Message);
        joined.Value.ShouldBe(new VendorJoined(RelationshipCreated: true, OrganizationAdded: true));
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe(new Dictionary<Guid, string>
        {
            [TestTenants.Acme.TenantId] = "approved",
            [TestTenants.Beta.TenantId] = "pending",
        });
        accounts.Steps.ShouldBe(["add-organization"]);
        accounts.Revoked.ShouldBeEmpty();
        var audit = (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).ShouldHaveSingleItem();
        audit.SubjectId.ShouldBe(companyId.ToString());
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.joined", Ct)).ShouldBeEmpty();

        // Joining again changes nothing and writes no second entry.
        accounts.State = new(HoldsVendorRole: true, OrganizationAliases: ["acme", "beta"]);
        var again = await JoinAsync(host, TestTenants.Beta, companyId, userId);
        again.Value.ShouldBe(new VendorJoined(RelationshipCreated: false, OrganizationAdded: false));
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Beta_sees_the_vendor_only_after_it_joins()
    {
        var (companyId, userId) = await VendorAsync("Beta Watched Company");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        (await ListInAsync(host, TestTenants.Beta)).ShouldNotContain(v => v.Id == companyId);
        (await RelatedInAsync(host, TestTenants.Beta, companyId)).ShouldBeNull();

        (await JoinAsync(host, TestTenants.Beta, companyId, userId)).IsSuccess.ShouldBeTrue();

        var listed = (await ListInAsync(host, TestTenants.Beta)).Single(v => v.Id == companyId);
        listed.Status.ShouldBe(VendorRelationshipStatus.Pending);
        listed.NameEn.ShouldBe("Beta Watched Company");
        (await RelatedInAsync(host, TestTenants.Beta, companyId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task A_join_keycloak_refuses_leaves_no_relationship_and_no_audit()
    {
        var (companyId, userId) = await VendorAsync("Refused Joiner");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]), FailOrganization = true };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        var result = await JoinAsync(host, TestTenants.Beta, companyId, userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinFailed);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Joining_needs_the_vendor_context_of_the_acting_user()
    {
        var (companyId, _) = await VendorAsync("Context Checked Joiner");
        var accounts = new FakeVendorAccounts();
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        // Another user acting in this company's context: refused before Keycloak is asked anything.
        await Should.ThrowAsync<InvalidOperationException>(() => JoinAsync(host, TestTenants.Beta, companyId, "not-a-user-of-the-company"));
        await using (var scope = host.ScopeFor(TestTenants.Beta, actingUserId: "anyone"))
        {
            await Should.ThrowAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct));
        }

        accounts.Steps.ShouldBeEmpty();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
    }

    [Fact]
    public async Task The_directory_refuses_a_scope_with_a_vendor_context()
    {
        var (companyId, userId) = await VendorAsync("Vendor Context Probe");
        var (otherId, _) = await VendorAsync("Competitor Company");
        await using var host = new ModuleHost(db.AppConnectionString);
        await using var scope = host.ScopeFor(TestTenants.Acme, companyId, userId);
        var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();

        await Should.ThrowAsync<InvalidOperationException>(() => directory.ListRelatedAsync(Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => directory.GetRelatedAsync(otherId, Ct));
        await Should.ThrowAsync<InvalidOperationException>(() => directory.ApproveAsync(otherId, userId, Ct));
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, otherId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task A_membership_restored_without_a_new_relationship_is_audited_as_such_and_not_as_a_join()
    {
        // Related to acme since registration, but Keycloak no longer lists the user in acme's organization.
        var (companyId, userId) = await VendorAsync("Restored Member Company");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: []) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        var result = await JoinAsync(host, TestTenants.Acme, companyId, userId);

        result.Value.ShouldBe(new VendorJoined(RelationshipCreated: false, OrganizationAdded: true));
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.joined", Ct)).ShouldBeEmpty();
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.membership_restored", Ct))
            .ShouldHaveSingleItem().SubjectId.ShouldBe(companyId.ToString());
    }

    [Fact]
    public async Task A_failed_database_step_takes_back_the_membership_the_join_added()
    {
        var (companyId, userId) = await VendorAsync("Rolled Back Joiner");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            s.Replace(ServiceDescriptor.Scoped<IAuditWriter, FailingAuditWriter>());
        });

        var result = await JoinAsync(host, TestTenants.Beta, companyId, userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinFailed);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
        accounts.Revoked.ShouldHaveSingleItem().ShouldBe(new VendorAccessGrant(userId, TestTenants.Beta.KeycloakOrgAlias, RoleAdded: false, OrganizationAdded: true));
    }

    [Fact]
    public async Task A_failed_database_step_keeps_the_membership_when_a_relationship_exists()
    {
        var (companyId, userId) = await VendorAsync("Kept Member Company");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: []) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            s.Replace(ServiceDescriptor.Scoped<IAuditWriter, FailingAuditWriter>());
        });

        // Acme's relationship exists; the membership this join re-added belongs to it and stays.
        var result = await JoinAsync(host, TestTenants.Acme, companyId, userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinFailed);
        accounts.Steps.ShouldContain("add-organization");
        accounts.Revoked.ShouldBeEmpty();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task Two_concurrent_joins_write_exactly_one_join_entry()
    {
        var (companyId, userId) = await VendorAsync("Concurrent Joiner");
        // Both calls report the membership as added, the worst case for the audit.
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        var results = await Task.WhenAll(
            Task.Run(() => JoinAsync(host, TestTenants.Beta, companyId, userId), Ct),
            Task.Run(() => JoinAsync(host, TestTenants.Beta, companyId, userId), Ct));

        results.ShouldAllBe(r => r.IsSuccess);
        results.Count(r => r.Value.RelationshipCreated).ShouldBe(1);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).Count.ShouldBe(1);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Beta.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task Two_concurrent_approvals_write_exactly_one_approval_entry()
    {
        var (companyId, _) = await VendorAsync("Concurrently Approved Company");
        var officer = await StaffAsync(TestTenants.Acme, TenantRoles.ContractsOfficer);
        await using var host = new ModuleHost(db.AppConnectionString);

        async Task<Platform.Shared.Results.Result<VendorRelationshipStatus>> ApproveOnceAsync()
        {
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
            return await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, officer, Ct);
        }

        var results = await Task.WhenAll(Task.Run(ApproveOnceAsync, Ct), Task.Run(ApproveOnceAsync, Ct));

        results.Count(r => r.IsSuccess).ShouldBe(1);
        results.Single(r => !r.IsSuccess).Error!.Code.ShouldBe(VendorDirectoryErrors.AlreadyApproved);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, officer, "vendor.approved", Ct)).Count.ShouldBe(1);
    }

    private static async Task<Platform.Shared.Results.Result<VendorJoined>> JoinAsync(ModuleHost host, Platform.Shared.Tenancy.TenantContext tenant, Guid companyId, string userId)
    {
        await using var scope = host.ScopeFor(tenant, companyId, userId);
        return await scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct);
    }

    private static async Task<RelatedVendorDetails?> RelatedInAsync(ModuleHost host, Platform.Shared.Tenancy.TenantContext tenant, Guid companyId)
    {
        await using var scope = host.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().GetRelatedAsync(companyId, Ct);
    }

    private static async Task<IReadOnlyList<RelatedVendor>> ListInAsync(ModuleHost host, Platform.Shared.Tenancy.TenantContext tenant)
    {
        await using var scope = host.ScopeFor(tenant);
        return await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ListRelatedAsync(Ct);
    }

    private async Task<(Guid CompanyId, string UserId)> VendorAsync(string nameEn)
    {
        var userId = Guid.NewGuid().ToString();
        var companyId = await VendorRows.RegisterAsync(db.AppConnectionString, TestTenants.Acme, userId, VendorRows.NewCrNumber(), nameEn, Ct);
        return (companyId, userId);
    }

    /// <summary>An audit log whose database refuses every write, as a failed insert into audit.events would.</summary>
    private sealed class FailingAuditWriter : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("forced audit failure", new InvalidOperationException("forced"));
    }

    private async Task<string> StaffAsync(Platform.Shared.Tenancy.TenantContext tenant, string role)
    {
        var subject = $"{role}-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, subject, $"{subject}@{tenant.Slug}.test", [role], "active", Ct);
        return subject;
    }
}
