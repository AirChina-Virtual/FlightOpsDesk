using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace VamSys.Infrastructure;

// secrets: connection secrets encrypted with the current Windows user's DPAPI key, bound to the workspace id.
internal sealed class SecretStore(StorageContext context)
{
    public void Save(Guid id, string secret)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), id.ToByteArray(), DataProtectionScope.CurrentUser);
        lock (context.WriteLock)
        {
            using var c = context.Connections.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "INSERT INTO secrets VALUES($id,$value) ON CONFLICT(id) DO UPDATE SET value=$value";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.Parameters.AddWithValue("$value", encrypted);
            cmd.ExecuteNonQuery();
        }
    }

    public string? Load(Guid id)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        using var c = context.Connections.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM secrets WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return cmd.ExecuteScalar() is byte[] bytes ? Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, id.ToByteArray(), DataProtectionScope.CurrentUser)) : null;
    }

    // Inside a save transaction: the secret travels with the workspace row in the same commit.
    public static void Upsert(SqliteConnection c, SqliteTransaction tx, Guid id, byte[] encrypted)
        => Sql.Execute(c, tx, "INSERT INTO secrets(id,value) VALUES($id,$s) ON CONFLICT(id) DO UPDATE SET value=$s", ("$id", id.ToString()), ("$s", encrypted));

    // The credentials a connection save will store, or null when the caller keeps the stored secret untouched.
    public OperationsCredentials? ResolveCredentials(Guid workspaceId, string clientId, string secret, bool requireNewSecret)
    {
        if (secret.Length > 0)
        {
            if (clientId.Length == 0 || string.IsNullOrWhiteSpace(secret))
                throw StorageErrors.Block("ApiCredentials");
            return new(clientId, secret); // Secret is opaque; never trim it.
        }
        if (requireNewSecret)
            throw StorageErrors.Block("ApiCredentials");
        if (Load(workspaceId) is string raw)
        {
            var saved = RowSerializer.Decode<OperationsCredentials>(raw) ?? throw StorageErrors.Block("ApiCredentials");
            if (saved.ClientId.Trim() != clientId)
                throw StorageErrors.Block("ApiCredentials");
            return new(clientId, saved.Secret);
        }
        return null;
    }

    public static byte[] Protect(OperationsCredentials credentials, Guid workspaceId)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        return ProtectedData.Protect(Encoding.UTF8.GetBytes(RowSerializer.Encode(credentials)), workspaceId.ToByteArray(), DataProtectionScope.CurrentUser);
    }
}
