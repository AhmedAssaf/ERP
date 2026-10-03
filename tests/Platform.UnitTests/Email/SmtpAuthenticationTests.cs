using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Platform.Modules.Operations.Health;
using Platform.Shared.Email;

namespace Platform.UnitTests.Email;

/// <summary>
/// Optional SMTP login and TLS options (pilot enabler, F-38): a login only when <c>Smtp:Username</c> is configured,
/// never against Mailpit's plain setup, and the password never appears in settings text, exceptions or health
/// results (N-10). The fake server speaks just enough SMTP (EHLO, AUTH PLAIN, MAIL, RCPT, DATA, NOOP, QUIT).
/// </summary>
public sealed class SmtpAuthenticationTests
{
    private const string Secret = "s3cr3t-Passw0rd-N10";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EmailMessage Message = new(["to@example.test"], "Subject", "Body");

    [Fact]
    public async Task No_login_is_attempted_when_no_username_is_configured()
    {
        await using var server = FakeSmtp.Start(advertiseAuth: true);
        var sender = new MailKitEmailSender(new EmailSettings(new SmtpConnectionSettings("127.0.0.1", server.Port, SmtpSecurity.None), "from@example.test"));

        await sender.SendAsync(Message, Ct);

        server.AuthAttempts.ShouldBeEmpty();
        server.Delivered.ShouldBe(1);
    }

    [Fact]
    public async Task The_configured_username_and_password_are_used_to_log_in()
    {
        await using var server = FakeSmtp.Start(advertiseAuth: true);
        var smtp = new SmtpConnectionSettings("127.0.0.1", server.Port, SmtpSecurity.None, "ocid1.user", Secret);
        var sender = new MailKitEmailSender(new EmailSettings(smtp, "from@example.test"));

        await sender.SendAsync(Message, Ct);

        server.AuthAttempts.Count.ShouldBe(1);
        server.AuthAttempts[0].ShouldBe(("ocid1.user", Secret));
        server.Delivered.ShouldBe(1);
    }

    [Fact]
    public async Task A_rejected_login_fails_delivery_without_the_password_in_the_exception()
    {
        await using var server = FakeSmtp.Start(advertiseAuth: true, acceptedPassword: "something-else");
        var smtp = new SmtpConnectionSettings("127.0.0.1", server.Port, SmtpSecurity.None, "ocid1.user", Secret);
        var sender = new MailKitEmailSender(new EmailSettings(smtp, "from@example.test"));

        var ex = await Should.ThrowAsync<EmailDeliveryException>(() => sender.SendAsync(Message, Ct));

        ex.ToString().ShouldNotContain(Secret);
        server.Delivered.ShouldBe(0);
    }

    [Fact]
    public async Task Starttls_is_required_when_configured_and_the_server_does_not_offer_it()
    {
        await using var server = FakeSmtp.Start(advertiseAuth: true);
        var smtp = new SmtpConnectionSettings("127.0.0.1", server.Port, SmtpSecurity.StartTls, "ocid1.user", Secret);
        var sender = new MailKitEmailSender(new EmailSettings(smtp, "from@example.test"));

        var ex = await Should.ThrowAsync<EmailDeliveryException>(() => sender.SendAsync(Message, Ct));

        ex.ToString().ShouldNotContain(Secret);
        server.AuthAttempts.ShouldBeEmpty("the password must never go over a connection that was meant to be encrypted");
    }

    [Fact]
    public async Task The_health_check_logs_in_when_configured_and_never_reports_the_password()
    {
        await using var server = FakeSmtp.Start(advertiseAuth: true);
        var good = new SmtpHealthCheck(new SmtpConnectionSettings("127.0.0.1", server.Port, SmtpSecurity.None, "ocid1.user", Secret));
        (await good.CheckHealthAsync(new HealthCheckContext(), Ct)).Status.ShouldBe(HealthStatus.Healthy);
        server.AuthAttempts.Count.ShouldBe(1);

        await using var rejecting = FakeSmtp.Start(advertiseAuth: true, acceptedPassword: "other");
        var bad = new SmtpHealthCheck(new SmtpConnectionSettings("127.0.0.1", rejecting.Port, SmtpSecurity.None, "ocid1.user", Secret));
        var result = await bad.CheckHealthAsync(new HealthCheckContext(), Ct);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        (result.Description ?? string.Empty).ShouldNotContain(Secret);
        (result.Exception?.ToString() ?? string.Empty).ShouldNotContain(Secret);
    }

    [Fact]
    public void The_password_is_not_part_of_the_settings_text()
    {
        var smtp = new SmtpConnectionSettings("smtp.example.test", 587, SmtpSecurity.StartTls, "user", Secret);

        smtp.ToString().ShouldNotContain(Secret);
        new EmailSettings(smtp, "from@example.test").ToString().ShouldNotContain(Secret);
    }

    [Fact]
    public void Settings_come_from_configuration_and_default_to_plain_mailpit()
    {
        var empty = SmtpConnectionSettings.FromConfiguration(new ConfigurationBuilder().Build());
        empty.ShouldBe(new SmtpConnectionSettings("localhost", 1025));
        empty.UsesAuthentication.ShouldBeFalse();

        var configured = SmtpConnectionSettings.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "smtp.email.me-jeddah-1.oci.oraclecloud.com",
            ["Smtp:Port"] = "587",
            ["Smtp:Security"] = "starttls",
            ["Smtp:Username"] = "ocid1.user",
            ["Smtp:Password"] = Secret,
        }).Build());
        configured.Port.ShouldBe(587);
        configured.Security.ShouldBe(SmtpSecurity.StartTls);
        configured.Username.ShouldBe("ocid1.user");
        configured.Password.ShouldBe(Secret);
    }

    private sealed class FakeSmtp : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly bool _advertiseAuth;
        private readonly string? _acceptedPassword;
        private Task _loop = Task.CompletedTask;
        private int _delivered;

        private FakeSmtp(bool advertiseAuth, string? acceptedPassword)
        {
            _advertiseAuth = advertiseAuth;
            _acceptedPassword = acceptedPassword;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public List<(string User, string Password)> AuthAttempts { get; } = [];

        public int Delivered => Volatile.Read(ref _delivered);

        public static FakeSmtp Start(bool advertiseAuth, string? acceptedPassword = null)
        {
            var server = new FakeSmtp(advertiseAuth, acceptedPassword);
            server._listener.Start();
            server._loop = Task.Run(server.AcceptLoopAsync);
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
            catch (ObjectDisposedException)
            {
                // The listener was stopped under the accept.
            }
            catch (SocketException)
            {
                // The listener was stopped under the accept.
            }

            _stop.Dispose();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => HandleAsync(client));
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    await using var writer = new StreamWriter(stream, new ASCIIEncoding()) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 fake ESMTP");
                    while (await reader.ReadLineAsync(_stop.Token) is { } line)
                    {
                        if (line.StartsWith("EHLO", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync(_advertiseAuth ? "250-fake\r\n250 AUTH PLAIN" : "250 fake");
                        }
                        else if (line.StartsWith("AUTH PLAIN", StringComparison.OrdinalIgnoreCase))
                        {
                            var encoded = line.Length > 11 ? line[11..] : string.Empty;
                            if (encoded.Length == 0)
                            {
                                await writer.WriteLineAsync("334 ");
                                encoded = await reader.ReadLineAsync(_stop.Token) ?? string.Empty;
                            }

                            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(encoded)).Split('\0');
                            lock (AuthAttempts)
                            {
                                AuthAttempts.Add((parts[1], parts[2]));
                            }

                            var ok = _acceptedPassword is null || parts[2] == _acceptedPassword;
                            await writer.WriteLineAsync(ok ? "235 2.7.0 Accepted" : "535 5.7.8 Authentication failed");
                        }
                        else if (line.StartsWith("DATA", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("354 go");
                            while (await reader.ReadLineAsync(_stop.Token) is { } data && data != ".")
                            {
                            }

                            Interlocked.Increment(ref _delivered);
                            await writer.WriteLineAsync("250 queued");
                        }
                        else if (line.StartsWith("QUIT", StringComparison.OrdinalIgnoreCase))
                        {
                            await writer.WriteLineAsync("221 bye");
                            break;
                        }
                        else
                        {
                            await writer.WriteLineAsync("250 ok");
                        }
                    }
                }
                catch (IOException)
                {
                    // The client hung up (a rejected login or a refused STARTTLS ends the session).
                }
                catch (OperationCanceledException)
                {
                    // Shutdown.
                }
            }
        }
    }
}
