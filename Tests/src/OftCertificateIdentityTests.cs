namespace BlueHeighliner.OpenFrameTransport.Tests;

public sealed class OftCertificateIdentityTests
{
    [Fact]
    public void FromCertificate_SelfSignedCertificate_NameAndIssuerAreBothTheCertificatesOwnCommonName()
    {
        using X509Certificate2 certificate = TestCertificate.Create();

        OftCertificateIdentity identity = OftCertificateIdentity.FromCertificate(certificate);

        Assert.Equal("localhost", identity.Name);
        Assert.Equal("localhost", identity.Issuer);
    }

    [Fact]
    public void FromCertificate_NoSanExtension_AlternativeNamesIsEmpty()
    {
        using X509Certificate2 certificate = TestCertificate.Create();

        OftCertificateIdentity identity = OftCertificateIdentity.FromCertificate(certificate);

        Assert.Empty(identity.AlternativeNames);
    }

    [Fact]
    public void FromCertificate_DnsSan_AlternativeNamesContainsIt()
    {
        using X509Certificate2 certificate = TestCertificate.CreateWithDnsName("example.oft.test");

        OftCertificateIdentity identity = OftCertificateIdentity.FromCertificate(certificate);

        Assert.Equal(["example.oft.test"], identity.AlternativeNames);
    }
}
