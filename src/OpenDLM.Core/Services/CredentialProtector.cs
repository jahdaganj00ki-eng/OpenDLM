using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OpenDLM.Core.Util;

namespace OpenDLM.Core.Services;

/// <summary>
/// Protects stored passwords at rest.
///
/// Primary path uses Windows DPAPI (<c>CryptProtectData</c>) scoped to the current
/// user, which means the ciphertext can only be read back by the same Windows
/// account on the same machine. This is achieved with a direct P/Invoke so
/// OpenDLM needs no extra NuGet package.
///
/// If DPAPI is unavailable the class falls back to AES-256-CBC with a key derived
/// from machine + user identifiers. That fallback is obfuscation rather than real
/// protection, and it is reported through <see cref="UsingWeakFallback"/>.
/// </summary>
public static class CredentialProtector
{
    private const int CryptProtectUiForbidden = 0x1;
    private static bool _dpapiFailed;

    /// <summary>True when passwords are only obfuscated because DPAPI was unavailable.</summary>
    public static bool UsingWeakFallback => _dpapiFailed;

    public static string? Protect(string? plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return null;
        }

        if (!_dpapiFailed)
        {
            try
            {
                return "dpapi:" + Convert.ToBase64String(DpapiProtect(Encoding.UTF8.GetBytes(plainText)));
            }
            catch (Exception ex)
            {
                _dpapiFailed = true;
                Log.Warn("DPAPI protect failed, falling back to AES obfuscation: " + ex.Message);
            }
        }

        return "aes:" + AesProtect(plainText);
    }

    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                var payload = Convert.FromBase64String(stored[6..]);
                return Encoding.UTF8.GetString(DpapiUnprotect(payload));
            }

            if (stored.StartsWith("aes:", StringComparison.Ordinal))
            {
                return AesUnprotect(stored[4..]);
            }

            // Legacy/plain value (e.g. hand-edited file): accept it as-is so the
            // user is never locked out of their own settings.
            return stored;
        }
        catch (Exception ex)
        {
            Log.Warn("Unable to unprotect stored credential: " + ex.Message);
            return null;
        }
    }

    public static bool HasValue(string? stored) => !string.IsNullOrEmpty(stored);

    // ------------------------------------------------------------------- DPAPI

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] DpapiProtect(byte[] plain)
    {
        var input = new DataBlob { Size = plain.Length, Data = Marshal.AllocHGlobal(plain.Length) };
        try
        {
            Marshal.Copy(plain, 0, input.Data, plain.Length);
            if (!CryptProtectData(ref input, AppPaths.ProductName, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(
                    "CryptProtectData failed with Win32 error " + Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(input.Data);
            }
        }
    }

    private static byte[] DpapiUnprotect(byte[] cipher)
    {
        var input = new DataBlob { Size = cipher.Length, Data = Marshal.AllocHGlobal(cipher.Length) };
        try
        {
            Marshal.Copy(cipher, 0, input.Data, cipher.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out var output))
            {
                throw new CryptographicException(
                    "CryptUnprotectData failed with Win32 error " + Marshal.GetLastWin32Error());
            }

            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(input.Data);
            }
        }
    }

    // --------------------------------------------------------------- AES fallback

    private static byte[] DeriveKey()
    {
        // Stable per machine+user material, stretched with PBKDF2.
        var material = string.Join('|',
            Environment.MachineName,
            Environment.UserName,
            Environment.OSVersion.VersionString,
            AppPaths.ProductName);
        var salt = Encoding.UTF8.GetBytes("OpenDLM.credential.salt.v1");
        using var kdf = new Rfc2898DeriveBytes(material, salt, 120_000, HashAlgorithmName.SHA256);
        return kdf.GetBytes(32);
    }

    private static string AesProtect(string plain)
    {
        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = encryptor.TransformFinalBlock(data, 0, data.Length);

        var combined = new byte[aes.IV.Length + cipher.Length];
        Buffer.BlockCopy(aes.IV, 0, combined, 0, aes.IV.Length);
        Buffer.BlockCopy(cipher, 0, combined, aes.IV.Length, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    private static string AesUnprotect(string encoded)
    {
        var combined = Convert.FromBase64String(encoded);
        if (combined.Length <= 16)
        {
            throw new CryptographicException("Stored credential is too short to be valid.");
        }

        using var aes = Aes.Create();
        aes.Key = DeriveKey();
        aes.IV = combined.AsSpan(0, 16).ToArray();

        using var decryptor = aes.CreateDecryptor();
        var plain = decryptor.TransformFinalBlock(combined, 16, combined.Length - 16);
        return Encoding.UTF8.GetString(plain);
    }
}
