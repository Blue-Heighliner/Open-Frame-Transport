package org.blueheighliner.openframetransport;

import org.junit.jupiter.api.Test;

import javax.net.ssl.SSLContext;
import java.time.Duration;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;

/** Builder round-trip coverage for every option type's setters, beyond what integration tests happen to exercise. */
final class OftOptionsTest {
    @Test
    void oftConnectOptions_builder_setsEveryField() throws Exception {
        SSLContext sslContext = TestCertificates.createClientContext();

        OftConnectOptions options = OftConnectOptions.builder()
                .info("client")
                .sslContext(sslContext)
                .maxPacketDataSize(2048)
                .rekeyInterval(Duration.ofMinutes(5))
                .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                .pollInterval(Duration.ofMillis(500))
                .pollTimeout(Duration.ofSeconds(2))
                .build();

        assertEquals("client", options.info());
        assertSame(sslContext, options.sslContext());
        assertEquals(2048, options.maxPacketDataSize());
        assertEquals(Duration.ofMinutes(5), options.rekeyInterval());
        assertEquals(OftSecurityMode.SERVER_AUTHENTICATION, options.securityMode());
        assertEquals(Duration.ofMillis(500), options.pollInterval());
        assertEquals(Duration.ofSeconds(2), options.pollTimeout());
    }

    @Test
    void oftConnectOptions_builder_defaults() {
        OftConnectOptions options = OftConnectOptions.builder().info("client").build();

        assertEquals(1024, options.maxPacketDataSize());
        assertEquals(OftSecurityMode.SECURE, options.securityMode());
        assertEquals(Duration.ofSeconds(1), options.pollInterval());
        assertEquals(Duration.ofSeconds(5), options.pollTimeout());
    }

    @Test
    void oftHostOptions_builder_setsEveryField() throws Exception {
        SSLContext sslContext = TestCertificates.createServerContext();

        OftHostOptions options = OftHostOptions.builder()
                .info("server")
                .sslContext(sslContext)
                .maxPacketDataSize(4096)
                .rekeyInterval(Duration.ofMinutes(10))
                .securityMode(OftSecurityMode.DUAL_AUTHENTICATION)
                .pollInterval(Duration.ofMillis(250))
                .pollTimeout(Duration.ofSeconds(3))
                .build();

        assertEquals("server", options.info());
        assertSame(sslContext, options.sslContext());
        assertEquals(4096, options.maxPacketDataSize());
        assertEquals(Duration.ofMinutes(10), options.rekeyInterval());
        assertEquals(OftSecurityMode.DUAL_AUTHENTICATION, options.securityMode());
        assertEquals(Duration.ofMillis(250), options.pollInterval());
        assertEquals(Duration.ofSeconds(3), options.pollTimeout());
    }

    @Test
    void oftHostOptions_builder_defaults() {
        OftHostOptions options = OftHostOptions.builder().info("server").build();

        assertEquals(1024, options.maxPacketDataSize());
        assertEquals(OftSecurityMode.SECURE, options.securityMode());
        assertEquals(Duration.ofSeconds(1), options.pollInterval());
        assertEquals(Duration.ofSeconds(5), options.pollTimeout());
    }

    @Test
    void oftPeerOptions_builder_setsEveryField() throws Exception {
        SSLContext sslContext = TestCertificates.createPeerContext();

        OftPeerOptions options = OftPeerOptions.builder()
                .info("peer")
                .sslContext(sslContext)
                .maxPacketDataSize(8192)
                .rekeyInterval(Duration.ofMinutes(15))
                .securityMode(OftSecurityMode.SERVER_AUTHENTICATION)
                .pollInterval(Duration.ofMillis(750))
                .pollTimeout(Duration.ofSeconds(4))
                .idleTimeout(Duration.ofMinutes(2))
                .maxConnectionLifetime(Duration.ofMinutes(30))
                .maxConnectionCount(64)
                .build();

        assertEquals("peer", options.info());
        assertSame(sslContext, options.sslContext());
        assertEquals(8192, options.maxPacketDataSize());
        assertEquals(Duration.ofMinutes(15), options.rekeyInterval());
        assertEquals(OftSecurityMode.SERVER_AUTHENTICATION, options.securityMode());
        assertEquals(Duration.ofMillis(750), options.pollInterval());
        assertEquals(Duration.ofSeconds(4), options.pollTimeout());
        assertEquals(Duration.ofMinutes(2), options.idleTimeout());
        assertEquals(Duration.ofMinutes(30), options.maxConnectionLifetime());
        assertEquals(64, options.maxConnectionCount());
    }

    @Test
    void oftPeerOptions_builder_defaults() {
        OftPeerOptions options = OftPeerOptions.builder().info("peer").build();

        assertEquals(1024, options.maxPacketDataSize());
        assertEquals(OftSecurityMode.SECURE, options.securityMode());
        assertEquals(Duration.ofSeconds(1), options.pollInterval());
        assertEquals(Duration.ofSeconds(5), options.pollTimeout());
        assertEquals(Duration.ofHours(2), options.idleTimeout());
        assertEquals(Duration.ofDays(1), options.maxConnectionLifetime());
        assertEquals(16, options.maxConnectionCount());
    }
}
