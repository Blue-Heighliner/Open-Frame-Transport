package org.blueheighliner.openframetransport;

import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.Timeout;

import java.security.cert.X509Certificate;
import java.util.concurrent.TimeUnit;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

@Timeout(value = 30, unit = TimeUnit.SECONDS)
final class OftCertificateIdentityTest {
    @Test
    void fromCertificate_selfSignedCertificate_nameAndIssuerAreBothTheCertificatesOwnCommonName() throws Exception {
        X509Certificate certificate = TestCertificates.createCertificate();

        OftCertificateIdentity identity = OftCertificateIdentity.fromCertificate(certificate);

        assertEquals("localhost", identity.name());
        assertEquals("localhost", identity.issuer());
    }

    @Test
    void fromCertificate_noSanExtension_alternativeNamesIsEmpty() throws Exception {
        X509Certificate certificate = TestCertificates.createCertificate();

        OftCertificateIdentity identity = OftCertificateIdentity.fromCertificate(certificate);

        assertTrue(identity.alternativeNames().isEmpty());
    }

    @Test
    void fromCertificate_dnsSan_alternativeNamesContainsIt() throws Exception {
        X509Certificate certificate = TestCertificates.createCertificateWithDnsName("example.oft.test");

        OftCertificateIdentity identity = OftCertificateIdentity.fromCertificate(certificate);

        assertEquals(1, identity.alternativeNames().size());
        assertEquals("example.oft.test", identity.alternativeNames().get(0));
    }
}
