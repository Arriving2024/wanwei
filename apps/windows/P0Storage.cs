using System.Security.Cryptography;

namespace Wanwei;

internal sealed class P0Storage : IDisposable
{
    private readonly string temporary;
    private readonly string final;
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly FileStream stream;
    private ulong count;
    private bool committed;
    internal bool CleanupConfirmed { get; private set; }
    internal string? SavedLocalName => committed ? Path.GetFileName(final) : null;
    internal P0Storage(string directory)
    {
        EnsureSafeDirectory(directory);
        string localName = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        temporary = Path.Combine(directory, localName + ".partial"); final = Path.Combine(directory, localName + ".bin");
        stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.WriteThrough);
    }
    internal static void RejectReparse(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("STORAGE_REPARSE_REJECTED");
    }
    internal static void EnsureSafeDirectory(string directory)
    {
        string full = Path.GetFullPath(directory);
        var paths = new Stack<string>();
        for (DirectoryInfo? current = new(full); current is not null; current = current.Parent) paths.Push(current.FullName);
        while (paths.TryPop(out string? path))
        {
            if (Directory.Exists(path)) RejectReparse(path);
            else { Directory.CreateDirectory(path); RejectReparse(path); }
        }
    }
    internal void Write(byte[] bytes)
    {
        if (committed || count > P0Protocol.Size || (ulong)bytes.Length > (ulong)P0Protocol.Size - count) throw new P0Exception("SIZE_MISMATCH");
        stream.Write(bytes); hash.AppendData(bytes); count += (ulong)bytes.Length;
    }
    internal void Commit()
    {
        if (committed || count != P0Protocol.Size) throw new P0Exception("SIZE_MISMATCH");
        if (Convert.ToHexStringLower(hash.GetHashAndReset()) != P0Protocol.Hash) throw new P0Exception("HASH_MISMATCH");
        RejectReparse(Path.GetDirectoryName(temporary)!); RejectReparse(temporary);
        stream.Flush(true); stream.Dispose();
        File.Move(temporary, final, false); // Same-directory atomic rename; no replacement.
        committed = true;
    }
    public void Dispose()
    {
        try { stream.Dispose(); }
        finally
        {
            hash.Dispose();
            if (!committed) { try { File.Delete(temporary); } catch { } }
            try { CleanupConfirmed = !File.Exists(temporary); } catch { CleanupConfirmed = false; }
        }
    }
}
