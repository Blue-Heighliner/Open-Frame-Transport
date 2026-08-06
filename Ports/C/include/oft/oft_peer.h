/*
 * A peer-to-peer convenience layer over oft_connect() and oft_host(). Sending a message to a
 * host/port transparently reuses an existing connection or creates and caches a new one; idle,
 * expired, or excess cached connections are disconnected automatically, and connections with a
 * configured rekey_interval_ms rekey themselves automatically (see Docs/OFT.md §8). There is no way
 * to enumerate or look up an individual connection this peer holds; oft_peer_rekey() and
 * oft_peer_disconnect() act on all of them at once. See ../../../../Docs/OFT.md and oft.h for the
 * underlying protocol and connection/connect/host API this builds on.
 *
 * Simplification versus the C#/Java ports: establishing a new outbound connection is fully
 * serialized through a single peer-wide lock (rather than only deduplicating concurrent connects to
 * the *same* host/port). This trades a little parallelism for a much simpler, still-correct
 * implementation; revisit if profiling ever shows a peer with many simultaneous outbound connects is
 * bottlenecked here.
 */

#ifndef OFT_PEER_H
#define OFT_PEER_H

#include "oft.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct oft_peer oft_peer;

typedef struct {
    /* Opaque, application-controlled data sent to every peer in this side's hail. Copied. */
    const char *info;

    /* Used to authenticate this peer, for both inbound and outbound connections. Not owned by the
     * peer; the caller must free it after the peer is closed. Required (non-NULL) for
     * OFT_SECURITY_MODE_AUTHENTICATION and OFT_SECURITY_MODE_DUAL_AUTHENTICATION; unused under
     * OFT_SECURITY_MODE_SECURE and OFT_SECURITY_MODE_INSECURE. */
    SSL_CTX *ssl_ctx;

    size_t max_packet_data_size;

    /* When > 0, every connection automatically rekeys its TLS session on this interval. Ignored
     * entirely when `security_mode` is OFT_SECURITY_MODE_INSECURE. */
    long rekey_interval_ms;

    /* The security mode connections are established under (see Docs/OFT.md §9). 0 (the default
     * value of this field, and of a zero-initialized oft_peer_options) is
     * OFT_SECURITY_MODE_SECURE. */
    enum oft_security_mode security_mode;

    /* How often each connection sends an empty Poll packet to its peer as a liveness signal, once
     * established (see Docs/OFT.md §10). 0 = default (1000ms). */
    long poll_interval_ms;

    /* How long a connection may go without receiving anything at all from its peer (a Poll packet
     * or any other packet) before it assumes the peer is unreachable and closes itself (see
     * Docs/OFT.md §10). 0 = default (5000ms). */
    long poll_timeout_ms;

    /* How long a connection may sit idle (no send or receive) before it is automatically
     * disconnected. 0 = default (5 minutes). */
    long idle_timeout_ms;

    /* The maximum total lifetime of a connection before it is automatically disconnected,
     * regardless of activity. 0 = default (1 hour). */
    long max_connection_lifetime_ms;

    /* The maximum number of connections this peer keeps at once. When exceeded, the oldest
     * connections (by when they were established) are disconnected first. 0 = default (128). */
    size_t max_connection_count;

    /* How often the peer checks connections against idle_timeout_ms, max_connection_lifetime_ms,
     * and max_connection_count. 0 = default (30 seconds). */
    long eviction_check_interval_ms;
} oft_peer_options;

/* Creates a peer using the given options. The options are copied; ssl_ctx is not (see oft_peer_close). */
oft_peer *oft_peer_create(const oft_peer_options *options);

/*
 * Starts listening for inbound connections on bind_host:bind_port. A peer that never calls this
 * only ever makes outbound connections. Returns OFT_OK, or OFT_ERROR on failure (with a message
 * written to error_buffer, if given).
 */
int oft_peer_open(oft_peer *peer, const char *bind_host, uint16_t bind_port, char *error_buffer, size_t error_buffer_size);

/*
 * Stops listening for new inbound connections. Already-established connections are left open. Not
 * named oft_peer_close(), unlike its C#/Java counterparts' Close()/close(): that name is reserved
 * here for the full stop-and-free operation below.
 */
void oft_peer_stop(oft_peer *peer);

/* The local port being listened on once oft_peer_open() has completed, or 0 if the peer isn't currently listening. */
int oft_peer_local_port(oft_peer *peer);

/* Registers the (single) received listener for this peer, covering every connection it holds. Not safe to call concurrently with itself. */
void oft_peer_set_received_callback(oft_peer *peer, oft_received_callback callback, void *user_data);

/*
 * Sends a message to host:port, reusing a cached connection if one already exists, or creating and
 * caching a new one otherwise. On success, writes the message id to *out_message_id (usable with
 * oft_connection_wait()/oft_connection_cancel() on *out_connection, if given) and returns OFT_OK.
 * Returns OFT_ERROR on failure (with a message written to error_buffer, if given). out_connection
 * may be NULL if the caller doesn't need it.
 */
int oft_peer_send(oft_peer *peer, const char *host, uint16_t port, const uint8_t *data, size_t length, int priority,
                   oft_connection **out_connection, uint64_t *out_message_id, char *error_buffer, size_t error_buffer_size);

/*
 * Requests a TLS 1.3 KeyUpdate (see Docs/OFT.md §8) on every connection this peer currently holds,
 * both outbound and inbound. Connections established after this call is issued are unaffected.
 * Returns OFT_OK once every request has been made, or OFT_ERROR if any connection failed to
 * request one (every connection is still attempted regardless of an earlier failure).
 */
int oft_peer_rekey(oft_peer *peer);

/*
 * Disconnects every connection this peer currently holds, both outbound and inbound. The peer
 * itself is left usable - a subsequent oft_peer_send() call creates and caches a new outbound
 * connection as usual, and, if listening, new inbound connections keep being accepted.
 */
void oft_peer_disconnect(oft_peer *peer);

/* Stops listening (if applicable), closes every connection this peer holds, waits for its background threads to finish, and frees it. Does not free the SSL_CTX passed to oft_peer_create. */
void oft_peer_close(oft_peer *peer);

#ifdef __cplusplus
}
#endif

#endif /* OFT_PEER_H */
