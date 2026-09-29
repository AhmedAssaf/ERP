using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Platform.IntegrationTests.Infrastructure;
using Platform.Modules.Identity.Contracts;
using Platform.Modules.Vendors.Contracts;
using Platform.Shared.Results;
using static Platform.IntegrationTests.Security.CrAttack;

namespace Platform.IntegrationTests.Security;

/// <summary>
/// Pentest of W-33 (CR ownership check before the first approval, migration 0018): the application role's sessions of
/// every kind against the new tables and functions, a tenant officer reaching past their tenant, every other way to an
/// approved relationship, and the race between an approval in flight and a dispute that is raised and upheld meanwhile.
/// Each test states the property; a red one is a finding that stays red until the code is fixed.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CrOwnershipAttackTests(DatabaseFixture db)
{
    // 1. Who may call the functions and touch the tables -------------------------------------------------------------

    [Fact]
    public async Task Sessions_without_the_right_context_cannot_set_the_method_verify_ownership_or_resolve_a_dispute()
    {
        var (companyId, vendorUser, crNumber) = await VendorAsync(db, "Caller Matrix Co");
        var officer = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);
        var tenantAdmin = await StaffAsync(db, TestTenants.Acme, TenantRoles.TenantAdmin);
        var disputeId = await RaiseAsync(Guid.NewGuid().ToString(), crNumber);

        // (session, function): each must be refused with insufficient_privilege and change nothing.
        var sessions = new (string Name, Guid? Tenant, Guid? Vendor, string? User)[]
        {
            ("vendor", TestTenants.Acme.TenantId, companyId, vendorUser),
            ("vendor context without tenant", null, companyId, vendorUser),
            ("no context, no user", null, null, null),
            ("tenant, no user", TestTenants.Acme.TenantId, null, null),
        };
        var calls = new (string Name, string Sql, bool OfficerMayCall)[]
        {
            ("set_ownership_method", "select vendor.set_ownership_method('wathq')", false),
            ("verify_ownership", "select vendor.verify_ownership(@company, 'manual', 'Forged check.')", true),
            ("resolve_cr_dispute", "select * from vendor.resolve_cr_dispute(@dispute, true, 'Forged uphold.')", false),
        };

        foreach (var (sessionName, tenant, vendor, user) in sessions)
        {
            foreach (var (callName, sql, _) in calls)
            {
                await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
                await using var command = new NpgsqlCommand(sql, session);
                command.Parameters.AddWithValue("company", companyId);
                command.Parameters.AddWithValue("dispute", disputeId);
                var ex = await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct), $"{sessionName} calling {callName}");
                ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{sessionName} calling {callName}: {ex.MessageText}");
            }
        }

        // A contracts officer or tenant admin verifies, but never sets the platform's method or resolves a dispute.
        foreach (var ((callName, sql, _), staff) in calls.Where(c => !c.OfficerMayCall).SelectMany(c => new[] { (c, officer), (c, tenantAdmin) }))
        {
            await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Acme.TenantId, null, staff, Ct);
            await using var command = new NpgsqlCommand(sql, session);
            command.Parameters.AddWithValue("company", companyId);
            command.Parameters.AddWithValue("dispute", disputeId);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct), $"officer calling {callName}"))
                .SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        }

        (await OwnershipRows.MethodAsync(db.OwnerConnectionString, Ct)).ShouldBe("manual");
        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("open");
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(vendorUser, "vendor-admin")]);
    }

    [Fact]
    public async Task No_session_of_the_app_role_writes_verifications_disputes_or_the_method_directly()
    {
        var (companyId, vendorUser, crNumber) = await VendorAsync(db, "Direct Write Co");
        var officer = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);
        var disputeId = await RaiseAsync(Guid.NewGuid().ToString(), crNumber);
        var admin = $"platform-admin-{Guid.NewGuid():N}";

        var sessions = new (string Name, Guid? Tenant, Guid? Vendor, string? User)[]
        {
            ("officer", TestTenants.Acme.TenantId, null, officer),
            ("vendor", TestTenants.Acme.TenantId, companyId, vendorUser),
            ("platform console", null, null, admin),
            ("no context", null, null, null),
        };
        var statements = new[]
        {
            "select count(*) from vendor.ownership_verifications",
            "insert into vendor.ownership_verifications (company_id, method, registrant_user_id, verified_by, verified_in_tenant, note) values (@company, 'manual', 'x', 'x', @tenant, 'Forged.')",
            "update vendor.ownership_verifications set registrant_user_id = 'x' where company_id = @company",
            "delete from vendor.ownership_verifications where company_id = @company",
            "insert into vendor.cr_disputes (id, company_id, claimant_user_id, claimant_email, claimant_name, statement, privacy_notice_version, privacy_notice_culture, raised_on_tenant) values (gen_random_uuid(), @company, 'x', 'x@x.test', 'X', 'Forged.', 'V1', 'en-US', @tenant)",
            "update vendor.cr_disputes set status = 'rejected', resolved_by = 'x', resolved_at = now(), resolution_note = 'Forged.' where id = @dispute",
            "delete from vendor.cr_disputes where id = @dispute",
            "update vendor.ownership_settings set method = 'wathq'",
            "insert into vendor.ownership_settings (id, method) values (false, 'wathq')",
            "delete from vendor.ownership_settings",
        };

        foreach (var (name, tenant, vendor, user) in sessions)
        {
            foreach (var sql in statements)
            {
                await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
                await using var command = new NpgsqlCommand(sql, session);
                command.Parameters.AddWithValue("company", companyId);
                command.Parameters.AddWithValue("dispute", disputeId);
                command.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId);
                var ex = await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct), $"{name}: {sql}");
                ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege, $"{name}: {sql}");
            }
        }

        (await OwnershipRows.DisputeAsync(db.OwnerConnectionString, disputeId, Ct)).ShouldNotBeNull().Status.ShouldBe("open");
        (await OwnershipRows.MethodAsync(db.OwnerConnectionString, Ct)).ShouldBe("manual");
    }

    [Fact]
    public async Task A_session_without_tenant_vendor_or_acting_user_reads_no_dispute()
    {
        // Claimants' emails, names and statements are personal data. The select policy on vendor.cr_disputes admits any
        // session without a tenant or vendor context, also one without an acting user (the worker, the migrator's app
        // connection, an anonymous request on a host without a tenant); open_cr_disputes() asks for an acting user, the
        // table's own policy does not.
        var (_, _, crNumber) = await VendorAsync(db, "Contextless Read Co");
        await RaiseAsync(Guid.NewGuid().ToString(), crNumber);

        await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, null, null, null, Ct);
        await using var command = new NpgsqlCommand("select count(*)::int from vendor.cr_disputes", session);

        ((int)(await command.ExecuteScalarAsync(Ct))!).ShouldBe(0, "a session with no acting user read claimants' disputes");
    }

    // 2. Tenant A's officer against tenant B -------------------------------------------------------------------------

    [Fact]
    public async Task An_officer_of_tenant_a_cannot_verify_or_approve_in_tenant_bs_context()
    {
        var (companyId, _, _) = await VendorAsync(db, "Cross Tenant Verify Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var acmeOfficer = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);

        await using (var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Beta.TenantId, null, acmeOfficer, Ct))
        {
            foreach (var sql in new[] { "select vendor.verify_ownership(@company, 'manual', 'Forged.')", "select vendor.approve_relationship(@company)" })
            {
                await using var command = new NpgsqlCommand(sql, session);
                command.Parameters.AddWithValue("company", companyId);
                (await Should.ThrowAsync<PostgresException>(() => command.ExecuteScalarAsync(Ct), sql)).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            }
        }

        await using var host = Host();
        await using (var scope = host.ScopeFor(TestTenants.Beta, actingUserId: acmeOfficer))
        {
            var result = await scope.ServiceProvider.GetRequiredService<IVendorDirectory>()
                .ApproveAsync(companyId, acmeOfficer, new OwnershipConfirmation(OfficerNote, false, CrLookupOutcome.Manual), Ct);
            result.IsSuccess.ShouldBeFalse();
            result.Error.Code.ShouldBe(VendorDirectoryErrors.NotAllowed);
        }

        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
        (await RelationshipStatusAsync(db, TestTenants.Beta.TenantId, companyId)).ShouldBe("pending");
        (await RelationshipStatusAsync(db, TestTenants.Acme.TenantId, companyId)).ShouldBe("pending");
    }

    [Fact]
    public async Task An_officer_cannot_verify_or_read_the_ownership_of_a_company_their_tenant_does_not_work_with()
    {
        var (companyId, _, _) = await VendorAsync(db, "Beta Only Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        await VendorRows.UnrelateAsync(db.OwnerConnectionString, TestTenants.Acme.TenantId, companyId, Ct);
        var acmeOfficer = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);

        await using (var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Acme.TenantId, null, acmeOfficer, Ct))
        {
            await using (var verify = new NpgsqlCommand("select vendor.verify_ownership(@company, 'manual', 'Forged.')", session))
            {
                verify.Parameters.AddWithValue("company", companyId);
                (await Should.ThrowAsync<PostgresException>(() => verify.ExecuteScalarAsync(Ct))).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            }

            await using var read = new NpgsqlCommand("select count(*)::int from vendor.related_ownership(@company)", session);
            read.Parameters.AddWithValue("company", companyId);
            ((int)(await read.ExecuteScalarAsync(Ct))!).ShouldBe(0);
        }

        await using var host = Host();
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: acmeOfficer))
        {
            var directory = scope.ServiceProvider.GetRequiredService<IVendorDirectory>();
            (await directory.GetOwnershipCheckAsync(companyId, Ct)).ShouldBeNull();
            (await directory.ApproveAsync(companyId, acmeOfficer, new OwnershipConfirmation(OfficerNote, false, CrLookupOutcome.Manual), Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(VendorDirectoryErrors.NotFound);
        }

        (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_second_tenant_learns_the_method_but_not_the_officer_the_tenant_or_the_note()
    {
        var (companyId, _, _) = await VendorAsync(db, "Who Verified Co");
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var acmeOfficer = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);
        var betaOfficer = await StaffAsync(db, TestTenants.Beta, TenantRoles.ContractsOfficer);
        var note = $"Checked certificate {Guid.NewGuid():N}.";
        await using var host = Host();
        await using (var scope = host.ScopeFor(TestTenants.Acme, actingUserId: acmeOfficer))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>()
                .ApproveAsync(companyId, acmeOfficer, new OwnershipConfirmation(note, false, CrLookupOutcome.Manual), Ct)).IsSuccess.ShouldBeTrue();
        }

        // Everything a Beta staff session can get about the company's ownership, as text.
        var seen = new List<string>();
        await using (var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Beta.TenantId, null, betaOfficer, Ct))
        {
            foreach (var sql in new[]
                     {
                         "select row_to_json(o)::text from vendor.related_ownership(@company) o",
                         "select coalesce(json_agg(e)::text, '') from audit.events e where e.subject_id = @company::text",
                         "select coalesce(json_agg(p)::text, '') from ops.platform_audit p where p.subject_id = @company::text",
                         "select coalesce(json_agg(c)::text, '') from vendor.related_company(@company) c",
                     })
            {
                await using var command = new NpgsqlCommand(sql, session);
                command.Parameters.AddWithValue("company", companyId);
                try
                {
                    seen.Add((string?)await command.ExecuteScalarAsync(Ct) ?? string.Empty);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege)
                {
                    // Not readable at all from a tenant session: nothing learnt.
                }
            }
        }

        var all = string.Join('\n', seen);
        all.ShouldContain("\"method\":\"manual\"");
        all.ShouldContain("\"verified_here\":false");
        all.ShouldNotContain(acmeOfficer);
        all.ShouldNotContain(TestTenants.Acme.TenantId.ToString());
        all.ShouldNotContain(note);
    }

    // 3. No other way to an approved relationship ---------------------------------------------------------------------

    [Fact]
    public async Task Joining_another_tenant_leaves_an_unverified_company_pending_and_unapprovable_there()
    {
        var (companyId, vendorUser, _) = await VendorAsync(db, "Join Path Co");
        var betaOfficer = await StaffAsync(db, TestTenants.Beta, TenantRoles.TenantAdmin);
        await using var host = Host();
        await using (var scope = host.ScopeFor(TestTenants.Beta, vendorCompanyId: companyId, actingUserId: vendorUser))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct)).IsSuccess.ShouldBeTrue();
        }

        (await RelationshipStatusAsync(db, TestTenants.Beta.TenantId, companyId)).ShouldBe("pending");
        await using (var scope = host.ScopeFor(TestTenants.Beta, actingUserId: betaOfficer))
        {
            (await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, betaOfficer, Ct))
                .Error.ShouldNotBeNull().Code.ShouldBe(CrOwnershipErrors.Unverified);
        }

        (await RelationshipStatusAsync(db, TestTenants.Beta.TenantId, companyId)).ShouldBe("pending");
    }

    [Fact]
    public async Task No_session_of_the_app_role_writes_a_relationship_status_directly()
    {
        var (companyId, vendorUser, _) = await VendorAsync(db, "Direct Approve Co");
        var officer = await StaffAsync(db, TestTenants.Acme, TenantRoles.TenantAdmin);
        foreach (var (tenant, vendor, user) in new (Guid?, Guid?, string?)[]
                 {
                     (TestTenants.Acme.TenantId, null, officer),
                     (TestTenants.Acme.TenantId, companyId, vendorUser),
                     (null, null, "platform-admin"),
                 })
        {
            foreach (var sql in new[]
                     {
                         "update vendor.relationships set status = 'approved', approved_by = 'x' where company_id = @company",
                         "insert into vendor.relationships (tenant_id, company_id, status, approved_by) values (@beta, @company, 'approved', 'x')",
                         "delete from vendor.relationships where company_id = @company",
                     })
            {
                await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, tenant, vendor, user, Ct);
                await using var command = new NpgsqlCommand(sql, session);
                command.Parameters.AddWithValue("company", companyId);
                command.Parameters.AddWithValue("beta", TestTenants.Beta.TenantId);
                (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync(Ct), sql)).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
            }
        }

        var relationships = await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct);
        relationships.Count.ShouldBe(1);
        relationships[TestTenants.Acme.TenantId].ShouldBe("pending");
    }

    // 5. A dispute raised and upheld while an approval is in flight ---------------------------------------------------

    [Fact]
    public async Task An_approval_in_flight_when_a_dispute_is_raised_does_not_commit_on_the_squatters_check()
    {
        // The officer's approval (verify_ownership, then approve_relationship) has passed its checks and waits on the
        // relationship row (held here as any concurrent writer of that row would). Meanwhile the real owner raises a
        // dispute and the platform admin upholds it. The approval must not then commit as if nothing happened: either the
        // dispute waits for the approval (it is then raised against an approved company), or the approval is refused.
        var (companyId, squatter, crNumber) = await VendorAsync(db, "In Flight Co");
        var officer = await StaffAsync(db, TestTenants.Acme, TenantRoles.ContractsOfficer);
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host();

        await using var holder = new NpgsqlConnection(db.OwnerConnectionString);
        await holder.OpenAsync(Ct);
        await using var hold = await holder.BeginTransactionAsync(Ct);
        await using (var lockRow = new NpgsqlCommand(
                         "select 1 from vendor.relationships where tenant_id = @tenant and company_id = @company for update", holder, hold))
        {
            lockRow.Parameters.AddWithValue("tenant", TestTenants.Acme.TenantId);
            lockRow.Parameters.AddWithValue("company", companyId);
            await lockRow.ExecuteScalarAsync(Ct);
        }

        var approval = Task.Run(async () =>
        {
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: officer);
            return await scope.ServiceProvider.GetRequiredService<IVendorDirectory>()
                .ApproveAsync(companyId, officer, new OwnershipConfirmation(OfficerNote, false, CrLookupOutcome.Manual), Ct);
        }, Ct);
        await WaitUntilBlockedOrDoneAsync(db, "approve_relationship", approval);
        approval.IsCompleted.ShouldBeFalse("the approval passed its checks and waits on the relationship row");

        var raise = Task.Run(async () =>
        {
            await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
            return await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), "owner@in-flight.test", "Real Owner", Ct);
        }, Ct);
        await WaitUntilBlockedOrDoneAsync(db, "raise_cr_dispute", raise);

        string? statusWhenDisputeCommitted = null;
        Task<Result<CrDisputeUpheld>>? uphold = null;
        if (raise.IsCompleted)
        {
            statusWhenDisputeCommitted = await RelationshipStatusAsync(db, TestTenants.Acme.TenantId, companyId);
            uphold = UpholdAsync(host, admin, (await raise).Value);
            await WaitUntilBlockedOrDoneAsync(db, "resolve_cr_dispute", uphold);
        }

        await hold.RollbackAsync(Ct);
        var approved = await approval;
        var raised = await raise;
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        statusWhenDisputeCommitted ??= await RelationshipStatusAsync(db, TestTenants.Acme.TenantId, companyId);
        var upheld = await (uphold ?? UpholdAsync(host, admin, raised.Value));

        upheld.IsSuccess.ShouldBeTrue(upheld.Error?.Message);
        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe([(claimant, "vendor-admin")]);
        var verification = (await OwnershipRows.VerificationAsync(db.OwnerConnectionString, companyId, Ct)).ShouldNotBeNull();
        (verification.Method, verification.RegistrantUserId).ShouldBe(("dispute", claimant));
        verification.RegistrantUserId.ShouldNotBe(squatter);
        var finalStatus = await RelationshipStatusAsync(db, TestTenants.Acme.TenantId, companyId);
        (statusWhenDisputeCommitted == "approved" || finalStatus == "pending").ShouldBeTrue(
            $"the approval (result: {(approved.IsSuccess ? "success" : approved.Error.Code)}) committed after the dispute against the squatter's " +
            $"registration was already open (status when the dispute committed: {statusWhenDisputeCommitted}, final: {finalStatus})");
    }

    // After an uphold, the removed vendor users --------------------------------------------------------------------

    [Fact]
    public async Task A_removed_users_open_vendor_scope_cannot_act_for_the_company_after_an_uphold()
    {
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Stale Scope Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        var recipient = await ConsentRows.AddRecipientAsync(db.OwnerConnectionString, $"Stale Scope Recipient {Guid.NewGuid():N}", Ct);
        await using var host = Host();

        // The squatter's circuit opened before the uphold keeps its vendor context (ADR-0013 consequence, W-21).
        await using var stale = host.ScopeFor(TestTenants.Beta, vendorCompanyId: companyId, actingUserId: squatter);
        var disputeId = await RaiseAsync(claimant, crNumber);
        (await UpholdAsync(host, admin, disputeId)).IsSuccess.ShouldBeTrue();

        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(3));
        (await stale.ServiceProvider.GetRequiredService<IConsentLedger>()
            .GrantAsync(recipient, ConsentScope.AwardRecords, today.AddDays(1), today.AddDays(30), squatter, Ct)).IsSuccess.ShouldBeFalse();
        (await ConsentRows.ForCompanyAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBeEmpty();
        Result<VendorJoined>? joined = null;
        try
        {
            joined = await stale.ServiceProvider.GetRequiredService<IVendorJoin>().JoinAsync(Ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PostgresException)
        {
            // Refused: the property holds.
        }

        (joined?.IsSuccess ?? false).ShouldBeFalse("the removed squatter's scope joined another tenant for the company");
        (await VendorRows.RelationshipsAsync(db.OwnerConnectionString, companyId, Ct)).Keys.ShouldBe([TestTenants.Acme.TenantId]);
    }

    [Fact]
    public async Task A_removed_users_stale_vendor_session_cannot_write_itself_back_into_the_company()
    {
        // erp_app holds INSERT on vendor.vendor_users under a policy keyed on the company only, so a session that still
        // carries the company's vendor context (the removed squatter's open circuit) can add any user as vendor admin.
        var (companyId, squatter, crNumber) = await VendorAsync(db, "Write Back Co");
        var claimant = Guid.NewGuid().ToString();
        var admin = $"platform-admin-{Guid.NewGuid():N}";
        await using var host = Host();
        (await UpholdAsync(host, admin, await RaiseAsync(claimant, crNumber))).IsSuccess.ShouldBeTrue();

        await using var session = await OwnershipRows.AppSessionAsync(db.AppConnectionString, TestTenants.Acme.TenantId, companyId, squatter, Ct);
        await using var command = new NpgsqlCommand("""
            insert into vendor.vendor_users (id, company_id, user_id, role, privacy_notice_version, privacy_notice_culture, privacy_accepted_at)
            values (gen_random_uuid(), @company, @user, 'vendor-admin', 'V1', 'en-US', now())
            """, session);
        command.Parameters.AddWithValue("company", companyId);
        command.Parameters.AddWithValue("user", squatter);
        try
        {
            await command.ExecuteNonQueryAsync(Ct);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.InsufficientPrivilege or PostgresErrorCodes.CheckViolation)
        {
            // Refused: the property holds.
        }

        (await OwnershipRows.VendorUsersAsync(db.OwnerConnectionString, companyId, Ct)).ShouldBe(
            [(claimant, "vendor-admin")], "the removed squatter wrote itself back as the company's vendor admin");
    }

    // Abuse of the dispute hold -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_dispute_from_a_throwaway_account_does_not_block_an_already_verified_company_at_a_new_tenant()
    {
        // Anyone who can sign up on a tenant host can raise a dispute (3 open per account, accounts are free). An open
        // dispute refuses every pending approval of the company, also one whose ownership an officer verified already, so
        // a competitor can keep a verified vendor from being approved by any new buyer until WaslaBid closes the dispute.
        var (companyId, _, crNumber) = await VendorAsync(db, "Griefed Vendor Co");
        await OwnershipRows.VerifyAsOwnerAsync(db.OwnerConnectionString, companyId, TestTenants.Acme.TenantId, Ct);
        await VendorRows.RelateAsync(db.OwnerConnectionString, TestTenants.Beta.TenantId, companyId, Ct);
        var betaOfficer = await StaffAsync(db, TestTenants.Beta, TenantRoles.ContractsOfficer);
        await RaiseAsync(Guid.NewGuid().ToString(), crNumber);
        await using var host = Host();

        await using var scope = host.ScopeFor(TestTenants.Beta, actingUserId: betaOfficer);
        var result = await scope.ServiceProvider.GetRequiredService<IVendorDirectory>().ApproveAsync(companyId, betaOfficer, Ct);

        result.IsSuccess.ShouldBeTrue($"a throwaway account's dispute held a verified company's approval ({result.Error?.Code})");
    }

    private ModuleHost Host(FakeVendorAccounts? accounts = null)
    {
        var fake = accounts ?? new FakeVendorAccounts();
        return new(db.AppConnectionString, configure: services => services.Replace(ServiceDescriptor.Scoped<IVendorAccounts>(_ => fake)));
    }

    private async Task<Guid> RaiseAsync(string claimant, string crNumber)
    {
        await using var host = Host();
        await using var scope = host.ScopeFor(TestTenants.Acme, actingUserId: claimant);
        var raised = await scope.ServiceProvider.GetRequiredService<ICrDisputes>().RaiseAsync(Request(crNumber), $"{claimant}@claimant.test", "Claimant", Ct);
        raised.IsSuccess.ShouldBeTrue(raised.Error?.Message);
        return raised.Value;
    }

    private static Task<Result<CrDisputeUpheld>> UpholdAsync(ModuleHost host, string admin, Guid disputeId) =>
        Task.Run(async () =>
        {
            await using var scope = host.PlatformScope(admin);
            return await scope.ServiceProvider.GetRequiredService<ICrOwnershipAdministration>()
                .UpholdAsync(disputeId, "Checked the claimant against the CR certificate.", admin, Ct);
        }, Ct);
}
