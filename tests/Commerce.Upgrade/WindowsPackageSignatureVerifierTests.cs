using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using Commerce.Updater;

namespace Commerce.Upgrade;

public sealed class WindowsPackageSignatureVerifierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sig-{Guid.NewGuid():N}");

    public WindowsPackageSignatureVerifierTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string BuildFakeMsix(string subject, bool withSignature)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.msix");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = archive.CreateEntry("AppxManifest.xml");
        using (var writer = new StreamWriter(manifest.Open()))
        {
            writer.Write("<Package/>");
        }

        if (withSignature)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            var content = new ContentInfo(new Oid("1.3.6.1.4.1.311.2.1.4"), [1, 2, 3]);
            var cms = new SignedCms(content);
            cms.ComputeSignature(new CmsSigner(certificate) { IncludeOption = X509IncludeOption.EndCertOnly });
            var p7x = new byte[] { (byte)'P', (byte)'K', (byte)'C', (byte)'X' }.Concat(cms.Encode()).ToArray();
            var entry = archive.CreateEntry("AppxSignature.p7x");
            using var stream = entry.Open();
            stream.Write(p7x);
        }

        return path;
    }

    [Fact]
    public void SignerSubject_IsReadFromThePackageSignatureBlock()
    {
        var path = BuildFakeMsix("CN=Incoders Commerce (Interim)", withSignature: true);

        Assert.Equal("CN=Incoders Commerce (Interim)", WindowsPackageSignatureVerifier.ReadSignerSubject(path));
    }

    [Fact]
    public void UnsignedPackage_HasNoSignerSubject()
    {
        Assert.Null(WindowsPackageSignatureVerifier.ReadSignerSubject(BuildFakeMsix("CN=x", withSignature: false)));
    }

    [Fact]
    public void GarbageFile_HasNoSignerSubject_AndNeverThrows()
    {
        var path = Path.Combine(_dir, "garbage.msix");
        File.WriteAllText(path, "not a zip");

        Assert.Null(WindowsPackageSignatureVerifier.ReadSignerSubject(path));
    }

    [Fact]
    public void Inspect_UnsignedPackage_IsNotSigned()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var signature = new WindowsPackageSignatureVerifier().Inspect(BuildFakeMsix("CN=x", withSignature: false));

        Assert.NotEqual(PackageSignatureStatus.Valid, signature.Status);
        Assert.Null(signature.SignerSubject);
    }

    [Fact]
    public void Inspect_ForgedSignatureBlock_IsNeverValid()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var signature = new WindowsPackageSignatureVerifier().Inspect(BuildFakeMsix("CN=Incoders Commerce (Interim)", withSignature: true));

        Assert.NotEqual(PackageSignatureStatus.Valid, signature.Status);
    }

    [Fact]
    public void Inspect_MissingFile_IsNotValid()
    {
        var signature = new WindowsPackageSignatureVerifier().Inspect(Path.Combine(_dir, "missing.msix"));

        Assert.NotEqual(PackageSignatureStatus.Valid, signature.Status);
    }
}
