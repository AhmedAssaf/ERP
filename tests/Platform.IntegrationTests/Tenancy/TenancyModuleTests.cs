using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Platform.Modules.Tenancy;

namespace Platform.IntegrationTests.Tenancy;

public class TenancyModuleTests
{
    [Fact]
    public void Data_source_pool_is_capped_at_20_by_default()
    {
        PoolSizeOf("Host=unused;Database=unused").ShouldBe(20);
    }

    [Fact]
    public void Data_source_keeps_a_pool_size_set_by_the_caller()
    {
        PoolSizeOf("Host=unused;Database=unused;Maximum Pool Size=5").ShouldBe(5);
    }

    private static int PoolSizeOf(string connectionString)
    {
        using var provider = new ServiceCollection().AddTenancyModule(connectionString).BuildServiceProvider();
        var dataSource = provider.GetRequiredKeyedService<NpgsqlDataSource>(TenancyModule.DataSourceKey);
        return new NpgsqlConnectionStringBuilder(dataSource.ConnectionString).MaxPoolSize;
    }
}
