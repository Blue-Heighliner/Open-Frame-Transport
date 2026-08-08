/*
 * A peer-to-peer convenience layer over oft_connect() and oft_host(). Sending a message to a
 * host/port transparently reuses an existing connection or creates and caches a new one; idle,
 * expired, or excess cached connections are disconnected automatically, and connections with a
 * configured rekey_interval_ms rekey themselves automatically (see Docs/OFT.md §8). A connection
 * only ever becomes eligible for automatic disconnection once it has had no pending data (see
 * oft_connection_has_pending_data()) for a fixed 30-second grace period - not configurable - giving
 * the underlying TLS/TCP layers time to actually flush and acknowledge everything after the last
 * application-level message completes. Eviction itself (checking connections against
 * idle_timeout_ms, max_connection_lifetime_ms, and max_connection_count) is likewise only ever run
 * on a fixed, non-configurable 30-second interval - so, combined with the grace period above,
 * neither idle_timeout_ms nor max_connection_lifetime_ms can take effect any sooner than roughly
 * 30-60 seconds after it's reached, regardless of how much shorter either is configured to be. There
 * is no way to enumerate or look up an individual connection this peer holds; oft_peer_rekey() and
 * oft_peer_drop() act on all of them at once. See ../../../../Docs/OFT.md and oft.h for the
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

/*
 * A message received via oft_peer_set_received_callback(): its payload plus the identity of the
 * connection it arrived on. Both are owned by this struct - a snapshot, independent of the
 * connection's own lifetime, safe to use even after that connection has disconnected - freed
 * together via oft_peer_reception_free(). Opaque; use oft_peer_reception_data()/_length()/
 * _identity() to read it.
 */
typedef struct oft_peer_reception oft_peer_reception;

/* Called for every message received on any connection an oft_peer holds. */
typedef void (*oft_peer_received_callback)(oft_peer_reception *reception, void *user_data);

/* reception's payload. Borrowed; valid until reception is freed. */
const uint8_t *oft_peer_reception_data(const oft_peer_reception *reception);

/* The length in bytes of reception's payload. */
size_t oft_peer_reception_length(const oft_peer_reception *reception);

/* The identity of the connection reception arrived on. Borrowed; valid until reception is freed. */
const oft_identity *oft_peer_reception_identity(const oft_peer_reception *reception);

/* Frees reception and everything it owns (its payload and identity, including any certificate
 * identity carried by its identity field). Safe to call with NULL. */
void oft_peer_reception_free(oft_peer_reception *reception);

typedef struct {
    /* Opaque, application-controlled data sent to every peer in this side's hail. Copied. */
    const char *info;

    /* Used to authenticate this peer, for both inbound and outbound connections. Not owned by the
     * peer; the caller must free it after the peer is closed. Required (non-NULL) for
     * OFT_SECURITY_MODE_DUAL_AUTHENTICATION (the only authenticating mode a peer supports - see
     * `security_mode` below); unused under OFT_SECURITY_MODE_SECURE and OFT_SECURITY_MODE_TRUSTED. */
    SSL_CTX *ssl_ctx;

    /* An optional callback used to validate a fully-established connection (see
     * oft_connection_validation_callback's own documentation, in oft.h). NULL = every connection is
     * accepted. */
    oft_connection_validation_callback connection_validation;
    void *connection_validation_user_data;

    size_t max_packet_data_size;

    /* When > 0, every connection automatically rekeys its TLS session on this interval. Ignored
     * entirely when `security_mode` is OFT_SECURITY_MODE_TRUSTED. */
    long rekey_interval_ms;

    /* The security mode connections are established under (see Docs/OFT.md §9). 0 (the default
     * value of this field, and of a zero-initialized oft_peer_options) is
     * OFT_SECURITY_MODE_SECURE. OFT_SECURITY_MODE_SERVER_AUTHENTICATION is not a valid value here -
     * see its own documentation for why - and oft_peer_create() returns NULL if it's set. */
    enum oft_security_mode security_mode;

    /* How often each connection sends an empty Poll packet to its peer as a liveness signal, once
     * established (see Docs/OFT.md §10). 0 = default (1000ms). */
    long poll_interval_ms;

    /* How long a connection may go without receiving anything at all from its peer (a Poll packet
     * or any other packet) before it assumes the peer is unreachable and closes itself (see
     * Docs/OFT.md §10). 0 = default (5000ms). */
    long poll_timeout_ms;

    /* How long a connection may sit idle (no send or receive) before it is automatically
     * disconnected. 0 = default (2 hours). Eviction is only ever checked on a fixed,
     * non-configurable 30-second interval (see this file's own top comment), so a value below 30
     * seconds here has no effect beyond that floor - the connection is disconnected on the first
     * check after it goes idle, not the instant it does. */
    long idle_timeout_ms;

    /* The maximum total lifetime of a connection before it is automatically disconnected,
     * regardless of activity. 0 = default (1 day). Eviction is only ever checked on a fixed,
     * non-configurable 30-second interval (see this file's own top comment), so a value below 30
     * seconds here has no effect beyond that floor - the connection is disconnected on the first
     * check after it expires, not the instant it does. */
    long max_connection_lifetime_ms;

    /* The maximum number of connections this peer keeps at once. When exceeded, the oldest
     * connections (by when they were established) are disconnected first. A connection with
     * pending data (see oft_connection_has_pending_data()) is never counted toward this limit for
     * eviction purposes - an application that briefly sends to more distinct hosts than this at
     * once is never cut off mid-send; connections beyond the limit are only evicted, oldest first,
     * once their data has finished sending and a fixed grace period (see this file's own top
     * comment) has passed. 0 = default (16). */
    size_t max_connection_count;
} oft_peer_options;

/*
 * Creates a peer using the given options. The options are copied; ssl_ctx is not (see
 * oft_peer_close). Returns NULL if options->security_mode is
 * OFT_SECURITY_MODE_SERVER_AUTHENTICATION (not a valid mode for a peer - see its own
 * documentation for why) or on allocation failure.
 */
oft_peer *oft_peer_create(const oft_peer_options *options);

/*
 * Starts listening for inbound connections on bind_host:bind_port. A peer that never calls this
 * only ever makes outbound connections. Returns OFT_OK, or OFT_ERROR on failure (with a message
 * written to error_buffer, if given).
 */
int oft_peer_listen(oft_peer *peer, const char *bind_host, uint16_t bind_port, char *error_buffer, size_t error_buffer_size);

/*
 * Stops listening for new inbound connections. Already-established connections are left open.
 * Not named oft_peer_close(), matching C#'s IOftPeer.StopListening() and Java's
 * OftPeer.stopListening(): that name is reserved here, as in both, for the full stop-and-free
 * operation below (oft_peer_close()).
 */
void oft_peer_stop_listening(oft_peer *peer);

/* The local port being listened on once oft_peer_listen() has completed, or 0 if the peer isn't currently listening. */
int oft_peer_local_port(oft_peer *peer);

/*
 * Assigns the (single) callback invoked for every message received on any connection this peer
 * holds, both inbound and outbound, replacing any previously assigned one. Pass NULL to clear it;
 * a message received while cleared is freed via oft_peer_reception_free() rather than delivered.
 * Not safe to call concurrently with itself. Same buffering guarantee as
 * oft_connection_set_received_callback(): nothing received before this is first called with a
 * non-NULL callback is lost. The reception's identity is only for identifying which connection a
 * message arrived on - this peer otherwise deliberately exposes no way to enumerate, look up, or
 * be notified about the individual connections it holds (e.g. no disconnected callback): connection
 * lifecycle is this peer's own implementation detail, transparently managed (reconnecting,
 * evicting, etc.) behind oft_peer_send().
 */
void oft_peer_set_received_callback(oft_peer *peer, oft_peer_received_callback callback, void *user_data);

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
void oft_peer_drop(oft_peer *peer);

/* Stops listening (if applicable), closes every connection this peer holds, waits for its background threads to finish, and frees it. Does not free the SSL_CTX passed to oft_peer_create. */
void oft_peer_close(oft_peer *peer);

#ifdef __cplusplus
}
#endif

#endif /* OFT_PEER_H */
