#include "oft/oft.h"
#include "oft_connection_internal.h"
#include "oft_ephemeral_ssl_ctx.h"
#include "oft_event.h"
#include "oft_event_buffer.h"
#include "oft_frame.h"
#include "oft_wire.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netinet/in.h>
#include <netinet/tcp.h>
#include <openssl/err.h>
#include <openssl/x509.h>
#include <openssl/x509v3.h>
#include <pthread.h>
#include <semaphore.h>
#include <signal.h>
#include <stdatomic.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

#define OFT_PROTOCOL_VERSION "oft/1"
#define OFT_DEFAULT_MAX_PACKET_DATA_SIZE 1024
#define OFT_DEFAULT_POLL_INTERVAL_MS 1000L
#define OFT_DEFAULT_POLL_TIMEOUT_MS 5000L

/* ---- Internal data structures ---- */

typedef struct oft_pending_message {
    uint64_t id;
    uint8_t *data;
    size_t length;
    int priority;
    int started;
    int cancel_requested;
    size_t bytes_sent;

    /* The opaque tag this send was queued with, or NULL if it wasn't - see acknowledged_callback
     * below. Not owned by this struct. */
    void *tag;

    oft_event completed; /* result: OFT_OK, OFT_ERROR_CANCELLED, or OFT_ERROR_CLOSED */
    struct oft_pending_message *next_in_queue;
    struct oft_pending_message *next_in_registry;
} oft_pending_message;

typedef struct oft_priority_queue {
    int priority;
    oft_pending_message *head;
    oft_pending_message *tail;
    struct oft_priority_queue *next;
} oft_priority_queue;

/* A single caller's request, queued by oft_connection_rekey() and drained only by the receive
 * thread (see process_pending_rekeys) - see that function for why. */
typedef struct oft_rekey_request {
    oft_event completed; /* result: OFT_OK or OFT_ERROR_CLOSED */
    struct oft_rekey_request *next;
} oft_rekey_request;

typedef struct {
    int priority;
    uint8_t **chunks;
    size_t *chunk_lengths;
    size_t chunk_count;
    size_t chunk_capacity;
} oft_inbound_buffer;

/* The dispatch target attached to received_buffer below. */
typedef struct {
    oft_received_callback callback;
    void *user_data;
} oft_received_target;

/* The dispatch target attached to disconnected_buffer below. */
typedef struct {
    oft_disconnected_callback callback;
    void *user_data;
} oft_disconnected_target;

struct oft_connection {
    int fd;
    SSL *ssl;
    SSL_CTX *ssl_ctx;
    int owns_ssl_ctx; /* set only for a connecting-side connection under OFT_SECURITY_MODE_SECURE,
                        * which creates its own throwaway trust-all ssl_ctx per connection; every
                        * other connection borrows one owned by its caller or listener. */

    oft_frame_stream frame_stream;

    int is_client;
    char *target_host;
    int require_client_cert;
    size_t max_packet_data_size;
    long rekey_interval_ms;
    int insecure;
    long poll_interval_ms;
    long poll_timeout_ms;

    pthread_mutex_t outbound_lock;
    oft_priority_queue *queues;
    oft_pending_message *registry;
    uint64_t next_message_id;
    sem_t send_signal;

    sem_t write_permit;
    pthread_mutex_t receipt_lock;
    oft_event *outstanding_receipt; /* not owned by this pointer; borrowed from the in-flight packet/rekey op */

    /* Only ever touched by the receive thread; no synchronization needed. */
    oft_inbound_buffer *inbound_buffers;
    size_t inbound_buffer_count;
    size_t inbound_buffer_capacity;

    /* Mirrors inbound_buffer_count > 0 for oft_connection_has_pending_data() to read from any
     * thread without synchronizing with the receive thread, which is otherwise the only thread
     * that ever touches inbound_buffers. Written only by the receive thread, immediately after
     * every mutation of inbound_buffer_count. */
    atomic_int has_pending_inbound_message;

    /* Requests queued by oft_connection_rekey(), drained only by the receive thread (see
     * process_pending_rekeys) - see that function's comment for why. */
    pthread_mutex_t rekey_queue_lock;
    oft_rekey_request *rekey_queue_head;
    oft_rekey_request *rekey_queue_tail;
    pthread_t rekey_timer_thread;
    int rekey_timer_active;

    pthread_t poll_timer_thread;
    int poll_timer_active;

    atomic_int closed;

    pthread_mutex_t timestamp_lock;
    struct timespec connected_at;
    struct timespec last_sent_at;
    struct timespec last_received_at;

    /* When the connection last received anything at all - a Poll packet or any other kind - used
     * exclusively by the liveness watchdog (see Docs/OFT.md §10). Deliberately tracked separately
     * from last_received_at (which only Poll leaves untouched): an oft_peer's idle-eviction relies
     * on oft_connection_last_received_at() reflecting application activity only, and automatic Poll
     * traffic would otherwise mask a connection an application never actually uses as perpetually
     * "active". Guarded by timestamp_lock alongside the fields above. */
    struct timespec last_inbound_activity;

    oft_identity identity;

    pthread_t receive_thread;
    pthread_t send_thread;
    int threads_started;

    /* Holds every received message until oft_connection_set_received_callback() is first called
     * with a non-NULL callback, then flushes that backlog to it, in order, before it becomes the
     * live target for anything received afterward - see oft_event_buffer's own doc comment.
     * received_target is the live target attached to received_buffer - embedded rather than
     * heap-allocated since there is only ever one at a time. */
    oft_event_buffer received_buffer;
    oft_received_target received_target;

    /* Same buffering guarantee as received_buffer above, applied to the one-shot disconnected
     * notification instead of a stream of messages. */
    oft_event_buffer disconnected_buffer;
    oft_disconnected_target disconnected_target;

    /* The (single) callback invoked when data sent with a non-NULL tag has been fully delivered and
     * acknowledged (see oft_connection_send() and oft_connection_set_acknowledged_callback()).
     * Deliberately not buffered like received_buffer/disconnected_buffer above: it can only ever be
     * raised in response to a caller's own oft_connection_send() call, so there's no message-loss
     * race to guard against by assigning it beforehand - this lock only protects the callback/
     * user_data pointer pair itself from a torn read/write across threads. */
    pthread_mutex_t acknowledged_callback_lock;
    oft_acknowledged_callback acknowledged_callback;
    void *acknowledged_callback_user_data;
};

/* ---- Small helpers ---- */

static void now(struct timespec *out) {
    clock_gettime(CLOCK_REALTIME, out);
}

static void describe_ssl_error(char *buffer, size_t size) {
    unsigned long code = ERR_get_error();
    if (code == 0) {
        snprintf(buffer, size, "unknown TLS error");
    } else {
        ERR_error_string_n(code, buffer, size);
    }
}

static void set_error(char *buffer, size_t size, const char *message) {
    if (buffer && size > 0) {
        snprintf(buffer, size, "%s", message);
    }
}

static pthread_once_t g_sigpipe_once = PTHREAD_ONCE_INIT;

/*
 * Writing to a socket whose peer has already closed the connection raises SIGPIPE, which by
 * default terminates the whole process. Ignoring it process-wide (once) is the standard way for a
 * sockets-based library to ensure such writes surface as an ordinary EPIPE/SSL_write error instead,
 * without which any connection teardown race could kill an unrelated host application outright.
 */
static void ignore_sigpipe(void) {
    signal(SIGPIPE, SIG_IGN);
}

static void free_received_buffer_item(void *item);
static void free_disconnected_buffer_item(void *item);

/* Captures fd's remote TCP endpoint into *out_identity's host/port fields. */
static void capture_remote_endpoint(int fd, oft_identity *out_identity) {
    struct sockaddr_storage address;
    socklen_t address_length = sizeof(address);
    if (getpeername(fd, (struct sockaddr *)&address, &address_length) != 0) {
        return;
    }

    if (address.ss_family == AF_INET) {
        struct sockaddr_in *v4 = (struct sockaddr_in *)&address;
        inet_ntop(AF_INET, &v4->sin_addr, out_identity->host, sizeof(out_identity->host));
        out_identity->port = ntohs(v4->sin_port);
    } else if (address.ss_family == AF_INET6) {
        struct sockaddr_in6 *v6 = (struct sockaddr_in6 *)&address;
        inet_ntop(AF_INET6, &v6->sin6_addr, out_identity->host, sizeof(out_identity->host));
        out_identity->port = ntohs(v6->sin6_port);
    }
}

static oft_connection *connection_alloc(int fd, SSL_CTX *ssl_ctx, int owns_ssl_ctx, int is_client, const char *target_host,
                                         int require_client_cert, size_t max_packet_data_size, long rekey_interval_ms,
                                         int insecure, long poll_interval_ms, long poll_timeout_ms) {
    pthread_once(&g_sigpipe_once, ignore_sigpipe);

    oft_connection *connection = calloc(1, sizeof(oft_connection));
    if (!connection) {
        return NULL;
    }

    connection->fd = fd;
    connection->ssl_ctx = ssl_ctx;
    connection->owns_ssl_ctx = owns_ssl_ctx;
    connection->is_client = is_client;
    connection->target_host = target_host ? strdup(target_host) : NULL;
    connection->require_client_cert = require_client_cert;
    connection->max_packet_data_size = max_packet_data_size > 0 ? max_packet_data_size : OFT_DEFAULT_MAX_PACKET_DATA_SIZE;
    connection->rekey_interval_ms = rekey_interval_ms;
    connection->insecure = insecure;
    connection->poll_interval_ms = poll_interval_ms > 0 ? poll_interval_ms : OFT_DEFAULT_POLL_INTERVAL_MS;
    connection->poll_timeout_ms = poll_timeout_ms > 0 ? poll_timeout_ms : OFT_DEFAULT_POLL_TIMEOUT_MS;
    connection->identity.info = strdup("");
    capture_remote_endpoint(fd, &connection->identity);

    pthread_mutex_init(&connection->outbound_lock, NULL);
    sem_init(&connection->send_signal, 0, 0);
    sem_init(&connection->write_permit, 0, 1);
    pthread_mutex_init(&connection->receipt_lock, NULL);
    pthread_mutex_init(&connection->rekey_queue_lock, NULL);
    pthread_mutex_init(&connection->timestamp_lock, NULL);
    pthread_mutex_init(&connection->acknowledged_callback_lock, NULL);
    atomic_init(&connection->closed, 0);
    atomic_init(&connection->has_pending_inbound_message, 0);
    oft_event_buffer_init(&connection->received_buffer, free_received_buffer_item);
    oft_event_buffer_init(&connection->disconnected_buffer, free_disconnected_buffer_item);

    return connection;
}

/* ---- TLS setup ---- */

static SSL *create_ssl(oft_connection *connection, char *error_buffer, size_t error_buffer_size) {
    SSL *ssl = SSL_new(connection->ssl_ctx);
    if (!ssl) {
        describe_ssl_error(error_buffer, error_buffer_size);
        return NULL;
    }

    /* TLS 1.3 is the only version OFT ever negotiates (see Docs/OFT.md §1). */
    SSL_set_min_proto_version(ssl, TLS1_3_VERSION);
    SSL_set_max_proto_version(ssl, TLS1_3_VERSION);

    BIO *bio = BIO_new_socket(connection->fd, BIO_NOCLOSE);
    if (!bio) {
        describe_ssl_error(error_buffer, error_buffer_size);
        SSL_free(ssl);
        return NULL;
    }

    SSL_set_bio(ssl, bio, bio);

    if (connection->is_client) {
        SSL_set_connect_state(ssl);
        if (connection->target_host) {
            SSL_set_tlsext_host_name(ssl, connection->target_host);
        }
    } else {
        SSL_set_accept_state(ssl);
        if (connection->require_client_cert) {
            SSL_set_verify(ssl, SSL_VERIFY_PEER | SSL_VERIFY_FAIL_IF_NO_PEER_CERT, NULL);
        }
    }

    if (SSL_do_handshake(ssl) != 1) {
        describe_ssl_error(error_buffer, error_buffer_size);
        SSL_free(ssl);
        return NULL;
    }

    /* SSL_get1_peer_certificate returns an owned reference (or NULL if none was presented, e.g. the
     * server's view of a connection established under OFT_SECURITY_MODE_SERVER_AUTHENTICATION,
     * which never requests one from the client) - freed via X509_free() when the connection closes. */
    connection->identity.certificate = SSL_get1_peer_certificate(ssl);

    return ssl;
}

/* ---- Inbound buffer management (receive thread only) ---- */

static oft_inbound_buffer *find_inbound_buffer(oft_connection *connection, int priority, int create) {
    for (size_t i = 0; i < connection->inbound_buffer_count; i++) {
        if (connection->inbound_buffers[i].priority == priority) {
            return &connection->inbound_buffers[i];
        }
    }

    if (!create) {
        return NULL;
    }

    if (connection->inbound_buffer_count == connection->inbound_buffer_capacity) {
        size_t new_capacity = connection->inbound_buffer_capacity == 0 ? 4 : connection->inbound_buffer_capacity * 2;
        oft_inbound_buffer *grown = realloc(connection->inbound_buffers, new_capacity * sizeof(oft_inbound_buffer));
        if (!grown) {
            return NULL;
        }

        connection->inbound_buffers = grown;
        connection->inbound_buffer_capacity = new_capacity;
    }

    oft_inbound_buffer *buffer = &connection->inbound_buffers[connection->inbound_buffer_count++];
    buffer->priority = priority;
    buffer->chunks = NULL;
    buffer->chunk_lengths = NULL;
    buffer->chunk_count = 0;
    buffer->chunk_capacity = 0;
    atomic_store(&connection->has_pending_inbound_message, 1);
    return buffer;
}

static int inbound_buffer_add_chunk(oft_inbound_buffer *buffer, const uint8_t *data, size_t length) {
    if (buffer->chunk_count == buffer->chunk_capacity) {
        size_t new_capacity = buffer->chunk_capacity == 0 ? 4 : buffer->chunk_capacity * 2;
        uint8_t **new_chunks = realloc(buffer->chunks, new_capacity * sizeof(uint8_t *));
        size_t *new_lengths = realloc(buffer->chunk_lengths, new_capacity * sizeof(size_t));
        if (!new_chunks || !new_lengths) {
            return -1;
        }

        buffer->chunks = new_chunks;
        buffer->chunk_lengths = new_lengths;
        buffer->chunk_capacity = new_capacity;
    }

    uint8_t *copy = NULL;
    if (length > 0) {
        copy = malloc(length);
        if (!copy) {
            return -1;
        }

        memcpy(copy, data, length);
    }

    buffer->chunks[buffer->chunk_count] = copy;
    buffer->chunk_lengths[buffer->chunk_count] = length;
    buffer->chunk_count++;
    return 0;
}

static void inbound_buffer_free_contents(oft_inbound_buffer *buffer) {
    for (size_t i = 0; i < buffer->chunk_count; i++) {
        free(buffer->chunks[i]);
    }

    free(buffer->chunks);
    free(buffer->chunk_lengths);
}

static void remove_inbound_buffer_at(oft_connection *connection, size_t index) {
    inbound_buffer_free_contents(&connection->inbound_buffers[index]);
    memmove(&connection->inbound_buffers[index], &connection->inbound_buffers[index + 1],
            (connection->inbound_buffer_count - index - 1) * sizeof(oft_inbound_buffer));
    connection->inbound_buffer_count--;
    atomic_store(&connection->has_pending_inbound_message, connection->inbound_buffer_count > 0);
}

static int find_highest_inbound_priority_index(oft_connection *connection) {
    int best_index = -1;
    for (size_t i = 0; i < connection->inbound_buffer_count; i++) {
        if (best_index == -1 || connection->inbound_buffers[i].priority > connection->inbound_buffers[(size_t)best_index].priority) {
            best_index = (int)i;
        }
    }

    return best_index;
}

/* ---- Outbound queue management (guarded by outbound_lock) ---- */

static oft_priority_queue *find_or_create_queue(oft_connection *connection, int priority) {
    for (oft_priority_queue *queue = connection->queues; queue; queue = queue->next) {
        if (queue->priority == priority) {
            return queue;
        }
    }

    oft_priority_queue *queue = calloc(1, sizeof(oft_priority_queue));
    if (!queue) {
        return NULL;
    }

    queue->priority = priority;
    queue->next = connection->queues;
    connection->queues = queue;
    return queue;
}

static void queue_append(oft_priority_queue *queue, oft_pending_message *message) {
    message->next_in_queue = NULL;
    if (queue->tail) {
        queue->tail->next_in_queue = message;
    } else {
        queue->head = message;
    }

    queue->tail = message;
}

static void queue_pop_front(oft_priority_queue *queue) {
    if (!queue->head) {
        return;
    }

    queue->head = queue->head->next_in_queue;
    if (!queue->head) {
        queue->tail = NULL;
    }
}

static int queue_remove(oft_priority_queue *queue, oft_pending_message *message) {
    oft_pending_message *prev = NULL;
    for (oft_pending_message *cur = queue->head; cur; prev = cur, cur = cur->next_in_queue) {
        if (cur == message) {
            if (prev) {
                prev->next_in_queue = cur->next_in_queue;
            } else {
                queue->head = cur->next_in_queue;
            }

            if (queue->tail == cur) {
                queue->tail = prev;
            }

            return 1;
        }
    }

    return 0;
}

static oft_pending_message *pick_next_message(oft_connection *connection) {
    oft_priority_queue *best = NULL;
    for (oft_priority_queue *queue = connection->queues; queue; queue = queue->next) {
        if (queue->head && (!best || queue->priority > best->priority)) {
            best = queue;
        }
    }

    return best ? best->head : NULL;
}

static oft_pending_message *registry_find(oft_connection *connection, uint64_t id) {
    for (oft_pending_message *message = connection->registry; message; message = message->next_in_registry) {
        if (message->id == id) {
            return message;
        }
    }

    return NULL;
}

static oft_pending_message *registry_remove(oft_connection *connection, uint64_t id) {
    oft_pending_message *prev = NULL;
    for (oft_pending_message *cur = connection->registry; cur; prev = cur, cur = cur->next_in_registry) {
        if (cur->id == id) {
            if (prev) {
                prev->next_in_registry = cur->next_in_registry;
            } else {
                connection->registry = cur->next_in_registry;
            }

            return cur;
        }
    }

    return NULL;
}

static void free_pending_message(oft_pending_message *message) {
    oft_event_destroy(&message->completed);
    free(message->data);
    free(message);
}

/* ---- Rekey ---- */

/*
 * Drains every rekey request queued by oft_connection_rekey() and requests a TLS 1.3 KeyUpdate
 * for each, in place on the existing session (see Docs/OFT.md §8). Only ever called from the
 * receive thread: OpenSSL's SSL_read() and the write path used to flush a KeyUpdate
 * (SSL_key_update/SSL_do_handshake) hold no lock against each other, and receiving an inbound
 * KeyUpdate that itself requests a reciprocal update can make SSL_read() write to the same
 * connection - so running our own outbound KeyUpdate from any thread other than the one already
 * reading this connection can corrupt it (observed as a spurious "bad record mac" when both peers
 * happen to rekey at nearly the same moment). Running it on the receive thread instead guarantees
 * the two never execute concurrently, since one thread can't do both at once. write_permit is
 * still acquired around the actual update so it can't interleave with an application packet write
 * from the send thread either.
 */
static void process_pending_rekeys(oft_connection *connection) {
    while (1) {
        pthread_mutex_lock(&connection->rekey_queue_lock);
        oft_rekey_request *request = connection->rekey_queue_head;
        if (request) {
            connection->rekey_queue_head = request->next;
            if (!connection->rekey_queue_head) {
                connection->rekey_queue_tail = NULL;
            }
        }
        pthread_mutex_unlock(&connection->rekey_queue_lock);

        if (!request) {
            return;
        }

        sem_wait(&connection->write_permit);
        int result = (SSL_key_update(connection->ssl, SSL_KEY_UPDATE_REQUESTED) == 1 &&
                      SSL_do_handshake(connection->ssl) == 1)
                ? OFT_OK
                : OFT_ERROR;
        sem_post(&connection->write_permit);

        oft_event_signal(&request->completed, result);
    }
}

int oft_connection_rekey(oft_connection *connection) {
    /* No-op: an insecure (non-TLS) connection has no TLS session to rekey. */
    if (connection->insecure) {
        return OFT_OK;
    }

    if (atomic_load(&connection->closed)) {
        return OFT_ERROR_CLOSED;
    }

    oft_rekey_request *request = calloc(1, sizeof(oft_rekey_request));
    if (!request) {
        return OFT_ERROR;
    }

    oft_event_init(&request->completed);

    pthread_mutex_lock(&connection->rekey_queue_lock);
    request->next = NULL;
    if (connection->rekey_queue_tail) {
        connection->rekey_queue_tail->next = request;
    } else {
        connection->rekey_queue_head = request;
    }
    connection->rekey_queue_tail = request;
    pthread_mutex_unlock(&connection->rekey_queue_lock);

    int result = oft_event_wait(&request->completed);
    oft_event_destroy(&request->completed);
    free(request);
    return result;
}

/* ---- Message send path ---- */

/* An item buffered on received_buffer. */
typedef struct {
    oft_connection *connection;
    uint8_t *data;
    size_t length;
} oft_received_buffer_item;

static void free_received_buffer_item(void *item) {
    oft_received_buffer_item *received = item;
    free(received->data);
    free(received);
}

static void dispatch_received_buffer_item(void *user_data, void *item) {
    oft_received_target *target = user_data;
    oft_received_buffer_item *received = item;

    if (target->callback) {
        target->callback(received->connection, received->data, received->length, target->user_data);
    } else {
        free(received->data);
    }

    free(received);
}

static void raise_received(oft_connection *connection, uint8_t *data, size_t length) {
    oft_received_buffer_item *received = malloc(sizeof(oft_received_buffer_item));
    if (!received) {
        free(data);
        return;
    }

    received->connection = connection;
    received->data = data;
    received->length = length;
    oft_event_buffer_raise(&connection->received_buffer, received);
}

/* An item buffered on disconnected_buffer. */
typedef struct {
    oft_connection *connection;
    char *error_message;
} oft_disconnected_buffer_item;

static void free_disconnected_buffer_item(void *item) {
    oft_disconnected_buffer_item *disconnected = item;
    free(disconnected->error_message);
    free(disconnected);
}

static void dispatch_disconnected_buffer_item(void *user_data, void *item) {
    oft_disconnected_target *target = user_data;
    oft_disconnected_buffer_item *disconnected = item;

    if (target->callback) {
        target->callback(disconnected->connection, disconnected->error_message, target->user_data);
    }

    free(disconnected->error_message);
    free(disconnected);
}

static void raise_disconnected(oft_connection *connection, const char *error_message) {
    oft_disconnected_buffer_item *disconnected = malloc(sizeof(oft_disconnected_buffer_item));
    if (!disconnected) {
        return;
    }

    disconnected->connection = connection;
    disconnected->error_message = error_message ? strdup(error_message) : NULL;
    oft_event_buffer_raise(&connection->disconnected_buffer, disconnected);
}

static int send_next_packet(oft_connection *connection, oft_pending_message *message) {
    oft_packet packet;
    int finishes_message;

    if (message->cancel_requested && message->started) {
        oft_packet_init(&packet, 1, NULL, 0);
        finishes_message = 1;
    } else if (!message->started && message->length <= connection->max_packet_data_size) {
        oft_packet_init(&packet, 3, message->data, message->length);
        message->started = 1;
        finishes_message = 1;
    } else {
        message->started = 1;
        size_t remaining = message->length - message->bytes_sent;
        size_t chunk_size = remaining < connection->max_packet_data_size ? remaining : connection->max_packet_data_size;
        int is_last = message->bytes_sent + chunk_size >= message->length;

        oft_packet_init(&packet, is_last ? 0 : (uint32_t)(message->priority + 4), message->data + message->bytes_sent, chunk_size);
        message->bytes_sent += chunk_size;
        finishes_message = is_last;
    }

    oft_buffer buffer;
    oft_buffer_init(&buffer);
    int encode_result = oft_packet_encode(&packet, &buffer);
    oft_packet_free(&packet);
    if (encode_result != 0) {
        oft_buffer_free(&buffer);
        return -1;
    }

    oft_event receipt;
    oft_event_init(&receipt);

    pthread_mutex_lock(&connection->receipt_lock);
    connection->outstanding_receipt = &receipt;
    pthread_mutex_unlock(&connection->receipt_lock);

    int write_result = oft_frame_stream_write(&connection->frame_stream, buffer.data, buffer.length);
    oft_buffer_free(&buffer);

    if (write_result != 0) {
        oft_event_destroy(&receipt);
        return -1;
    }

    pthread_mutex_lock(&connection->timestamp_lock);
    now(&connection->last_sent_at);
    pthread_mutex_unlock(&connection->timestamp_lock);

    int receipt_result = oft_event_wait(&receipt);
    oft_event_destroy(&receipt);

    if (receipt_result != OFT_OK) {
        /* The connection closed while this packet's Receipt was outstanding (see close_connection).
         * The message's completion was already (or will be) signaled by close_connection /
         * oft_connection_close; don't touch its queue/registry linkage here, since those are only
         * safe to mutate up until the connection starts tearing them down. */
        return -1;
    }

    if (finishes_message) {
        /* Only pop the send queue here; the registry entry itself stays until
         * oft_connection_wait() (or the connection's final close cleanup) removes and frees it, so
         * a caller that calls oft_connection_wait() after the message has already completed can
         * still find it and retrieve its already-signaled result rather than racing this thread. */
        pthread_mutex_lock(&connection->outbound_lock);
        oft_priority_queue *queue = find_or_create_queue(connection, message->priority);
        queue_pop_front(queue);
        pthread_mutex_unlock(&connection->outbound_lock);

        if (message->cancel_requested) {
            oft_event_signal(&message->completed, OFT_ERROR_CANCELLED);
        } else {
            oft_event_signal(&message->completed, OFT_OK);

            if (message->tag) {
                pthread_mutex_lock(&connection->acknowledged_callback_lock);
                oft_acknowledged_callback callback = connection->acknowledged_callback;
                void *user_data = connection->acknowledged_callback_user_data;
                pthread_mutex_unlock(&connection->acknowledged_callback_lock);

                if (callback) {
                    callback(message->tag, user_data);
                }
            }
        }
    }

    return 0;
}

static void *send_loop(void *arg) {
    oft_connection *connection = arg;

    while (!atomic_load(&connection->closed)) {
        sem_wait(&connection->send_signal);
        if (atomic_load(&connection->closed)) {
            break;
        }

        while (1) {
            pthread_mutex_lock(&connection->outbound_lock);
            oft_pending_message *message = pick_next_message(connection);
            pthread_mutex_unlock(&connection->outbound_lock);

            if (!message) {
                break;
            }

            sem_wait(&connection->write_permit);
            if (atomic_load(&connection->closed)) {
                sem_post(&connection->write_permit);
                break;
            }

            int result = send_next_packet(connection, message);
            sem_post(&connection->write_permit);

            if (result != 0) {
                return NULL;
            }
        }
    }

    return NULL;
}

/* ---- Message receive path ---- */

static int complete_inbound_message(oft_connection *connection, const uint8_t *final_chunk, size_t final_length, int cancelled) {
    int index = find_highest_inbound_priority_index(connection);
    if (index < 0) {
        return -1; /* protocol violation: no pending message on any channel */
    }

    oft_inbound_buffer *buffer = &connection->inbound_buffers[index];

    if (cancelled) {
        remove_inbound_buffer_at(connection, (size_t)index);
        return 0;
    }

    if (final_length > 0 && inbound_buffer_add_chunk(buffer, final_chunk, final_length) != 0) {
        return -1;
    }

    size_t total_length = 0;
    for (size_t i = 0; i < buffer->chunk_count; i++) {
        total_length += buffer->chunk_lengths[i];
    }

    uint8_t *message = total_length > 0 ? malloc(total_length) : NULL;
    if (total_length > 0 && !message) {
        return -1;
    }

    size_t offset = 0;
    for (size_t i = 0; i < buffer->chunk_count; i++) {
        if (buffer->chunk_lengths[i] > 0) {
            memcpy(message + offset, buffer->chunks[i], buffer->chunk_lengths[i]);
            offset += buffer->chunk_lengths[i];
        }
    }

    remove_inbound_buffer_at(connection, (size_t)index);
    raise_received(connection, message, total_length);
    return 0;
}

static int handle_packet(oft_connection *connection, const oft_packet *packet) {
    if (packet->control == 2) {
        pthread_mutex_lock(&connection->receipt_lock);
        oft_event *receipt = connection->outstanding_receipt;
        connection->outstanding_receipt = NULL;
        pthread_mutex_unlock(&connection->receipt_lock);

        if (receipt) {
            oft_event_signal(receipt, OFT_OK);
        }

        return 0;
    }

    switch (packet->control) {
        case 3: {
            uint8_t *data = NULL;
            if (packet->length > 0) {
                data = malloc(packet->length);
                if (!data) {
                    return -1;
                }

                memcpy(data, packet->data, packet->length);
            }

            raise_received(connection, data, packet->length);
            break;
        }
        case 0:
            if (complete_inbound_message(connection, packet->data, packet->length, 0) != 0) {
                return -1;
            }

            break;
        case 1:
            if (complete_inbound_message(connection, NULL, 0, 1) != 0) {
                return -1;
            }

            break;
        default: {
            int priority = (int)packet->control - 4;
            if (priority < 0) {
                return -1;
            }

            oft_inbound_buffer *buffer = find_inbound_buffer(connection, priority, 1);
            if (!buffer || inbound_buffer_add_chunk(buffer, packet->data, packet->length) != 0) {
                return -1;
            }

            break;
        }
    }

    oft_packet receipt_packet;
    oft_packet_init(&receipt_packet, 2, NULL, 0);
    oft_buffer buffer;
    oft_buffer_init(&buffer);
    int encode_result = oft_packet_encode(&receipt_packet, &buffer);
    oft_packet_free(&receipt_packet);

    int write_result = -1;
    if (encode_result == 0) {
        write_result = oft_frame_stream_write(&connection->frame_stream, buffer.data, buffer.length);
    }

    oft_buffer_free(&buffer);
    if (write_result != 0) {
        return -1;
    }

    return 0;
}

static void close_connection(oft_connection *connection, const char *error_message);

static void *receive_loop(void *arg) {
    oft_connection *connection = arg;

    while (1) {
        process_pending_rekeys(connection);

        uint8_t *data;
        size_t length;
        int read_result = oft_frame_stream_read(&connection->frame_stream, &data, &length);

        if (read_result == 0) {
            close_connection(connection, NULL);
            return NULL;
        }

        if (read_result < 0) {
            if (!atomic_load(&connection->closed)) {
                close_connection(connection, "connection read failed");
            }

            return NULL;
        }

        pthread_mutex_lock(&connection->timestamp_lock);
        now(&connection->last_inbound_activity);
        pthread_mutex_unlock(&connection->timestamp_lock);

        if (length == 0) {
            /* A zero-length frame is a Poll (see Docs/OFT.md §4 and §10) - deliberately not a
             * dedicated control value, since protobuf's proto3 wire format never emits any bytes
             * for a message with every field at its default value, so an all-default Packet (and
             * only that) already serializes to zero bytes. It deliberately doesn't count as
             * last_received_at activity - see last_inbound_activity's declaration - so it can't
             * mask an otherwise-unused connection as active to an oft_peer's idle-eviction. */
            continue;
        }

        oft_packet packet;
        int decode_result = oft_packet_decode(data, length, &packet);
        free(data);

        if (decode_result != 0) {
            if (!atomic_load(&connection->closed)) {
                close_connection(connection, "received a malformed packet");
            }

            return NULL;
        }

        pthread_mutex_lock(&connection->timestamp_lock);
        now(&connection->last_received_at);
        pthread_mutex_unlock(&connection->timestamp_lock);

        int handle_result = handle_packet(connection, &packet);
        oft_packet_free(&packet);

        if (handle_result != 0) {
            if (!atomic_load(&connection->closed)) {
                close_connection(connection, "protocol violation");
            }

            return NULL;
        }
    }
}

/* ---- Rekey timer ---- */

static void *rekey_timer_loop(void *arg) {
    oft_connection *connection = arg;
    long remaining_ms = connection->rekey_interval_ms;

    while (!atomic_load(&connection->closed)) {
        long step_ms = remaining_ms < 50 ? remaining_ms : 50;
        struct timespec sleep_time = {step_ms / 1000, (step_ms % 1000) * 1000000L};
        nanosleep(&sleep_time, NULL);

        remaining_ms -= step_ms;
        if (remaining_ms > 0) {
            continue;
        }

        remaining_ms = connection->rekey_interval_ms;
        if (!atomic_load(&connection->closed)) {
            oft_connection_rekey(connection);
        }
    }

    return NULL;
}

/* ---- Poll timer (see Docs/OFT.md §10) ---- */

static void *poll_timer_loop(void *arg) {
    oft_connection *connection = arg;
    long remaining_ms = connection->poll_interval_ms;

    while (!atomic_load(&connection->closed)) {
        long step_ms = remaining_ms < 50 ? remaining_ms : 50;
        struct timespec sleep_time = {step_ms / 1000, (step_ms % 1000) * 1000000L};
        nanosleep(&sleep_time, NULL);

        remaining_ms -= step_ms;
        if (remaining_ms > 0) {
            continue;
        }

        remaining_ms = connection->poll_interval_ms;
        if (atomic_load(&connection->closed)) {
            break;
        }

        /* Only sent when write_permit is immediately available (never waited on): skipping a tick
         * when busy is harmless, since real application traffic already keeps the peer's watchdog
         * satisfied whenever the permit is in heavy use, and an otherwise-idle connection always
         * has the permit free. */
        if (sem_trywait(&connection->write_permit) == 0) {
            /* A bare zero-length frame (see Docs/OFT.md §4 and §10) - no Packet encoding needed, and
             * no control value required, since proto3's default-value omission already makes an
             * all-default Packet serialize to zero bytes.
             *
             * Best-effort: a single failed poll write isn't itself fatal - the watchdog check below
             * is what detects a genuinely dead connection, and the next tick tries again. */
            oft_frame_stream_write(&connection->frame_stream, NULL, 0);
            sem_post(&connection->write_permit);
        }

        struct timespec now_time;
        struct timespec last_activity;
        now(&now_time);
        pthread_mutex_lock(&connection->timestamp_lock);
        last_activity = connection->last_inbound_activity;
        pthread_mutex_unlock(&connection->timestamp_lock);

        long elapsed_ms = (now_time.tv_sec - last_activity.tv_sec) * 1000L +
                (now_time.tv_nsec - last_activity.tv_nsec) / 1000000L;
        if (elapsed_ms > connection->poll_timeout_ms && !atomic_load(&connection->closed)) {
            close_connection(connection, "no poll or message was received from the peer within the configured timeout");
        }
    }

    return NULL;
}

/* ---- Handshake ---- */

static int complete_handshake(
        oft_connection *connection, const char *info,
        oft_connection_validation_callback connection_validation, void *connection_validation_user_data,
        char *error_buffer, size_t error_buffer_size) {
    if (connection->insecure) {
        oft_frame_stream_init_plain(&connection->frame_stream, connection->fd);
    } else {
        oft_frame_stream_init(&connection->frame_stream, connection->ssl);
    }

    oft_hail our_hail;
    oft_hail_init(&our_hail, OFT_PROTOCOL_VERSION, info ? info : "");
    oft_buffer hail_buffer;
    oft_buffer_init(&hail_buffer);
    int encode_result = oft_hail_encode(&our_hail, &hail_buffer);
    oft_hail_free(&our_hail);

    if (encode_result != 0) {
        oft_buffer_free(&hail_buffer);
        set_error(error_buffer, error_buffer_size, "failed to encode hail");
        return -1;
    }

    if (oft_frame_stream_write(&connection->frame_stream, hail_buffer.data, hail_buffer.length) != 0) {
        oft_buffer_free(&hail_buffer);
        set_error(error_buffer, error_buffer_size, "failed to send hail");
        return -1;
    }

    oft_buffer_free(&hail_buffer);

    uint8_t *received_data;
    size_t received_length;
    int read_result = oft_frame_stream_read(&connection->frame_stream, &received_data, &received_length);
    if (read_result <= 0) {
        set_error(error_buffer, error_buffer_size, "connection closed before completing the OFT hail handshake");
        return -1;
    }

    oft_hail received_hail;
    int decode_result = oft_hail_decode(received_data, received_length, &received_hail);
    free(received_data);

    if (decode_result != 0) {
        set_error(error_buffer, error_buffer_size, "received a malformed hail");
        return -1;
    }

    if (strcmp(received_hail.version, OFT_PROTOCOL_VERSION) != 0) {
        snprintf(error_buffer, error_buffer_size, "incompatible OFT protocol version '%s'", received_hail.version);
        oft_hail_free(&received_hail);
        return -1;
    }

    free(connection->identity.info);
    connection->identity.info = strdup(received_hail.info);
    oft_hail_free(&received_hail);

    if (connection_validation) {
        X509 *certificate = connection->ssl ? SSL_get1_peer_certificate(connection->ssl) : NULL;
        STACK_OF(X509) *chain = connection->ssl ? SSL_get_peer_cert_chain(connection->ssl) : NULL;
        long verify_result = connection->ssl ? SSL_get_verify_result(connection->ssl) : X509_V_OK;

        int accepted = connection_validation(&connection->identity, certificate, chain, verify_result, connection_validation_user_data);
        X509_free(certificate);

        if (!accepted) {
            set_error(error_buffer, error_buffer_size, "the connection was rejected by connection_validation");
            return -1;
        }
    }

    pthread_mutex_lock(&connection->timestamp_lock);
    now(&connection->connected_at);
    now(&connection->last_sent_at);
    now(&connection->last_received_at);
    now(&connection->last_inbound_activity);
    pthread_mutex_unlock(&connection->timestamp_lock);

    return 0;
}

/*
 * Starts this connection's background threads: the receive loop (which begins delivering inbound
 * messages and the disconnected notification to its callbacks), the send loop, and (if configured)
 * the automatic rekey timer. Safe to call immediately after establishment, with no need to wait for
 * a caller to call oft_connection_set_received_callback()/oft_connection_set_disconnected_callback()
 * first: received_buffer/disconnected_buffer each hold onto everything raised until a non-NULL
 * callback is first assigned, so nothing is ever lost between establishment and that call - see
 * oft_event_buffer's own doc comment.
 */
void oft_connection_start_processing(oft_connection *connection) {
    pthread_create(&connection->receive_thread, NULL, receive_loop, connection);
    pthread_create(&connection->send_thread, NULL, send_loop, connection);
    connection->threads_started = 1;

    /* Rekeying requires a TLS session to rekey, so the timer is never started for an insecure
     * connection, even if rekey_interval_ms happens to be set. */
    if (!connection->insecure && connection->rekey_interval_ms > 0) {
        connection->rekey_timer_active = 1;
        pthread_create(&connection->rekey_timer_thread, NULL, rekey_timer_loop, connection);
    }

    connection->poll_timer_active = 1;
    pthread_create(&connection->poll_timer_thread, NULL, poll_timer_loop, connection);
}

oft_connection *oft_connection_establish_as_client(
        int fd, const char *target_host, SSL_CTX *ssl_ctx, const oft_connect_options *options,
        char *error_buffer, size_t error_buffer_size) {
    int insecure = options->security_mode == OFT_SECURITY_MODE_TRUSTED;

    /* Under OFT_SECURITY_MODE_SECURE this connection accepts whatever certificate the accepting
     * side presents unconditionally (there's nothing meaningful to validate an ephemeral
     * certificate against): it creates and owns its own throwaway trust-all ssl_ctx per
     * connection rather than using the caller-supplied one (ignored in this mode). For
     * AUTHENTICATION/DUAL_AUTHENTICATION, ssl_ctx is the caller-supplied one, already validated
     * non-NULL by oft_connect() before this is ever reached; not owned by this connection. */
    SSL_CTX *effective_ssl_ctx = ssl_ctx;
    int owns_ssl_ctx = 0;
    if (!insecure && options->security_mode == OFT_SECURITY_MODE_SECURE) {
        effective_ssl_ctx = oft_ephemeral_ssl_ctx_create_trust_all();
        if (!effective_ssl_ctx) {
            set_error(error_buffer, error_buffer_size, "failed to create trust-all SSL_CTX");
            return NULL;
        }

        owns_ssl_ctx = 1;
    }

    oft_connection *connection = connection_alloc(
            fd, effective_ssl_ctx, owns_ssl_ctx, 1, target_host, 0,
            options->max_packet_data_size, options->rekey_interval_ms,
            insecure, options->poll_interval_ms, options->poll_timeout_ms);
    if (!connection) {
        if (owns_ssl_ctx) {
            SSL_CTX_free(effective_ssl_ctx);
        }

        set_error(error_buffer, error_buffer_size, "out of memory");
        return NULL;
    }

    if (!insecure) {
        SSL *ssl = create_ssl(connection, error_buffer, error_buffer_size);
        if (!ssl) {
            oft_connection_close(connection);
            return NULL;
        }

        connection->ssl = ssl;
    }

    if (complete_handshake(connection, options->info, options->connection_validation, options->connection_validation_user_data, error_buffer, error_buffer_size) != 0) {
        oft_connection_close(connection);
        return NULL;
    }

    return connection;
}

oft_connection *oft_connection_establish_as_server(
        int fd, SSL_CTX *ssl_ctx, const oft_host_options *options,
        char *error_buffer, size_t error_buffer_size) {
    int insecure = options->security_mode == OFT_SECURITY_MODE_TRUSTED;
    int require_client_cert = options->security_mode == OFT_SECURITY_MODE_DUAL_AUTHENTICATION;

    /* By this point ssl_ctx is always resolved for OFT_SECURITY_MODE_SECURE: oft_host() has
     * already replaced it with a listener-lifetime ephemeral context; for
     * OFT_SECURITY_MODE_SERVER_AUTHENTICATION/OFT_SECURITY_MODE_DUAL_AUTHENTICATION, it's the
     * caller-supplied one, already validated non-NULL by oft_host() before this is ever reached.
     * Never owned by the connection itself - the listener (or, in
     * SERVER_AUTHENTICATION/DUAL_AUTHENTICATION mode, the caller) owns it. */
    oft_connection *connection = connection_alloc(
            fd, ssl_ctx, 0, 0, NULL, require_client_cert,
            options->max_packet_data_size, options->rekey_interval_ms,
            insecure, options->poll_interval_ms, options->poll_timeout_ms);
    if (!connection) {
        set_error(error_buffer, error_buffer_size, "out of memory");
        return NULL;
    }

    if (!insecure) {
        SSL *ssl = create_ssl(connection, error_buffer, error_buffer_size);
        if (!ssl) {
            oft_connection_close(connection);
            return NULL;
        }

        connection->ssl = ssl;
    }

    if (complete_handshake(connection, options->info, options->connection_validation, options->connection_validation_user_data, error_buffer, error_buffer_size) != 0) {
        oft_connection_close(connection);
        return NULL;
    }

    return connection;
}

/* ---- Public API ---- */

int oft_connection_send(oft_connection *connection, const uint8_t *data, size_t length, int priority, void *tag, uint64_t *out_message_id) {
    if (atomic_load(&connection->closed)) {
        return OFT_ERROR_CLOSED;
    }

    if (priority < 0) {
        return OFT_ERROR;
    }

    oft_pending_message *message = calloc(1, sizeof(oft_pending_message));
    if (!message) {
        return OFT_ERROR;
    }

    message->priority = priority;
    message->length = length;
    message->tag = tag;
    if (length > 0) {
        message->data = malloc(length);
        if (!message->data) {
            free(message);
            return OFT_ERROR;
        }

        memcpy(message->data, data, length);
    }

    oft_event_init(&message->completed);

    pthread_mutex_lock(&connection->outbound_lock);
    message->id = ++connection->next_message_id;
    oft_priority_queue *queue = find_or_create_queue(connection, priority);
    if (!queue) {
        pthread_mutex_unlock(&connection->outbound_lock);
        free_pending_message(message);
        return OFT_ERROR;
    }

    queue_append(queue, message);
    message->next_in_registry = connection->registry;
    connection->registry = message;
    pthread_mutex_unlock(&connection->outbound_lock);

    if (out_message_id) {
        *out_message_id = message->id;
    }

    sem_post(&connection->send_signal);
    return OFT_OK;
}

int oft_connection_wait(oft_connection *connection, uint64_t message_id) {
    pthread_mutex_lock(&connection->outbound_lock);
    oft_pending_message *message = registry_remove(connection, message_id);
    pthread_mutex_unlock(&connection->outbound_lock);

    if (!message) {
        return OFT_ERROR;
    }

    int result = oft_event_wait(&message->completed);
    free_pending_message(message);
    return result;
}

void oft_connection_cancel(oft_connection *connection, uint64_t message_id) {
    pthread_mutex_lock(&connection->outbound_lock);
    oft_pending_message *message = registry_find(connection, message_id);
    if (!message) {
        pthread_mutex_unlock(&connection->outbound_lock);
        return;
    }

    /*
     * Note: the message stays in the registry either way - only oft_connection_wait() or the
     * connection's final close cleanup ever remove and free a registry entry, so ownership of when
     * it's freed is always unambiguous.
     */
    if (!message->started) {
        oft_priority_queue *queue = find_or_create_queue(connection, message->priority);
        if (queue && queue_remove(queue, message)) {
            pthread_mutex_unlock(&connection->outbound_lock);
            oft_event_signal(&message->completed, OFT_ERROR_CANCELLED);
            return;
        }
    }

    message->cancel_requested = 1;
    pthread_mutex_unlock(&connection->outbound_lock);
    sem_post(&connection->send_signal);
}

void oft_connection_set_received_callback(oft_connection *connection, oft_received_callback callback, void *user_data) {
    connection->received_target.callback = callback;
    connection->received_target.user_data = user_data;
    oft_event_buffer_attach(&connection->received_buffer, callback ? dispatch_received_buffer_item : NULL, &connection->received_target);
}

void oft_connection_set_disconnected_callback(oft_connection *connection, oft_disconnected_callback callback, void *user_data) {
    connection->disconnected_target.callback = callback;
    connection->disconnected_target.user_data = user_data;
    oft_event_buffer_attach(&connection->disconnected_buffer, callback ? dispatch_disconnected_buffer_item : NULL, &connection->disconnected_target);
}

void oft_connection_set_acknowledged_callback(oft_connection *connection, oft_acknowledged_callback callback, void *user_data) {
    pthread_mutex_lock(&connection->acknowledged_callback_lock);
    connection->acknowledged_callback = callback;
    connection->acknowledged_callback_user_data = user_data;
    pthread_mutex_unlock(&connection->acknowledged_callback_lock);
}

const oft_identity *oft_connection_identity(oft_connection *connection) {
    return &connection->identity;
}

void oft_connection_connected_at(oft_connection *connection, struct timespec *out_time) {
    pthread_mutex_lock(&connection->timestamp_lock);
    *out_time = connection->connected_at;
    pthread_mutex_unlock(&connection->timestamp_lock);
}

void oft_connection_last_sent_at(oft_connection *connection, struct timespec *out_time) {
    pthread_mutex_lock(&connection->timestamp_lock);
    *out_time = connection->last_sent_at;
    pthread_mutex_unlock(&connection->timestamp_lock);
}

void oft_connection_last_received_at(oft_connection *connection, struct timespec *out_time) {
    pthread_mutex_lock(&connection->timestamp_lock);
    *out_time = connection->last_received_at;
    pthread_mutex_unlock(&connection->timestamp_lock);
}

int oft_connection_has_pending_data(oft_connection *connection) {
    pthread_mutex_lock(&connection->outbound_lock);
    int has_outbound = 0;
    for (oft_priority_queue *queue = connection->queues; queue; queue = queue->next) {
        if (queue->head) {
            has_outbound = 1;
            break;
        }
    }
    pthread_mutex_unlock(&connection->outbound_lock);

    if (has_outbound) {
        return 1;
    }

    return atomic_load(&connection->has_pending_inbound_message);
}

int oft_connection_is_connected(oft_connection *connection) {
    return !atomic_load(&connection->closed);
}

static void close_connection(oft_connection *connection, const char *error_message) {
    int expected = 0;
    if (!atomic_compare_exchange_strong(&connection->closed, &expected, 1)) {
        return;
    }

    /* Unblock whichever single packet (an application message) is currently awaiting its Receipt.
     * Its sender (send_next_packet) checks the signaled result and bails out without touching
     * outbound state on anything other than OFT_OK, so it's safe to do this before taking
     * outbound_lock below. */
    pthread_mutex_lock(&connection->receipt_lock);
    oft_event *outstanding = connection->outstanding_receipt;
    connection->outstanding_receipt = NULL;
    pthread_mutex_unlock(&connection->receipt_lock);

    if (outstanding) {
        oft_event_signal(outstanding, OFT_ERROR_CLOSED);
    }

    pthread_mutex_lock(&connection->rekey_queue_lock);
    oft_rekey_request *cancelled_rekeys = connection->rekey_queue_head;
    connection->rekey_queue_head = NULL;
    connection->rekey_queue_tail = NULL;
    pthread_mutex_unlock(&connection->rekey_queue_lock);

    for (oft_rekey_request *request = cancelled_rekeys; request; request = request->next) {
        oft_event_signal(&request->completed, OFT_ERROR_CLOSED);
    }

    /* Signal every pending message so any oft_connection_wait() call in progress can return, but
     * don't unlink or free them here: the send loop may still be holding a reference to whichever
     * one is currently in flight. It's only safe to free them once both background threads have
     * fully exited, which oft_connection_close() does after joining them. */
    pthread_mutex_lock(&connection->outbound_lock);
    for (oft_pending_message *message = connection->registry; message; message = message->next_in_registry) {
        oft_event_signal(&message->completed, OFT_ERROR_CLOSED);
    }
    pthread_mutex_unlock(&connection->outbound_lock);

    sem_post(&connection->send_signal);

    if (connection->ssl) {
        SSL_shutdown(connection->ssl);
    }

    shutdown(connection->fd, SHUT_RDWR);
    close(connection->fd);

    raise_disconnected(connection, error_message);
}

void oft_connection_disconnect(oft_connection *connection) {
    close_connection(connection, NULL);
}

void oft_connection_close(oft_connection *connection) {
    close_connection(connection, NULL);

    if (connection->threads_started) {
        pthread_join(connection->receive_thread, NULL);
        pthread_join(connection->send_thread, NULL);
    }

    if (connection->rekey_timer_active) {
        pthread_join(connection->rekey_timer_thread, NULL);
    }

    if (connection->poll_timer_active) {
        pthread_join(connection->poll_timer_thread, NULL);
    }

    /* Safe now: both background threads have fully exited, so nothing else in the library can be
     * touching the outbound queues, message registry, or rekey operations concurrently. */
    pthread_mutex_lock(&connection->outbound_lock);
    for (oft_priority_queue *queue = connection->queues; queue;) {
        oft_priority_queue *next_queue = queue->next;
        free(queue);
        queue = next_queue;
    }

    connection->queues = NULL;

    for (oft_pending_message *message = connection->registry; message;) {
        oft_pending_message *next_message = message->next_in_registry;
        free_pending_message(message);
        message = next_message;
    }

    connection->registry = NULL;
    pthread_mutex_unlock(&connection->outbound_lock);

    if (connection->ssl) {
        SSL_free(connection->ssl);
    }

    if (connection->owns_ssl_ctx) {
        SSL_CTX_free(connection->ssl_ctx);
    }

    oft_frame_stream_destroy(&connection->frame_stream);

    for (size_t i = 0; i < connection->inbound_buffer_count; i++) {
        inbound_buffer_free_contents(&connection->inbound_buffers[i]);
    }

    free(connection->inbound_buffers);
    free(connection->target_host);
    free(connection->identity.info);
    X509_free(connection->identity.certificate);

    /* Safe now too, for the same reason as above: the receive thread (the only thread that ever
     * calls oft_event_buffer_raise on these buffers) has fully exited. */
    oft_event_buffer_destroy(&connection->received_buffer);
    oft_event_buffer_destroy(&connection->disconnected_buffer);

    pthread_mutex_destroy(&connection->outbound_lock);
    sem_destroy(&connection->send_signal);
    sem_destroy(&connection->write_permit);
    pthread_mutex_destroy(&connection->receipt_lock);
    pthread_mutex_destroy(&connection->rekey_queue_lock);
    pthread_mutex_destroy(&connection->timestamp_lock);
    pthread_mutex_destroy(&connection->acknowledged_callback_lock);

    free(connection);
}
