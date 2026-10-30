using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RTFM.Licensing;

/// <summary>The stored state could not be read or written; nothing in an unreadable state is trusted.</summary>
public sealed class StoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Where <see cref="LicenseState"/> is kept. <see cref="Commit"/> must replace the whole state atomically or
/// throw, leaving the previous state in place.</summary>
public interface ILicenseStore
{
    /// <summary>The stored state, or null when there is none.</summary>
    LicenseState? Load();

    /// <summary>Replaces the stored state.</summary>
    void Commit(LicenseState state);

    /// <summary>Removes the stored state.</summary>
    void Erase();
}

/// <summary>A store in memory, for tests and apps that manage persistence themselves.</summary>
public sealed class MemoryLicenseStore : ILicenseStore
{
    private readonly object gate = new();
    private byte[]? data;

    /// <summary>When set, commits throw this and leave the stored state unchanged.</summary>
    internal Exception? FailCommits { get; set; }

    /// <inheritdoc />
    public LicenseState? Load() { lock (gate) return data is null ? null : LicenseStateCoding.Decode(data); }

    /// <inheritdoc />
    public void Commit(LicenseState state)
    {
        lock (gate)
        {
            if (FailCommits is { } failure) throw failure;
            data = LicenseStateCoding.Encode(state);
        }
    }

    /// <inheritdoc />
    public void Erase() { lock (gate) data = null; }
}

/// <summary>Keeps <see cref="LicenseState"/> in one file in the user's local application data, replaced atomically.
/// On Windows the file is encrypted with DPAPI for the current user; elsewhere it is readable by the user only.</summary>
public sealed class FileLicenseStore : ILicenseStore
{
    private static readonly byte[] Entropy = "RTFM.Licensing license state v1"u8.ToArray();

    /// <summary>The state file.</summary>
    public string Path { get; }

    /// <summary>A store at <paramref name="path"/>.</summary>
    public FileLicenseStore(string path) => Path = path;

    /// <summary>The store for one app: <c>%LOCALAPPDATA%\{publisher}\{app}\license-state.dat</c> on Windows (the local
    /// application data folder elsewhere). Use names that won't change between versions.</summary>
    public static FileLicenseStore ForApp(string publisher, string app)
    {
        foreach (var name in new[] { publisher, app })
        {
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name is "." or "..")
            {
                throw new ArgumentException("Publisher and app must be plain folder names.");
            }
        }
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new FileLicenseStore(System.IO.Path.Combine(root, publisher, app, "license-state.dat"));
    }

    /// <inheritdoc />
    public LicenseState? Load()
    {
        byte[] stored;
        try { stored = File.ReadAllBytes(Path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException error) { throw new StoreException("The license state file could not be read.", error); }
        return LicenseStateCoding.Decode(Unprotect(stored));
    }

    /// <inheritdoc />
    public void Commit(LicenseState state)
    {
        var directory = System.IO.Path.GetDirectoryName(Path)!;
        var temporary = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(Protect(LicenseStateCoding.Encode(state)));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, Path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
        {
            try { File.Delete(temporary); } catch (IOException) { }
            throw new StoreException("The license state file could not be written.", error);
        }
    }

    /// <inheritdoc />
    public void Erase()
    {
        try { File.Delete(Path); }
        catch (DirectoryNotFoundException) { }
        catch (IOException error) { throw new StoreException("The license state file could not be removed.", error); }
    }

    private static byte[] Protect(byte[] data) => OperatingSystem.IsWindows() ? ProtectWindows(data) : data;

    private static byte[] Unprotect(byte[] data)
    {
        if (!OperatingSystem.IsWindows()) return data;
        try { return UnprotectWindows(data); }
        catch (CryptographicException error) { throw new StoreException("The license state file could not be decrypted.", error); }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectWindows(byte[] data) => ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectWindows(byte[] data) => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
}
