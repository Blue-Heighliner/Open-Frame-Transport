package org.blueheighliner.openframetransport;

import javax.naming.InvalidNameException;
import javax.naming.ldap.LdapName;
import javax.naming.ldap.Rdn;
import javax.security.auth.x500.X500Principal;
import java.security.cert.CertificateParsingException;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

/**
 * Identity information extracted from an X.509 certificate presented during a TLS handshake.
 *
 * @param name             the Common Name (CN) of the certificate's subject, or {@code null} if its
 *                         subject has none
 * @param issuer           the Common Name (CN) of the certificate's issuer, or {@code null} if its
 *                         issuer has none
 * @param alternativeNames the certificate's Subject Alternative Name entries (DNS names and IP
 *                         addresses), in the order they appear on the certificate; empty if the
 *                         certificate has no Subject Alternative Name extension
 */
public record OftCertificateIdentity(String name, String issuer, List<String> alternativeNames) {
    /**
     * Extracts identity information from {@code certificate}.
     *
     * @param certificate the certificate to extract identity information from
     * @return the extracted identity information
     */
    public static OftCertificateIdentity fromCertificate(X509Certificate certificate) {
        return new OftCertificateIdentity(
                extractCommonName(certificate.getSubjectX500Principal()),
                extractCommonName(certificate.getIssuerX500Principal()),
                extractSubjectAlternativeNames(certificate));
    }

    /**
     * Extracts the Common Name (CN) relative distinguished name component from {@code principal}
     * (a certificate's subject or issuer).
     */
    private static String extractCommonName(X500Principal principal) {
        try {
            for (Rdn rdn : new LdapName(principal.getName()).getRdns()) {
                if (rdn.getType().equalsIgnoreCase("CN")) {
                    return rdn.getValue().toString();
                }
            }

            return null;
        } catch (InvalidNameException e) {
            return null;
        }
    }

    /** Extracts a certificate's Subject Alternative Name DNS and IP address entries. */
    private static List<String> extractSubjectAlternativeNames(X509Certificate certificate) {
        List<String> names = new ArrayList<>();

        try {
            Collection<List<?>> subjectAlternativeNames = certificate.getSubjectAlternativeNames();
            if (subjectAlternativeNames != null) {
                for (List<?> entry : subjectAlternativeNames) {
                    // GeneralName type per RFC 5280 &sect;4.2.1.6: 2 = dNSName, 7 = iPAddress.
                    int type = (Integer) entry.get(0);
                    if (type == 2 || type == 7) {
                        names.add((String) entry.get(1));
                    }
                }
            }
        } catch (CertificateParsingException ignored) {
            // Malformed Subject Alternative Name extension: treat as if none were present.
        }

        return names;
    }
}
