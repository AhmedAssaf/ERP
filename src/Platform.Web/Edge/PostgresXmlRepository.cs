using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Platform.Web.Edge;

/// <summary>
/// W-24: the Data Protection key ring in <c>platform.data_protection_keys</c> (shared migration 0007). The data source is
/// the repository's own, never an EF Core connection, so its connections carry no tenant, vendor or user context, which
/// the table's row-level security requires. It only adds rows (Data Protection never changes a stored element; a
/// revocation is a new element) and never logs an element: a key's XML is key material (N-10).
/// </summary>
internal sealed class PostgresXmlRepository(NpgsqlDataSource dataSource) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("select xml from platform.data_protection_keys order by id", connection);
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "insert into platform.data_protection_keys (friendly_name, xml) values (@friendlyName, @xml)", connection);
        command.Parameters.AddWithValue("friendlyName", friendlyName ?? string.Empty);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
