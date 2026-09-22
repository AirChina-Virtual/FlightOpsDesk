using System.Security.Cryptography;
using System.Text;

namespace VamSys.Infrastructure;

public sealed class DataDirectoryLease : IDisposable
{
    readonly FileStream stream;
    public DataDirectoryLease(string directory)
    {
        Directory.CreateDirectory(directory);
        try { stream=new FileStream(Path.Combine(directory,"application.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None); }
        catch(IOException) { throw OperationsAdapter.Block("StorageInUse"); }
    }
    public void Dispose()=>stream.Dispose();
}
