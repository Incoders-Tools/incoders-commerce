using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

namespace Commerce.Updater;

/// <summary>The certificate that signed a package, as read from its signature block.</summary>
/// <param name="Sha256Thumbprint">SHA-256 of the certificate DER, uppercase hex.</param>
public sealed record PackageSigner(string Subject, string Sha256Thumbprint);

/// <summary>
/// Windows implementation of <see cref="IPackageSignatureVerifier"/>. Trust is
/// decided by the OS (<c>WinVerifyTrust</c>, the same engine behind
/// <c>Get-AuthenticodeSignature</c>, which understands the MSIX signature
/// block); the signer subject is read from the package's
/// <c>AppxSignature.p7x</c> PKCS#7 block. No process is started.
/// Revocation is not checked: terminals may be offline, and the interim
/// certificate has no revocation endpoint.
/// </summary>
public sealed class WindowsPackageSignatureVerifier : IPackageSignatureVerifier
{
    private const string SignatureEntry = "AppxSignature.p7x";
    private static readonly byte[] P7xMagic = "PKCX"u8.ToArray();

    public PackageSignature Inspect(string packagePath)
    {
        if (!File.Exists(packagePath))
        {
            return new PackageSignature(PackageSignatureStatus.Unknown, null, "Package file not found.");
        }

        var signer = ReadSigner(packagePath);
        if (signer is null)
        {
            return new PackageSignature(PackageSignatureStatus.NotSigned, null, "No package signature block.");
        }

        var subject = signer.Subject;

        if (!OperatingSystem.IsWindows())
        {
            return new PackageSignature(PackageSignatureStatus.Unknown, subject, "Signature trust can only be verified on Windows.", signer.Sha256Thumbprint);
        }

        var code = TrustNative.Verify(packagePath);
        return new PackageSignature(MapTrustResult(code), subject, $"WinVerifyTrust 0x{code:X8}", signer.Sha256Thumbprint);
    }

    /// <summary>
    /// Maps a <c>WinVerifyTrust</c> result. <c>UntrustedRoot</c> (an intact
    /// signature whose chain is not trusted on this machine) is only ever the
    /// result for an unmodified package: a modified one fails the digest check
    /// and yields <c>Invalid</c>. An expired certificate is its own status so it
    /// can never be mistaken for an untrusted root.
    /// </summary>
    public static PackageSignatureStatus MapTrustResult(uint code) => code switch
    {
        0 => PackageSignatureStatus.Valid,
        0x800B0100 => PackageSignatureStatus.NotSigned,
        0x800B0109 or 0x800B010A => PackageSignatureStatus.UntrustedRoot,
        0x800B0101 => PackageSignatureStatus.Expired,
        0x80096010 or 0x80096004 or 0x800B0004 or 0x80096019 => PackageSignatureStatus.Invalid,
        _ => PackageSignatureStatus.Unknown
    };

    /// <summary>Subject of the certificate that signed the package, or null when unsigned or unreadable.</summary>
    public static string? ReadSignerSubject(string packagePath) => ReadSigner(packagePath)?.Subject;

    /// <summary>
    /// The single certificate that signed the package (subject and SHA-256
    /// thumbprint), or null when unsigned, unreadable or signed more than once.
    /// </summary>
    public static PackageSigner? ReadSigner(string packagePath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var entry = archive.GetEntry(SignatureEntry);
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var bytes = memory.ToArray();
            if (bytes.Length <= P7xMagic.Length || !bytes.AsSpan(0, P7xMagic.Length).SequenceEqual(P7xMagic))
            {
                return null;
            }

            var cms = new SignedCms();
            cms.Decode(bytes.AsSpan(P7xMagic.Length).ToArray());
            if (cms.SignerInfos.Count != 1 || cms.SignerInfos[0].Certificate is not { } certificate)
            {
                return null;
            }

            // The thumbprint pin trusts this certificate, so prove its key
            // produced the signature; a certificate that only rides along in
            // the block must not pass as the signer. Throws
            // CryptographicException (handled below) when it did not.
            cms.SignerInfos[0].CheckSignature(verifySignatureOnly: true);

            return new PackageSigner(certificate.Subject, Convert.ToHexString(SHA256.HashData(certificate.RawData)));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException
                                       or CryptographicException)
        {
            return null;
        }
    }

    private static class TrustNative
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustFileInfo
        {
            public uint cbStruct;
            public IntPtr pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WinTrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid action, IntPtr data);

        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionVerify = 1;
        private const uint WtdStateActionClose = 2;
        private const uint WtdRevocationCheckNone = 0x10;

        public static uint Verify(string path)
        {
            var filePathPointer = Marshal.StringToHGlobalUni(path);
            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = filePathPointer
            };
            var fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            var dataPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustData>());
            var opened = false;
            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = fileInfoPointer,
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone
            };
            try
            {
                Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
                Marshal.StructureToPtr(data, dataPointer, false);
                opened = true;
                return unchecked((uint)WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, dataPointer));
            }
            finally
            {
                if (opened)
                {
                    // Release provider state allocated by the verify call.
                    var closing = Marshal.PtrToStructure<WinTrustData>(dataPointer);
                    closing.dwStateAction = WtdStateActionClose;
                    Marshal.StructureToPtr(closing, dataPointer, false);
                    _ = WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, dataPointer);
                }

                Marshal.FreeHGlobal(dataPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
                Marshal.FreeHGlobal(filePathPointer);
            }
        }
    }
}
