# Observability (W-10) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** draft for the user's review (2026-09-30). Do not start until the user has answered the open questions in spec section 11, or has said to build on the recommendations. Where an answer changes a task, the task says how.

**Goal:** The web host and the worker emit redacted logs, traces and metrics with tenant, user, job and trace ids through an OpenTelemetry Collector to Loki, Tempo and Prometheus, readable in Grafana; `/alive` beside `/health`; a dead pipeline alerts through F-60; concurrent and active users per tenant and kind on a console page `/platform/usage` and a provisioned Grafana dashboard "WaslaBid usage", with the tender and opportunity metrics named for the tender slices (spec section 6).

**Architecture:** Shared telemetry registration in `Platform.Shared/Telemetry` (resource, log provider, redaction processors, job telemetry), web-only parts in `Platform.Web/Telemetry` (ASP.NET Core instrumentation, request and circuit context, inbound trace context, correlation header, `/alive`), the Telemetry health check in the Operations module, five services in `infra/compose`. Business metrics: the circuit registry, the activity middleware and the usage page in `Platform.Web/Usage` and `Components/Pages/Console`, the activity table and its counting functions in the Identity module, the usage job and `ops.active_user_counts` in the Operations module, `StatTile` in `Platform.UI`. No new module or schema; two tables and one console page (spec Q11).

**Tech Stack:** as the vendor slice, plus the OpenTelemetry .NET SDK (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`; tests: `OpenTelemetry.Exporter.InMemory`), Npgsql's built-in `Npgsql` activity source and meter, the OpenTelemetry Collector, Loki 3, Tempo, Prometheus 3, Grafana. No Serilog and no Sentry SDK unless the user decides otherwise (spec Q1, Q2).

**Spec:** `docs/superpowers/specs/2026-09-30-observability-design.md` (decisions O-1 to O-26, open questions Q1 to Q12).

**Style:** like the admin and vendor plans: each task gives files, contracts and the exact tests with their assertions; the implementer writes the code test-first to make them pass and reports every design choice it had to make. Anything that changes an O-decision, a contract, or an existing test's assertion is escalated, not decided.

**Conventions:** worktree `C:\Repo\ERP-w10`, branch `w-10-observability`; commit per task, no AI attribution; test first; `TestContext.Current.CancellationToken` in tests; after each task `dotnet build WaslaBid.slnx -warnaserror`, `dotnet test WaslaBid.slnx` and `dotnet format WaslaBid.slnx --verify-no-changes` are green (Docker running for Testcontainers); new packages pinned in `Directory.Packages.props` at the current stable version with an OSV advisory check recorded in a comment, as the existing entries do; images pinned by version and digest, as Mailpit is; no secret value in any output (N-10).

**Order:** tasks 1 to 7 (developer) in sequence; task 8 (devops) can run beside them; task 9 after 1 to 8; task 10 (qa-engineer) last; task 11 only if Q1 keeps a Sentry-protocol service. Reviewer after each developer task; pentester after task 10, before the merge (redaction, the new endpoints, the activity table's row-level security and the usage page are security-sensitive). Delivery in two pull requests (spec Q6): the pipeline (tasks 1 to 4 and 8, with their parts of 9 and 10), then the business metrics (tasks 5 to 7 and the rest of 8 to 10).

**Size:** about six days (spec Q6): tasks 1 to 4, 8, 9 and 10 about two and a half days as first drafted; task 5 half a day; task 6 one and a half days; task 7 one day; the usage parts of tasks 8 to 10 half a day. W-10 is re-sized from S to L when the user accepts Q6.

---

### Task 1: Telemetry registration in both hosts (O-3, O-5, O-6, O-15 exclusions, O-16)

**Agent:** developer.

**Files:**
- `src/Platform.Shared/Telemetry/TelemetryModule.cs`: `AddPlatformTelemetry(this IHostApplicationBuilder builder, string serviceName)` registering the resource (`service.name`, `service.version` from `AssemblyInformationalVersionAttribute`, `service.instance.id`, `deployment.environment.name` from `Telemetry:Environment`, default the host environment name in lower case), tracing (sources `Npgsql`, `WaslaBid.*`; `System.Net.Http` through `AddHttpClientInstrumentation`), metrics (meters `System.Runtime`, `Npgsql`, `System.Net.Http`, `WaslaBid.*`), logging (`builder.Logging.AddOpenTelemetry(o => { o.IncludeScopes = true; o.IncludeFormattedMessage = true; })`), and the OTLP exporter only when `Telemetry:OtlpEndpoint` (or the standard `OTEL_EXPORTER_OTLP_ENDPOINT`) is set. `TelemetryNames` constants for every attribute name in spec O-9 and section 5.
- `src/Platform.Web/Telemetry/WebTelemetry.cs`: `AddWebTelemetry()` adding ASP.NET Core instrumentation (filter: no span for `/health`, `/alive`, `/_framework/*`, `/_content/*`, `/_blazor/negotiate` and static asset endpoints) and meters `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`; `Microsoft.AspNetCore.Components*` sources and meters if .NET 10 publishes them (report which names exist).
- `src/Platform.Web/Program.cs`, `src/Platform.Worker/Program.cs`: call the registration; outside Development and Testing remove the console provider (`builder.Logging.ClearProviders()` before adding OpenTelemetry, then nothing else) per O-16. Startup failures before the host builds still reach stderr.
- `src/Platform.Web/appsettings.Development.json`, `src/Platform.Worker/appsettings.Development.json`: `Telemetry:OtlpEndpoint` = `http://localhost:4317`.
- `Directory.Packages.props`: the packages named in the header.
- `tests/Platform.IntegrationTests/Infrastructure/CapturedTelemetry.cs`: a helper that adds in-memory exporters for spans, log records and metrics to a host through `ConfigureTestServices` (`ConfigureOpenTelemetryTracerProvider`, `ConfigureOpenTelemetryMeterProvider`, `ConfigureOpenTelemetryLoggerProvider`), used by tasks 1 to 7.

**If Q2 keeps Serilog:** add `Serilog.Extensions.Logging` and `Serilog.Sinks.OpenTelemetry` and register Serilog only through `builder.Logging.AddSerilog(logger, dispose: true)`; add the test `Serilog_is_a_logging_provider_and_never_replaces_the_logger_factory` (the resolved `ILoggerFactory` is `Microsoft.Extensions.Logging.LoggerFactory`).

**Tests (integration unless noted):**
- `A_tenant_request_produces_one_server_span_with_its_route_and_status`: GET a tenant page on acme; exactly one server span, `http.route` set, `http.response.status_code` 200.
- `Health_alive_and_framework_requests_produce_no_span`.
- `A_database_call_during_a_request_is_a_child_span_without_parameter_values`: a staff page that queries; a child span from source `Npgsql` exists; no tag value contains the test tenant's id as a literal parameter (the statement text uses placeholders).
- `Both_hosts_name_their_service_version_and_environment_in_the_resource`: web and worker (through `JobServerHost`) resources carry `service.name` `waslabid-web` and `waslabid-worker`, a non-empty `service.version`, and `deployment.environment.name`.
- `Without_an_otlp_endpoint_the_host_starts_and_registers_no_otlp_exporter`.
- `With_the_collector_unreachable_requests_still_succeed`: endpoint set to an unused local port; 20 requests all 200; the host does not throw on shutdown.
- `Outside_development_no_console_log_provider_is_registered` (environment `Production` through `PlatformWebFactory`); `In_development_the_console_log_provider_stays`.
- Unchanged and green: `A_host_that_would_log_the_key_ring_below_information_does_not_start`, `The_worker_and_the_projects_it_is_built_from_do_not_load_the_key_ring`, `The_worker_does_not_reference_the_web_host` (the worker must not gain an ASP.NET Core framework reference through `Platform.Shared`; the ASP.NET Core instrumentation stays in `Platform.Web`).

**Verify:** `dotnet build WaslaBid.slnx -warnaserror`, `dotnet test WaslaBid.slnx`, `dotnet format WaslaBid.slnx --verify-no-changes`.

---

### Task 2: Context on every record, correlation id, job traces (O-7, O-8, O-9)

**Agent:** developer.

**Files:**
- `src/Platform.Web/Telemetry/RequestTelemetryMiddleware.cs`: runs right after `TenantMiddleware`; when a tenant is set, tags the server span (`waslabid.tenant.id`, `waslabid.tenant.slug`) and opens a log scope with the same pair for the rest of the request; registers `Response.OnStarting` to add `X-Correlation-Id` = `Activity.Current.TraceId` (hex, 32 characters) on every response, platform host and `/health` included.
- `src/Platform.Web/Telemetry/UserTelemetryMiddleware.cs`: runs after `ActingUserMiddleware`; adds `user.id` (the `sub` claim, never `email`, `preferred_username` or `name`) to the span and a nested scope; after `VendorContextMiddleware`, `waslabid.vendor_company.id` when a vendor context is set (one middleware with two entry points is fine; report the choice).
- `src/Platform.Web/Telemetry/UntrustedTraceContextPropagator.cs`: a `DistributedContextPropagator` registered in DI for the web host that extracts nothing from inbound requests (so every request starts its own trace) and injects normally on outbound calls.
- `src/Platform.Web/Telemetry/CircuitTelemetryHandler.cs`: a `CircuitHandler` ordered after `TenantCircuitHandler`, using `CreateInboundActivityHandler` to open the same tenant, user and vendor scope around every inbound circuit activity.
- `src/Platform.Shared/Jobs/TenantJobFilter.cs`: also stamps `TraceParent` (`Activity.Current?.Id`) when a current activity exists.
- `src/Platform.Shared/Jobs/JobTelemetryFilter.cs`: an `IServerFilter` that, in `OnPerforming`, starts activity `job <Type>.<Method>` from source `WaslaBid.Jobs` with the stored `TraceParent` as parent (or a new trace), tags `waslabid.job.id`, `waslabid.job.type`, `waslabid.tenant.id`, and opens a log scope with the same; in `OnPerformed`, sets status Error with `exception.type` on failure, records `waslabid.jobs.duration` and `waslabid.jobs.failed`, and disposes both. Never records job arguments (the rule `A_job_alert_never_contains_the_job_arguments` already holds for alerts).
- `src/Platform.Shared/Jobs/JobsModule.cs`: `HostScopedFilterProvider` also takes the host's `IServerFilter` registrations (today it takes only `IElectStateFilter` and `IApplyStateFilter`); register `JobTelemetryFilter` in `AddJobServer`.
- `src/Platform.Web/Program.cs`: the middleware in the order above; `app.UseMiddleware<RequestTelemetryMiddleware>()` directly after `TenantMiddleware`.
- `src/Platform.Web/Components/Pages/Dev/Throw.razor` or a minimal endpoint `GET /dev/throw` (Development only, behind the existing `/dev` 404 outside Development) that throws `InvalidOperationException("Deliberate failure for the W-10 checks.")`, used by task 10.

**Tests (integration; logs through the in-memory log exporter):**
- `A_log_written_during_a_tenant_request_carries_the_tenant_id_slug_and_trace_id`.
- `A_log_written_after_sign_in_carries_the_user_id_and_never_the_email`: test principal with `sub` and `email`; the record has `user.id` = sub; no attribute or message contains the email.
- `A_vendor_request_log_carries_the_vendor_company_id`.
- `A_platform_host_request_carries_no_tenant_id`.
- `Every_response_carries_its_trace_id_as_the_correlation_id`: tenant page, platform page, `/health`, and a 404 on an unknown host all carry `X-Correlation-Id`; for the tenant page it equals the captured server span's trace id.
- `A_traceparent_sent_by_a_client_does_not_become_the_request_trace_id`.
- `A_log_written_inside_a_circuit_event_carries_the_tenant_id`: invoke the handler's inbound activity delegate around a logging call (the pattern of `CircuitRevalidationTests`).
- `A_job_enqueued_during_a_request_continues_that_requests_trace`: enqueue from an acme request scope in the web factory, run it on a `JobServerHost` against the same database; the job span's trace id equals the request's.
- `A_log_written_inside_a_job_carries_the_job_id_type_and_tenant_id`.
- `A_failed_job_marks_its_span_as_an_error_with_the_exception_type_and_no_arguments`.
- `A_recurring_job_without_a_request_starts_its_own_trace`.
- `The_deliberate_failure_endpoint_is_not_served_outside_development` (404 in `Testing` and `Production`).
- Unchanged and green: `A_job_runs_as_the_tenant_that_enqueued_it`, `A_job_runs_once_with_two_worker_instances`, `A_recurring_job_failing_on_three_consecutive_runs_sends_one_alert`.

**Verify:** the three commands.

---

### Task 3: Redaction (O-10, O-11; N-10 and PDPL)

**Agent:** developer.

**Files:**
- `src/Platform.Shared/Telemetry/TelemetryRedactor.cs`: `string Redact(string value)`, pure and allocation-light: emails to `[email]`; runs of ten or more digits not inside a hexadecimal or dashed run to `[digits]` (Western and Arabic-Indic digits); JWTs (`eyJ` base64url with two dots) and `Bearer <token>` to `[token]`; `password=`, `pwd=`, `secret=`, `apikey=` key-value pairs (case-insensitive, up to `;`, `&` or whitespace) to `key=[secret]`.
- `src/Platform.Shared/Telemetry/RedactingLogProcessor.cs`: `BaseProcessor<LogRecord>`: redacts `FormattedMessage`, `Body` and string attribute values; for an exception, replaces `LogRecord.Exception` with attributes `exception.type`, `exception.message` (redacted) and `exception.stacktrace` (redacted), so the exporter never serialises the raw message (if the SDK version does not allow that, stop and report).
- `src/Platform.Shared/Telemetry/RedactingSpanProcessor.cs`: `OnEnd`: removes `url.query`, redacts `url.full` (query part dropped) and every string tag and exception event message.
- `src/Platform.Shared/Telemetry/ComponentAttribute.cs` plus a log processor adding `waslabid.component` from the category by the table in spec section 5.3 (the category is `LogRecord.CategoryName`).
- Registered last in both pipelines, so they run just before export.
- `tests/Platform.UnitTests/Architecture/LogTemplateTests.cs`: scans every `*.cs` under `src/` (with `TestRepo`) for `[LoggerMessage(... Message = "...")]` templates and for `Log*(` calls with a literal template.

**If Q2 keeps Serilog:** the same `TelemetryRedactor` runs in a Serilog enricher and a destructuring policy instead of the log processor; the span processor is unchanged.

**Tests:**
- Unit: `Emails_long_digit_runs_jwts_and_password_pairs_are_masked` (theory: English and Arabic sentences, `ahmad@example.sa`, a CR `1010123456`, an iqama `2123456789`, `0551234567`, `SA0380000000608010167519`, a JWT, `Host=db;Password=abc;`); `Trace_ids_guids_and_short_numbers_are_kept` (a 32-hex trace id, a GUID whose last group is twelve digits, `404`, `2026-09-30`); `Arabic_indic_digit_runs_are_masked_too`.
- Unit: `No_log_template_names_a_personal_or_secret_value`: placeholders refused (case-insensitive): `Email`, `Name`, `DisplayName`, `Phone`, `Cr`, `CrNumber`, `NationalId`, `Iqama`, `Iban`, `FileName`, `Password`, `Secret`, `Token`, `ConnectionString`, `Price`, `Amount`, `Total`, `Offer`, `Envelope`; the test lists the offending file and line. It must pass on today's 64 templates; if one fails, stop and report it rather than editing the template silently.
- Integration: `A_logged_exception_leaves_with_its_type_and_stack_and_a_masked_message` (an exception whose message holds an email).
- Integration: `A_keycloak_admin_lookup_by_email_leaves_no_email_in_any_span_or_log`: `KeycloakAdminClient.FindUserByEmail` against `FakeHttpServer`; no exported span tag, log message or attribute contains the address (`url.full` of `/users?email=...&exact=true`).
- Integration: `No_request_body_form_value_or_query_string_reaches_a_log_or_span`: post the vendor company form and a query string, each with a unique marker; the marker appears in no exported record.
- Integration: `The_key_ring_cap_also_holds_for_the_telemetry_exporter`: with `Logging:LogLevel:Default` = `Trace` in `Testing`, the in-memory log exporter receives no record from `Microsoft.AspNetCore.DataProtection*` below Information.
- Integration: `Every_error_record_carries_a_component` (an error from a `Platform.Modules.Vendors` category carries `Vendors`, one from `Npgsql` carries `PostgreSQL`).
- Unchanged and green: `A_key_the_host_cannot_decrypt_is_never_written_whole_to_a_log_even_at_trace_level`, `A_failure_message_never_contains_a_secret`, `An_alert_contains_no_secret_value`.

**Verify:** the three commands.

---

### Task 4: `/alive`, health metrics and the Telemetry check (O-14, O-15)

**Agent:** developer.

**Files:**
- `src/Platform.Web/Program.cs`: `app.MapHealthChecks("/alive", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();` (no check runs; the process answers). `/health` stays as it is.
- `src/Platform.Web/Tenancy/TenantMiddleware.cs`, `src/Platform.Web/PlatformHost/PlatformRequest.cs`, `src/Modules/Identity/Platform.Modules.Identity/Members/StaticRequests.cs`: treat the exact path `/alive` like the exact path `/health` (no tenant resolution, answered on the platform host, no claims transformation). Keep the comment's rule: only exact paths.
- `src/Modules/Operations/Platform.Modules.Operations/Health/HealthCheckJob.cs`: after the checks run, publish each result on meter `WaslaBid.Operations`: observable gauge `waslabid.health.status` (0, 1, 2) and histogram `waslabid.health.check.duration` (ms), tag `component`; the run is one activity from `WaslaBid.Operations` with a child per check.
- `src/Modules/Operations/Platform.Modules.Operations/Health/TelemetryHealthCheck.cs`: `GET {Telemetry:CollectorHealthUrl}` (collector `health_check` extension, `http://localhost:13133/` in Development) and `GET {Telemetry:LokiReadyUrl}` (`http://localhost:3100/ready`); Unhealthy names which of the two failed, exception type only (N-10). Registered only when both settings are present, so hosts without the stack (CI) skip it.
- `src/Modules/Operations/Platform.Modules.Operations.Contracts/HealthComponents.cs`: `Telemetry` constant, documented like `Disk`: alerted, not a board tile; `Board` unchanged.
- `src/Platform.Worker/appsettings.Development.json`: the two URLs.

**Tests:**
- Integration: `Alive_answers_healthy_without_a_database_tenant_or_sign_in` (the same unusable connection string as `Health_endpoint_answers_without_a_tenant_or_sign_in_and_is_unhealthy_without_a_database`, which stays green).
- Integration: `Alive_is_served_on_tenant_and_platform_hosts_and_on_an_unknown_host`; `A_path_under_alive_is_an_ordinary_tenant_path` (`/alive/x` on an unknown host is 404, as `/health/x` is).
- Integration: `Each_health_result_is_published_as_a_status_and_latency_metric` (in-memory metric exporter on the worker; one point per component with the right status value).
- Integration: `Telemetry_reports_unhealthy_naming_the_collector_or_loki_when_either_is_down` (`FakeHttpServer` for both; one answering 503, one unreachable).
- Integration: `A_telemetry_outage_opens_one_incident_and_sends_one_alert_and_one_recovery` (through the existing pipeline and Mailpit, like `An_incident_sends_one_email_and_no_repeat_while_open`).
- Unit: `Telemetry_is_not_a_board_tile`.
- Unchanged and green: `The_board_lists_every_component_with_status_latency_and_last_check`, `The_check_reports_on_the_configured_path`.

**Verify:** the three commands.

---

### Task 5: Concurrent users (spec 6.1 to 6.3; O-19, O-20, O-24)

**Agent:** developer. First task of the second pull request (spec Q6).

**Files:**
- `src/Platform.Shared/Telemetry/TelemetryNames.cs`: meter `WaslaBid.Usage` and every metric, tag and tag value of spec 6.1, the tender ones included, so the tender slices use the same constants.
- `src/Platform.Web/Usage/UsageKind.cs`: `static UsageKind? Of(ClaimsPrincipal user, ITenantAccessor tenants, VendorAccessor vendor, IPlatformRequestContext platform)` returning `Staff`, `Vendor`, `Platform` or null (not counted) by the table in spec 6.2.
- `src/Platform.Web/Usage/ConnectedCircuits.cs`: singleton, thread-safe registry of connected circuits (circuit id to tenant slug, kind, `sub`); `Snapshot()` gives circuits and distinct users per tenant slug and kind; registers the observable gauges `waslabid.circuits.connected` and `waslabid.users.concurrent` on `WaslaBid.Usage` (the `platform` kind without a tenant tag).
- `src/Platform.Web/Usage/UsageCircuitHandler.cs`: `CircuitHandler`, `Order => int.MinValue + 3` (after `CircuitSessionGuard`); classifies in `OnConnectionUpAsync` and adds; removes in `OnConnectionDownAsync` and `OnCircuitClosedAsync`, and when `CircuitSessionGuard.Ended` is seen. Task 6 adds its inbound activity handler.
- `src/Platform.Web/Program.cs`: registrations beside the other circuit handlers.

**Tests (integration; handlers driven as in `CircuitRevalidationTests` and `TenantCircuitHandlerTests`; gauges read through the in-memory metric exporter of `CapturedTelemetry`):**
- `A_connected_staff_circuit_counts_as_one_concurrent_user_of_its_tenant`: acme staff principal; `waslabid.users.concurrent` 1 and `waslabid.circuits.connected` 1 with exactly the tags `waslabid.tenant.slug` = `acme` and `waslabid.user.kind` = `staff`.
- `A_user_with_two_tabs_counts_once`: two circuits with the same `sub`; circuits 2, users 1.
- `A_closed_circuit_no_longer_counts`: after `OnCircuitClosedAsync`, both gauges 0 for acme staff.
- `A_disconnected_circuit_stops_counting_and_counts_again_on_reconnection`.
- `Vendor_and_platform_circuits_count_under_their_own_kind`: a vendor on acme counts as `vendor` for acme; a platform admin on the platform host counts as `platform` with no tenant tag.
- `Anonymous_and_applicant_circuits_are_not_counted`.
- `A_circuit_whose_session_ended_no_longer_counts` (W-21 guard ended).
- `No_usage_metric_carries_a_user_or_company_id`: every exported point of `WaslaBid.Usage` has only tag keys from spec 6.1, and no tag value equals the test `sub` or vendor company id.
- Unit: `The_usage_kind_of_each_session` (theory over the rows of spec 6.2).
- Unchanged and green: `CircuitRevalidationTests`, `TenantCircuitHandlerTests`, `VendorAccessTests`.

**Verify:** the three commands.

---

### Task 6: Active users (spec 6.4, 6.6 storage, 6.8; O-21)

**Agent:** developer.

**Files:**
- `src/Modules/Identity/Platform.Modules.Identity/Migrations/0002_identity_user_activity.sql`: table `identity.user_activity` as in spec 6.4; the trigger setting `hour` from the database clock; `platform.enable_tenant_rls` is not used (its policy is for all commands): forced row-level security with the two policies of spec 6.4 (`tenant_isolation` for SELECT with the helper's staff-only text, `tenant_activity_insert` for INSERT), as audit 0002 and 0003 do for `audit.events`; `grant insert` only to `erp_app`; security-definer `identity.activity_counts(p_now timestamptz)` and `identity.prune_activity(p_before timestamptz)` with `set search_path`, execute revoked from public and granted to `erp_app`, answering only a session with neither a tenant nor a vendor context (the `vendor.stale_uploads()` rule of ADR-0012 point 4); the owner guard of vendors and operations (run as a role with BYPASSRLS).
- `src/Modules/Identity/Platform.Modules.Identity.Contracts/UserActivity.cs`: `enum ActivityKind { Staff, Vendor }`, `enum ActivityWindow { OneDay, SevenDays, ThirtyDays }`, `record ActivityCount(Guid? TenantId, ActivityKind Kind, ActivityWindow Window, int Users)` (null tenant = across tenants), `IUserActivityRecorder.RecordAsync(ActivityKind kind, CancellationToken)`, `IUserActivityCounts.CountAsync(DateTimeOffset now, CancellationToken)` and `PruneAsync(DateTimeOffset before, CancellationToken)`.
- `src/Modules/Identity/Platform.Modules.Identity/Activity/UserActivityRecorder.cs` with a singleton `ActivityThrottle` (key tenant, `sub`, kind, hour from `TimeProvider`; entries older than two hours dropped); inserts through the scope's tenant connection with `on conflict do nothing`; a failure is logged with `{ErrorType}` and swallowed.
- `src/Modules/Identity/Platform.Modules.Identity/Activity/UserActivityCounts.cs`; `IdentityModule.cs`: `AddIdentityActivityCounts()` for the worker (counts and prune only, no Keycloak settings).
- `src/Platform.Web/Usage/UserActivityMiddleware.cs`, after `VendorContextMiddleware`: records `Staff` or `Vendor` by `UsageKind`; nothing for `/health`, `/alive`, static files, anonymous or uncounted sessions. `UsageCircuitHandler.CreateInboundActivityHandler` records the same way for circuit activity.
- `src/Modules/Operations/Platform.Modules.Operations/Migrations/0006_operations_active_user_counts.sql`: `ops.active_user_counts` as in spec 6.6; `erp_app` select, insert, delete.
- `src/Modules/Operations/Platform.Modules.Operations.Contracts/IUsageLog.cs`: `LatestAsync()` returning the rows and their `computed_at` (the `IHealthLog` pattern).
- `src/Modules/Operations/Platform.Modules.Operations/Usage/UsageMetricsJob.cs` and `UsageSnapshot.cs`: recurring job `usage-metrics`, `*/5 * * * *`, registered as `HealthCheckJob` is; counts, maps tenant ids to slugs through `ITenantCatalog`, replaces `ops.active_user_counts` in one transaction, stores the result in `UsageSnapshot` (singleton) whose observable gauges `waslabid.users.active` and `waslabid.users.active.all_tenants` report nothing when the result is older than 15 minutes; prunes buckets older than 35 days once a day. The Operations module gains references to `Platform.Modules.Identity.Contracts` and `Platform.Modules.Tenancy.Contracts` (contracts only, as the module rules require).
- `src/Platform.Worker/Platform.Worker.csproj` and `Program.cs`: reference the Identity module; `AddIdentityActivityCounts()`.

**Tests (integration unless noted; a manual clock as in `MembershipRevalidationTests`):**
- `A_user_seen_today_counts_as_active_for_the_day_week_and_month`: one acme staff request, the job runs; `waslabid.users.active` for acme `staff` is 1 for `1d`, `7d` and `30d`, and `ops.active_user_counts` holds the same.
- `A_user_seen_eight_days_ago_counts_for_the_month_only` (bucket inserted as owner).
- `Last_seen_is_written_at_most_once_an_hour_per_user_and_tenant`: 50 requests in one hour leave one row and one insert span from `Npgsql`; the clock one hour later, the next request adds a second row.
- `Two_instances_writing_the_same_hour_leave_one_row` (two recorders with their own throttles; no error, one row).
- `Circuit_activity_counts_as_activity`.
- `Health_alive_static_and_anonymous_requests_record_no_activity`.
- `Another_tenants_session_cannot_read_last_seen_rows`: beta staff, acme staff and an acme vendor session all get a permission error on `select` from `identity.user_activity`; `identity.activity_counts` answers nothing or refuses for any session with a tenant or vendor context.
- `A_session_writes_only_its_own_row_for_its_own_tenant_and_kind` (theory: another `sub`, another tenant, `staff` from a vendor session, `vendor` from a staff session; all refused by the policy).
- `The_activity_hour_is_the_database_clock_not_the_writers`.
- `A_vendor_active_on_two_tenants_counts_once_across_tenants`.
- `Activity_older_than_35_days_is_pruned`.
- `A_failed_activity_write_does_not_fail_the_request`.
- `A_stale_usage_result_is_not_reported`.
- Changed on purpose, the one existing assertion this plan changes (decided in spec 6.4, so not escalated): `Every_tenant_table_uses_the_staff_only_policy_or_the_explicit_vendor_policy` gains a case for `identity.user_activity` (exactly the two policies above), beside the one for `audit.events`.
- Unchanged and green: `Every_tenant_owned_table_has_forced_row_level_security_and_the_isolation_policy` and `No_view_reads_a_tenant_table` (the ops table has no `tenant_id`), `VendorFunctionCallerTests`, `The_worker_and_the_projects_it_is_built_from_do_not_load_the_key_ring` (the worker now references Identity), `The_worker_does_not_reference_the_web_host`.

**If Q10 chooses Keycloak login events:** drop the table, the recorder, the middleware and the circuit hook; `UserActivityCounts` reads `LOGIN` events through the Admin API per realm; the tests above become `A_user_who_signed_in_today_counts_as_signed_in_for_the_day_week_and_month` and the isolation tests fall away.

**Verify:** the three commands.

---

### Task 7: The console usage page (spec 6.6; O-23, O-25, O-26)

**Agent:** developer.

**Files:**
- `src/UI/Platform.UI/Components/StatTile.razor`: label, value (`int?`; null shows a dash with an accessible "Unknown"), optional split lines (label and value), `data-*` passthrough; logical direction utilities only; no uppercase text.
- `src/Platform.Web/Components/Pages/Dev/Gallery.razor`: `StatTile` with a value, a split and no value, in both panels.
- `src/Platform.Web/Components/Pages/Console/Usage.razor`: `@page "/platform/usage"`, `[Authorize(Policy = PlatformAuthentication.PolicyName)]`, `ConsoleLayout`, `InteractiveServer`; four tiles and the per-tenant `DataTable` of spec 6.6 with `data-tenant`, `data-kind`, `data-window` hooks; `ConsoleTime` for the result's time; a stale result shows dashes; the Grafana link only when `Observability:GrafanaUrl` is set; Refresh as on the health board.
- `src/Platform.Web/Usage/UsageOverview.cs`: joins `ITenantCatalog` (portal names), `ConnectedCircuits.Snapshot()` and `IUsageLog.LatestAsync()`; never an HTTP client.
- `src/Platform.Web/Components/Layout/ConsoleLayout.razor`: navigation entry `Console.Nav.Usage` between Tenants and Jobs.
- `src/UI/Platform.UI/Resources/SharedResource.ar-SA.resx` and `SharedResource.en-US.resx`: every new string (`Console.Nav.Usage`, `Console.Usage.*`, `StatTile.Unknown`) in Arabic and English.
- `tests/Platform.UITests/golden/*.png` and its README: retaken with `tests/e2e/golden.mjs` (the gallery now shows `StatTile`).

**Tests:**
- bUnit (`tests/Platform.UITests/Components/StatTileTests.cs`): `StatTile_shows_its_label_value_and_split`; `StatTile_without_a_value_shows_a_dash_named_unknown`; `ComponentConventionTests` and `PhysicalUtilityLintTests` cover it unchanged.
- Integration (`PlatformConsoleTests` style): `The_usage_section_shows_concurrent_and_active_users_per_tenant` (rows in `ops.active_user_counts` and a registered circuit; acme's row and the totals show them split by staff and vendor); `The_usage_totals_count_a_vendor_active_on_two_tenants_once`; `A_stale_usage_result_shows_unknown`; `Usage_counts_are_read_from_the_stored_job_results_not_from_prometheus` (no telemetry settings and the Prometheus and Grafana URLs pointing at an unused port; the page renders the stored numbers and makes no outbound HTTP call); `The_grafana_link_appears_only_when_configured`; `The_usage_page_lists_no_user`: no `sub`, email or name of the seeded users in the markup.
- Integration: `A_tenant_session_cannot_open_the_usage_page` (acme staff and vendor session cookies on the platform host are sent to the platform realm's sign-in and see no count; add `/platform/usage` to the theory of `A_tenant_admin_session_on_the_platform_host_is_sent_to_the_platform_realm`); `A_platform_admin_without_otp_cannot_open_the_usage_page` (`acr` 1).
- Integration: add `/platform/usage` in both cultures to `A_console_page_follows_the_culture_and_shows_no_resource_key`.
- Unchanged and green: the rest of `PlatformConsoleTests` and `CrossHostSessionTests`.

**If Q12 chooses the tenants page:** the tiles and columns go on `/platform/tenants` above the tenant table, no navigation entry; the tests keep their names with that path.

**Verify:** the three commands; the golden screenshots retaken and compared by eye in both directions at 360 and 1280 px.

---

### Task 8: Compose services (O-3 to O-6, O-12, O-13, O-17; section 9 limits)

**Agent:** devops.

**Files:**
- `infra/compose/docker-compose.yml`: services `otel-collector`, `loki`, `tempo`, `prometheus`, `grafana`, with container names `erp-otel-collector`, `erp-loki`, `erp-tempo`, `erp-prometheus`, `erp-grafana` (the existing `erp-*` pattern), each pinned by version and digest (multi-architecture digests, since the pilot is arm64), with `mem_limit` from spec section 9 and a health check. Images without a shell cannot run a `CMD` check: use the image's own probe command where it has one, otherwise document which service has no Compose health check and why (W-01's acceptance counts healthy services; update its count in docs/09 only through the user).
- Ports, all bound to `127.0.0.1`: collector 4317 (OTLP gRPC), 4318 (OTLP HTTP), 13133 (health); Loki 3100; Tempo 3200; Prometheus 9090; Grafana 3000. No clash with the existing 5432, 6379, 8080, 9000, 9002, 9003, 3310, 1025, 8025.
- `infra/compose/observability/collector.yaml`: receivers `otlp` (gRPC and HTTP); processors `memory_limiter` (200 MB), `batch`, and an attributes step deleting `url.query` and `http.request.header.*` from spans and logs; exporters: OTLP HTTP to Loki `/otlp`, OTLP to Tempo, OTLP HTTP to Prometheus `/api/v1/otlp`; extension `health_check`. Use the core distribution if it covers every component used; otherwise contrib (report which).
- `infra/compose/observability/loki.yaml`: single binary, filesystem storage, schema v13 with TSDB, `allow_structured_metadata: true`, `otlp_config` resource attributes as index labels limited to `service.name` and `deployment.environment.name`, compactor retention on, `retention_period` from `LOKI_RETENTION` (default `72h` locally; `720h` for the pilot).
- `infra/compose/observability/tempo.yaml`: local storage, OTLP receiver on the Compose network only, `block_retention` from `TEMPO_RETENTION` (default `72h`; `168h` pilot).
- Prometheus: command flags `--web.enable-otlp-receiver` and `--storage.tsdb.retention.time=${PROMETHEUS_RETENTION:-3d}` (`30d` pilot).
- `infra/compose/observability/grafana/provisioning/datasources/datasources.yaml`: Loki (derived field `trace_id` linking to Tempo), Tempo (trace to logs by `trace_id` on Loki, service map from Prometheus), Prometheus; anonymous access off; admin user `admin` with `GF_SECURITY_ADMIN_PASSWORD=${GRAFANA_ADMIN_PASSWORD:?Set GRAFANA_ADMIN_PASSWORD in infra/compose/.env}`.
- `infra/compose/observability/grafana/provisioning/dashboards/dashboards.yaml`: a file provider, folder "WaslaBid", `allowUiUpdates: false`, path `/var/lib/grafana/dashboards`; `infra/compose/observability/grafana/dashboards/waslabid-usage.json` mounted there read-only: the dashboard of spec 6.7 (uid `waslabid-usage`; variables `tenant` from `label_values(waslabid_users_active, waslabid_tenant_slug)`, multi with All, and `kind`; rows Now, Active and Tenders with the panels listed there; the Prometheus datasource by the uid set in `datasources.yaml`; the tender panels' descriptions say they wait for the tender slice). Metric names as spec 6.1 with dots as underscores; task 10 confirms them and corrects the JSON in the same pull request if Prometheus names them differently.
- `infra/compose/.env.example`: `GRAFANA_ADMIN_PASSWORD=` (generate with `openssl rand -hex 16`), `LOKI_RETENTION`, `TEMPO_RETENTION`, `PROMETHEUS_RETENTION` with the local defaults and a comment naming the pilot values.
- Volumes `loki-data`, `tempo-data`, `prometheus-data`, `grafana-data`.
- CI: the Trivy step covers the new images the same way it covers the existing ones.

**Acceptance (recorded evidence, not a unit test):** from a clean clone, `cp .env.example .env`, fill the values, `docker compose up -d`: every new service healthy (or documented) within four minutes, `docker compose ps` shows no restarts, a second `up -d` is clean; `curl -s http://127.0.0.1:3100/ready` answers `ready`; Grafana at `http://127.0.0.1:3000` shows the three datasources, each "working" on its test button, and the "WaslaBid usage" dashboard in folder "WaslaBid", whose panels show "No data" rather than an error before the app runs; `docker stats --no-stream` shows each new service under its limit; nothing new listens on a non-loopback address (`netstat -an` on Windows).

---

### Task 9: Documentation and decisions

**Agent:** developer (docs only), after tasks 1 to 8.

**Files:**
- `docs/07-ways-of-working.md` section 4: five rows in the service table (purpose, port, credentials: `GRAFANA_ADMIN_PASSWORD`), the new `.env` value in the "Run the app locally" list, `Telemetry:*` settings, how to find a failed request (copy `X-Correlation-Id` from the browser's network panel, Grafana Explore, Tempo, search by trace id; Loki `{service_name="waslabid-web"} | trace_id="<id>"`), `/alive` versus `/health`, that the stack costs about 1.5 GB of memory, and where usage shows: the console page `/platform/usage` and the Grafana dashboard "WaslaBid usage" (`Observability:GrafanaUrl` for the console's link).
- `docs/02-core-features-and-tech-stack.md` observability row and the section 4.2 diagram label, `docs/03-diagrams.md` diagram 9 label: per the answers to Q1 and Q2 (for example "OpenTelemetry for .NET, Prometheus, Grafana, Loki for logs, Tempo for traces; Sentry-compatible error tracking later (ADR-0014)"). Render every edited diagram with `npx @mermaid-js/mermaid-cli`.
- `docs/adr/0014-observability-stack-for-the-pilot.md` from `0000-template.md`, only if Q1 or Q2 changes the stack row: Sentry and Serilog decisions, the pilot memory budget, retention.
- `docs/09-backlog.md`: W-10 status and the acceptance text from spec section 10 (as the user approved it), size per Q6; the F-60 note "W-10 is not Done yet" updated; W-19 dependency per Q4; the F-54 note on usage (added with the draft) kept or corrected.
- `docs/05-mvp-scope.md` section 8: the hardening line names the usage metrics and page per Q11.
- `README.md` onboarding (the new `.env` value), `CLAUDE.md` "What this repository is" (one sentence: telemetry through the collector to Loki, Tempo and Prometheus, Grafana on port 3000 with the "WaslaBid usage" dashboard, and the console usage page `/platform/usage`).

**Verify:** Mermaid renders; `dotnet build WaslaBid.slnx -warnaserror` still green (no code change expected).

---

### Task 10: End-to-end smoke and evidence

**Agent:** qa-engineer (writes only under `tests/`).

**Files:** `tests/e2e/observability.mjs` (steps 1 to 7 need no browser: `fetch` through Caddy with the local certificate accepted as the other scripts do; step 8 uses the Playwright helpers of `lib.mjs`), a section in `tests/e2e/README.md`.

**Steps the script runs and checks:**
1. `GET https://acme.localhost:8443/dev/throw` answers 500 and carries `X-Correlation-Id`.
2. Within 60 seconds, Tempo `GET http://127.0.0.1:3200/api/traces/<id>` returns the trace; its server span has `waslabid.tenant.id` equal to acme's id.
3. Within 60 seconds, Loki `query_range` for `{service_name="waslabid-web"} | trace_id="<id>"` returns one Error line with `exception_type` `System.InvalidOperationException`, `waslabid_component` `Web` and the tenant id; record the exact structured-metadata names Loki uses (spec section 8 assumes dots become underscores) and correct the spec's query in the same pull request if they differ.
4. The F-53 query from spec section 8 over the last hour counts at least one error for `waslabid-web`.
5. After one run of `tests/e2e/vendor.mjs` (registration, uploads and consent, which handle real email addresses and CR numbers), Loki over the last hour holds no line from `waslabid-web` or `waslabid-worker` matching an email address (`|~ "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]+"`) or the CR number the run used.
6. `docker stop erp-otel-collector`; ten `GET` requests to `https://acme.localhost:8443/` all answer within their usual time (print p95); within two minutes Mailpit holds one "[WaslaBid] Telemetry is down" email; `docker start erp-otel-collector`; one "has recovered" email follows.
7. `GET /alive` on acme, on the platform host and with PostgreSQL stopped (`docker stop erp-postgres`, then start it again) answers 200; `/health` answers 503 while PostgreSQL is stopped, as today.
8. Usage: sign in on acme as a throwaway staff admin (`throwawayStaff` of `admin.mjs`, cleaned up at the end) and keep the page open. Within two export intervals (about two minutes) Prometheus `GET http://127.0.0.1:9090/api/v1/query` for the concurrent-users metric of acme `staff` returns 1, and the "Concurrent users" panel of the "WaslaBid usage" dashboard returns 1 for acme through Grafana's `/api/ds/query` with that panel's own expression (admin credentials from `.env`, never printed); record the exact Prometheus names and correct the dashboard JSON and spec 6.1 if they differ. After the usage job has run (up to five minutes), the active-users metric for acme `staff` and `1d` is at least 1, and the console usage page, opened as a platform admin with OTP as `platform.mjs` does, shows the same numbers for acme and lists no email or name. Close the staff page; within two export intervals the concurrent count for acme `staff` is 0. Screenshot the console page in both cultures as evidence.

Record the run (date, pass or fail per step, the measured p95) in the W-10 row's evidence, as earlier rows do.

---

### Task 11 (only if Q1 keeps a Sentry-protocol service): error events

**Agents:** devops (service), then developer (SDK).

- Devops: GlitchTip (web and worker, pinned by digest, `mem_limit` 512 MB each) with its database in the existing PostgreSQL (`01-databases.sql` adds `glitchtip`), port `127.0.0.1:8000`, secrets in `.env`; or self-hosted Sentry on a separate machine if the user chose it (not on the pilot VM, spec section 9).
- Developer: `Sentry.AspNetCore` and `Sentry.Extensions.Logging` behind `Sentry:Dsn` (empty means off, and no events leave); `SendDefaultPii = false`, `MaxRequestBodySize = None`, no breadcrumbs from HTTP bodies, `BeforeSend` runs `TelemetryRedactor` over message, exception values, tags and breadcrumbs and drops request cookies and headers; the event carries the trace id and `waslabid.tenant.id` as tags.
- Tests: `Without_a_dsn_no_event_is_sent`; `An_event_carries_the_trace_id_and_tenant_id_and_no_personal_data` (a local HTTP listener standing in for the DSN endpoint captures the envelope; the marker email and a JWT are masked); `An_event_carries_no_request_body_cookie_or_header_value`.
- Spec section 10's first acceptance line then includes the Sentry clause, and task 10 adds a step: the deliberate failure appears once in GlitchTip with the same trace id.
