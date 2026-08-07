/*
 * Open Frame Transport (OFT) - C implementation.
 *
 * Implements the protocol described in ../../../../Docs/OFT.md: framing, the hail handshake,
 * priority-based sending with interruption, cancellation, and deadlock-safe TLS rekeying. See
 * README.md in this directory for build instructions and the scope of this port.
 *
 * Threading: each oft_connection owns two background threads (a receive loop and a send loop).
 * All public functions are safe to call concurrently from any thread unless documented otherwise.
 *
 * Memory: oft_connection_send() copies the data it is given; the caller retains ownership of its
 * buffer and may free or reuse it as soon as the call returns. Data delivered to an
 * oft_received_callback is heap-allocated by the library and ownership passes to the callback,
 * which must free() it.
 */

#ifndef OFT_H
#define OFT_H

#include <openssl/ssl.h>
#include <stddef.h>
#include <stdint.h>
#include <time.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Return codes used throughout the public API. */
enum oft_result {
    OFT_OK = 0,
    OFT_ERROR = -1,
    OFT_ERROR_TIMEOUT = -2,
    OFT_ERROR_CANCELLED = -3,
    OFT_ERROR_CLOSED = -4,
};

/*
 * The security mode a connection is established under (see Docs/OFT.md §9). Mirrors the C#
 * reference implementation's OftSecurityMode enum.
 */
enum oft_security_mode {
    /* No TLS at all: hails are sent directly over the raw TCP connection as soon as it's formed.
     * No confidentiality, integrity, or authentication of either side. ssl_ctx is unused. Only
     * appropriate on a network already trusted by other means. */
    OFT_SECURITY_MODE_TRUSTED = 0,

    /* TLS provides confidentiality and integrity but no authentication of either side. The
     * accepting side (oft_host()) uses a certificate it generates internally rather than one
     * supplied by the caller - a caller-supplied ssl_ctx is accepted but ignored. The connecting
     * side (oft_connect()) accepts whatever certificate it's presented with unconditionally. This
     * is the default. */
    OFT_SECURITY_MODE_SECURE = 1,

    /* Traditional one-way TLS: oft_host() requires a non-NULL ssl_ctx already configured with the
     * accepting side's certificate and private key. oft_connect()'s ssl_ctx validates it normally
     * (via whatever verification the caller configured on that context). Not a valid mode for
     * oft_peer, which has no client/server delineation and so cannot express a one-sided
     * authentication requirement (use OFT_SECURITY_MODE_DUAL_AUTHENTICATION instead). */
    OFT_SECURITY_MODE_SERVER_AUTHENTICATION = 2,

    /* Mutual TLS: everything OFT_SECURITY_MODE_SERVER_AUTHENTICATION requires, plus
     * oft_connect()'s ssl_ctx must also be configured with this side's own certificate and private
     * key, which the accepting side requests and validates. */
    OFT_SECURITY_MODE_DUAL_AUTHENTICATION = 3,
};

typedef struct oft_connection oft_connection;
typedef struct oft_listener oft_listener;

/*
 * Called whenever a complete application message has been received.
 *
 * `data` is heap-allocated and owned by the callee: it must be free()d when no longer needed.
 */
typedef void (*oft_received_callback)(oft_connection *connection, uint8_t *data, size_t length, void *user_data);

/* Called once, when a connection closes for any reason. `error_message` is NULL if it closed cleanly. */
typedef void (*oft_disconnected_callback)(oft_connection *connection, const char *error_message, void *user_data);

/* Called whenever an oft_listener accepts and establishes a new inbound connection. */
typedef void (*oft_connected_callback)(oft_listener *listener, oft_connection *connection, void *user_data);

typedef struct {
    /* Opaque, application-controlled data sent to the peer in this side's hail (see Docs/OFT.md §3). Copied. */
    const char *info;

    /* The maximum number of payload bytes carried in a single packet's data field. 0 = default (1024). */
    size_t max_packet_data_size;

    /* When > 0, the connection automatically rekeys its TLS session on this interval. 0 = disabled,
     * or ignored entirely when `security_mode` is OFT_SECURITY_MODE_TRUSTED. */
    long rekey_interval_ms;

    /* The security mode this connection is established under (see Docs/OFT.md §9). 0 (the default
     * value of this field, and of a zero-initialized oft_connect_options) is
     * OFT_SECURITY_MODE_SECURE. */
    enum oft_security_mode security_mode;

    /* How often the connection sends an empty Poll packet to the peer as a liveness signal, once
     * established (see Docs/OFT.md §10). 0 = default (1000ms). */
    long poll_interval_ms;

    /* How long the connection may go without receiving anything at all from the peer (a Poll
     * packet or any other packet) before it assumes the peer is unreachable and closes itself (see
     * Docs/OFT.md §10). 0 = default (5000ms). */
    long poll_timeout_ms;
} oft_connect_options;

typedef struct {
    const char *info;
    size_t max_packet_data_size;

    /* When > 0, the connection automatically rekeys its TLS session on this interval. 0 = disabled,
     * or ignored entirely when `security_mode` is OFT_SECURITY_MODE_TRUSTED. */
    long rekey_interval_ms;

    /* The security mode connections are established under (see Docs/OFT.md §9). 0 (the default
     * value of this field, and of a zero-initialized oft_host_options) is
     * OFT_SECURITY_MODE_SECURE. */
    enum oft_security_mode security_mode;

    /* How often each connection sends an empty Poll packet to its peer as a liveness signal, once
     * established (see Docs/OFT.md §10). 0 = default (1000ms). */
    long poll_interval_ms;

    /* How long a connection may go without receiving anything at all from its peer (a Poll packet
     * or any other packet) before it assumes the peer is unreachable and closes itself (see
     * Docs/OFT.md §10). 0 = default (5000ms). */
    long poll_timeout_ms;
} oft_host_options;

/* ---- Connection ---- */

/*
 * Queues a message for sending at the given priority (see Docs/OFT.md §5-§7). Larger priority values
 * are sent first; a lower-priority message already being sent is transparently interrupted and
 * resumed later (Docs/OFT.md §6). Returns immediately once the message is queued.
 *
 * On success, writes an identifier for the message to *out_message_id, usable with
 * oft_connection_wait() and oft_connection_cancel(). Returns OFT_OK, or OFT_ERROR_CLOSED if the
 * connection is already closed.
 */
int oft_connection_send(oft_connection *connection, const uint8_t *data, size_t length, int priority, uint64_t *out_message_id);

/*
 * Blocks the calling thread until the message identified by message_id has been fully delivered,
 * cancelled, or the connection closes. Returns OFT_OK if delivered, OFT_ERROR_CANCELLED if
 * cancelled, or OFT_ERROR_CLOSED if the connection closed first.
 */
int oft_connection_wait(oft_connection *connection, uint64_t message_id);

/*
 * Abandons a previously queued message (see Docs/OFT.md §7): immediately if it has not yet started
 * sending, or by sending a Cancellation packet if it has.
 */
void oft_connection_cancel(oft_connection *connection, uint64_t message_id);

/*
 * Rekeys the connection's TLS session in place, without closing the underlying TCP connection (see
 * Docs/OFT.md §8). Blocks until the new session is established. If a rekey is already in progress,
 * joins it instead of starting a new one. Returns OFT_OK immediately (a no-op) if the connection
 * was established with OFT_SECURITY_MODE_TRUSTED - there is no TLS session to rekey - or
 * OFT_ERROR_CLOSED if the connection closes before the rekey completes.
 */
int oft_connection_rekey(oft_connection *connection);

/*
 * Assigns the (single) callback invoked whenever a complete application message has been received,
 * replacing any previously assigned one. Pass NULL to clear it; a message received while cleared is
 * simply dropped, not buffered for a later callback. Not safe to call concurrently with itself.
 *
 * Safe to call at any point after the connection is established, even well after it starts
 * processing inbound packets: every message received before this is first called with a non-NULL
 * callback is buffered and delivered to it, in order, before it becomes the live target for
 * anything received afterward - there is no message-loss race to guard against by calling this
 * before some other event.
 */
void oft_connection_set_received_callback(oft_connection *connection, oft_received_callback callback, void *user_data);

/*
 * Assigns the (single) callback invoked once, when the connection closes for any reason, replacing
 * any previously assigned one. Pass NULL to clear it. Not safe to call concurrently with itself.
 *
 * Safe to call at any point after the connection is established: if the connection already closed
 * before this is ever called with a non-NULL callback, the first such call is still notified,
 * exactly like oft_connection_set_received_callback() - the same buffering guarantee, applied to
 * this one-shot notification instead of a stream of messages.
 */
void oft_connection_set_disconnected_callback(oft_connection *connection, oft_disconnected_callback callback, void *user_data);

/* The opaque, application-controlled data the peer sent in its hail (see Docs/OFT.md §3). Owned by the connection. */
const char *oft_connection_remote_info(oft_connection *connection);

/* Writes the remote host (NUL-terminated) and port of this connection. Returns OFT_OK, or OFT_ERROR if host_buffer is too small. */
int oft_connection_remote_endpoint(oft_connection *connection, char *host_buffer, size_t host_buffer_size, uint16_t *out_port);

/* When the OFT handshake (TLS session plus hail exchange) completed. */
void oft_connection_connected_at(oft_connection *connection, struct timespec *out_time);

/* When the last packet was sent on this connection. */
void oft_connection_last_sent_at(oft_connection *connection, struct timespec *out_time);

/* When the last packet was received on this connection. */
void oft_connection_last_received_at(oft_connection *connection, struct timespec *out_time);

/*
 * Non-zero if this connection currently has any outbound message that hasn't been fully
 * acknowledged yet (queued, in flight, or awaiting its final Receipt), or any inbound multi-packet
 * message that has started arriving but hasn't been fully reassembled yet. oft_peer never
 * automatically disconnects a connection while this is non-zero, regardless of its idle timeout,
 * maximum lifetime, or maximum connection count settings, so that in-flight data is never silently
 * dropped.
 */
int oft_connection_has_pending_data(oft_connection *connection);

/* Closes the connection. Safe to call more than once. */
void oft_connection_disconnect(oft_connection *connection);

/* Closes the connection (if not already closed), waits for its background threads to finish, and frees it. */
void oft_connection_close(oft_connection *connection);

/* ---- Connector ---- */

/*
 * Dials host:port, performs the TLS handshake and hail exchange, and returns the resulting
 * established connection, or NULL on failure (with a message written to error_buffer, if given).
 *
 * options may be NULL to use default options. ssl_ctx is used to validate the accepting side's
 * certificate under OFT_SECURITY_MODE_SERVER_AUTHENTICATION/OFT_SECURITY_MODE_DUAL_AUTHENTICATION (and,
 * under OFT_SECURITY_MODE_DUAL_AUTHENTICATION, must also be configured with this side's own
 * certificate and private key) - required (non-NULL) for both of those modes; unused under
 * OFT_SECURITY_MODE_SECURE and OFT_SECURITY_MODE_TRUSTED. Not owned by this call; the caller must
 * free it whenever it's done being used for connecting (including after the returned connection is
 * closed).
 *
 * The returned connection already started processing inbound packets by the time this returns -
 * assigning a received/disconnected callback to it afterward (see
 * oft_connection_set_received_callback/oft_connection_set_disconnected_callback) is always safe,
 * with no message-loss race to guard against.
 */
oft_connection *oft_connect(
        const char *host, uint16_t port, const oft_connect_options *options, SSL_CTX *ssl_ctx,
        char *error_buffer, size_t error_buffer_size);

/* ---- Hoster ---- */

/*
 * Starts listening for inbound connections on bind_host:bind_port and returns the resulting
 * listener, or NULL on failure (with a message written to error_buffer, if given). Each call
 * starts a fresh, independent listener; there is no separate create-then-open step.
 *
 * options may be NULL to use default options. ssl_ctx must already be configured with the
 * accepting side's certificate and private key (e.g. via SSL_CTX_use_certificate_file /
 * SSL_CTX_use_PrivateKey_file) when options->security_mode is OFT_SECURITY_MODE_SERVER_AUTHENTICATION or
 * OFT_SECURITY_MODE_DUAL_AUTHENTICATION (required, non-NULL, in both cases) - ignored under
 * OFT_SECURITY_MODE_SECURE (an internally generated certificate is used instead) and
 * OFT_SECURITY_MODE_TRUSTED. Not owned by the listener; the caller must free it after the
 * listener is closed.
 */
oft_listener *oft_host(
        const char *bind_host, uint16_t bind_port, const oft_host_options *options, SSL_CTX *ssl_ctx,
        char *error_buffer, size_t error_buffer_size);

/* The local port being listened on. */
int oft_listener_local_port(oft_listener *listener);

/*
 * Assigns the (single) callback invoked whenever a new inbound connection completes its handshake,
 * replacing any previously assigned one. Pass NULL to clear it; a connection accepted while cleared
 * is simply dropped (and closed), not buffered for a later callback. Not safe to call concurrently
 * with itself.
 *
 * Safe to call at any point after oft_host() returns the listener, even well after connections
 * start being accepted: every connection accepted before this is first called with a non-NULL
 * callback is buffered and delivered to it, in order, before it becomes the live target for
 * anything accepted afterward - there is no accept-before-subscribe race to guard against by
 * calling this before some other event.
 */
void oft_listener_set_connected_callback(oft_listener *listener, oft_connected_callback callback, void *user_data);

/*
 * Stops listening for new inbound connections and frees the listener. Already-accepted connections
 * are left open; this type doesn't track them (see oft_host()'s own doc comment). There is no way
 * to stop listening short of closing the listener entirely - call oft_host() again for a fresh
 * listener if needed.
 */
void oft_listener_close(oft_listener *listener);

#ifdef __cplusplus
}
#endif

#endif /* OFT_H */
