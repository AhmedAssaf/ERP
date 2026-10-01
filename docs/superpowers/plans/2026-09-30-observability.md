# Observability (W-10) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** approved by the user on 2026-10-01 with the answers to spec section 11 (Q1 to Q8; ADR-0014). Tasks 5 to 7 were built on branch `w-10-observability` and merged in PR #6 on 2026-10-01. The rest (tasks 1 to 4, 7b and 8 to 10) is being built on branch `w-10-pipeline`, worktree `C:\Repo\ERP-w10-pipeline`. Task 11 is dropped (Q1: no Sentry).

**Goal:** The web host and the worker emit redacted logs (Serilog as a `Microsoft.Extensions.Logging` provider, OTLP sink) and traces and metrics (OpenTelemetry SDK) with tenant, user, job and trace ids through an OpenTelemetry Collector to Elasticsearch, readable in Kibana; `/alive` beside `/health`; a dead pipeline alerts through F-60; concurrent and active users per tenant and kind on a console page `/platform/usage` and a Kibana dashboard "WaslaBid usage" linked from it, with the tender and opportunity metrics named for the tender slices (spec section 6).

**Architecture:** Shared telemetry registration in `Platform.Shared/Telemetry` (resource, the Serilog logger and its provider registration, the redaction enricher, destructuring policy and span processor, job telemetry), web-only parts in `Platform.Web/Telemetry` (ASP.NET Core instrumentation, request and circuit context, inbound trace context, correlation header, `/alive`), the Telemetry health check in the Operations module, four services in `infra/compose` (collector, Elasticsearch, Kibana, the one-shot `elastic-setup`). Business metrics: the circuit registry, the activity middleware and the usage page in `Platform.Web/Usage` and `Components/Pages/Console`, the activity table and its counting functions in the Identity module, the usage job and `ops.active_user_counts` in the Operations module, `StatTile` in `Platform.UI`. No new module or schema; two tables and one console page (spec Q11).

**Tech Stack:** as the vendor slice, plus the OpenTelemetry .NET SDK for traces and metrics (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`; tests: `OpenTelemetry.Exporter.InMemory`), Serilog for logs (`Serilog`, `Serilog.Extensions.Logging`, `Serilog.Sinks.OpenTelemetry`), Npgsql's built-in `Npgsql` activity source and meter, the OpenTelemetry Collector (contrib distribution, for the `elasticsearch` exporter), Elasticsearch 9.x and Kibana 9.x on the free Basic licence. No Sentry SDK, no OpenTelemetry log provider, no Logstash (ADR-0014).

**Spec:** `docs/superpowers/specs/2026-09-30-observability-design.md` (decisions O-1 to O-26, answers Q1 to Q12).

**Style:** like the admin and vendor plans: each task gives files, contracts and the exact tests with their assertions; the implementer writes the code test-first to make them pass and reports every design choice it had to make. Anything that changes an O-decision, a contract, or an existing test's assertion is escalated, not decided.

**Conventions:** worktree `C:\Repo\ERP-w10-pipeline`, branch `w-10-pipeline` (tasks 5 to 7 were built in `C:\Repo\ERP-w10` on `w-10-observability`); commit per task, no AI attribution; test first; `TestContext.Current.CancellationToken` in tests; after each task `dotnet build WaslaBid.slnx -warnaserror`, `dotnet test WaslaBid.slnx` and `dotnet format WaslaBid.slnx --verify-no-changes` are green (Docker running for Testcontainers); new packages pinned in `Directory.Packages.props` at the current stable version with an OSV advisory check recorded in a comment, as the existing entries do; images pinned by version and digest, as Mailpit is; no secret value in any output (N-10).

**Order:** tasks 1 to 4 and 7b (developer) in sequence; task 8 (devops) can run beside them; task 9 after 1 to 8; task 10 (qa-engineer) last; task 11 dropped. Reviewer after each developer task; pentester after task 10, before the merge (redaction, the new endpoints, the Elasticsearch users and roles, and the changed link are security-sensitive). Delivery in two pull requests (spec Q6): PR #6 carried the business metrics (tasks 5 to 7, merged 2026-10-01); the second carries the pipeline (tasks 1 to 4, 7b and 8 to 10).

**Size:** L (spec Q6, 2026-10-01), about six days in all: tasks 5 to 7 (about three days) are done; tasks 1 to 4, 7b and 8 to 10 are about three days, the Elastic setup container included.

---

### Task 1: Telemetry registration in both hosts (O-2, O-3, O-5, O-6, O-15 exclusions, O-16)

**Agent:** developer.

**Files:**
- `src/Platform.Shared/Telemetry/TelemetryModule.cs`: `AddPlatformTelemetry(this IHostApplicationBuilder builder, string serviceName)` registering:
  - the resource (`service.name`, `service.version` from `AssemblyInformationalVersionAttribute`, `service.instance.id`, `deployment.environment.name` from `Telemetry:Environment`, default the host environment name in lower case), given to both the OpenTelemetry SDK and Serilog's OTLP sink;
  - tracing (sources `Npgsql`, `WaslaBid.*`; `System.Net.Http` through `AddHttpClientInstrumentation`) and metrics (meters `System.Runtime`, `Npgsql`, `System.Net.Http`, `WaslaBid.*`), with the OTLP exporter only when `Telemetry:OtlpEndpoint` (or the standard `OTEL_EXPORTER_OTLP_ENDPOINT`) is set;
  - logging through Serilog: a `Logger` built with `MinimumLevel.Verbose()` (the MEL filter rules, W-24's `KeyRing.CapDataProtectionLogging` included, stay the only level gate), `Enrich.FromLogContext()`, the enrichers and destructuring policy of task 3 (task 1 leaves the place for them), `WriteTo.Sink(TelemetryLogSinks)` and, only when the OTLP endpoint is set, `WriteTo.OpenTelemetry(...)` (gRPC, the resource attributes, trace and span ids included); registered only as `builder.Logging.AddSerilog(logger, dispose: true)`. Never `UseSerilog()`, `builder.Host.UseSerilog()` or `services.AddSerilog()`; no `AddOpenTelemetry()` on the logging builder;
  - `TelemetryLogSinks`: a thread-safe fan-out `ILogEventSink`, a singleton in DI, empty in production; tests add their capture sink to it after the host builds (built in task 1);
  - `TelemetryNames` constants for every attribute name in spec O-9 and section 5.
- `src/Platform.Web/Telemetry/WebTelemetry.cs`: `AddWebTelemetry()` adding ASP.NET Core instrumentation (filter: no span for `/health`, `/alive`, `/_framework/*`, `/_content/*`, `/_blazor` and everything under it (the negotiate call and the circuit's WebSocket request, which lives as long as the circuit; circuit activity is traced by task 2's `CircuitTelemetryHandler`; ruled 2026-10-01) and static asset endpoints) and meters `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`; `Microsoft.AspNetCore.Components*` sources and meters if .NET 10 publishes them (report which names exist).
- `src/Platform.Web/Program.cs`, `src/Platform.Worker/Program.cs`: call the registration; outside Development and Testing call `builder.Logging.ClearProviders()` before it, so Serilog is the only provider (O-16, Q8); in Development and Testing MEL's console provider stays beside Serilog. Startup failures before the host builds still reach stderr.
- `src/Platform.Web/appsettings.Development.json`, `src/Platform.Worker/appsettings.Development.json`: `Telemetry:OtlpEndpoint` = `http://localhost:4317`.
- `Directory.Packages.props`: the packages named in the header.
- `tests/Platform.IntegrationTests/Infrastructure/CapturedTelemetry.cs`: a helper for a host: spans and metrics through the OpenTelemetry in-memory exporters (`ConfigureOpenTelemetryTracerProvider`, `ConfigureOpenTelemetryMeterProvider` in `ConfigureTestServices`); logs through `CapturingLogSink`, an in-memory Serilog `ILogEventSink` (a thread-safe list of `LogEvent`) added to `TelemetryLogSinks`, so a test sees each event after the redaction enricher, as the OTLP sink does. Used by tasks 1 to 7b.

**Tests (integration unless noted):**
- `Serilog_is_a_logging_provider_and_never_replaces_the_logger_factory`: in the web host and the worker (through `JobServerHost`), the resolved `ILoggerFactory` is `Microsoft.Extensions.Logging.LoggerFactory`; the registered `ILoggerProvider`s include `SerilogLoggerProvider` exactly once and no `OpenTelemetryLoggerProvider`.
- Unit: `No_code_replaces_the_logger_factory_with_serilog`: scans every `*.cs` under `src/` (with `TestRepo`): `UseSerilog(` appears nowhere, and no `AddSerilog(` appears either (the provider is registered directly, so the configured levels stay the only gate).
- `The_mel_filter_rules_are_the_only_level_gate`: with `Logging:LogLevel:Default` = `Warning`, an Information record is not captured; with `Debug` for one category, a Debug record of that category is captured.
- `A_tenant_request_produces_one_server_span_with_its_route_and_status`: GET a tenant page on acme; exactly one server span, `http.route` set, `http.response.status_code` 200.
- `Health_alive_and_framework_requests_produce_no_span`.
- `A_database_call_during_a_request_is_a_child_span_without_parameter_values`: a staff page that queries; a child span from source `Npgsql` exists; no tag value contains the test tenant's id as a literal parameter (the statement text uses placeholders).
- `Both_hosts_name_their_service_version_and_environment_in_the_resource`: web and worker resources carry `service.name` `waslabid-web` and `waslabid-worker`, a non-empty `service.version`, and `deployment.environment.name`.
- `Without_an_otlp_endpoint_the_host_starts_and_registers_no_otlp_exporter` (neither the OpenTelemetry exporter nor Serilog's OTLP sink).
- `With_the_collector_unreachable_requests_still_succeed`: endpoint set to an unused local port; 20 requests all 200; the host does not throw on shutdown.
- `Outside_development_no_console_log_provider_is_registered` (environment `Production` through `PlatformWebFactory`); `In_development_the_console_log_provider_stays`.
- Unchanged and green: `A_host_that_would_log_the_key_ring_below_information_does_not_start`, `A_key_the_host_cannot_decrypt_is_never_written_whole_to_a_log_even_at_trace_level`, `The_worker_and_the_projects_it_is_built_from_do_not_load_the_key_ring`, `The_worker_does_not_reference_the_web_host` (the worker must not gain an ASP.NET Core framework reference through `Platform.Shared`; the ASP.NET Core instrumentation stays in `Platform.Web`).

**Verify:** `dotnet build WaslaBid.slnx -warnaserror`, `dotnet test WaslaBid.slnx`, `dotnet format WaslaBid.slnx --verify-no-changes`.

---

### Task 2: Context on every record, correlation id, job traces (O-7, O-8, O-9)

**Agent:** developer.

**Files:**
- `src/Platform.Web/Telemetry/RequestTelemetryMiddleware.cs`: runs right after `TenantMiddleware`; when a tenant is set, tags the server span (`waslabid.tenant.id`, `waslabid.tenant.slug`) and opens a log scope with the same pair for the rest of the request (the Serilog provider turns scope entries into event properties); registers `Response.OnStarting` to add `X-Correlation-Id` = `Activity.Current.TraceId` (hex, 32 characters) on every response, platform host and `/health` included.
- `src/Platform.Web/Telemetry/UserTelemetryMiddleware.cs`: runs after `ActingUserMiddleware`; adds `user.id` (the `sub` claim, never `email`, `preferred_username` or `name`) to the span and a nested scope; after `VendorContextMiddleware`, `waslabid.vendor_company.id` when a vendor context is set (one middleware with two entry points is fine; report the choice).
- `src/Platform.Web/Telemetry/UntrustedTraceContextPropagator.cs`: a `DistributedContextPropagator` registered in DI for the web host that extracts nothing from inbound requests (so every request starts its own trace) and injects normally on outbound calls.
- `src/Platform.Web/Telemetry/CircuitTelemetryHandler.cs`: a `CircuitHandler` ordered after `TenantCircuitHandler`, using `CreateInboundActivityHandler` to open the same tenant, user and vendor scope around every inbound circuit activity.
- `src/Platform.Shared/Jobs/TenantJobFilter.cs`: also stamps `TraceParent` (`Activity.Current?.Id`) when a current activity exists.
- `src/Platform.Shared/Jobs/JobTelemetryFilter.cs`: an `IServerFilter` that, in `OnPerforming`, starts activity `job <Type>.<Method>` from source `WaslaBid.Jobs` with the stored `TraceParent` as parent (or a new trace), tags `waslabid.job.id`, `waslabid.job.type`, `waslabid.tenant.id`, and opens a log scope with the same; in `OnPerformed`, sets status Error with `exception.type` on failure, records `waslabid.jobs.duration` and `waslabid.jobs.failed`, and disposes both. Never records job arguments (the rule `A_job_alert_never_contains_the_job_arguments` already holds for alerts).
- `src/Platform.Shared/Jobs/JobsModule.cs`: `HostScopedFilterProvider` also takes the host's `IServerFilter` registrations (today it takes only `IElectStateFilter` and `IApplyStateFilter`); register `JobTelemetryFilter` in `AddJobServer`.
- `src/Platform.Web/Program.cs`: the middleware in the order above; `app.UseMiddleware<RequestTelemetryMiddleware>()` directly after `TenantMiddleware`.
- `src/Platform.Web/Components/Pages/Dev/Throw.razor` or a minimal endpoint `GET /dev/throw` (Development only, behind the existing `/dev` 404 outside Development) that throws `InvalidOperationException("Deliberate failure for the W-10 checks.")`, used by task 10.

**Tests (integration; logs as events captured by `CapturingLogSink`, asserting on their properties and `TraceId`):**
- `A_log_written_during_a_tenant_request_carries_the_tenant_id_slug_and_trace_id`: the event has properties `waslabid.tenant.id` and `waslabid.tenant.slug`, and its `TraceId` equals the server span's.
- `A_log_written_after_sign_in_carries_the_user_id_and_never_the_email`: test principal with `sub` and `email`; the event has property `user.id` = sub; no property value or rendered message contains the email.
- `A_vendor_request_log_carries_the_vendor_company_id` (property `waslabid.vendor_company.id`).
- `A_platform_host_request_carries_no_tenant_id`.
- `Every_response_carries_its_trace_id_as_the_correlation_id`: tenant page, platform page, `/health`, and a 404 on an unknown host all carry `X-Correlation-Id`; for the tenant page it equals the captured server span's trace id.
- `A_traceparent_sent_by_a_client_does_not_become_the_request_trace_id`.
- `A_log_written_inside_a_circuit_event_carries_the_tenant_id`: invoke the handler's inbound activity delegate around a logging call (the pattern of `CircuitRevalidationTests`).
- `A_job_enqueued_during_a_request_continues_that_requests_trace`: enqueue from an acme request scope in the web factory, run it on a `JobServerHost` against the same database; the job span's trace id equals the request's.
- `A_log_written_inside_a_job_carries_the_job_id_type_and_tenant_id` (properties `waslabid.job.id`, `waslabid.job.type`, `waslabid.tenant.id`).
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
- `src/Platform.Shared/Telemetry/RedactingEnricher.cs`: a Serilog `ILogEventEnricher`, registered after every other enricher: redacts every string scalar property value (also inside structure, sequence and dictionary values); for an event with an exception, adds `exception.type` (full type name), `exception.message` and `exception.stacktrace` (both redacted; the stack from `Exception.ToString()`).
- `src/Platform.Shared/Telemetry/RedactingDestructuringPolicy.cs`: a Serilog `IDestructuringPolicy`: a value logged with `{@...}` is destructured with every string member redacted; `HttpRequest`, `HttpContext`, `IFormCollection`, `IHeaderDictionary`, `ClaimsPrincipal` and `Stream` are refused (logged as their type name only); the logger also sets `Destructure.ToMaximumDepth(4)`, `ToMaximumStringLength(4096)` and `ToMaximumCollectionCount(32)`.
- `src/Platform.Shared/Telemetry/RedactedEventSink.cs`: wraps both the OTLP sink and `TelemetryLogSinks`. An enricher cannot replace Serilog's `LogEvent.Exception`, and the OTLP sink would export the raw message from it; so the wrapper passes a copy of each event with `Exception` null (the enricher's `exception.*` properties carry type, masked message and masked stack) and, when the message template has no property tokens (library text logged as a literal), with the template text redacted. The exported record never carries the raw exception message. If the Serilog version does not allow that, stop and report.
- `src/Platform.Shared/Telemetry/ComponentEnricher.cs`: adds `waslabid.component` from `SourceContext` (the MEL category) by the table in spec section 5.3.
- `src/Platform.Shared/Telemetry/RedactingSpanProcessor.cs`: `BaseProcessor<Activity>`, `OnEnd`: removes `url.query`, redacts `url.full` (query part dropped) and every string tag and exception event message; registered last in the tracer pipeline.
- `tests/Platform.UnitTests/Architecture/LogTemplateTests.cs`: scans every `*.cs` under `src/` (with `TestRepo`) for `[LoggerMessage(... Message = "...")]` templates and for `Log*(` calls with a literal template.

**Tests:**
- Unit: `Emails_long_digit_runs_jwts_and_password_pairs_are_masked` (theory: English and Arabic sentences, `ahmad@example.sa`, a CR `1010123456`, an iqama `2123456789`, `0551234567`, `SA0380000000608010167519`, a JWT, `Host=db;Password=abc;`); `Trace_ids_guids_and_short_numbers_are_kept` (a 32-hex trace id, a GUID whose last group is twelve digits, `404`, `2026-09-30`); `Arabic_indic_digit_runs_are_masked_too`.
- Unit: `No_log_template_names_a_personal_or_secret_value`: placeholders refused (case-insensitive): `Email`, `Name`, `DisplayName`, `Phone`, `Cr`, `CrNumber`, `NationalId`, `Iqama`, `Iban`, `FileName`, `Password`, `Secret`, `Token`, `ConnectionString`, `Price`, `Amount`, `Total`, `Offer`, `Envelope`; the test lists the offending file and line. It must pass on today's templates; if one fails, stop and report it rather than editing the template silently.
- Unit: `An_object_logged_with_destructuring_has_its_strings_masked_and_request_types_refused`.
- Unit: `A_literal_library_message_is_masked_before_export` (a template with no tokens that holds an email reaches the wrapped sink as `[email]`).
- Integration: `A_logged_exception_leaves_with_its_type_and_stack_and_a_masked_message`: an exception whose message holds an email; the captured event has no `Exception`, `exception.type` = `System.InvalidOperationException`, `exception.message` with `[email]`, a non-empty `exception.stacktrace`, and no property or rendered message contains the address.
- Integration: `A_keycloak_admin_lookup_by_email_leaves_no_email_in_any_span_or_log`: `KeycloakAdminClient.FindUserByEmail` against `FakeHttpServer`; no exported span tag, captured log property or message contains the address (`url.full` of `/users?email=...&exact=true`).
- Integration: `No_request_body_form_value_or_query_string_reaches_a_log_or_span`: post the vendor company form and a query string, each with a unique marker; the marker appears in no exported span or captured event.
- Integration: `The_key_ring_cap_also_holds_for_the_telemetry_exporter`: with `Logging:LogLevel:Default` = `Trace` in `Testing`, `CapturingLogSink` receives no event whose `SourceContext` starts with `Microsoft.AspNetCore.DataProtection` below Information.
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
- `src/Modules/Operations/Platform.Modules.Operations/Health/TelemetryHealthCheck.cs`: `GET {Telemetry:CollectorHealthUrl}` (the collector's `health_check` extension, `http://localhost:13133/` in Development) and `GET {Telemetry:ElasticsearchHealthUrl}` (`http://localhost:9200/_cluster/health`) with Basic credentials from `Telemetry:ElasticsearchUser` and `Telemetry:ElasticsearchPassword` (the monitoring user `waslabid_monitor` of task 8, cluster privilege `monitor` only; the password in user secrets on a developer machine and the secret store on the pilot, never in appsettings). Healthy when the collector answers 200 and the cluster status is `green` or `yellow`; Unhealthy names which failed (`collector`, `elasticsearch`, or both) with the exception type, the HTTP status or the cluster status only, never a URL with credentials, the user name, the password or the response body (N-10). Registered only when both URLs are present, so hosts without the stack (CI) skip it.
- `src/Modules/Operations/Platform.Modules.Operations.Contracts/HealthComponents.cs`: `Telemetry` constant, documented like `Disk`: alerted, not a board tile; `Board` unchanged.
- `src/Platform.Worker/appsettings.Development.json`: the two URLs and `Telemetry:ElasticsearchUser` = `waslabid_monitor`; the password through user secrets (`ELASTIC_MONITOR_PASSWORD` from `infra/compose/.env`).

**Tests:**
- Integration: `Alive_answers_healthy_without_a_database_tenant_or_sign_in` (the same unusable connection string as `Health_endpoint_answers_without_a_tenant_or_sign_in_and_is_unhealthy_without_a_database`, which stays green).
- Integration: `Alive_is_served_on_tenant_and_platform_hosts_and_on_an_unknown_host`; `A_path_under_alive_is_an_ordinary_tenant_path` (`/alive/x` on an unknown host is 404, as `/health/x` is).
- Integration: `Each_health_result_is_published_as_a_status_and_latency_metric` (in-memory metric exporter on the worker; one point per component with the right status value).
- Integration: `Telemetry_reports_unhealthy_naming_the_collector_or_elasticsearch_when_either_is_down` (`FakeHttpServer` for both; theory: the collector answering 503, Elasticsearch unreachable, Elasticsearch answering status `red`, both down).
- Integration: `The_telemetry_check_sends_the_monitoring_credentials_and_never_reports_them` (the fake Elasticsearch sees Basic credentials; no check result, incident text or captured log contains the password or the user name).
- Integration: `A_telemetry_outage_opens_one_incident_and_sends_one_alert_and_one_recovery` (through the existing pipeline and Mailpit, like `An_incident_sends_one_email_and_no_repeat_while_open`).
- Unit: `Telemetry_is_not_a_board_tile`.
- Unchanged and green: `The_board_lists_every_component_with_status_latency_and_last_check`, `The_check_reports_on_the_configured_path`.

**Verify:** the three commands.

---

### Task 5: Concurrent users (spec 6.1 to 6.3; O-19, O-20, O-24)

**Agent:** developer. Built and merged in PR #6 (2026-10-01).

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

**Agent:** developer. Built and merged in PR #6 (2026-10-01).

**Files:**
- `src/Modules/Identity/Platform.Modules.Identity/Migrations/0003_identity_user_activity.sql` (as built: 0003, since PR #5 adds identity 0002): table `identity.user_activity` as in spec 6.4; the trigger setting `hour` from the database clock; `platform.enable_tenant_rls` is not used (its policy is for all commands): forced row-level security with the two policies of spec 6.4 (`tenant_isolation` for SELECT with the helper's staff-only text, `tenant_activity_insert` for INSERT), as audit 0002 and 0003 do for `audit.events`; `grant insert` only to `erp_app`; security-definer `identity.activity_counts(p_now timestamptz)` and `identity.prune_activity(p_before timestamptz)` with `set search_path`, execute revoked from public and granted to `erp_app`, answering only a session with neither a tenant nor a vendor context (the `vendor.stale_uploads()` rule of ADR-0012 point 4); the owner guard of vendors and operations (run as a role with BYPASSRLS).
- `src/Modules/Identity/Platform.Modules.Identity.Contracts/UserActivity.cs`: `enum ActivityKind { Staff, Vendor }`, `enum ActivityWindow { OneDay, SevenDays, ThirtyDays }`, `record ActivityCount(Guid? TenantId, ActivityKind Kind, ActivityWindow Window, int Users)` (null tenant = across tenants), `IUserActivityRecorder.RecordAsync(ActivityKind kind, CancellationToken)`, `IUserActivityCounts.CountAsync(DateTimeOffset now, CancellationToken)` and `PruneAsync(DateTimeOffset before, CancellationToken)`.
- `src/Modules/Identity/Platform.Modules.Identity/Activity/UserActivityRecorder.cs` with a singleton `ActivityThrottle` (key tenant, `sub`, kind, hour from `TimeProvider`; entries older than two hours dropped); inserts through the scope's tenant connection with `on conflict do nothing`; a failure is logged with `{ErrorType}` and swallowed.
- `src/Modules/Identity/Platform.Modules.Identity/Activity/UserActivityCounts.cs`; `IdentityModule.cs`: `AddIdentityActivityCounts()` for the worker (counts and prune only, no Keycloak settings).
- `src/Platform.Web/Usage/UserActivityMiddleware.cs`, after `VendorContextMiddleware`: records `Staff` or `Vendor` by `UsageKind`; nothing for `/health`, `/alive`, static files, anonymous or uncounted sessions. `UsageCircuitHandler.CreateInboundActivityHandler` records the same way for circuit activity.
- `src/Modules/Operations/Platform.Modules.Operations/Migrations/0006_operations_active_user_counts.sql`: `ops.active_user_counts` as in spec 6.6; `erp_app` select, insert, delete.
- `src/Modules/Operations/Platform.Modules.Operations.Contracts/IUsageLog.cs`: `LatestAsync()` returning the rows and their `computed_at` (the `IHealthLog` pattern).
- `src/Modules/Operations/Platform.Modules.Operations/Usage/UsageMetricsJob.cs` and `UsageSnapshot.cs`: recurring job `usage-metrics`, `*/5 * * * *`, registered as `HealthCheckJob` is; counts, maps tenant ids to slugs through `ITenantSlugs` (as built: a new Tenancy contract, since `ITenantCatalog` refuses the worker's scopes), replaces `ops.active_user_counts` in one transaction, stores the result in `UsageSnapshot` (singleton) whose observable gauges `waslabid.users.active` and `waslabid.users.active.all_tenants` report nothing when the result is older than 15 minutes; prunes buckets older than 35 days once a day. The Operations module gains references to `Platform.Modules.Identity.Contracts` and `Platform.Modules.Tenancy.Contracts` (contracts only, as the module rules require).
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

**Verify:** the three commands.

---

### Task 7: The console usage page (spec 6.6; O-23, O-25, O-26)

**Agent:** developer. Built and merged in PR #6 (2026-10-01), with the Grafana link that task 7b replaces.

**Files:**
- `src/UI/Platform.UI/Components/StatTile.razor`: label, value (`int?`; null shows a dash with an accessible "Unknown"), optional split lines (label and value), `data-*` passthrough; logical direction utilities only; no uppercase text.
- `src/Platform.Web/Components/Pages/Dev/Gallery.razor`: `StatTile` with a value, a split and no value, in both panels.
- `src/Platform.Web/Components/Pages/Console/Usage.razor`: `@page "/platform/usage"`, `[Authorize(Policy = PlatformAuthentication.PolicyName)]`, `ConsoleLayout`, `InteractiveServer`; four tiles and the per-tenant `DataTable` of spec 6.6 with `data-tenant`, `data-kind`, `data-window` hooks; `ConsoleTime` for the result's time; a stale result shows dashes; the dashboard link only when its setting is valid; Refresh as on the health board.
- `src/Platform.Web/Usage/UsageOverview.cs`: joins `ITenantCatalog` (portal names), `ConnectedCircuits.Snapshot()` and `IUsageLog.LatestAsync()`; never an HTTP client.
- `src/Platform.Web/Components/Layout/ConsoleLayout.razor`: navigation entry `Console.Nav.Usage` between Tenants and Jobs.
- `src/UI/Platform.UI/Resources/SharedResource.ar-SA.resx` and `SharedResource.en-US.resx`: every new string (`Console.Nav.Usage`, `Console.Usage.*`, `StatTile.Unknown`) in Arabic and English.
- `tests/Platform.UITests/golden/*.png` and its README: retaken with `tests/e2e/golden.mjs` (the gallery now shows `StatTile`).

**Tests:**
- bUnit (`tests/Platform.UITests/Components/StatTileTests.cs`): `StatTile_shows_its_label_value_and_split`; `StatTile_without_a_value_shows_a_dash_named_unknown`; `ComponentConventionTests` and `PhysicalUtilityLintTests` cover it unchanged.
- Integration (`PlatformConsoleUsageTests`): `The_usage_section_shows_concurrent_and_active_users_per_tenant`; `The_usage_totals_count_a_vendor_active_on_two_tenants_once`; `A_stale_usage_result_shows_unknown`; `Before_the_first_count_active_users_show_unknown`; `Usage_counts_are_read_from_the_stored_job_results_not_from_prometheus` (renamed by task 7b); the Grafana link tests (renamed by task 7b); `The_usage_page_lists_no_user`; `A_tenant_session_cannot_open_the_usage_page`; `A_platform_admin_without_otp_cannot_open_the_usage_page`; `The_console_navigation_lists_usage_between_tenants_and_jobs`; `The_web_host_registers_one_registry_and_the_usage_circuit_handler`.
- Integration: `/platform/usage` in both cultures in `A_console_page_follows_the_culture_and_shows_no_resource_key`.

**Verify:** the three commands; the golden screenshots retaken and compared by eye in both directions at 360 and 1280 px.

---

### Task 7b: Kibana link on the usage page (spec 6.6; O-23, ADR-0014)

**Agent:** developer. In the second pull request; it changes code merged in PR #6.

**Files:**
- `src/Platform.Web/Usage/GrafanaLink.cs` renamed to `KibanaLink.cs`: class `KibanaLink`, `Setting = "Observability:KibanaUrl"`, dashboard path `app/dashboards#/view/waslabid-usage` (the saved dashboard id of task 8). Validation unchanged: only an absolute `http` or `https` address with a host makes a link; anything else makes none and logs one warning per process naming the scheme only, its message now naming Kibana.
- `src/Platform.Web/Usage/UsageOverview.cs`: `GrafanaDashboardUrl` becomes `KibanaDashboardUrl`; the doc comment says the overview never queries Elasticsearch or Kibana.
- `src/Platform.Web/Components/Pages/Console/Usage.razor`: `data-kibana="dashboard"`, text `Console.Usage.OpenKibana`.
- `src/Platform.Web/Program.cs`: `AddSingleton<KibanaLink>()`.
- `src/UI/Platform.UI/Resources/SharedResource.ar-SA.resx` and `SharedResource.en-US.resx`: `Console.Usage.OpenGrafana` replaced by `Console.Usage.OpenKibana` ("Open the history in Kibana"; "عرض السجل في Kibana").
- `src/Platform.Web/appsettings.Development.json`: `Observability:KibanaUrl` = `http://127.0.0.1:5601`.

**Tests (`tests/Platform.IntegrationTests/Web/PlatformConsoleUsageTests.cs`, renamed and adjusted):**
- `The_grafana_link_appears_only_when_configured` becomes `The_kibana_link_appears_only_when_configured`: without the setting no `data-kibana`; with `http://127.0.0.1:5601/` the link's `href` is `http://127.0.0.1:5601/app/dashboards#/view/waslabid-usage`.
- `The_grafana_link_is_shown_only_for_an_absolute_http_address` becomes `The_kibana_link_is_shown_only_for_an_absolute_http_address`: the same theory with `kibana-secret` values (`javascript:`, `data:`, `file:`, `ftp:`, relative, a host without a scheme); one warning from a category ending `.KibanaLink` naming the scheme; no log contains the value.
- `The_grafana_link_accepts_an_https_address` becomes `The_kibana_link_accepts_an_https_address` (`https://kibana.example/base` gives `https://kibana.example/base/app/dashboards#/view/waslabid-usage`).
- `Usage_counts_are_read_from_the_stored_job_results_not_from_prometheus` becomes `Usage_counts_are_read_from_the_stored_job_results_not_from_elasticsearch`: `Observability:KibanaUrl` and `Observability:ElasticsearchUrl` pointing at an unused port; the page renders the stored numbers and makes no outbound HTTP call.
- Unchanged and green: `A_console_page_follows_the_culture_and_shows_no_resource_key` (now covering `Console.Usage.OpenKibana`), the rest of `PlatformConsoleUsageTests`, `PlatformConsoleTests` and `CrossHostSessionTests`.

**Verify:** the three commands; `grep -ri grafana src tests` finds nothing.

---

### Task 8: Compose services (O-3, O-4, O-12, O-13, O-17, O-18; section 9 limits)

**Agent:** devops.

**Files:**
- `infra/compose/docker-compose.yml`, services with container names in the existing `erp-*` pattern, each image pinned by version and digest (multi-architecture digests, since the pilot is arm64), all ports bound to `127.0.0.1`:
  - `otel-collector` (`otel/opentelemetry-collector-contrib`, `erp-otel-collector`, `mem_limit: 256m`): ports 4317 (OTLP gRPC), 4318 (OTLP HTTP), 13133 (health); `collector.yaml` mounted read-only; `ELASTIC_COLLECTOR_PASSWORD` from `.env`; starts after `elastic-setup` completed successfully.
  - `elasticsearch` (`docker.elastic.co/elasticsearch/elasticsearch:9.x`, `erp-elasticsearch`, `mem_limit: 1536m`): port 9200; `discovery.type=single-node`, `ES_JAVA_OPTS=-Xms768m -Xmx768m`, `xpack.security.enabled=true`, `xpack.security.http.ssl.enabled=false`, `xpack.security.transport.ssl.enabled=false`, `xpack.license.self_generated.type=basic`, `ELASTIC_PASSWORD=${ELASTIC_PASSWORD:?Set ELASTIC_PASSWORD in infra/compose/.env}`; volume `elasticsearch-data`; health check without credentials (`curl` answering 401 means the node is up), so no password appears in the health check.
  - `kibana` (`docker.elastic.co/kibana/kibana:9.x`, same version as Elasticsearch, `erp-kibana`, `mem_limit: 768m`, `NODE_OPTIONS=--max-old-space-size=512`, `profiles: [kibana]` so it runs only on `docker compose --profile kibana up -d` (spec O-17)): port 5601; `ELASTICSEARCH_HOSTS=http://elasticsearch:9200`, `ELASTICSEARCH_USERNAME=kibana_system`, `ELASTICSEARCH_PASSWORD=${KIBANA_SYSTEM_PASSWORD:?...}`, the three Kibana encryption keys from `KIBANA_ENCRYPTION_KEY`, Kibana's usage telemetry to Elastic off (`TELEMETRY_OPTIN=false`, `TELEMETRY_ALLOWCHANGINGOPTINSTATUS=false`; data residency, N-01); starts after Elasticsearch is healthy; health check on `/api/status` (200 or 401 means up).
  - `elastic-setup` (the Elasticsearch image, `erp-elastic-setup`, `restart: "no"`): runs `infra/compose/observability/elastic-setup.sh` (mounted read-only) as `elastic` after Elasticsearch is healthy; idempotent, so every `up -d` may run it again. It (1) sets the `kibana_system` password to `KIBANA_SYSTEM_PASSWORD`; (2) creates roles `waslabid_collector_writer` (`auto_configure` and `create_doc` on `logs-*`, `traces-*`, `metrics-*`), `waslabid_monitor` (cluster `monitor` only) and `waslabid_errors_reader` (F-53: `read` and `view_index_metadata` on `logs-*` only, no write, no cluster privilege beyond what ES|QL needs), and users `waslabid_collector` (`ELASTIC_COLLECTOR_PASSWORD`), `waslabid_monitor` (`ELASTIC_MONITOR_PASSWORD`) and the named staff user `${KIBANA_STAFF_USER}` (`KIBANA_STAFF_PASSWORD`, built-in role `viewer`); (3) installs index lifecycle policies `waslabid-logs`, `waslabid-traces`, `waslabid-metrics` that delete after `TELEMETRY_LOGS_RETENTION`, `TELEMETRY_TRACES_RETENTION` and `TELEMETRY_METRICS_RETENTION`, attached with `index.number_of_replicas: 0` through the `@custom` component templates that Elasticsearch's built-in OTel index templates compose (record their names; if those templates manage retention by data stream lifecycle instead, set `data_retention` there with the same values and report it); It prints no password and exits non-zero on any failed step.
  - `kibana-setup` (a small curl image pinned by digest, `erp-kibana-setup`, `restart: "no"`, `profiles: [kibana]`): after Kibana is healthy, imports `infra/compose/observability/kibana/waslabid-usage.ndjson` with `POST /api/saved_objects/_import?overwrite=true` as the staff user; idempotent; prints no password; exits non-zero on failure.
- `infra/compose/observability/collector.yaml`: receivers `otlp` (gRPC and HTTP); processors `memory_limiter` (200 MB), `batch` (or the exporter's own batching if the pinned version recommends it; report which), and an `attributes` step deleting `url.query` and the pattern `http.request.header.*` from spans and logs; exporter `elasticsearch` (endpoint `http://elasticsearch:9200`, user `waslabid_collector`, password `${env:ELASTIC_COLLECTOR_PASSWORD}`, `mapping: mode: otel`); extension `health_check` (`0.0.0.0:13133` inside the network); pipelines for traces, logs and metrics.
- `infra/compose/observability/kibana/waslabid-usage.ndjson`: data view `waslabid-metrics` on `metrics-*`; dashboard id `waslabid-usage`, title "WaslaBid usage": controls `tenant` (options list on the tenant slug, multi-select) and `kind`; sections Now, Active and Tenders with the Lens panels of spec 6.7; field names as the otel mapping stores them (for example `metrics.waslabid.users.concurrent` by `attributes.waslabid.tenant.slug`); the tender panels' descriptions say they wait for the tender slice. Task 10 confirms the field names and corrects the file in the same pull request if they differ.
- `infra/compose/.env.example`: `ELASTIC_PASSWORD=`, `KIBANA_SYSTEM_PASSWORD=`, `KIBANA_STAFF_USER=`, `KIBANA_STAFF_PASSWORD=`, `ELASTIC_MONITOR_PASSWORD=`, `ELASTIC_COLLECTOR_PASSWORD=` (each generated with `openssl rand -hex 16`), `KIBANA_ENCRYPTION_KEY=` (`openssl rand -hex 32`; at least 32 characters), `TELEMETRY_LOGS_RETENTION=3d`, `TELEMETRY_TRACES_RETENTION=3d`, `TELEMETRY_METRICS_RETENTION=3d`, with a comment naming the pilot values `30d`, `7d`, `30d`.
- Volume `elasticsearch-data` (Kibana keeps its state in Elasticsearch).
- The host needs `vm.max_map_count` of at least 262144 (Docker Desktop's WSL2 VM on Windows; a sysctl on the pilot VM in W-19); task 9 documents it.
- CI: the Trivy step covers the new images the same way it covers the existing ones.

**Acceptance (recorded evidence, not a unit test):** from a clean clone, `cp .env.example .env`, fill the values, `docker compose up -d`: Elasticsearch and Kibana healthy and the collector running within four minutes (a missing Compose health check, such as the collector image's, is documented with the reason; W-01's acceptance counts healthy services, and its count changes in docs/09 only through the user); `erp-elastic-setup` exits 0, and a second `up -d` runs it again cleanly; `docker compose ps` shows no restarts; `curl -s -u waslabid_monitor:... http://127.0.0.1:9200/_cluster/health` answers `green` or `yellow`; `GET _ilm/policy/waslabid-*` shows the three policies with the configured ages; `curl -s http://127.0.0.1:13133/` answers; Kibana at `http://127.0.0.1:5601` refuses anonymous access, the staff user signs in, sees the "WaslaBid usage" dashboard with "No results" rather than an error before the app runs, and cannot open user or role management; `docker stats --no-stream` shows each new service under its limit; nothing new listens on a non-loopback address (`netstat -an` on Windows); no password appears in `docker compose logs`.

---

### Task 9: Documentation

**Agent:** developer (docs only), after tasks 1 to 8.

The decisions themselves were recorded on 2026-10-01 in a separate change (branch `w-10-elk-docs`): ADR-0014, the docs/02 observability row and diagram label, the docs/03 diagram 9 label, docs/05 row 21, the W-10 row, size and acceptance and W-19's dependency in docs/09, this plan and the spec. Task 9 changes those only where the build differs from them (for example the ECS fallback of O-4).

**Files:**
- `docs/07-ways-of-working.md` section 4: rows in the service table for the collector, Elasticsearch, `elastic-setup`, and Kibana with `kibana-setup` (profile `kibana`: `docker compose --profile kibana up -d`) (purpose, port, credentials from `.env`), the new `.env` values in the "Run the app locally" list, `vm.max_map_count` on Docker Desktop, the `Telemetry:*` settings with the monitoring password in user secrets, `Observability:KibanaUrl`, how to find a failed request (copy `X-Correlation-Id` from the browser's network panel, then Kibana Discover `trace_id : "<id>"` on the logs and traces data views), `/alive` versus `/health`, that the Elastic part costs about 1.75 GB always on plus 768 MB while Kibana runs, and where usage shows: the console page `/platform/usage` and the Kibana dashboard "WaslaBid usage".
- `docs/09-backlog.md`: W-10 status and evidence; the F-60 note "W-10 is not Done yet" updated; the F-54 note on usage kept or corrected.
- `README.md` onboarding (the new `.env` values), `CLAUDE.md` "What this repository is" (one sentence: telemetry through the collector to Elasticsearch, Kibana on port 5601 with the "WaslaBid usage" dashboard, and the console usage page `/platform/usage`).

**Verify:** Mermaid renders for any edited diagram; `dotnet build WaslaBid.slnx -warnaserror` still green (no code change expected).

---

### Task 10: End-to-end smoke and evidence

**Agent:** qa-engineer (writes only under `tests/`).

**Files:** `tests/e2e/observability.mjs` (steps 1 to 7 need no browser: `fetch` through Caddy with the local certificate accepted as the other scripts do, and to Elasticsearch and Kibana on `127.0.0.1` with credentials read from `infra/compose/.env`, never printed; step 8 uses the Playwright helpers of `lib.mjs`), a section in `tests/e2e/README.md`.

**Steps the script runs and checks:**
1. `GET https://acme.localhost:8443/dev/throw` answers 500 and carries `X-Correlation-Id`.
2. Within 60 seconds, Elasticsearch `POST http://127.0.0.1:9200/traces-*/_search` with a term query on the trace id returns the trace; its server span carries `waslabid.tenant.id` equal to acme's id. Record the exact field names the otel mapping uses for the trace id and span attributes.
3. Within 60 seconds, ES|QL (`POST /_query`) `FROM logs-* | WHERE trace_id == "<id>"` returns one record at Error severity with `exception.type` `System.InvalidOperationException`, `waslabid.component` `Web` and the tenant id, and no raw exception object. Record the exact field names and correct spec section 8's query in the same pull request if they differ.
4. The F-53 query from spec section 8, run as a temporary user with the `waslabid_errors_reader` role (created by the script with the `elastic` user and deleted at the end), counts at least one error for `waslabid-web` over the last hour; the same user's attempt to index a document into `logs-*` is refused (403).
5. After one run of `tests/e2e/vendor.mjs` (registration, uploads and consent, which handle real email addresses and CR numbers), no log record from `waslabid-web` or `waslabid-worker` of the last hour matches an email address (ES|QL `RLIKE` on the message field) or holds the CR number the run used (a `query_string` search for it over all fields of `logs-*`).
6. `docker stop erp-otel-collector`; ten `GET` requests to `https://acme.localhost:8443/` all answer within their usual time (print p95); within two minutes Mailpit holds one "[WaslaBid] Telemetry is down" email naming the collector; `docker start erp-otel-collector`; one "has recovered" email follows. The same with `docker stop erp-elasticsearch`, the email naming Elasticsearch.
7. `GET /alive` on acme, on the platform host and with PostgreSQL stopped (`docker stop erp-postgres`, then start it again) answers 200; `/health` answers 503 while PostgreSQL is stopped, as today.
8. Usage: sign in on acme as a throwaway staff admin (`throwawayStaff` of `admin.mjs`, cleaned up at the end) and keep the page open. Within two export intervals (about two minutes) an ES|QL query on `metrics-*` for the concurrent-users metric of acme `staff` returns 1; `GET http://127.0.0.1:5601/api/saved_objects/dashboard/waslabid-usage` as the staff user finds the dashboard, and a screenshot of it signed in as the staff user shows 1 concurrent `staff` user for acme. Record the exact metric field names and correct the dashboard NDJSON and spec 6.1 if they differ. After the usage job has run (up to five minutes), the active-users metric for acme `staff` and `1d` is at least 1, and the console usage page, opened as a platform admin with OTP as `platform.mjs` does, shows the same numbers for acme, links to Kibana, and lists no email or name. Close the staff page; within two export intervals the concurrent count for acme `staff` is 0. Screenshot the console page in both cultures as evidence.
9. Kibana's Observability views (traces and logs) open the deliberate failure's trace by its id. If they do not read the OTel-native data, record it: that is the O-4 risk, and the fallback (ECS mapping in the collector) is escalated to the user, not switched silently.

Record the run (date, pass or fail per step, the measured p95, the recorded field names) in the W-10 row's evidence, as earlier rows do.

---

### Task 11: error events (dropped 2026-10-01)

Dropped by the user's answer to Q1: no Sentry in the pilot. It would have added GlitchTip or Sentry and the Sentry SDK; GlitchTip remains the later option if error triage in Kibana proves too weak (spec O-1).
