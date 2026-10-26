using System.Security.Cryptography;
using System.Text;

namespace VamSys.Core;

// Exports a stored task as redacted JSON beside a SHA-256 file. Only what the database holds is exported, never the
// live copy in memory, so the evidence matches what a restart would show.
public sealed class DiagnosticsService(WorkspaceSession session, Func<BatchJob, string> serialize)
{
    // Returns the path written, or null when the person did not choose a file.
    public async Task<string?> ExportAsync(Guid jobId, Func<Task<string?>> chooseFile)
    {
        var persisted = session.LoadJob(jobId) ?? throw new InvalidOperationException("The task is not stored.");
        var path = await chooseFile();
        if (path == null)
            return null;
        var json = serialize(persisted);
        await File.WriteAllTextAsync(path, json, Encoding.UTF8);
        var hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
        await File.WriteAllTextAsync(path + ".sha256", hash + "  " + Path.GetFileName(path) + Environment.NewLine, Encoding.UTF8);
        return path;
    }
}
