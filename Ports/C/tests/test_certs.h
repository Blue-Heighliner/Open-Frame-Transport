#ifndef TEST_CERTS_H
#define TEST_CERTS_H

#include <openssl/ssl.h>

/* An SSL_CTX carrying a freshly generated, throwaway self-signed certificate and private key,
 * suitable for a test server. Caller must SSL_CTX_free() it. */
SSL_CTX *test_create_server_context(void);

/* An SSL_CTX that accepts any server certificate. Insecure; suitable only for tests connecting to
 * test_create_server_context(), which uses a certificate no real trust store would recognize. */
SSL_CTX *test_create_client_context(void);

/*
 * An SSL_CTX carrying a freshly generated self-signed certificate and accepting any peer
 * certificate, suitable for an oft_peer that both listens and dials (i.e.
 * test_create_server_context() and test_create_client_context() combined into one context, since
 * oft_peer uses a single SSL_CTX for both roles - see oft_peer.h).
 */
SSL_CTX *test_create_peer_context(void);

#endif
