#include "oft/oft_peer.h"

#include <pthread.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

#define OFT_PEER_DEFAULT_IDLE_TIMEOUT_MS (5L * 60L * 1000L)
#define OFT_PEER_DEFAULT_MAX_CONNECTION_LIFETIME_MS (60L * 60L * 1000L)
#define OFT_PEER_DEFAULT_MAX_CONNECTION_COUNT 128
#define OFT_PEER_DEFAULT_EVICTION_CHECK_INTERVAL_MS (30L * 1000L)

/* Tracks a single outbound connection this peer has cached, keyed by host:port for reuse lookups. */
typedef struct oft_peer_outbound_node {
    char host[256];
    uint16_t port;
    oft_connection *connection;
    struct oft_peer_outbound_node *next;
} oft_peer_outbound_node;

/* Tracks a single inbound connection this peer has accepted. */
typedef struct oft_peer_inbound_node {
    oft_connection *connection;
    struct oft_peer_inbound_node *next;
} oft_peer_inbound_node;

struct oft_peer {
    oft_peer_options options;
    char *info_copy;

    oft_connect_options connect_options;
    oft_host_options host_options;

    oft_listener *listener;

    /*
     * Held across the entire find-or-connect sequence in get_or_connect(), including the blocking
     * connect itself. This serializes all outbound connection establishment through this peer
     * (whether to the same or different hosts) - see the trade-off note in oft_peer.h.
     */
    pthread_mutex_t outbound_lock;
    oft_peer_outbound_node *outbound_connections;

    pthread_mutex_t inbound_lock;
    oft_peer_inbound_node *inbound_connections;

    pthread_mutex_t callback_lock;
    oft_received_callback received_callback;
    void *received_callback_user_data;

    pthread_t eviction_thread;
    atomic_int eviction_stop;

    atomic_int disposed;
};

static long apply_default_ms(long value, long default_value) {
    return value > 0 ? value : default_value;
}

static size_t apply_default_count(size_t value, size_t default_value) {
    return value > 0 ? value : default_value;
}

static long timespec_diff_ms(const struct timespec *a, const struct timespec *b) {
    long sec_diff = (long)(a->tv_sec - b->tv_sec);
    long nsec_diff = (long)(a->tv_nsec - b->tv_nsec);
    return sec_diff * 1000L + nsec_diff / 1000000L;
}

static void raise_received(oft_peer *peer, oft_connection *connection, uint8_t *data, size_t length) {
    pthread_mutex_lock(&peer->callback_lock);
    oft_received_callback callback = peer->received_callback;
    void *user_data = peer->received_callback_user_data;
    pthread_mutex_unlock(&peer->callback_lock);

    if (callback) {
        callback(connection, data, length, user_data);
    } else {
        free(data);
    }
}

static void on_outbound_received(oft_connection *connection, uint8_t *data, size_t length, void *user_data) {
    raise_received(user_data, connection, data, length);
}

static void on_outbound_disconnected(oft_connection *connection, const char *error_message, void *user_data) {
    (void)error_message;
    oft_peer *peer = user_data;

    pthread_mutex_lock(&peer->outbound_lock);
    oft_peer_outbound_node *prev = NULL;
    for (oft_peer_outbound_node *node = peer->outbound_connections; node; prev = node, node = node->next) {
        if (node->connection == connection) {
            if (prev) {
                prev->next = node->next;
            } else {
                peer->outbound_connections = node->next;
            }

            free(node);
            break;
        }
    }
    pthread_mutex_unlock(&peer->outbound_lock);
}

/* Passed to oft_connect() as on_established: registering these here, rather than after
 * oft_connect() returns, guarantees they're in place before the connection starts processing
 * inbound packets - otherwise a peer that replies the instant the connection is up could have its
 * first message delivered (and discarded, for lack of a callback) before oft_connect() ever
 * returns. */
static void on_outbound_established(oft_connection *connection, void *user_data) {
    oft_peer *peer = user_data;
    oft_connection_set_received_callback(connection, on_outbound_received, peer);
    oft_connection_add_disconnected_listener(connection, on_outbound_disconnected, peer);
}

static void on_inbound_received(oft_connection *connection, uint8_t *data, size_t length, void *user_data) {
    raise_received(user_data, connection, data, length);
}

static void on_inbound_disconnected(oft_connection *connection, const char *error_message, void *user_data) {
    (void)error_message;
    oft_peer *peer = user_data;

    pthread_mutex_lock(&peer->inbound_lock);
    oft_peer_inbound_node *prev = NULL;
    for (oft_peer_inbound_node *node = peer->inbound_connections; node; prev = node, node = node->next) {
        if (node->connection == connection) {
            if (prev) {
                prev->next = node->next;
            } else {
                peer->inbound_connections = node->next;
            }

            free(node);
            break;
        }
    }
    pthread_mutex_unlock(&peer->inbound_lock);
}

static void on_inbound_established(oft_listener *listener, oft_connection *connection, void *user_data) {
    (void)listener;
    oft_peer *peer = user_data;

    oft_peer_inbound_node *node = calloc(1, sizeof(oft_peer_inbound_node));
    if (!node) {
        oft_connection_disconnect(connection);
        return;
    }

    node->connection = connection;

    pthread_mutex_lock(&peer->inbound_lock);
    node->next = peer->inbound_connections;
    peer->inbound_connections = node;
    pthread_mutex_unlock(&peer->inbound_lock);

    oft_connection_set_received_callback(connection, on_inbound_received, peer);
    oft_connection_add_disconnected_listener(connection, on_inbound_disconnected, peer);
}

static void *eviction_loop(void *arg);

oft_peer *oft_peer_create(const oft_peer_options *options) {
    oft_peer *peer = calloc(1, sizeof(oft_peer));
    if (!peer) {
        return NULL;
    }

    peer->options = *options;
    peer->info_copy = strdup(options->info ? options->info : "");
    peer->options.info = peer->info_copy;

    peer->options.idle_timeout_ms = apply_default_ms(options->idle_timeout_ms, OFT_PEER_DEFAULT_IDLE_TIMEOUT_MS);
    peer->options.max_connection_lifetime_ms = apply_default_ms(options->max_connection_lifetime_ms, OFT_PEER_DEFAULT_MAX_CONNECTION_LIFETIME_MS);
    peer->options.max_connection_count = apply_default_count(options->max_connection_count, OFT_PEER_DEFAULT_MAX_CONNECTION_COUNT);
    peer->options.eviction_check_interval_ms = apply_default_ms(options->eviction_check_interval_ms, OFT_PEER_DEFAULT_EVICTION_CHECK_INTERVAL_MS);

    pthread_mutex_init(&peer->outbound_lock, NULL);
    pthread_mutex_init(&peer->inbound_lock, NULL);
    pthread_mutex_init(&peer->callback_lock, NULL);
    atomic_init(&peer->eviction_stop, 0);
    atomic_init(&peer->disposed, 0);

    memset(&peer->connect_options, 0, sizeof(peer->connect_options));
    peer->connect_options.info = peer->options.info;
    peer->connect_options.max_packet_data_size = peer->options.max_packet_data_size;
    peer->connect_options.rekey_interval_ms = peer->options.rekey_interval_ms;
    peer->connect_options.security_mode = peer->options.security_mode;
    peer->connect_options.poll_interval_ms = peer->options.poll_interval_ms;
    peer->connect_options.poll_timeout_ms = peer->options.poll_timeout_ms;

    memset(&peer->host_options, 0, sizeof(peer->host_options));
    peer->host_options.info = peer->options.info;
    peer->host_options.max_packet_data_size = peer->options.max_packet_data_size;
    peer->host_options.rekey_interval_ms = peer->options.rekey_interval_ms;
    peer->host_options.security_mode = peer->options.security_mode;
    peer->host_options.poll_interval_ms = peer->options.poll_interval_ms;
    peer->host_options.poll_timeout_ms = peer->options.poll_timeout_ms;

    pthread_create(&peer->eviction_thread, NULL, eviction_loop, peer);

    return peer;
}

int oft_peer_open(oft_peer *peer, const char *bind_host, uint16_t bind_port, char *error_buffer, size_t error_buffer_size) {
    oft_listener *listener = oft_host(bind_host, bind_port, &peer->host_options, peer->options.ssl_ctx, error_buffer, error_buffer_size);
    if (!listener) {
        return OFT_ERROR;
    }

    oft_listener_set_connected_callback(listener, on_inbound_established, peer);
    peer->listener = listener;
    return OFT_OK;
}

void oft_peer_stop(oft_peer *peer) {
    if (!peer->listener) {
        return;
    }

    oft_listener_close(peer->listener);
    peer->listener = NULL;
}

int oft_peer_local_port(oft_peer *peer) {
    return peer->listener ? oft_listener_local_port(peer->listener) : 0;
}

void oft_peer_set_received_callback(oft_peer *peer, oft_received_callback callback, void *user_data) {
    pthread_mutex_lock(&peer->callback_lock);
    peer->received_callback = callback;
    peer->received_callback_user_data = user_data;
    pthread_mutex_unlock(&peer->callback_lock);
}

static oft_peer_outbound_node *get_or_connect(oft_peer *peer, const char *host, uint16_t port, char *error_buffer, size_t error_buffer_size) {
    pthread_mutex_lock(&peer->outbound_lock);

    for (oft_peer_outbound_node *node = peer->outbound_connections; node; node = node->next) {
        if (node->port == port && strcmp(node->host, host) == 0) {
            pthread_mutex_unlock(&peer->outbound_lock);
            return node;
        }
    }

    oft_connection *connection = oft_connect(
            host, port, &peer->connect_options, peer->options.ssl_ctx,
            on_outbound_established, peer, error_buffer, error_buffer_size);
    if (!connection) {
        pthread_mutex_unlock(&peer->outbound_lock);
        return NULL;
    }

    oft_peer_outbound_node *node = calloc(1, sizeof(oft_peer_outbound_node));
    if (!node) {
        pthread_mutex_unlock(&peer->outbound_lock);
        oft_connection_disconnect(connection);
        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size, "out of memory");
        }

        return NULL;
    }

    snprintf(node->host, sizeof(node->host), "%s", host);
    node->port = port;
    node->connection = connection;
    node->next = peer->outbound_connections;
    peer->outbound_connections = node;

    pthread_mutex_unlock(&peer->outbound_lock);

    return node;
}

int oft_peer_send(oft_peer *peer, const char *host, uint16_t port, const uint8_t *data, size_t length, int priority,
                   oft_connection **out_connection, uint64_t *out_message_id, char *error_buffer, size_t error_buffer_size) {
    oft_peer_outbound_node *connection = get_or_connect(peer, host, port, error_buffer, error_buffer_size);
    if (!connection) {
        return OFT_ERROR;
    }

    if (out_connection) {
        *out_connection = connection->connection;
    }

    int result = oft_connection_send(connection->connection, data, length, priority, out_message_id);
    if (result != OFT_OK && error_buffer) {
        snprintf(error_buffer, error_buffer_size, "failed to queue the message for sending");
    }

    return result;
}

/* Every connection this peer currently holds, both outbound and inbound. Returns a malloc'd array
 * of *out_count connection handles that the caller must free(), or NULL if there are none or
 * allocation failed (in which case *out_count is 0). */
static oft_connection **get_tracked_connections(oft_peer *peer, size_t *out_count) {
    pthread_mutex_lock(&peer->outbound_lock);
    size_t outbound_count = 0;
    for (oft_peer_outbound_node *node = peer->outbound_connections; node; node = node->next) {
        outbound_count++;
    }
    pthread_mutex_unlock(&peer->outbound_lock);

    pthread_mutex_lock(&peer->inbound_lock);
    size_t inbound_count = 0;
    for (oft_peer_inbound_node *node = peer->inbound_connections; node; node = node->next) {
        inbound_count++;
    }
    pthread_mutex_unlock(&peer->inbound_lock);

    size_t count = outbound_count + inbound_count;
    *out_count = 0;
    if (count == 0) {
        return NULL;
    }

    oft_connection **handles = malloc(count * sizeof(oft_connection *));
    if (!handles) {
        return NULL;
    }

    size_t index = 0;

    pthread_mutex_lock(&peer->outbound_lock);
    for (oft_peer_outbound_node *node = peer->outbound_connections; node && index < count; node = node->next) {
        handles[index++] = node->connection;
    }
    pthread_mutex_unlock(&peer->outbound_lock);

    pthread_mutex_lock(&peer->inbound_lock);
    for (oft_peer_inbound_node *node = peer->inbound_connections; node && index < count; node = node->next) {
        handles[index++] = node->connection;
    }
    pthread_mutex_unlock(&peer->inbound_lock);

    *out_count = index;
    return handles;
}

int oft_peer_rekey(oft_peer *peer) {
    size_t count;
    oft_connection **handles = get_tracked_connections(peer, &count);
    if (!handles) {
        return OFT_OK;
    }

    int result = OFT_OK;
    for (size_t i = 0; i < count; i++) {
        if (oft_connection_rekey(handles[i]) != OFT_OK) {
            result = OFT_ERROR;
        }
    }

    free(handles);
    return result;
}

void oft_peer_disconnect(oft_peer *peer) {
    size_t count;
    oft_connection **handles = get_tracked_connections(peer, &count);
    if (!handles) {
        return;
    }

    for (size_t i = 0; i < count; i++) {
        oft_connection_disconnect(handles[i]);
    }

    free(handles);
}

static void run_eviction(oft_peer *peer) {
    struct timespec now;
    clock_gettime(CLOCK_REALTIME, &now);

    size_t index;
    oft_connection **handles = get_tracked_connections(peer, &index);
    if (!handles) {
        return;
    }

    int *should_evict = calloc(index, sizeof(int));
    if (should_evict) {
        size_t evict_count = 0;
        for (size_t i = 0; i < index; i++) {
            /* A connection with pending/unacknowledged data (see oft_connection_has_pending_data())
             * is never auto-disconnected here, regardless of which eviction condition it would
             * otherwise meet: doing so could silently drop a message that's still queued, in
             * flight, or only partially reassembled. It's only a candidate once all of its data has
             * been acknowledged. */
            if (oft_connection_has_pending_data(handles[i])) {
                continue;
            }

            struct timespec last_sent;
            struct timespec last_received;
            struct timespec connected_at;
            oft_connection_last_sent_at(handles[i], &last_sent);
            oft_connection_last_received_at(handles[i], &last_received);
            oft_connection_connected_at(handles[i], &connected_at);

            struct timespec *last_activity = timespec_diff_ms(&last_sent, &last_received) > 0 ? &last_sent : &last_received;

            if (timespec_diff_ms(&now, last_activity) > peer->options.idle_timeout_ms ||
                    timespec_diff_ms(&now, &connected_at) > peer->options.max_connection_lifetime_ms) {
                should_evict[i] = 1;
                evict_count++;
            }
        }

        size_t remaining = index - evict_count;
        if (remaining > peer->options.max_connection_count) {
            size_t excess = remaining - peer->options.max_connection_count;

            while (excess > 0) {
                size_t oldest_index = (size_t)-1;
                struct timespec oldest_connected_at = {0};
                for (size_t i = 0; i < index; i++) {
                    if (should_evict[i] || oft_connection_has_pending_data(handles[i])) {
                        continue;
                    }

                    struct timespec connected_at;
                    oft_connection_connected_at(handles[i], &connected_at);

                    if (oldest_index == (size_t)-1 || timespec_diff_ms(&connected_at, &oldest_connected_at) < 0) {
                        oldest_index = i;
                        oldest_connected_at = connected_at;
                    }
                }

                if (oldest_index == (size_t)-1) {
                    break;
                }

                should_evict[oldest_index] = 1;
                excess--;
            }
        }

        for (size_t i = 0; i < index; i++) {
            if (should_evict[i]) {
                oft_connection_disconnect(handles[i]);
            }
        }

        free(should_evict);
    }

    free(handles);
}

static void *eviction_loop(void *arg) {
    oft_peer *peer = arg;
    long remaining_ms = peer->options.eviction_check_interval_ms;

    while (!atomic_load(&peer->eviction_stop)) {
        long step_ms = remaining_ms < 50 ? remaining_ms : 50;
        struct timespec sleep_time = {step_ms / 1000, (step_ms % 1000) * 1000000L};
        nanosleep(&sleep_time, NULL);

        remaining_ms -= step_ms;
        if (remaining_ms > 0) {
            continue;
        }

        remaining_ms = peer->options.eviction_check_interval_ms;
        if (!atomic_load(&peer->eviction_stop)) {
            run_eviction(peer);
        }
    }

    return NULL;
}

void oft_peer_close(oft_peer *peer) {
    int expected = 0;
    if (!atomic_compare_exchange_strong(&peer->disposed, &expected, 1)) {
        return;
    }

    atomic_store(&peer->eviction_stop, 1);
    pthread_join(peer->eviction_thread, NULL);

    oft_peer_stop(peer);

    size_t count;
    oft_connection **handles = get_tracked_connections(peer, &count);
    for (size_t i = 0; i < count; i++) {
        oft_connection_close(handles[i]);
    }
    free(handles);

    pthread_mutex_destroy(&peer->outbound_lock);
    pthread_mutex_destroy(&peer->inbound_lock);
    pthread_mutex_destroy(&peer->callback_lock);

    free(peer->info_copy);
    free(peer);
}
