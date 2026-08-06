#include "test_certs.h"

#include <openssl/evp.h>
#include <openssl/x509.h>
#include <time.h>

static EVP_PKEY *generate_key(void) {
    EVP_PKEY_CTX *ctx = EVP_PKEY_CTX_new_id(EVP_PKEY_RSA, NULL);
    if (!ctx) {
        return NULL;
    }

    EVP_PKEY *key = NULL;
    if (EVP_PKEY_keygen_init(ctx) <= 0 ||
        EVP_PKEY_CTX_set_rsa_keygen_bits(ctx, 2048) <= 0 ||
        EVP_PKEY_keygen(ctx, &key) <= 0) {
        EVP_PKEY_CTX_free(ctx);
        return NULL;
    }

    EVP_PKEY_CTX_free(ctx);
    return key;
}

static X509 *generate_certificate(EVP_PKEY *key) {
    X509 *cert = X509_new();
    if (!cert) {
        return NULL;
    }

    ASN1_INTEGER_set(X509_get_serialNumber(cert), (long)time(NULL));
    X509_gmtime_adj(X509_get_notBefore(cert), -300);
    X509_gmtime_adj(X509_get_notAfter(cert), 60 * 60 * 24);
    X509_set_pubkey(cert, key);

    X509_NAME *name = X509_get_subject_name(cert);
    X509_NAME_add_entry_by_txt(name, "CN", MBSTRING_ASC, (const unsigned char *)"localhost", -1, -1, 0);
    X509_set_issuer_name(cert, name);

    if (X509_sign(cert, key, EVP_sha256()) == 0) {
        X509_free(cert);
        return NULL;
    }

    return cert;
}

SSL_CTX *test_create_server_context(void) {
    EVP_PKEY *key = generate_key();
    if (!key) {
        return NULL;
    }

    X509 *cert = generate_certificate(key);
    if (!cert) {
        EVP_PKEY_free(key);
        return NULL;
    }

    SSL_CTX *ctx = SSL_CTX_new(TLS_server_method());
    if (ctx) {
        if (SSL_CTX_use_certificate(ctx, cert) != 1 || SSL_CTX_use_PrivateKey(ctx, key) != 1) {
            SSL_CTX_free(ctx);
            ctx = NULL;
        }
    }

    X509_free(cert);
    EVP_PKEY_free(key);
    return ctx;
}

SSL_CTX *test_create_client_context(void) {
    SSL_CTX *ctx = SSL_CTX_new(TLS_client_method());
    if (!ctx) {
        return NULL;
    }

    SSL_CTX_set_verify(ctx, SSL_VERIFY_NONE, NULL);
    return ctx;
}

/*
 * Always accepts whatever certificate is presented, regardless of trust chain. Registering this
 * (rather than leaving OpenSSL's default verification in place) is what lets
 * test_create_peer_context() double as an SSL_CTX oft_host() can require a client certificate
 * against (OFT_SECURITY_MODE_DUAL_AUTHENTICATION sets SSL_VERIFY_PEER on the SSL object, which
 * would otherwise reject a self-signed certificate no CA store trusts): the callback set here is
 * inherited from the SSL_CTX by every SSL object created from it, even after the verification mode
 * itself is later overridden per-connection.
 */
static int accept_any_certificate(int preverify_ok, X509_STORE_CTX *store_ctx) {
    (void)preverify_ok;
    (void)store_ctx;
    return 1;
}

SSL_CTX *test_create_peer_context(void) {
    EVP_PKEY *key = generate_key();
    if (!key) {
        return NULL;
    }

    X509 *cert = generate_certificate(key);
    if (!cert) {
        EVP_PKEY_free(key);
        return NULL;
    }

    SSL_CTX *ctx = SSL_CTX_new(TLS_method());
    if (ctx) {
        if (SSL_CTX_use_certificate(ctx, cert) != 1 || SSL_CTX_use_PrivateKey(ctx, key) != 1) {
            SSL_CTX_free(ctx);
            ctx = NULL;
        } else {
            SSL_CTX_set_verify(ctx, SSL_VERIFY_NONE, accept_any_certificate);
        }
    }

    X509_free(cert);
    EVP_PKEY_free(key);
    return ctx;
}
