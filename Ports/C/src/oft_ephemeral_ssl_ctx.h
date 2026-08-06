#ifndef OFT_EPHEMERAL_SSL_CTX_H
#define OFT_EPHEMERAL_SSL_CTX_H

#include <openssl/ssl.h>

/*
 * Supports OFT_SECURITY_MODE_SECURE: a throwaway, self-signed server identity, and a client-side
 * context that accepts any certificate unconditionally, since there's nothing meaningful to
 * validate an ephemeral certificate against. Mirrors the C# reference implementation's
 * OftEphemeralCertificate.
 */

/*
 * Creates a new SSL_CTX carrying a freshly generated self-signed certificate, or NULL on failure.
 * Caller owns the result and must SSL_CTX_free() it. Expensive (generates an RSA keypair): callers
 * must resolve this once per listener, not once per accepted connection.
 */
SSL_CTX *oft_ephemeral_ssl_ctx_create_server(void);

/*
 * An SSL_CTX that trusts any server certificate unconditionally, or NULL on failure. Caller owns
 * the result and must SSL_CTX_free() it.
 */
SSL_CTX *oft_ephemeral_ssl_ctx_create_trust_all(void);

#endif
