using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// The Data Protection key ring in Postgres (0061, railway-deploy-pipeline S10). The default ring lives in the container's
/// file system, so every Railway deploy or restart created a new one and every auth cookie stopped decrypting: each
/// deploy signed every user out. Every instance now reads and writes the same rows, so a cookie issued before a deploy
/// is still valid after it.
///
/// The key XML is stored UNENCRYPTED at rest. It is protected by the database's private network, the least-privilege
/// grants of 0061 (app_runtime SELECT/INSERT only, nothing for platform_readonly) and Railway's encrypted backups.
/// Encrypting it with a certificate (<c>ProtectKeysWithCertificate</c>) is a possible follow-up.
///
/// The interface is synchronous: the key manager calls it rarely (startup, key-ring refresh, key rotation) and caches
/// the result.
/// </summary>
public sealed class PostgresDataProtectionKeyRepository(NpgsqlDataSource dataSource) : IXmlRepository
{
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var connection = dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT xml FROM data_protection_keys ORDER BY id", connection);
        var elements = new List<XElement>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0)));
        }

        return elements;
    }

    /// <summary>
    /// Two instances starting together may both store an element; the friendly name (<c>key-&lt;guid&gt;</c>) is unique
    /// per element, so a repeat is skipped rather than failing the caller.
    /// </summary>
    public void StoreElement(XElement element, string friendlyName)
    {
        using var connection = dataSource.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO data_protection_keys (friendly_name, xml) VALUES ($1, $2) ON CONFLICT (friendly_name) DO NOTHING",
            connection);
        cmd.Parameters.AddWithValue(friendlyName);
        cmd.Parameters.AddWithValue(element.ToString(SaveOptions.DisableFormatting));
        cmd.ExecuteNonQuery();
    }
}

public static class DataProtectionRegistration
{
    /// <summary>
    /// Fixed, so the key isolation never depends on the content-root path (which differs between the container, a local
    /// run and the tests). Changing it invalidates every cookie issued before the change.
    /// </summary>
    public const string ApplicationName = "Commerce.Cloud.Api";

    /// <summary>
    /// Data Protection over <see cref="PostgresDataProtectionKeyRepository"/>, built from the singleton
    /// <see cref="NpgsqlDataSource"/>. Every environment uses it, tests included. The key lifetime stays the default.
    /// </summary>
    public static IDataProtectionBuilder AddCommerceDataProtection(this IServiceCollection services)
    {
        var builder = services.AddDataProtection().SetApplicationName(ApplicationName);
        services.AddOptions<KeyManagementOptions>()
            .Configure<NpgsqlDataSource>((options, dataSource) =>
                options.XmlRepository = new PostgresDataProtectionKeyRepository(dataSource));
        return builder;
    }
}
