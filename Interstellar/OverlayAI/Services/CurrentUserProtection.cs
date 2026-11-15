using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OverlayAI.Services;

/// <summary>
/// Protects small pieces of local configuration with Windows DPAPI in CurrentUser scope.
/// The protected value can be decrypted only in the same Windows user profile.
/// This is credential-at-rest protection, not process or administrator concealment.
/// </summary>
internal static class CurrentUserProtection
{
    private const uint CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        out IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public static string ProtectToBase64(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var plainBytes = Encoding.UTF8.GetBytes(value);
        try { return Convert.ToBase64String(Transform(plainBytes, protect: true)); }
        finally { CryptographicOperations.ZeroMemory(plainBytes); }
    }

    public static string UnprotectFromBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A protected credential value is required.", nameof(value));
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(value); }
        catch (FormatException ex) { throw new CryptographicException("The protected credential is not valid Base64.", ex); }

        var plainBytes = Transform(encrypted, protect: false);
        try { return Encoding.UTF8.GetString(plainBytes); }
        finally { CryptographicOperations.ZeroMemory(plainBytes); }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (input.Length == 0) return Array.Empty<byte>();

        var inputBlob = new DataBlob
        {
            Length = input.Length,
            Data = Marshal.AllocHGlobal(input.Length)
        };
        Marshal.Copy(input, 0, inputBlob.Data, input.Length);

        DataBlob outputBlob = default;
        IntPtr description = IntPtr.Zero;
        try
        {
            bool ok = protect
                ? CryptProtectData(ref inputBlob, "Interstellar configuration", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out outputBlob);

            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect or unprotect the Interstellar credential.");

            var result = new byte[outputBlob.Length];
            if (outputBlob.Length > 0) Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Length);
            return result;
        }
        finally
        {
            if (inputBlob.Data != IntPtr.Zero)
            {
                // Best-effort clearing before freeing the unmanaged input buffer.
                Marshal.Copy(new byte[inputBlob.Length], 0, inputBlob.Data, inputBlob.Length);
                Marshal.FreeHGlobal(inputBlob.Data);
            }
            if (outputBlob.Data != IntPtr.Zero) LocalFree(outputBlob.Data);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }
}
