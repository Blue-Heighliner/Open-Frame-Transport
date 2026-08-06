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
 * Generates throwaway self-signed certificates for exercising TLS handshakes in tests, using the
 * JDK's own {@code keytool} so the tests don't need a third-party certificate library.
 */
final class TestCertificates {
    private TestCertificates() {
    }

    /** An {@link SSLContext} carrying a freshly generated self-signed server certificate. */
    static SSLContext createServerContext() throws Exception {
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(generateKeyManagers(), null, new SecureRandom());
        return context;
    }

    /**
     * An {@link SSLContext} that trusts any server certificate. Insecure; suitable only for tests
     * connecting to {@link #createServerContext()}, which uses a certificate no real trust store
     * would recognize.
     */
    static SSLContext createClientContext() throws Exception {
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, trustAllCerts(), new SecureRandom());
        return context;
    }

    /**
     * An {@link SSLContext} carrying a freshly generated self-signed certificate and trusting any
     * peer certificate, suitable for an {@link OftPeer} that both listens and dials (i.e.
     * {@link #createServerContext()} and {@link #createClientContext()} combined into one context).
     */
    static SSLContext createPeerContext() throws Exception {
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(generateKeyManagers(), trustAllCerts(), new SecureRandom());
        return context;
    }

    private static KeyManager[] generateKeyManagers() throws Exception {
        char[] password = "changeit".toCharArray();
        File keystoreFile = File.createTempFile("oft-test", ".p12");
        keystoreFile.delete();
        keystoreFile.deleteOnExit();

        String keytool = System.getProperty("java.home") + File.separator + "bin" + File.separator + "keytool";
        Process process = new ProcessBuilder(
                keytool, "-genkeypair",
                "-alias", "oft-test",
                "-keyalg", "RSA",
                "-keysize", "2048",
                "-validity", "1",
                "-dname", "CN=localhost",
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
