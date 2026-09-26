using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;

namespace Platform.IntegrationTests.Infrastructure;

/// <summary>Mailpit (generic container, plan tasks 3-4): catches SMTP and exposes it over its HTTP API for assertions.</summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private readonly IContainer _container = new ContainerBuilder("axllent/mailpit:latest")
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
