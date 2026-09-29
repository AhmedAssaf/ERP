using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Modules.Audit.Contracts;
using Platform.Modules.Identity;
using Platform.Shared.Tenancy;

namespace Platform.UnitTests.Identity;

/// <summary>
/// W-21: a session on a tenant host is revalidated against the Keycloak organization of that tenant. A confirmed
/// membership is reused for <see cref="MembershipRevalidator.MemberFor"/>, a removal is refused and audited once, a
/// sign-in counts as proof of membership at its time, and when Keycloak cannot answer a session confirmed within
/// <see cref="MembershipRevalidator.Grace"/> is kept and an older one is refused.
/// </summary>
public sealed class MembershipRevalidatorTests
{
    private static readonly TenantContext Acme = new(
        Guid.Parse("0199a000-0000-7000-8000-00000000ac3e"), "acme", "acme", "ar-SA", new TenantBranding("Acme", "#0F766E", null));

    private static readonly TenantContext Beta = new(
        Guid.Parse("0199a000-0000-7000-8000-0000000be7a0"), "beta", "beta", "en-US", new TenantBranding("Beta", "#1D4ED8", null));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_confirmed_membership_is_reused_for_two_minutes_before_keycloak_is_asked_again()
    {
        var world = new World(Acme);
        var user = Staff("u1", "acme");

        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeTrue();
        world.Source.Answers["u1"] = OrganizationMembership.NotMember;
        world.Clock.Advance(TimeSpan.FromSeconds(119));
        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeTrue();
        world.Source.Calls.ShouldBe(1);

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeFalse();
        world.Source.Calls.ShouldBe(2);
        MembershipRevalidator.MemberFor.ShouldBe(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task A_removed_member_is_refused_and_audited_once_naming_the_user_and_the_organization()
    {
        var world = new World(Acme);
        world.Source.Answers["u1"] = OrganizationMembership.NotMember;
        var user = Staff("u1", "acme");

        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeFalse();
        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeFalse();
        world.Clock.Advance(TimeSpan.FromMinutes(4));
        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeFalse();

        world.Source.Calls.ShouldBe(1);
        var entry = world.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("identity.session_revoked");
        entry.ActorId.ShouldBe("u1");
        entry.SubjectType.ShouldBe("user");
        entry.SubjectId.ShouldBe("u1");
        entry.Data.ShouldNotBeNull();
        entry.Data["organization"].ShouldBe("acme");
        entry.Data["reason"].ShouldBe("removed_from_organization");
        entry.Data["session"].ShouldBe("staff");
        world.Audit.Tenants.ShouldBe([Acme.TenantId]);
    }

    [Fact]
    public async Task A_vendor_removed_from_the_organization_is_refused_and_audited_as_a_vendor_session()
    {
        var world = new World(Acme);
        world.Source.Answers["v1"] = OrganizationMembership.NotMember;
        var vendor = Principal("v1", ["acme"], vendorRole: true);

        (await world.Revalidator().IsStillMemberAsync(vendor, null, Ct)).ShouldBeFalse();

        world.Audit.Entries.ShouldHaveSingleItem().Data!["session"].ShouldBe("vendor");
    }

    [Fact]
    public async Task A_disabled_account_is_refused_like_a_removed_member()
    {
        var world = new World(Acme);
        world.Source.Answers["u1"] = OrganizationMembership.Disabled;

        (await world.Revalidator().IsStillMemberAsync(Staff("u1", "acme"), null, Ct)).ShouldBeFalse();

        world.Audit.Entries.ShouldHaveSingleItem().Data!["reason"].ShouldBe("account_disabled");
    }

    [Fact]
    public async Task A_sign_in_after_the_removal_proves_membership_again_without_asking_keycloak()
    {
        var world = new World(Acme);
        world.Source.Answers["u1"] = OrganizationMembership.NotMember;
        var user = Staff("u1", "acme");
        var oldSignIn = world.Clock.GetUtcNow().AddMinutes(-10);

        (await world.Revalidator().IsStillMemberAsync(user, oldSignIn, Ct)).ShouldBeFalse();

        // Re-added and signed in again: Keycloak issued the organization claim, which is proof at that moment.
        world.Clock.Advance(TimeSpan.FromMinutes(1));
        world.Source.Answers["u1"] = OrganizationMembership.Member;
        var newSignIn = world.Clock.GetUtcNow();
        (await world.Revalidator().IsStillMemberAsync(user, newSignIn, Ct)).ShouldBeTrue();
        world.Source.Calls.ShouldBe(1);

        // Membership belongs to the user, not to one cookie: once re-added, the user's older session is valid again.
        (await world.Revalidator().IsStillMemberAsync(user, oldSignIn, Ct)).ShouldBeTrue();
        world.Source.Calls.ShouldBe(1);
        world.Audit.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_session_signed_in_before_a_seen_removal_is_refused_without_asking_keycloak()
    {
        var world = new World(Acme);
        world.Source.Answers["u1"] = OrganizationMembership.NotMember;
        var user = Staff("u1", "acme");
        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeFalse();

        (await world.Revalidator().IsStillMemberAsync(user, world.Clock.GetUtcNow().AddSeconds(-1), Ct)).ShouldBeFalse();

        world.Source.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task A_session_on_a_host_without_a_tenant_or_without_a_user_is_not_revalidated()
    {
        // The platform host has no tenant (its own realm has no organizations), nor has /health; neither costs a call.
        var world = new World(null);
        world.Source.Answers["u1"] = OrganizationMembership.NotMember;
        world.Source.AccountAnswers["u1"] = OrganizationMembership.Disabled;

        (await world.Revalidator().IsStillMemberAsync(Staff("u1", "acme"), null, Ct)).ShouldBeTrue();
        (await new World(Acme).Revalidator().IsStillMemberAsync(new ClaimsPrincipal(new ClaimsIdentity()), null, Ct)).ShouldBeTrue();

        world.Source.Calls.ShouldBe(0);
        world.Source.AccountCalls.ShouldBe(0);
        world.Audit.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("beta")]
    [InlineData(null)]
    public async Task A_disabled_account_is_refused_on_a_session_without_the_host_tenants_organization(string? organization)
    {
        // P-2: a vendor on another tenant's join page, an acme cookie replayed on another host, or an applicant: no
        // organization of this host to revalidate, but the account itself must still be enabled.
        var world = new World(Acme);
        world.Source.AccountAnswers["u1"] = OrganizationMembership.Disabled;
        var user = organization is null ? Principal("u1", [], vendorRole: true) : Principal("u1", [organization], vendorRole: true);

        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeFalse();

        world.Source.Calls.ShouldBe(0, "no organization of this host is claimed, so none is asked about");
        world.Source.AccountCalls.ShouldBe(1);
        var entry = world.Audit.Entries.ShouldHaveSingleItem();
        entry.Action.ShouldBe("identity.session_revoked");
        entry.SubjectId.ShouldBe("u1");
        entry.Data!["reason"].ShouldBe("account_disabled");
        entry.Data.ContainsKey("organization").ShouldBeFalse();
        entry.Data["session"].ShouldBe("vendor");
    }

    [Fact]
    public async Task The_account_check_is_reused_for_two_minutes_across_tenants()
    {
        var world = new World(Acme);
        var user = Principal("u1", [], vendorRole: true);

        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeTrue();
        world.Clock.Advance(TimeSpan.FromSeconds(119));
        (await world.Revalidator(Beta).IsStillMemberAsync(user, null, Ct)).ShouldBeTrue();
        world.Source.AccountCalls.ShouldBe(1);

        world.Source.AccountAnswers["u1"] = OrganizationMembership.NotMember;
        world.Clock.Advance(TimeSpan.FromSeconds(1));
        (await world.Revalidator(Beta).IsStillMemberAsync(user, null, Ct)).ShouldBeFalse("a deleted account ends its sessions too");
        world.Audit.Entries.ShouldHaveSingleItem().Data!["reason"].ShouldBe("account_removed");
    }

    [Fact]
    public async Task When_keycloak_cannot_answer_a_membership_confirmed_within_three_and_a_half_minutes_is_kept_and_an_older_one_is_refused()
    {
        var world = new World(Acme);
        world.Source.Answers["u1"] = OrganizationMembership.Unavailable;
        var user = Staff("u1", "acme");
        var signedIn = world.Clock.GetUtcNow();

        world.Clock.Advance(TimeSpan.FromSeconds(180));
        (await world.Revalidator().IsStillMemberAsync(user, signedIn, Ct)).ShouldBeTrue();
        world.Clock.Advance(TimeSpan.FromSeconds(29));
        (await world.Revalidator().IsStillMemberAsync(user, signedIn, Ct)).ShouldBeTrue();

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        (await world.Revalidator().IsStillMemberAsync(user, signedIn, Ct)).ShouldBeFalse();

        // Nothing is known about the membership itself, so nothing is audited; the host logs the outage.
        world.Audit.Entries.ShouldBeEmpty();
        MembershipRevalidator.Grace.ShouldBe(TimeSpan.FromSeconds(210));
    }

    [Fact]
    public async Task A_circuit_without_any_known_membership_is_refused_while_keycloak_cannot_answer()
    {
        var world = new World(Acme);
        world.Source.Answers["u1"] = OrganizationMembership.Unavailable;

        (await world.Revalidator().IsStillMemberAsync(Staff("u1", "acme"), null, Ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task After_keycloak_failed_for_an_organization_it_is_not_asked_about_that_organization_for_thirty_seconds()
    {
        var world = new World(Acme);
        world.Source.Answers["u1"] = OrganizationMembership.Unavailable;
        world.Source.Answers["u2"] = OrganizationMembership.Member;
        var signedIn = world.Clock.GetUtcNow();
        world.Clock.Advance(TimeSpan.FromSeconds(150));

        (await world.Revalidator().IsStillMemberAsync(Staff("u1", "acme"), signedIn, Ct)).ShouldBeTrue();
        world.Clock.Advance(TimeSpan.FromSeconds(29));
        (await world.Revalidator().IsStillMemberAsync(Staff("u2", "acme"), signedIn, Ct)).ShouldBeTrue();
        world.Source.Calls.ShouldBe(1);

        world.Clock.Advance(TimeSpan.FromSeconds(1));
        (await world.Revalidator().IsStillMemberAsync(Staff("u2", "acme"), signedIn, Ct)).ShouldBeTrue();
        world.Source.Calls.ShouldBe(2);
        MembershipRevalidator.Backoff.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Concurrent_checks_of_the_same_member_ask_keycloak_once()
    {
        var world = new World(Acme);
        var gate = new TaskCompletionSource();
        world.Source.Gate = gate.Task;
        world.Source.Answers["u1"] = OrganizationMembership.NotMember;
        var user = Staff("u1", "acme");

        var checks = Enumerable.Range(0, 8).Select(_ => world.Revalidator().IsStillMemberAsync(user, null, Ct)).ToList();
        gate.SetResult();
        var results = await Task.WhenAll(checks);

        results.ShouldAllBe(r => !r);
        world.Source.Calls.ShouldBe(1);
        world.Audit.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_removal_whose_audit_failed_is_audited_on_the_next_check()
    {
        var world = new World(Acme);
        world.Source.Answers["u1"] = OrganizationMembership.NotMember;
        world.Audit.FailNext = true;
        var user = Staff("u1", "acme");

        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeFalse();
        world.Audit.Entries.ShouldBeEmpty();

        (await world.Revalidator().IsStillMemberAsync(user, null, Ct)).ShouldBeFalse();
        world.Audit.Entries.Count.ShouldBe(1);
    }

    [Fact]
    public void At_capacity_confirmed_memberships_are_dropped_before_seen_removals()
    {
        var evidence = new MembershipEvidence(capacity: 4);
        var at = new DateTimeOffset(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);
        evidence.Record("acme", "removed-1", new MembershipFact(Member: false, at));
        evidence.Record("acme", "removed-2", new MembershipFact(Member: false, at));
        evidence.Record("acme", "member-1", new MembershipFact(Member: true, at));
        evidence.Record("acme", "member-2", new MembershipFact(Member: true, at));

        evidence.Record("acme", "member-3", new MembershipFact(Member: true, at));

        evidence.Latest("acme", "removed-1").ShouldBe(new MembershipFact(false, at));
        evidence.Latest("acme", "removed-2").ShouldBe(new MembershipFact(false, at));
        evidence.Latest("acme", "member-1").ShouldBeNull();
        evidence.Latest("acme", "member-3").ShouldBe(new MembershipFact(true, at));
        evidence.Count.ShouldBe(3);
    }

    private static ClaimsPrincipal Staff(string subject, string organization) => Principal(subject, [organization], vendorRole: false);

    private static ClaimsPrincipal Principal(string subject, IReadOnlyList<string> organizations, bool vendorRole)
    {
        var claims = new List<Claim> { new("sub", subject) };
        claims.AddRange(organizations.Select(o => new Claim("organization", o)));
        if (vendorRole)
        {
            claims.Add(new Claim("roles", "vendor"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private sealed class World(TenantContext? tenant)
    {
        public ManualClock Clock { get; } = new();

        public ScriptedSource Source { get; } = new();

        public RecordingAudit Audit { get; } = new();

        public MembershipEvidence Evidence { get; } = new();

        public MembershipRevalidator Revalidator() => Revalidator(tenant);

        public MembershipRevalidator Revalidator(TenantContext? on)
        {
            var accessor = new TenantAccessor();
            if (on is not null)
            {
                accessor.Set(on);
            }

            Audit.Accessor = accessor;
            return new MembershipRevalidator(accessor, Evidence, Source, Audit, Clock, NullLogger<MembershipRevalidator>.Instance);
        }
    }

    private sealed class ScriptedSource : IOrganizationMembershipSource
    {
        private int _calls;

        private int _accountCalls;

        public Dictionary<string, OrganizationMembership> Answers { get; } = [];

        public Dictionary<string, OrganizationMembership> AccountAnswers { get; } = [];

        public int AccountCalls => _accountCalls;

        public Task<OrganizationMembership> CheckAccountAsync(string userId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _accountCalls);
            return Task.FromResult(AccountAnswers.TryGetValue(userId, out var answer) ? answer : OrganizationMembership.Member);
        }

        public Task? Gate { get; set; }

        public int Calls => _calls;

        public async Task<OrganizationMembership> CheckAsync(string organizationAlias, string userId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            if (Gate is { } gate)
            {
                await gate;
            }

            return Answers.TryGetValue(userId, out var answer) ? answer : OrganizationMembership.Member;
        }
    }

    private sealed class RecordingAudit : IAuditWriter
    {
        private readonly Lock _gate = new();

        public List<AuditEntry> Entries { get; } = [];

        public List<Guid> Tenants { get; } = [];

        public bool FailNext { get; set; }

        public TenantAccessor? Accessor { get; set; }

        public Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (FailNext)
                {
                    FailNext = false;
                    throw new InvalidOperationException("The audit log is not reachable.");
                }

                Entries.Add(entry);
                Tenants.Add(Accessor!.Current!.TenantId);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 29, 8, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
