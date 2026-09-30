# Observability (W-10) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Status:** draft for the user's review (2026-09-30). Do not start until the user has answered the open questions in spec section 10, or has said to build on the recommendations. Where an answer changes a task, the task says how.

**Goal:** The web host and the worker emit redacted logs, traces and metrics with tenant, user, job and trace ids through an OpenTelemetry Collector to Loki, Tempo and Prometheus, readable in Grafana; `/alive` beside `/health`; a dead pipeline alerts through F-60.

**Architecture:** Shared telemetry registration in `Platform.Shared/Telemetry` (resource, log provider, redaction processors, job telemetry), web-only parts in `Platform.Web/Telemetry` (ASP.NET Core instrumentation, request and circuit context, inbound trace context, correlation header, `/alive`), the Telemetry health check in the Operations module, five services in `infra/compose`. No new module, schema or page.

**Tech Stack:** as the vendor slice, plus the OpenTelemetry .NET SDK (`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`; tests: `OpenTelemetry.Exporter.InMemory`), Npgsql's built-in `Npgsql` activity source and meter, the OpenTelemetry Collector, Loki 3, Tempo, Prometheus 3, Grafana. No Serilog and no Sentry SDK unless the user decides otherwise (spec Q1, Q2).

**Spec:** `docs/superpowers/specs/2026-09-30-observability-design.md` (decisions O-1 to O-18, open questions Q1 to Q8).

**Style:** like the admin and vendor plans: each task gives files, contracts and the exact tests with their assertions; the implementer writes the code test-first to make them pass and reports every design choice it had to make. Anything that changes an O-decision, a contract, or an existing test's assertion is escalated, not decided.

**Conventions:** worktree `C:\Repo\ERP-w10`, branch `w-10-observability`; commit per task, no AI attribution; test first; `TestContext.Current.CancellationToken` in tests; after each task `dotnet build WaslaBid.slnx -warnaserror`, `dotnet test WaslaBid.slnx` and `dotnet format WaslaBid.slnx --verify-no-changes` are green (Docker running for Testcontainers); new packages pinned in `Directory.Packages.props` at the current stable version with an OSV advisory check recorded in a comment, as the existing entries do; images pinned by version and digest, as Mailpit is; no secret value in any output (N-10).

**Order:** tasks 1 to 4 (developer) in sequence; task 5 (devops) can run beside them; task 6 after 1 to 5; task 7 (qa-engineer) last; task 8 only if Q1 keeps a Sentry-protocol service. Reviewer after each developer task; pentester after task 7, before the merge (redaction and new endpoints are security-sensitive).

---

### Task 1: Telemetry registration in both hosts (O-3, O-5, O-6, O-15 exclusions, O-16)

**Agent:** developer.

**Files:**
- `src/Platform.Shared/Telemetry/TelemetryModule.cs`: `AddPlatformTelemetry(this IHostApplicationBuilder builder, string serviceName)` registering the resource (`service.name`, `service.version` from `AssemblyInformationalVersionAttribute`, `service.instance.id`, `deployment.environment.name` from `Telemetry:Environment`, default the host environment name in lower case), tracing (sources `Npgsql`, `WaslaBid.*`; `System.Net.Http` through `AddHttpClientInstrumentation`), metrics (meters `System.Runtime`, `Npgsql`, `System.Net.Http`, `WaslaBid.*`), logging (`builder.Logging.AddOpenTelemetry(o => { o.IncludeScopes = true; o.IncludeFormattedMessage = true; })`), and the OTLP exporter only when `Telemetry:OtlpEndpoint` (or the standard `OTEL_EXPORTER_OTLP_ENDPOINT`) is set. `TelemetryNames` constants for every attribute name in spec O-9 and section 5.
- `src/Platform.Web/Telemetry/WebTelemetry.cs`: `AddWebTelemetry()` adding ASP.NET Core instrumentation (filter: no span for `/health`, `/alive`, `/_framework/*`, `/_content/*`, `/_blazor/negotiate` and static asset endpoints) and meters `Microsoft.AspNetCore.Hosting`, `Microsoft.AspNetCore.Server.Kestrel`; `Microsoft.AspNetCore.Components*` sources and meters if .NET 10 publishes them (report which names exist).
- `src/Platform.Web/Program.cs`, `src/Platform.Worker/Program.cs`: call the registration; outside Development and Testing remove the console provider (`builder.Logging.ClearProviders()` before adding OpenTelemetry, then nothing else) per O-16. Startup failures before the host builds still reach stderr.
- `src/Platform.Web/appsettings.Development.json`, `src/Platform.Worker/appsettings.Development.json`: `Telemetry:OtlpEndpoint` = `http://localhost:4317`.
- `Directory.Packages.props`: the packages named in the header.
- `tests/Platform.IntegrationTests/Infrastructure/CapturedTelemetry.cs`: a helper that adds in-memory exporters for spans, log records and metrics to a host through `ConfigureTestServices` (`ConfigureOpenTelemetryTracerProvider`, `ConfigureOpenTelemetryMeterProvider`, `ConfigureOpenTelemetryLoggerProvider`), used by tasks 1 to 4.

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
- `src/Platform.Web/Components/Pages/Dev/Throw.razor` or a minimal endpoint `GET /dev/throw` (Development only, behind the existing `/dev` 404 outside Development) that throws `InvalidOperationException("Deliberate failure for the W-10 checks.")`, used by task 7.

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

### Task 5: Compose services (O-3 to O-6, O-12, O-13, O-17; section 8 limits)

**Agent:** devops.

**Files:**
- `infra/compose/docker-compose.yml`: services `otel-collector`, `loki`, `tempo`, `prometheus`, `grafana`, with container names `erp-otel-collector`, `erp-loki`, `erp-tempo`, `erp-prometheus`, `erp-grafana` (the existing `erp-*` pattern), each pinned by version and digest (multi-architecture digests, since the pilot is arm64), with `mem_limit` from spec section 8 and a health check. Images without a shell cannot run a `CMD` check: use the image's own probe command where it has one, otherwise document which service has no Compose health check and why (W-01's acceptance counts healthy services; update its count in docs/09 only through the user).
- Ports, all bound to `127.0.0.1`: collector 4317 (OTLP gRPC), 4318 (OTLP HTTP), 13133 (health); Loki 3100; Tempo 3200; Prometheus 9090; Grafana 3000. No clash with the existing 5432, 6379, 8080, 9000, 9002, 9003, 3310, 1025, 8025.
- `infra/compose/observability/collector.yaml`: receivers `otlp` (gRPC and HTTP); processors `memory_limiter` (200 MB), `batch`, and an attributes step deleting `url.query` and `http.request.header.*` from spans and logs; exporters: OTLP HTTP to Loki `/otlp`, OTLP to Tempo, OTLP HTTP to Prometheus `/api/v1/otlp`; extension `health_check`. Use the core distribution if it covers every component used; otherwise contrib (report which).
- `infra/compose/observability/loki.yaml`: single binary, filesystem storage, schema v13 with TSDB, `allow_structured_metadata: true`, `otlp_config` resource attributes as index labels limited to `service.name` and `deployment.environment.name`, compactor retention on, `retention_period` from `LOKI_RETENTION` (default `72h` locally; `720h` for the pilot).
- `infra/compose/observability/tempo.yaml`: local storage, OTLP receiver on the Compose network only, `block_retention` from `TEMPO_RETENTION` (default `72h`; `168h` pilot).
- Prometheus: command flags `--web.enable-otlp-receiver` and `--storage.tsdb.retention.time=${PROMETHEUS_RETENTION:-3d}` (`30d` pilot).
- `infra/compose/observability/grafana/provisioning/datasources/datasources.yaml`: Loki (derived field `trace_id` linking to Tempo), Tempo (trace to logs by `trace_id` on Loki, service map from Prometheus), Prometheus; anonymous access off; admin user `admin` with `GF_SECURITY_ADMIN_PASSWORD=${GRAFANA_ADMIN_PASSWORD:?Set GRAFANA_ADMIN_PASSWORD in infra/compose/.env}`.
- `infra/compose/.env.example`: `GRAFANA_ADMIN_PASSWORD=` (generate with `openssl rand -hex 16`), `LOKI_RETENTION`, `TEMPO_RETENTION`, `PROMETHEUS_RETENTION` with the local defaults and a comment naming the pilot values.
- Volumes `loki-data`, `tempo-data`, `prometheus-data`, `grafana-data`.
- CI: the Trivy step covers the new images the same way it covers the existing ones.

**Acceptance (recorded evidence, not a unit test):** from a clean clone, `cp .env.example .env`, fill the values, `docker compose up -d`: every new service healthy (or documented) within four minutes, `docker compose ps` shows no restarts, a second `up -d` is clean; `curl -s http://127.0.0.1:3100/ready` answers `ready`; Grafana at `http://127.0.0.1:3000` shows the three datasources, each "working" on its test button; `docker stats --no-stream` shows each new service under its limit; nothing new listens on a non-loopback address (`netstat -an` on Windows).

---

### Task 6: Documentation and decisions

**Agent:** developer (docs only), after tasks 1 to 5.

**Files:**
- `docs/07-ways-of-working.md` section 4: five rows in the service table (purpose, port, credentials: `GRAFANA_ADMIN_PASSWORD`), the new `.env` value in the "Run the app locally" list, `Telemetry:*` settings, how to find a failed request (copy `X-Correlation-Id` from the browser's network panel, Grafana Explore, Tempo, search by trace id; Loki `{service_name="waslabid-web"} | trace_id="<id>"`), `/alive` versus `/health`, and that the stack costs about 1.5 GB of memory.
- `docs/02-core-features-and-tech-stack.md` observability row and the section 4.2 diagram label, `docs/03-diagrams.md` diagram 9 label: per the answers to Q1 and Q2 (for example "OpenTelemetry for .NET, Prometheus, Grafana, Loki for logs, Tempo for traces; Sentry-compatible error tracking later (ADR-0013)"). Render every edited diagram with `npx @mermaid-js/mermaid-cli`.
- `docs/adr/0013-observability-stack-for-the-pilot.md` from `0000-template.md`, only if Q1 or Q2 changes the stack row: Sentry and Serilog decisions, the pilot memory budget, retention.
- `docs/09-backlog.md`: W-10 status and the acceptance text from spec section 9 (as the user approved it), size per Q6; the F-60 note "W-10 is not Done yet" updated; W-19 dependency per Q4.
- `README.md` onboarding (the new `.env` value), `CLAUDE.md` "What this repository is" (one sentence: telemetry through the collector to Loki, Tempo and Prometheus, Grafana on port 3000).

**Verify:** Mermaid renders; `dotnet build WaslaBid.slnx -warnaserror` still green (no code change expected).

---

### Task 7: End-to-end smoke and evidence

**Agent:** qa-engineer (writes only under `tests/`).

**Files:** `tests/e2e/observability.mjs` (no browser needed; `fetch` through Caddy with the local certificate accepted as the other scripts do), a section in `tests/e2e/README.md`.

**Steps the script runs and checks:**
1. `GET https://acme.localhost:8443/dev/throw` answers 500 and carries `X-Correlation-Id`.
2. Within 60 seconds, Tempo `GET http://127.0.0.1:3200/api/traces/<id>` returns the trace; its server span has `waslabid.tenant.id` equal to acme's id.
3. Within 60 seconds, Loki `query_range` for `{service_name="waslabid-web"} | trace_id="<id>"` returns one Error line with `exception_type` `System.InvalidOperationException`, `waslabid_component` `Web` and the tenant id; record the exact structured-metadata names Loki uses (spec section 7 assumes dots become underscores) and correct the spec's query in the same pull request if they differ.
4. The F-53 query from spec section 7 over the last hour counts at least one error for `waslabid-web`.
5. After one run of `tests/e2e/vendor.mjs` (registration, uploads and consent, which handle real email addresses and CR numbers), Loki over the last hour holds no line from `waslabid-web` or `waslabid-worker` matching an email address (`|~ "[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\\.[A-Za-z]+"`) or the CR number the run used.
6. `docker stop erp-otel-collector`; ten `GET` requests to `https://acme.localhost:8443/` all answer within their usual time (print p95); within two minutes Mailpit holds one "[WaslaBid] Telemetry is down" email; `docker start erp-otel-collector`; one "has recovered" email follows.
7. `GET /alive` on acme, on the platform host and with PostgreSQL stopped (`docker stop erp-postgres`, then start it again) answers 200; `/health` answers 503 while PostgreSQL is stopped, as today.

Record the run (date, pass or fail per step, the measured p95) in the W-10 row's evidence, as earlier rows do.

---

### Task 8 (only if Q1 keeps a Sentry-protocol service): error events

**Agents:** devops (service), then developer (SDK).

- Devops: GlitchTip (web and worker, pinned by digest, `mem_limit` 512 MB each) with its database in the existing PostgreSQL (`01-databases.sql` adds `glitchtip`), port `127.0.0.1:8000`, secrets in `.env`; or self-hosted Sentry on a separate machine if the user chose it (not on the pilot VM, spec section 8).
- Developer: `Sentry.AspNetCore` and `Sentry.Extensions.Logging` behind `Sentry:Dsn` (empty means off, and no events leave); `SendDefaultPii = false`, `MaxRequestBodySize = None`, no breadcrumbs from HTTP bodies, `BeforeSend` runs `TelemetryRedactor` over message, exception values, tags and breadcrumbs and drops request cookies and headers; the event carries the trace id and `waslabid.tenant.id` as tags.
- Tests: `Without_a_dsn_no_event_is_sent`; `An_event_carries_the_trace_id_and_tenant_id_and_no_personal_data` (a local HTTP listener standing in for the DSN endpoint captures the envelope; the marker email and a JWT are masked); `An_event_carries_no_request_body_cookie_or_header_value`.
- Spec section 9's first acceptance line then includes the Sentry clause, and task 7 adds a step: the deliberate failure appears once in GlitchTip with the same trace id.
