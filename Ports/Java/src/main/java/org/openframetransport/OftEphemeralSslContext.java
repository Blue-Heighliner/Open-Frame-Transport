package org.openframetransport;

import javax.net.ssl.KeyManager;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;
import java.io.File;
import java.io.InputStream;
import java.nio.file.Files;
import java.security.KeyStore;
import java.security.SecureRandom;
import java.security.cert.X509Certificate;

/**
 * Supports {@link OftSecurityMode#SECURE}: a throwaway, self-signed server identity (generated via
 * the JDK's own {@code keytool}, since the JDK has no public API for generating a certificate), and
 * a client-side trust manager that accepts any certificate unconditionally, since there's nothing
 * meaningful to validate an ephemeral certificate against. Mirrors the C# reference
 * implementation's {@code OftEphemeralCertificate}.
 */
final class OftEphemeralSslContext {
    private static volatile SSLContext trustAllContext;

    private OftEphemeralSslContext() {
    }

    /**
     * Creates a new {@link SSLContext} carrying a freshly generated self-signed certificate.
     * Expensive (spawns a {@code keytool} process to generate an RSA keypair): callers must resolve
     * this once per listener, not once per accepted connection.
     */
    static SSLContext createServerContext() throws Exception {
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(generateKeyManagers(), null, new SecureRandom());
        return context;
    }

    /**
     * An {@link SSLContext} that trusts any server certificate unconditionally. Cheap and stateless,
     * so a single instance is lazily created and reused for the lifetime of the process.
     */
    static SSLContext trustAllContext() throws Exception {
        SSLContext context = trustAllContext;
        if (context == null) {
            synchronized (OftEphemeralSslContext.class) {
                context = trustAllContext;
                if (context == null) {
                    context = SSLContext.getInstance("TLS");
                    context.init(null, trustAllCerts(), new SecureRandom());
                    trustAllContext = context;
                }
            }
        }

        return context;
    }

    private static KeyManager[] generateKeyManagers() throws Exception {
        char[] password = "changeit".toCharArray();
        File keystoreFile = File.createTempFile("oft-ephemeral", ".p12");
        keystoreFile.delete();
        keystoreFile.deleteOnExit();

        String keytool = System.getProperty("java.home") + File.separator + "bin" + File.separator + "keytool";
        Process process = new ProcessBuilder(
                keytool, "-genkeypair",
                "-alias", "oft-ephemeral",
                "-keyalg", "RSA",
                "-keysize", "2048",
                "-validity", "1",
                "-dname", "CN=oft-ephemeral",
                "-keystore", keystoreFile.getAbsolutePath(),
                "-storetype", "PKCS12",
                "-storepass", new String(password),
                "-keypass", new String(password))
                .redirectErrorStream(true)
                .start();

        String output = new String(process.getInputStream().readAllBytes());
        int exitCode = process.waitFor();
        if (exitCode != 0) {
            throw new IllegalStateException("keytool failed with exit code " + exitCode + ": " + output);
        }

        KeyStore keyStore = KeyStore.getInstance("PKCS12");
        try (InputStream in = Files.newInputStream(keystoreFile.toPath())) {
            keyStore.load(in, password);
        }

        KeyManagerFactory keyManagerFactory = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
        keyManagerFactory.init(keyStore, password);
        return keyManagerFactory.getKeyManagers();
    }

    private static TrustManager[] trustAllCerts() {
        return new TrustManager[]{
                new X509TrustManager() {
                    @Override
                    public void checkClientTrusted(X509Certificate[] chain, String authType) {
                    }

                    @Override
                    public void checkServerTrusted(X509Certificate[] chain, String authType) {
                    }

                    @Override
                    public X509Certificate[] getAcceptedIssuers() {
                        return new X509Certificate[0];
                    }
                }
        };
    }
}
