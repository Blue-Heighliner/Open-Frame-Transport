#ifndef OFT_CONNECTION_INTERNAL_H
#define OFT_CONNECTION_INTERNAL_H

#include "oft/oft.h"

/*
 * Establish functions shared between oft_connection.c (which implements them) and oft_connect.c /
 * oft_host.c (which call them). Not part of the public API in oft/oft.h. Both take ownership of fd
 * on success; on failure the caller retains ownership and must close it itself.
 */

oft_connection *oft_connection_establish_as_client(
        int fd, const char *target_host, SSL_CTX *ssl_ctx, const oft_connect_options *options,
        char *error_buffer, size_t error_buffer_size);

oft_connection *oft_connection_establish_as_server(
        int fd, SSL_CTX *ssl_ctx, const oft_host_options *options,
        char *error_buffer, size_t error_buffer_size);

/*
 * Starts the connection's background threads (receive loop, send loop, and automatic rekey timer
 * if configured). Not started automatically by the establish functions above so callers can finish
 * their own bookkeeping first, though this is not required for correctness: notifications are
 * buffered until oft_connection_set_received_callback()/oft_connection_set_disconnected_callback()
 * is first called (see oft_event_buffer), so nothing is ever lost regardless of when that happens
 * relative to this call.
 */
void oft_connection_start_processing(oft_connection *connection);

/*
 * Certificate identity duplication, shared between oft_connection.c (where the extraction logic
 * that first builds an oft_certificate_identity lives) and oft_peer.c (which needs an independent
 * copy for each oft_peer_reception it delivers, since a reception must stay valid even after the
 * connection it came from has disconnected - unlike oft_connection_identity(), which is only ever
 * borrowed from a still-live connection). Not part of the public API in oft/oft.h.
 */

/* Deep-copies identity, or returns NULL if identity is NULL. Caller must free the result with
 * oft_certificate_identity_free(). */
oft_certificate_identity *oft_certificate_identity_copy(const oft_certificate_identity *identity);

/* Frees identity and everything it owns. Safe to call with NULL. */
void oft_certificate_identity_free(oft_certificate_identity *identity);

#endif
