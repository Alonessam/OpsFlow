using System.Data.Common;
using Npgsql;

namespace OpsFlow.Infrastructure.Persistence;

/// <summary>
/// Managed Postgres providers (Neon, Render, Heroku, Supabase ...) hand out a <c>postgres://user:pass@host/db?sslmode=require</c>
/// URL, which Npgsql's key=value connection strings do not accept. This turns the URL into the form Npgsql
/// understands, so the value can be pasted into <c>ConnectionStrings__Default</c> as it is. Key=value strings pass through untouched.
/// </summary>
public static class PostgresConnectionString
{
    /// <summary>
    /// <see cref="Normalize"/> plus the defaults this app wants: Kerberos/GSS encryption is not used here, but Npgsql would
    /// still try to load the native GSSAPI library on every Linux host and log a confusing error when it is missing
    /// (it is, in the slim container image). Turning the probe off removes the noise and a pointless library load.
    /// An explicit "Gss Encryption Mode" in the connection string is respected.
    /// </summary>
    public static string Prepare(string connectionString)
    {
        var normalized = Normalize(connectionString);
        var builder = new NpgsqlConnectionStringBuilder(normalized);

        // NpgsqlConnectionStringBuilder reports every known keyword as present, so ask a plain builder what was really written.
        var written = new DbConnectionStringBuilder { ConnectionString = normalized };
        var chose = written.ContainsKey("Gss Encryption Mode") || written.ContainsKey("GssEncryptionMode") || written.ContainsKey("GSS Encryption Mode");
        if (!chose)
        {
            builder.GssEncryptionMode = GssEncryptionMode.Disable;
        }

        return builder.ConnectionString;
    }

    public static string Normalize(string connectionString)
    {
        var trimmed = connectionString.Trim();
        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return connectionString;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            throw new FormatException("The PostgreSQL URL is not valid. Expected postgres://user:password@host[:port]/database[?sslmode=require]. " +
                                      "Special characters in the password must be percent-encoded.");
        }

        var credentials = uri.UserInfo.Split(':', 2);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.Port > 0 ? uri.Port : 5432,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = Uri.UnescapeDataString(credentials[0])
        };

        if (credentials.Length > 1)
        {
            builder.Password = Uri.UnescapeDataString(credentials[1]);
        }

        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            switch (kv[0].ToLowerInvariant())
            {
                case "sslmode":
                    builder.SslMode = value.ToLowerInvariant() switch
                    {
                        "disable" => SslMode.Disable,
                        "allow" => SslMode.Allow,
                        "prefer" => SslMode.Prefer,
                        "require" => SslMode.Require,
                        "verify-ca" => SslMode.VerifyCA,
                        "verify-full" => SslMode.VerifyFull,
                        _ => throw new FormatException($"Unknown sslmode '{value}'.")
                    };
                    break;
                case "channel_binding":
                    builder.ChannelBinding = value.ToLowerInvariant() switch
                    {
                        "disable" => ChannelBinding.Disable,
                        "prefer" => ChannelBinding.Prefer,
                        "require" => ChannelBinding.Require,
                        _ => throw new FormatException($"Unknown channel_binding '{value}'.")
                    };
                    break;
                // Other URL parameters (application_name, options ...) are not needed here and are ignored.
            }
        }

        return builder.ConnectionString;
    }
}
