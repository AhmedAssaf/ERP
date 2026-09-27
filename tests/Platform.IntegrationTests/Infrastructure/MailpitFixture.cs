using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>Mailpit (generic container, plan tasks 3-4): catches SMTP and exposes it over its HTTP API for assertions.</summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:v1.31.3@sha256:ed9b00c609e77e99c79b93f1178255ebc271868920f2c69a8d166bd5634ed10d")
        .WithPortBinding(1025, assignRandomHostPort: true)
        .WithPortBinding(8025, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(8025))
        .Build();

    public string SmtpHost => _container.Hostname;

    public int SmtpPort => _container.GetMappedPublicPort(1025);

    public Uri ApiBaseAddress => new($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8025)}");

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class MailpitCollection : ICollectionFixture<MailpitFixture>
{
    public const string Name = "mailpit";
}
