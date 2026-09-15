using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace PostRouter.Infrastructure;

public interface IMasterKeyStore
{
    byte[] GetOrCreate(string installationId);
}

public sealed partial class WindowsCredentialMasterKeyStore : IMasterKeyStore
{
    public byte[] GetOrCreate(string installationId)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Credential Manager is required by this profile.");
        var target = $"post-router/{installationId}/master-key-v1";
        if (NativeMethods.CredRead(target, 1, 0, out var pointer))
        {
            try
            {
                var credential = Marshal.PtrToStructure<Credential>(pointer);
                if (credential.CredentialBlobSize != 32) throw new CryptographicException("Stored master key has an invalid length.");
                var value = new byte[32];
                Marshal.Copy(credential.CredentialBlob, value, 0, value.Length);
                return value;
            }
            finally { NativeMethods.CredFree(pointer); }
        }
        var error = Marshal.GetLastWin32Error();
        if (error != 1168) throw new Win32Exception(error);
        var key = RandomNumberGenerator.GetBytes(32);
        var blob = Marshal.AllocHGlobal(key.Length);
        var targetPointer = Marshal.StringToCoTaskMemUni(target);
        var userPointer = Marshal.StringToCoTaskMemUni(Environment.UserName);
        try
        {
            Marshal.Copy(key, 0, blob, key.Length);
            var credential = new Credential
            {
                Type = 1,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)key.Length,
                CredentialBlob = blob,
                Persist = 2,
                UserName = userPointer,
            };
            if (!NativeMethods.CredWrite(ref credential, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return key;
        }
        catch { CryptographicOperations.ZeroMemory(key); throw; }
        finally
        {
            for (var index = 0; index < key.Length; index++) Marshal.WriteByte(blob, index, 0);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeCoTaskMem(targetPointer);
            Marshal.FreeCoTaskMem(userPointer);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private static partial class NativeMethods
    {
        [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool CredWrite(ref Credential credential, uint flags);

        [LibraryImport("advapi32.dll")]
        internal static partial void CredFree(IntPtr credential);
    }
}

public sealed class InMemoryMasterKeyStore(byte[]? key = null) : IMasterKeyStore
{
    private readonly byte[] _key = key?.ToArray() ?? RandomNumberGenerator.GetBytes(32);
    public byte[] GetOrCreate(string installationId) => _key.ToArray();
}
