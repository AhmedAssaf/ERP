using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Modules.Vendors.Persistence;

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
        // Order-free: the rows come back in heap order, and the approval's new tuple version can land after beta's row.
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe(
            new Dictionary<Guid, string>
            {
                [TestTenants.Acme.TenantId] = "approved",
                [TestTenants.Beta.TenantId] = "pending",
            },
            ignoreOrder: true);
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
    public async Task A_user_of_a_related_company_who_lost_the_membership_cannot_restore_it_by_joining()
    {
        // W-21 pentest P-1: related to acme since registration, but acme removed the user from its organization. Joining
        // must not put it back; restoring access is the tenant's decision. The refusal is audited in acme's log.
        var (companyId, userId) = await VendorAsync("Removed Member Company");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: []) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        var result = await JoinAsync(host, TestTenants.Acme, companyId, userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.MembershipRemoved);
        accounts.Steps.ToArray().ShouldBe(["describe"], customMessage: "Keycloak is asked, never changed");
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.membership_restored", Ct)).ShouldBeEmpty();
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.membership_restore_refused", Ct))
            .ShouldHaveSingleItem().SubjectId.ShouldBe(companyId.ToString());
    }

    [Fact]
    public async Task A_user_of_a_related_company_still_in_the_organization_joins_again_without_any_change()
    {
        // The button stays useful for a member whose token does not carry the organization yet: nothing to restore.
        var (companyId, userId) = await VendorAsync("Still Member Company");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        var result = await JoinAsync(host, TestTenants.Acme, companyId, userId);

        result.Value.ShouldBe(new VendorJoined(RelationshipCreated: false, OrganizationAdded: false));
        accounts.Steps.ToArray().ShouldBe(["describe"]);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, userId, "vendor.membership_restore_refused", Ct)).ShouldBeEmpty();
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
    public async Task A_related_company_is_refused_without_any_keycloak_change_even_when_the_audit_fails()
    {
        var (companyId, userId) = await VendorAsync("Refused Despite Audit Failure");
        var accounts = new FakeVendorAccounts { State = new(HoldsVendorRole: true, OrganizationAliases: []) };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            s.Replace(ServiceDescriptor.Scoped<IAuditWriter, FailingAuditWriter>());
        });

        var result = await JoinAsync(host, TestTenants.Acme, companyId, userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.MembershipRemoved);
        accounts.Steps.ToArray().ShouldBe(["describe"]);
        accounts.Revoked.ShouldBeEmpty();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task A_failed_database_step_keeps_the_membership_when_a_parallel_join_created_the_relationship_meanwhile()
    {
        // The only way into UndoAsync's "relationship exists, keep the membership" branch since W-21: beta is not related
        // when this join checks, a parallel join of the same vendor creates the relationship while this one is adding the
        // membership in Keycloak, and this join's own database step then fails. The membership belongs to the winner's
        // relationship and must stay; taking it back would lock the vendor out of a tenant it works with.
        var (companyId, userId) = await VendorAsync("Parallel Join Winner Company");
        var accounts = new FakeVendorAccounts
        {
            State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]),
            OnAddOrganization = _ => VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct),
        };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            s.Replace(ServiceDescriptor.Scoped<IAuditWriter, FailingAuditWriter>());
        });

        var result = await JoinAsync(host, TestTenants.Beta, companyId, userId);

        result.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinFailed);
        accounts.Steps.ToArray().ShouldBe(["add-organization"]);
        accounts.Revoked.ShouldBeEmpty("the parallel join's relationship owns the membership now");
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe(
            [TestTenants.Acme.TenantId, TestTenants.Beta.TenantId], ignoreOrder: true);
    }

    [Fact]
    public async Task Two_concurrent_joins_write_exactly_one_join_entry()
    {
        var (companyId, userId) = await VendorAsync("Concurrent Joiner");
        // Both calls report the membership as added, the worst case for the audit. Adding the membership comes after a
        // join's relationship check and before its insert, so holding each call there until both arrive makes both pass the
        // check before either inserts. Without it the scheduler may finish one join before the other starts (seen on CI):
        // the second then takes the related-company path, which this fake answers from State, without the membership the
        // first join added, and refuses with MembershipRemoved; that path has its own tests above.
        var bothChecked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var accounts = new FakeVendorAccounts
        {
            State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]),
            OnAddOrganization = async _ =>
            {
                if (Interlocked.Increment(ref arrived) == 2)
                {
                    bothChecked.SetResult();
                }

                await bothChecked.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            },
        };
        await using var host = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));

        var results = await Task.WhenAll(
            Task.Run(() => JoinAsync(host, TestTenants.Beta, companyId, userId), Ct),
            Task.Run(() => JoinAsync(host, TestTenants.Beta, companyId, userId), Ct));

        results.ShouldAllBe(r => r.IsSuccess, string.Join("; ", results.Where(r => !r.IsSuccess).Select(r => $"{r.Error!.Code}: {r.Error.Message}")));
        accounts.Steps.ToArray().ShouldBe(["add-organization", "add-organization"]);
        results.Count(r => r.Value.RelationshipCreated).ShouldBe(1);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).Count.ShouldBe(1);
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.membership_restored", Ct)).Count.ShouldBe(1);
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Beta.TenantId].ShouldBe("pending");
    }

    [Fact]
    public async Task A_join_arriving_while_a_failed_join_takes_back_its_membership_ends_related_and_in_the_organization()
    {
        // W-40, first interleaving: join A added the membership, its database step failed, and its undo found no relationship
        // and is taking the membership back. Join B of the same vendor arrives at that moment. Before W-40, B's Keycloak add
        // answered "already a member" (A's membership), B committed its relationship, and A's revoke then removed the
        // membership B relied on: related to beta but outside its organization, so every later join was refused. Now the
        // undo holds the join lock while it takes the membership back, so B waits and adds the membership itself.
        var (companyId, userId) = await VendorAsync("Undo Race Late Joiner");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var secondAdding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adds = 0;
        var accounts = new FakeVendorAccounts
        {
            State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]),
            AnswerFromMemberships = true,
            OnAddOrganization = _ =>
            {
                if (Interlocked.Increment(ref adds) == 2)
                {
                    secondAdding.TrySetResult();
                }

                return Task.CompletedTask;
            },
        };
        await using var failing = new ModuleHost(db.AppConnectionString, configure: s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            s.Replace(ServiceDescriptor.Scoped<IAuditWriter, FailingAuditWriter>());
        });
        await using var working = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));
        Task<Platform.Shared.Results.Result<VendorJoined>>? second = null;
        accounts.OnRevoke = async _ =>
        {
            second = Task.Run(() => JoinAsync(working, TestTenants.Beta, companyId, userId), Ct);
            var waiting = JoinLockWaiterAsync(TestTenants.Beta.TenantId, companyId, stop.Token);
            if (await Task.WhenAny(secondAdding.Task, waiting).WaitAsync(TimeSpan.FromSeconds(30), Ct) == secondAdding.Task)
            {
                // B got past the point where it could wait for this undo: let it commit before the membership goes.
                await second.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            }
        };

        Platform.Shared.Results.Result<VendorJoined> first, joined;
        try
        {
            first = await JoinAsync(failing, TestTenants.Beta, companyId, userId);
            joined = await second.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            // The pg_locks poller stops here, also when a join above threw.
            await stop.CancelAsync();
        }

        first.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinFailed);
        joined.IsSuccess.ShouldBeTrue(joined.Error?.Message);
        joined.Value.RelationshipCreated.ShouldBeTrue();
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Beta.TenantId].ShouldBe("pending");
        accounts.OrganizationsOf(userId).ShouldContain(TestTenants.Beta.KeycloakOrgAlias, "related to beta, so a member of its organization");
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_join_that_relied_on_the_membership_of_a_failed_join_keeps_it()
    {
        // W-40, second interleaving: join B of the same vendor has already found A's membership in Keycloak ("already a
        // member") and is about to save its relationship when A's database step fails. Before W-40, A's undo found no
        // relationship yet and took the membership back while B committed. Now B holds the join lock from before its
        // Keycloak add until its commit, so A's undo waits, finds B's relationship and keeps the membership.
        var (companyId, userId) = await VendorAsync("Undo Race Early Joiner");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var secondAdding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adds = 0;
        var accounts = new FakeVendorAccounts
        {
            State = new(HoldsVendorRole: true, OrganizationAliases: ["acme"]),
            AnswerFromMemberships = true,
            OnAddOrganization = async _ =>
            {
                if (Interlocked.Increment(ref adds) == 2)
                {
                    secondAdding.TrySetResult();
                    await releaseSecond.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
                }
            },
        };
        await using var working = new ModuleHost(db.AppConnectionString, configure: s => s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts)));
        Task<Platform.Shared.Results.Result<VendorJoined>>? second = null;
        var auditOfFirst = new CallbackAuditWriter(async () =>
        {
            // A has added the membership and is in its database step: start B and hold it in its Keycloak add.
            second = Task.Run(() => JoinAsync(working, TestTenants.Beta, companyId, userId), Ct);
            await secondAdding.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
            // Let B go on once A's undo waits for it; an undo that does not wait releases B from its revoke instead.
            _ = JoinLockWaiterAsync(TestTenants.Beta.TenantId, companyId, stop.Token)
                .ContinueWith(t => releaseSecond.TrySetResult(), TaskScheduler.Default);
            throw new DbUpdateException("forced audit failure", new InvalidOperationException("forced"));
        });
        await using var failing = new ModuleHost(db.AppConnectionString, configure: s =>
        {
            s.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => accounts));
            s.Replace(ServiceDescriptor.Scoped<IAuditWriter>(_ => auditOfFirst));
        });
        accounts.OnRevoke = async _ =>
        {
            releaseSecond.TrySetResult();
            await second.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        };

        Platform.Shared.Results.Result<VendorJoined> first, joined;
        try
        {
            first = await JoinAsync(failing, TestTenants.Beta, companyId, userId);
            joined = await second.ShouldNotBeNull().WaitAsync(TimeSpan.FromSeconds(30), Ct);
        }
        finally
        {
            // The pg_locks poller stops here, also when a join above threw.
            await stop.CancelAsync();
        }

        first.Error.ShouldNotBeNull().Code.ShouldBe(VendorErrors.JoinFailed);
        joined.Value.ShouldBe(new VendorJoined(RelationshipCreated: true, OrganizationAdded: false));
        accounts.Revoked.ShouldBeEmpty("B's relationship owns the membership A added");
        accounts.OrganizationsOf(userId).ShouldContain(TestTenants.Beta.KeycloakOrgAlias, "related to beta, so a member of its organization");
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct))[TestTenants.Beta.TenantId].ShouldBe("pending");
        (await VendorRows.AuditsAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, userId, "vendor.joined", Ct)).Count.ShouldBe(1);
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
        // W-33: approval needs a verified owner; these tests are about approval itself (CrOwnershipTests covers the check).
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, TestTenants.Acme.TenantId, Ct);
        return (companyId, userId);
    }

    /// <summary>An audit log whose database refuses every write, as a failed insert into audit.events would.</summary>
    private sealed class FailingAuditWriter : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("forced audit failure", new InvalidOperationException("forced"));
    }

    /// <summary>An audit log that runs a test's step and then fails as <see cref="FailingAuditWriter"/> does (the step throws).</summary>
    private sealed class CallbackAuditWriter(Func<Task> write) : IAuditWriter
    {
        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default) => write();
    }

    /// <summary>
    /// True once a session waits for the join lock of the tenant and company (W-40, key from <c>JoinLock.Key</c>), false
    /// when <paramref name="stop"/> fires first. A bigint advisory key shows in pg_locks as classid (its high 32 bits) and
    /// objid (its low 32 bits) with objsubid 1.
    /// </summary>
    private async Task<bool> JoinLockWaiterAsync(Guid tenantId, Guid companyId, CancellationToken stop)
    {
        try
        {
            await using var connection = new NpgsqlConnection(db.OwnerConnectionString);
            await connection.OpenAsync(stop);
            await using var command = new NpgsqlCommand(
                """
                select exists (
                    select 1 from pg_locks
                    where locktype = 'advisory' and not granted and objsubid = 1
                      and ((classid::bigint << 32) | objid::bigint) = hashtextextended(@key, 0))
                """,
                connection);
            command.Parameters.AddWithValue("key", JoinLock.Key(tenantId, companyId));
            while (!(bool)(await command.ExecuteScalarAsync(stop))!)
            {
                await Task.Delay(20, stop);
            }

            return true;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // The test has its answer; nobody waits for this one any more.
            return false;
        }
    }

    private async Task<string> StaffAsync(Platform.Shared.Tenancy.TenantContext tenant, string role)
    {
        var subject = $"{role}-{Guid.NewGuid():N}";
        await MemberRows.InsertAsync(db.AppConnectionString, tenant.TenantId, subject, $"{subject}@{tenant.Slug}.test", [role], "active", Ct);
        return subject;
    }
}
