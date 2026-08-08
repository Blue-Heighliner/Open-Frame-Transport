#include "oft/oft.h"
#include "oft/oft_peer.h"
#include "oft_event.h"
#include "oft_frame.h"
#include "oft_wire.h"
#include "test_certs.h"

#include <arpa/inet.h>
#include <assert.h>
#include <netinet/in.h>
#include <poll.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <time.h>
#include <unistd.h>

/* ---- Tiny test framework ---- */

static int g_failures = 0;
static const char *g_current_test;

#define TEST_ASSERT(cond)                                                                     \
    do {                                                                                       \
        if (!(cond)) {                                                                         \
            fprintf(stderr, "  FAIL %s: assertion failed: %s (%s:%d)\n", g_current_test, #cond, __FILE__, __LINE__); \
            g_failures++;                                                                      \
            return;                                                                            \
        }                                                                                       \
    } while (0)

#define RUN_TEST(fn)                                                                           \
    do {                                                                                        \
        g_current_test = #fn;                                                                   \
        int before = g_failures;                                                                \
        fn();                                                                                    \
        printf("  %s %s\n", g_failures == before ? "PASS" : "FAIL", #fn);                        \
    } while (0)

/* ---- Synchronization helpers used only by the tests ---- */

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t cond;
    uint8_t *data;
    size_t length;
    int received;
} message_capture;

static void message_capture_init(message_capture *capture) {
    pthread_mutex_init(&capture->mutex, NULL);
    pthread_cond_init(&capture->cond, NULL);
    capture->data = NULL;
    capture->length = 0;
    capture->received = 0;
}

static void message_capture_destroy(message_capture *capture) {
    pthread_mutex_destroy(&capture->mutex);
    pthread_cond_destroy(&capture->cond);
    free(capture->data);
}

static void on_message_capture(oft_connection *connection, uint8_t *data, size_t length, void *user_data) {
    (void)connection;
    message_capture *capture = user_data;
    pthread_mutex_lock(&capture->mutex);
    free(capture->data);
    capture->data = data;
    capture->length = length;
    capture->received = 1;
    pthread_cond_broadcast(&capture->cond);
    pthread_mutex_unlock(&capture->mutex);
}

static void on_peer_message_capture(oft_peer_reception *reception, void *user_data) {
    message_capture *capture = user_data;
    size_t length = oft_peer_reception_length(reception);
    uint8_t *data = malloc(length);
    memcpy(data, oft_peer_reception_data(reception), length);
    oft_peer_reception_free(reception);

    pthread_mutex_lock(&capture->mutex);
    free(capture->data);
    capture->data = data;
    capture->length = length;
    capture->received = 1;
    pthread_cond_broadcast(&capture->cond);
    pthread_mutex_unlock(&capture->mutex);
}

static int message_capture_wait(message_capture *capture, int timeout_seconds) {
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_seconds;

    pthread_mutex_lock(&capture->mutex);
    int timed_out = 0;
    while (!capture->received && !timed_out) {
        if (pthread_cond_timedwait(&capture->cond, &capture->mutex, &deadline) != 0) {
            timed_out = 1;
        }
    }
    int received = capture->received;
    pthread_mutex_unlock(&capture->mutex);
    return received && !timed_out ? 0 : -1;
}

#define MAX_ORDERED_MESSAGES 16

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t cond;
    size_t lengths[MAX_ORDERED_MESSAGES];
    size_t count;
} ordered_capture;

static void ordered_capture_init(ordered_capture *capture) {
    pthread_mutex_init(&capture->mutex, NULL);
    pthread_cond_init(&capture->cond, NULL);
    capture->count = 0;
}

static void ordered_capture_destroy(ordered_capture *capture) {
    pthread_mutex_destroy(&capture->mutex);
    pthread_cond_destroy(&capture->cond);
}

/* Since priority isn't exposed to received callbacks (see oft_received_callback), tests that need to
 * tell messages apart by which priority channel they were sent on distinguish them by length
 * instead, using payloads of deliberately different sizes. */
static void on_ordered_message(oft_connection *connection, uint8_t *data, size_t length, void *user_data) {
    (void)connection;
    free(data);

    ordered_capture *capture = user_data;
    pthread_mutex_lock(&capture->mutex);
    if (capture->count < MAX_ORDERED_MESSAGES) {
        capture->lengths[capture->count++] = length;
    }
    pthread_cond_broadcast(&capture->cond);
    pthread_mutex_unlock(&capture->mutex);
}

static int ordered_capture_wait_count(ordered_capture *capture, size_t expected, int timeout_seconds) {
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_seconds;

    pthread_mutex_lock(&capture->mutex);
    int timed_out = 0;
    while (capture->count < expected && !timed_out) {
        if (pthread_cond_timedwait(&capture->cond, &capture->mutex, &deadline) != 0) {
            timed_out = 1;
        }
    }
    int ok = capture->count >= expected;
    pthread_mutex_unlock(&capture->mutex);
    return ok && !timed_out ? 0 : -1;
}

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t cond;
    oft_connection *connection;
    int established;
} connection_capture;

static void connection_capture_init(connection_capture *capture) {
    pthread_mutex_init(&capture->mutex, NULL);
    pthread_cond_init(&capture->cond, NULL);
    capture->connection = NULL;
    capture->established = 0;
}

static void connection_capture_destroy(connection_capture *capture) {
    pthread_mutex_destroy(&capture->mutex);
    pthread_cond_destroy(&capture->cond);
}

static void on_connection_established(oft_listener *listener, oft_connection *connection, void *user_data) {
    (void)listener;
    connection_capture *capture = user_data;
    pthread_mutex_lock(&capture->mutex);
    capture->connection = connection;
    capture->established = 1;
    pthread_cond_broadcast(&capture->cond);
    pthread_mutex_unlock(&capture->mutex);
}

static oft_connection *connection_capture_wait(connection_capture *capture, int timeout_seconds) {
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_seconds;

    pthread_mutex_lock(&capture->mutex);
    int timed_out = 0;
    while (!capture->established && !timed_out) {
        if (pthread_cond_timedwait(&capture->cond, &capture->mutex, &deadline) != 0) {
            timed_out = 1;
        }
    }
    oft_connection *connection = capture->established ? capture->connection : NULL;
    pthread_mutex_unlock(&capture->mutex);
    return connection;
}

typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t cond;
    int closed;
} closed_capture;

static void closed_capture_init(closed_capture *capture) {
    pthread_mutex_init(&capture->mutex, NULL);
    pthread_cond_init(&capture->cond, NULL);
    capture->closed = 0;
}

static void closed_capture_destroy(closed_capture *capture) {
    pthread_mutex_destroy(&capture->mutex);
    pthread_cond_destroy(&capture->cond);
}

static void on_closed_capture(oft_connection *connection, const char *error_message, void *user_data) {
    (void)connection;
    (void)error_message;
    closed_capture *capture = user_data;
    pthread_mutex_lock(&capture->mutex);
    capture->closed = 1;
    pthread_cond_broadcast(&capture->cond);
    pthread_mutex_unlock(&capture->mutex);
}

static int closed_capture_wait(closed_capture *capture, int timeout_seconds) {
    struct timespec deadline;
    clock_gettime(CLOCK_REALTIME, &deadline);
    deadline.tv_sec += timeout_seconds;

    pthread_mutex_lock(&capture->mutex);
    int timed_out = 0;
    while (!capture->closed && !timed_out) {
        if (pthread_cond_timedwait(&capture->cond, &capture->mutex, &deadline) != 0) {
            timed_out = 1;
        }
    }
    int closed = capture->closed;
    pthread_mutex_unlock(&capture->mutex);
    return closed ? 0 : -1;
}

/* ---- Test harness: a connected listener/connection pair over real loopback TCP/TLS ---- */

typedef struct {
    oft_listener *listener;
    oft_connection *server_connection;
    oft_connection *client_connection;
    SSL_CTX *server_ssl_ctx;
    SSL_CTX *client_ssl_ctx;
} test_pair;

static int establish_pair(test_pair *pair, size_t max_packet_data_size, long rekey_interval_ms) {
    memset(pair, 0, sizeof(*pair));

    pair->server_ssl_ctx = test_create_server_context();
    pair->client_ssl_ctx = test_create_client_context();
    if (!pair->server_ssl_ctx || !pair->client_ssl_ctx) {
        return -1;
    }

    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.max_packet_data_size = max_packet_data_size;
    host_options.rekey_interval_ms = rekey_interval_ms;
    host_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    pair->listener = oft_host("127.0.0.1", 0, &host_options, pair->server_ssl_ctx, error_buffer, sizeof(error_buffer));
    if (!pair->listener) {
        fprintf(stderr, "oft_host failed: %s\n", error_buffer);
        connection_capture_destroy(&accepted);
        return -1;
    }

    oft_listener_set_connected_callback(pair->listener, on_connection_established, &accepted);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.max_packet_data_size = max_packet_data_size;
    connect_options.rekey_interval_ms = rekey_interval_ms;
    connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    int port = oft_listener_local_port(pair->listener);
    pair->client_connection = oft_connect(
            "127.0.0.1", (uint16_t)port, &connect_options, pair->client_ssl_ctx,
            error_buffer, sizeof(error_buffer));
    if (!pair->client_connection) {
        fprintf(stderr, "oft_connect failed: %s\n", error_buffer);
        connection_capture_destroy(&accepted);
        return -1;
    }

    pair->server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);

    return pair->server_connection ? 0 : -1;
}

static void destroy_pair(test_pair *pair) {
    if (pair->client_connection) {
        oft_connection_close(pair->client_connection);
    }

    if (pair->server_connection) {
        oft_connection_close(pair->server_connection);
    }

    if (pair->listener) {
        oft_listener_close(pair->listener);
    }

    if (pair->server_ssl_ctx) {
        SSL_CTX_free(pair->server_ssl_ctx);
    }

    if (pair->client_ssl_ctx) {
        SSL_CTX_free(pair->client_ssl_ctx);
    }
}

/* ---- Tests ---- */

static void test_establish_exchanges_info_as_hail(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    TEST_ASSERT(strcmp(oft_connection_identity(pair.client_connection)->info, "server") == 0);
    TEST_ASSERT(strcmp(oft_connection_identity(pair.server_connection)->info, "client") == 0);

    destroy_pair(&pair);
}

static void test_identity_server_authentication_client_sees_server_certificate_identity(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    const oft_certificate_identity *client_certificate = oft_connection_identity(pair.client_connection)->certificate;
    TEST_ASSERT(client_certificate != NULL);
    TEST_ASSERT(client_certificate->name != NULL && strcmp(client_certificate->name, "localhost") == 0);

    /* Server authentication only authenticates the server - the server never sees a client
     * certificate. */
    TEST_ASSERT(oft_connection_identity(pair.server_connection)->certificate == NULL);

    destroy_pair(&pair);
}

typedef struct {
    int called;
    char info_copy[64];
    int certificate_non_null;
    int chain_non_null;
    long verify_result;
} validation_capture;

static int record_connection_validation(const oft_identity *identity, X509 *certificate, STACK_OF(X509) *chain, long verify_result, void *user_data) {
    validation_capture *capture = user_data;
    capture->called = 1;
    strncpy(capture->info_copy, identity->info ? identity->info : "", sizeof(capture->info_copy) - 1);
    capture->certificate_non_null = certificate != NULL;
    capture->chain_non_null = chain != NULL;
    capture->verify_result = verify_result;
    return 1;
}

static int reject_connection_validation(const oft_identity *identity, X509 *certificate, STACK_OF(X509) *chain, long verify_result, void *user_data) {
    (void)identity;
    (void)certificate;
    (void)chain;
    (void)verify_result;
    (void)user_data;
    return 0;
}

static void test_connection_validation_server_authentication_sees_identity_certificate_and_chain(void) {
    SSL_CTX *server_ctx = test_create_server_context();
    SSL_CTX *client_ctx = test_create_client_context();
    TEST_ASSERT(server_ctx != NULL && client_ctx != NULL);

    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, server_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);

    validation_capture capture;
    memset(&capture, 0, sizeof(capture));

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;
    connect_options.connection_validation = record_connection_validation;
    connect_options.connection_validation_user_data = &capture;

    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, client_ctx,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    TEST_ASSERT(capture.called == 1);
    TEST_ASSERT(strcmp(capture.info_copy, "server") == 0);
    TEST_ASSERT(capture.certificate_non_null == 1);
    TEST_ASSERT(capture.chain_non_null == 1);

    oft_connection_close(client_connection);
    oft_listener_close(listener);
    SSL_CTX_free(server_ctx);
    SSL_CTX_free(client_ctx);
}

static void test_connection_validation_trusted_mode_sees_no_certificate_or_chain(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);

    validation_capture capture;
    memset(&capture, 0, sizeof(capture));
    capture.verify_result = -1; /* sentinel: overwritten by the callback if it's actually invoked */

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_TRUSTED;
    connect_options.connection_validation = record_connection_validation;
    connect_options.connection_validation_user_data = &capture;

    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, NULL,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    TEST_ASSERT(capture.called == 1);
    TEST_ASSERT(capture.certificate_non_null == 0);
    TEST_ASSERT(capture.chain_non_null == 0);
    TEST_ASSERT(capture.verify_result == X509_V_OK);

    oft_connection_close(client_connection);
    oft_listener_close(listener);
}

static void test_connection_validation_returns_zero_connect_fails(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SECURE;

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SECURE;
    connect_options.connection_validation = reject_connection_validation;

    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, NULL,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection == NULL);

    oft_listener_close(listener);
}

static void test_remote_endpoint_returns_the_peers_actual_address(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    const oft_identity *identity = oft_connection_identity(pair.server_connection);
    TEST_ASSERT(strcmp(identity->host, "127.0.0.1") == 0);
    TEST_ASSERT(identity->port != 0);

    destroy_pair(&pair);
}

static void test_disconnected_callback_reassigned_to_null_ignores_notification(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    closed_capture unexpected;
    closed_capture_init(&unexpected);
    oft_connection_set_disconnected_callback(pair.server_connection, on_closed_capture, &unexpected);
    oft_connection_set_disconnected_callback(pair.server_connection, NULL, NULL);

    oft_connection_disconnect(pair.server_connection);

    pthread_mutex_lock(&unexpected.mutex);
    TEST_ASSERT(unexpected.closed == 0);
    pthread_mutex_unlock(&unexpected.mutex);

    closed_capture_destroy(&unexpected);
    destroy_pair(&pair);
}

static void test_is_connected_true_until_disconnected(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    TEST_ASSERT(oft_connection_is_connected(pair.client_connection));

    oft_connection_disconnect(pair.client_connection);

    TEST_ASSERT(!oft_connection_is_connected(pair.client_connection));

    destroy_pair(&pair);
}

static void test_is_connected_false_after_remote_disconnect(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    closed_capture capture;
    closed_capture_init(&capture);
    oft_connection_set_disconnected_callback(pair.client_connection, on_closed_capture, &capture);

    oft_connection_disconnect(pair.server_connection);

    TEST_ASSERT(closed_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(!oft_connection_is_connected(pair.client_connection));

    closed_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_send_small_message_delivered_as_unit(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    const char *payload = "hello";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.client_connection, message_id) == OFT_OK);

    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(capture.length == strlen(payload));
    TEST_ASSERT(memcmp(capture.data, payload, capture.length) == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_send_empty_payload_delivered_as_empty_message(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, NULL, 0, 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.client_connection, message_id) == OFT_OK);

    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(capture.length == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_send_large_message_split_and_reassembled(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16, 0) == 0);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    uint8_t payload[1000];
    for (size_t i = 0; i < sizeof(payload); i++) {
        payload[i] = (uint8_t)i;
    }

    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, payload, sizeof(payload), 3, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.client_connection, message_id) == OFT_OK);

    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(capture.length == sizeof(payload));
    TEST_ASSERT(memcmp(capture.data, payload, sizeof(payload)) == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_send_one_byte_over_packet_size_split_with_minimal_final_chunk(void) {
    /* The smallest possible split: one full Data chunk plus a 1-byte Completion chunk. This is the
     * boundary case the Completion-carries-the-proto3-default-control-value design (README.md §4)
     * depends on - a Completion packet's data must never be empty, and this is as close to empty as
     * a real one can get. */
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16, 0) == 0);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    uint8_t payload[17];
    for (size_t i = 0; i < sizeof(payload); i++) {
        payload[i] = (uint8_t)i;
    }

    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, payload, sizeof(payload), 1, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.client_connection, message_id) == OFT_OK);

    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(capture.length == sizeof(payload));
    TEST_ASSERT(memcmp(capture.data, payload, sizeof(payload)) == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_higher_priority_interrupts_lower_priority(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 8, 0) == 0);

    ordered_capture capture;
    ordered_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_ordered_message, &capture);

    /* Large enough (with an 8-byte packet size, ~300 packets/round-trips) that it's still in
     * flight well after the short delay below, regardless of how fast this build processes
     * packets, while still being small enough to reliably finish within the timeout below even
     * under heavy system load (every packet needs a full acknowledged round trip). */
    size_t low_payload_size = 2500;
    uint8_t *low_payload = malloc(low_payload_size);
    TEST_ASSERT(low_payload != NULL);
    memset(low_payload, 1, low_payload_size);
    uint8_t high_payload[24];
    memset(high_payload, 2, sizeof(high_payload));

    uint64_t low_id;
    uint64_t high_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, low_payload, low_payload_size, 0, &low_id) == OFT_OK);
    free(low_payload); /* oft_connection_send() copies it. */
    usleep(20000);
    TEST_ASSERT(oft_connection_send(pair.client_connection, high_payload, sizeof(high_payload), 5, &high_id) == OFT_OK);

    /* Not TEST_ASSERT below: this test's connection carries ~2500 sequential, fully-acknowledged
     * round trips (one packet may be in flight at a time), so on failure it's important to still
     * fall through to destroy_pair() rather than leaking a connection whose background threads
     * would otherwise keep running (and keep sending packets) for the rest of the process. */
#define CHECK_NO_RETURN(cond)                                                                     \
    do {                                                                                           \
        if (!(cond)) {                                                                             \
            fprintf(stderr, "  FAIL %s: assertion failed: %s (%s:%d)\n", g_current_test, #cond, __FILE__, __LINE__); \
            g_failures++;                                                                          \
        }                                                                                           \
    } while (0)

    if (ordered_capture_wait_count(&capture, 2, 90) != 0) {
        CHECK_NO_RETURN(0);
    } else {
        CHECK_NO_RETURN(oft_connection_wait(pair.client_connection, low_id) == OFT_OK);
        CHECK_NO_RETURN(oft_connection_wait(pair.client_connection, high_id) == OFT_OK);

        CHECK_NO_RETURN(capture.count == 2);
        CHECK_NO_RETURN(capture.lengths[0] == sizeof(high_payload));
        CHECK_NO_RETURN(capture.lengths[1] == low_payload_size);
    }

#undef CHECK_NO_RETURN

    ordered_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_cancel_before_start_never_delivered(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    const char *payload = "should not arrive";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    oft_connection_cancel(pair.client_connection, message_id);

    TEST_ASSERT(oft_connection_wait(pair.client_connection, message_id) == OFT_ERROR_CANCELLED);
    TEST_ASSERT(message_capture_wait(&capture, 1) != 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_cancel_after_start_connection_stays_healthy(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 8, 0) == 0);

    uint8_t payload[400];
    memset(payload, 9, sizeof(payload));

    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, payload, sizeof(payload), 0, &message_id) == OFT_OK);
    usleep(50000);
    oft_connection_cancel(pair.client_connection, message_id);

    int wait_result = oft_connection_wait(pair.client_connection, message_id);
    TEST_ASSERT(wait_result == OFT_ERROR_CANCELLED || wait_result == OFT_OK);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    const char *follow_up = "still alive";
    uint64_t follow_up_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, (const uint8_t *)follow_up, strlen(follow_up), 0, &follow_up_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.client_connection, follow_up_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, follow_up, capture.length) == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_send_after_close_fails(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    oft_connection_close(pair.client_connection);

    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, (const uint8_t *)"x", 1, 0, &message_id) == OFT_ERROR_CLOSED);

    pair.client_connection = NULL; /* already closed above */
    destroy_pair(&pair);
}

static void test_send_negative_priority_fails(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, (const uint8_t *)"x", 1, -1, &message_id) == OFT_ERROR);

    destroy_pair(&pair);
}

static void test_wait_unknown_message_id_fails(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    TEST_ASSERT(oft_connection_wait(pair.client_connection, 999999) == OFT_ERROR);

    destroy_pair(&pair);
}

static void test_cancel_unknown_message_id_is_a_no_op(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    oft_connection_cancel(pair.client_connection, 999999);

    destroy_pair(&pair);
}

static void test_remote_endpoint_returns_ipv6_address(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("::1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    oft_connection *client_connection = oft_connect(
            "::1", (uint16_t)oft_listener_local_port(listener), &connect_options, NULL,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);

    const oft_identity *identity = oft_connection_identity(server_connection);
    TEST_ASSERT(strcmp(identity->host, "::1") == 0);
    TEST_ASSERT(identity->port != 0);

    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
}

static void test_disconnected_callback_assigned_after_close_with_none_set_still_receives_it(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    /* No callback is assigned yet when this closes - the connection's disconnected notification
     * would otherwise be lost forever, since close only ever happens once. */
    oft_connection_disconnect(pair.client_connection);

    closed_capture closed;
    closed_capture_init(&closed);
    oft_connection_set_disconnected_callback(pair.client_connection, on_closed_capture, &closed);

    TEST_ASSERT(closed_capture_wait(&closed, 10) == 0);

    closed_capture_destroy(&closed);
    destroy_pair(&pair);
}

static void test_rekey_from_client(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    TEST_ASSERT(oft_connection_rekey(pair.client_connection) == OFT_OK);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    const char *payload = "post-rekey";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.client_connection, message_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, payload, capture.length) == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_rekey_from_server(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    TEST_ASSERT(oft_connection_rekey(pair.server_connection) == OFT_OK);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.client_connection, on_message_capture, &capture);

    const char *payload = "post-rekey-from-server";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.server_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.server_connection, message_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, payload, capture.length) == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

typedef struct {
    oft_connection *connection;
    int result;
} rekey_thread_arg;

static void *rekey_thread_fn(void *arg) {
    rekey_thread_arg *rekey_arg = arg;
    rekey_arg->result = oft_connection_rekey(rekey_arg->connection);
    return NULL;
}

static void test_rekey_simultaneous_does_not_deadlock(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 0) == 0);

    rekey_thread_arg client_arg = {pair.client_connection, OFT_ERROR};
    rekey_thread_arg server_arg = {pair.server_connection, OFT_ERROR};

    pthread_t client_thread;
    pthread_t server_thread;
    pthread_create(&client_thread, NULL, rekey_thread_fn, &client_arg);
    pthread_create(&server_thread, NULL, rekey_thread_fn, &server_arg);
    pthread_join(client_thread, NULL);
    pthread_join(server_thread, NULL);

    TEST_ASSERT(client_arg.result == OFT_OK);
    TEST_ASSERT(server_arg.result == OFT_OK);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    const char *payload = "after simultaneous rekey";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.client_connection, message_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_rekey_interval_automatically_rekeys(void) {
    test_pair pair;
    TEST_ASSERT(establish_pair(&pair, 16384, 150) == 0);

    usleep(500000);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(pair.server_connection, on_message_capture, &capture);

    const char *payload = "still here";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(pair.client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(pair.client_connection, message_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);

    message_capture_destroy(&capture);
    destroy_pair(&pair);
}

static void test_connect_nothing_listening_fails(void) {
    int probe_fd = socket(AF_INET, SOCK_STREAM, 0);
    TEST_ASSERT(probe_fd >= 0);

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    TEST_ASSERT(bind(probe_fd, (struct sockaddr *)&address, sizeof(address)) == 0);

    struct sockaddr_in bound;
    socklen_t bound_len = sizeof(bound);
    getsockname(probe_fd, (struct sockaddr *)&bound, &bound_len);
    uint16_t port = ntohs(bound.sin_port);
    close(probe_fd);

    SSL_CTX *client_ctx = test_create_client_context();
    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    char error_buffer[256];
    oft_connection *connection = oft_connect("127.0.0.1", port, &connect_options, client_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(connection == NULL);

    SSL_CTX_free(client_ctx);
}

static void test_connect_dns_resolution_failure_fails(void) {
    /* The ".invalid" TLD is reserved by RFC 2606 to never resolve, so this fails fast without
     * needing genuine network access. */
    char error_buffer[256];
    oft_connection *connection = oft_connect(
            "nonexistent.invalid", 12345, NULL, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(connection == NULL);
}

static void test_host_dns_resolution_failure_fails(void) {
    char error_buffer[256];
    oft_listener *listener = oft_host("nonexistent.invalid", 12345, NULL, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener == NULL);
}

static void test_host_bind_failure_when_port_already_in_use(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    char first_error[256];
    oft_listener *first = oft_host("127.0.0.1", 0, &host_options, NULL, first_error, sizeof(first_error));
    TEST_ASSERT(first != NULL);

    char second_error[256];
    oft_listener *second = oft_host(
            "127.0.0.1", (uint16_t)oft_listener_local_port(first), &host_options, NULL, second_error, sizeof(second_error));
    TEST_ASSERT(second == NULL);

    oft_listener_close(first);
}

static void test_default_options_establish_trusted_connection(void) {
    /* Passing NULL for both host and connect options exercises each side's own resolve_options
     * default-fill path (zeroed defaults resolve to OFT_SECURITY_MODE_TRUSTED), not just the
     * explicitly-populated options struct every other test in this file uses. */
    connection_capture accepted;
    connection_capture_init(&accepted);

    char host_error[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, NULL, NULL, host_error, sizeof(host_error));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    char connect_error[256];
    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), NULL, NULL,
            connect_error, sizeof(connect_error));
    TEST_ASSERT(client_connection != NULL);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);

    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
}

static void test_connect_handshake_failure_does_not_leak_socket(void) {
    SSL_CTX *server_ctx = test_create_server_context();
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION; /* the client below presents no certificate, so the handshake fails */

    char server_error[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, server_ctx, server_error, sizeof(server_error));
    TEST_ASSERT(listener != NULL);

    SSL_CTX *client_ctx = test_create_client_context();
    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    char error_buffer[256];
    oft_connection *connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, client_ctx,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(connection == NULL);

    SSL_CTX_free(client_ctx);
    oft_listener_close(listener);
    SSL_CTX_free(server_ctx);
}

static void on_immediate_reply_established(oft_listener *listener, oft_connection *connection, void *user_data) {
    (void)listener;
    (void)user_data;

    /* Queued as early as structurally possible - before this connection's own send loop even
     * exists yet (see oft_connection_start_processing) - so it's flushed as the very first thing
     * once the listener starts processing this connection, immediately after this callback
     * returns: about as fast as a peer's first message could possibly arrive. */
    oft_connection_send(connection, (const uint8_t *)"immediate", 9, 0, NULL);
}

static void test_connect_received_never_misses_a_message_sent_immediately(void) {
    SSL_CTX *server_ctx = test_create_server_context();
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, server_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_immediate_reply_established, NULL);

    SSL_CTX *client_ctx = test_create_client_context();
    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    message_capture capture;
    message_capture_init(&capture);

    oft_connection *connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, client_ctx,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(connection != NULL);

    /* Registering after oft_connect() returns is safe precisely because received callbacks are
     * buffered (see oft_connection_set_received_callback): nothing raised before this call is lost,
     * so this isn't a race against the listener's immediate reply above. */
    oft_connection_set_received_callback(connection, on_message_capture, &capture);

    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(capture.length == 9);
    TEST_ASSERT(memcmp(capture.data, "immediate", 9) == 0);

    message_capture_destroy(&capture);
    oft_connection_close(connection);
    SSL_CTX_free(client_ctx);
    oft_listener_close(listener);
    SSL_CTX_free(server_ctx);
}

static void test_listener_connected_callback_attached_after_accept_still_receives_it(void) {
    SSL_CTX *server_ctx = test_create_server_context();
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, server_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);

    SSL_CTX *client_ctx = test_create_client_context();
    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    /* No connected callback is registered yet, so the accept below races against
     * handle_accepted's own thread with nothing here to synchronize on but a plain sleep - exactly
     * the scenario that would silently lose the connected notification without connected_buffer. */
    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, client_ctx,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    struct timespec delay = {0, 200 * 1000 * 1000};
    nanosleep(&delay, NULL);

    connection_capture accepted;
    connection_capture_init(&accepted);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    TEST_ASSERT(server_connection != NULL);

    connection_capture_destroy(&accepted);
    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    SSL_CTX_free(client_ctx);
    oft_listener_close(listener);
    SSL_CTX_free(server_ctx);
}

static void test_listener_close_does_not_affect_already_accepted_connections(void) {
    SSL_CTX *server_ctx = test_create_server_context();
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, server_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    SSL_CTX *client_ctx = test_create_client_context();
    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    uint16_t port = (uint16_t)oft_listener_local_port(listener);
    oft_connection *client_connection = oft_connect("127.0.0.1", port, &connect_options, client_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    TEST_ASSERT(server_connection != NULL);
    connection_capture_destroy(&accepted);

    /* The listener doesn't track the connections it has accepted (see oft_host()'s own doc
     * comment), so closing it only stops the accept loop - it must leave an already-accepted
     * connection fully alive and usable. */
    oft_listener_close(listener);
    SSL_CTX_free(server_ctx);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(server_connection, on_message_capture, &capture);

    const char *payload = "still alive";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(client_connection, message_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, payload, capture.length) == 0);

    message_capture_destroy(&capture);
    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    SSL_CTX_free(client_ctx);
}

/* ---- oft_wire tests: exercise the wire codec's malformed-input and edge-case paths directly,
 * which a live connection (always speaking well-formed protobuf to itself) never reaches. ---- */

/* Only valid for field_number <= 15: larger field numbers need a multi-byte varint tag, which
 * these hand-crafted single-byte tests don't produce. */
static uint8_t tag_byte(uint32_t field_number, uint32_t wire_type) {
    assert(field_number <= 15);
    return (uint8_t)((field_number << 3) | wire_type);
}

static void test_wire_hail_decode_empty_input_fills_defaults(void) {
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(NULL, 0, &hail) == 0);
    TEST_ASSERT(strcmp(hail.version, "") == 0);
    TEST_ASSERT(strcmp(hail.info, "") == 0);
    oft_hail_free(&hail);
}

static void test_wire_hail_decode_truncated_tag_fails(void) {
    uint8_t data[] = {0x80}; /* continuation bit set, but no further bytes: truncated varint */
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(data, sizeof(data), &hail) != 0);
}

static void test_wire_hail_decode_overlong_varint_fails(void) {
    /* 11 continuation bytes pushes the accumulated shift past 63, the hard limit for a 64-bit varint. */
    uint8_t data[11];
    memset(data, 0xFF, sizeof(data));
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(data, sizeof(data), &hail) != 0);
}

static void test_wire_hail_decode_length_delimited_overrun_fails(void) {
    /* Field 1 (version), wire type 2, declared length 5, but only 1 byte actually follows. */
    uint8_t data[] = {tag_byte(1, 2), 0x05, 'x'};
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(data, sizeof(data), &hail) != 0);
}

static void test_wire_hail_decode_truncated_length_varint_fails(void) {
    /* A valid tag (field 1, length-delimited) followed by a truncated length varint - distinct
     * from a truncated *tag*, this exercises the nested varint read inside
     * reader_read_length_delimited itself. */
    uint8_t data[] = {tag_byte(1, 2), 0x80};
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(data, sizeof(data), &hail) != 0);
}

static void test_wire_hail_decode_info_field_truncated_length_fails(void) {
    /* Same as above but for field 2 (info), a separate code path from field 1 (version). */
    uint8_t data[] = {tag_byte(2, 2), 0x80};
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(data, sizeof(data), &hail) != 0);
}

static void test_wire_hail_encode_field_requiring_multibyte_varint_roundtrips(void) {
    /* A 200-byte info string forces its length-delimited varint length prefix past a single byte. */
    char info[200];
    memset(info, 'a', sizeof(info) - 1);
    info[sizeof(info) - 1] = '\0';

    oft_hail source;
    oft_hail_init(&source, "oft/1", info);
    oft_buffer buffer;
    oft_buffer_init(&buffer);
    TEST_ASSERT(oft_hail_encode(&source, &buffer) == 0);
    oft_hail_free(&source);

    oft_hail decoded;
    TEST_ASSERT(oft_hail_decode(buffer.data, buffer.length, &decoded) == 0);
    TEST_ASSERT(strcmp(decoded.info, info) == 0);

    oft_hail_free(&decoded);
    oft_buffer_free(&buffer);
}

static void test_wire_hail_decode_skips_unknown_fields(void) {
    oft_hail source;
    oft_hail_init(&source, "oft/1", "hello");
    oft_buffer buffer;
    oft_buffer_init(&buffer);
    TEST_ASSERT(oft_hail_encode(&source, &buffer) == 0);
    oft_hail_free(&source);

    /* Append unknown fields covering every wire type reader_skip_field understands, interleaved
     * before the real fields so the decoder must skip past them and keep going. */
    uint8_t extra_varint[] = {tag_byte(10, 0), 0x2A};                 /* wire type 0: varint */
    uint8_t extra_length_delimited[] = {tag_byte(11, 2), 0x02, 'h', 'i'}; /* wire type 2: length-delimited */
    uint8_t extra_fixed32[] = {tag_byte(12, 5), 0x01, 0x02, 0x03, 0x04};  /* wire type 5: 32-bit */
    uint8_t extra_fixed64[] = {tag_byte(13, 1), 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08}; /* wire type 1: 64-bit */

    oft_buffer combined;
    oft_buffer_init(&combined);
    TEST_ASSERT(oft_buffer_append(&combined, extra_varint, sizeof(extra_varint)) == 0);
    TEST_ASSERT(oft_buffer_append(&combined, extra_length_delimited, sizeof(extra_length_delimited)) == 0);
    TEST_ASSERT(oft_buffer_append(&combined, extra_fixed32, sizeof(extra_fixed32)) == 0);
    TEST_ASSERT(oft_buffer_append(&combined, extra_fixed64, sizeof(extra_fixed64)) == 0);
    TEST_ASSERT(oft_buffer_append(&combined, buffer.data, buffer.length) == 0);
    oft_buffer_free(&buffer);

    oft_hail decoded;
    TEST_ASSERT(oft_hail_decode(combined.data, combined.length, &decoded) == 0);
    TEST_ASSERT(strcmp(decoded.version, "oft/1") == 0);
    TEST_ASSERT(strcmp(decoded.info, "hello") == 0);

    oft_hail_free(&decoded);
    oft_buffer_free(&combined);
}

static void test_wire_skip_field_rejects_invalid_wire_type(void) {
    uint8_t data[] = {tag_byte(10, 6)}; /* wire type 6 doesn't exist in protobuf */
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(data, sizeof(data), &hail) != 0);
}

static void test_wire_skip_field_fixed32_truncated_fails(void) {
    uint8_t data[] = {tag_byte(10, 5), 0x01, 0x02}; /* only 2 of the required 4 bytes */
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(data, sizeof(data), &hail) != 0);
}

static void test_wire_skip_field_fixed64_truncated_fails(void) {
    uint8_t data[] = {tag_byte(10, 1), 0x01, 0x02}; /* only 2 of the required 8 bytes */
    oft_hail hail;
    TEST_ASSERT(oft_hail_decode(data, sizeof(data), &hail) != 0);
}

static void test_wire_packet_decode_truncated_fails(void) {
    uint8_t data[] = {tag_byte(1, 0)}; /* control field tag with no value following */
    oft_packet packet;
    TEST_ASSERT(oft_packet_decode(data, sizeof(data), &packet) != 0);
}

static void test_wire_packet_decode_skips_unknown_field(void) {
    oft_packet source;
    oft_packet_init(&source, 7, (const uint8_t *)"payload", 7);
    oft_buffer buffer;
    oft_buffer_init(&buffer);
    TEST_ASSERT(oft_packet_encode(&source, &buffer) == 0);
    oft_packet_free(&source);

    uint8_t extra[] = {tag_byte(10, 0), 0x01};
    oft_buffer combined;
    oft_buffer_init(&combined);
    TEST_ASSERT(oft_buffer_append(&combined, extra, sizeof(extra)) == 0);
    TEST_ASSERT(oft_buffer_append(&combined, buffer.data, buffer.length) == 0);
    oft_buffer_free(&buffer);

    oft_packet decoded;
    TEST_ASSERT(oft_packet_decode(combined.data, combined.length, &decoded) == 0);
    TEST_ASSERT(decoded.control == 7);
    TEST_ASSERT(decoded.length == 7);
    TEST_ASSERT(memcmp(decoded.data, "payload", 7) == 0);

    oft_packet_free(&decoded);
    oft_buffer_free(&combined);
}

static void test_wire_packet_decode_data_field_truncated_length_fails(void) {
    /* Field 2 (data), wire type 2, with a truncated length varint - the packet-decode counterpart
     * of test_wire_hail_decode_truncated_length_varint_fails. */
    uint8_t data[] = {tag_byte(2, 2), 0x80};
    oft_packet packet;
    TEST_ASSERT(oft_packet_decode(data, sizeof(data), &packet) != 0);
}

static void test_wire_packet_decode_skip_field_rejects_invalid_wire_type(void) {
    uint8_t data[] = {tag_byte(10, 6)}; /* wire type 6 doesn't exist in protobuf */
    oft_packet packet;
    TEST_ASSERT(oft_packet_decode(data, sizeof(data), &packet) != 0);
}

static void test_wire_packet_encode_large_data_roundtrips(void) {
    /* A 200-byte data payload forces both its own length-delimited varint length prefix, and the
     * control field's tag, past a single byte where applicable. */
    uint8_t payload[200];
    memset(payload, 'b', sizeof(payload));

    oft_packet source;
    oft_packet_init(&source, 300, payload, sizeof(payload));
    oft_buffer buffer;
    oft_buffer_init(&buffer);
    TEST_ASSERT(oft_packet_encode(&source, &buffer) == 0);
    oft_packet_free(&source);

    oft_packet decoded;
    TEST_ASSERT(oft_packet_decode(buffer.data, buffer.length, &decoded) == 0);
    TEST_ASSERT(decoded.control == 300);
    TEST_ASSERT(decoded.length == sizeof(payload));
    TEST_ASSERT(memcmp(decoded.data, payload, sizeof(payload)) == 0);

    oft_packet_free(&decoded);
    oft_buffer_free(&buffer);
}

static void test_wire_buffer_append_grows_capacity(void) {
    oft_buffer buffer;
    oft_buffer_init(&buffer);

    uint8_t chunk[100];
    memset(chunk, 'a', sizeof(chunk));

    /* Initial capacity is 64 bytes; appending 100 forces the doubling growth loop. */
    TEST_ASSERT(oft_buffer_append(&buffer, chunk, sizeof(chunk)) == 0);
    TEST_ASSERT(buffer.length == sizeof(chunk));
    TEST_ASSERT(buffer.capacity >= sizeof(chunk));

    oft_buffer_free(&buffer);
}

static void test_wire_buffer_append_zero_length_is_a_no_op(void) {
    oft_buffer buffer;
    oft_buffer_init(&buffer);
    TEST_ASSERT(oft_buffer_append(&buffer, NULL, 0) == 0);
    TEST_ASSERT(buffer.length == 0);
    oft_buffer_free(&buffer);
}

/* ---- oft_frame_stream tests: exercised directly over a pipe(2), bypassing TCP/TLS entirely, to
 * deterministically reach edge cases (truncated/overlong length prefixes, multi-byte varints, a
 * write failing outright) a live connection's small test payloads never naturally reach. ---- */

static void test_frame_stream_write_large_payload_roundtrips(void) {
    /* A 200-byte payload forces its length prefix's varint past a single byte on the write side. */
    int fds[2];
    TEST_ASSERT(pipe(fds) == 0);

    oft_frame_stream writer;
    oft_frame_stream_init_plain(&writer, fds[1]);
    oft_frame_stream reader;
    oft_frame_stream_init_plain(&reader, fds[0]);

    uint8_t payload[200];
    memset(payload, 'c', sizeof(payload));
    TEST_ASSERT(oft_frame_stream_write(&writer, payload, sizeof(payload)) == 0);

    uint8_t *received_data;
    size_t received_length;
    TEST_ASSERT(oft_frame_stream_read(&reader, &received_data, &received_length) == 1);
    TEST_ASSERT(received_length == sizeof(payload));
    TEST_ASSERT(memcmp(received_data, payload, sizeof(payload)) == 0);
    free(received_data);

    oft_frame_stream_destroy(&writer);
    oft_frame_stream_destroy(&reader);
    close(fds[0]);
    close(fds[1]);
}

static void test_frame_stream_read_truncated_after_continuation_byte_fails(void) {
    /* A length-prefix byte with its continuation bit set, but the writer closes before sending
     * any further byte: distinct from a clean EOF before any byte at all is read. */
    int fds[2];
    TEST_ASSERT(pipe(fds) == 0);

    uint8_t continuation_byte = 0x80;
    TEST_ASSERT(write(fds[1], &continuation_byte, 1) == 1);
    close(fds[1]);

    oft_frame_stream reader;
    oft_frame_stream_init_plain(&reader, fds[0]);
    uint8_t *received_data;
    size_t received_length;
    TEST_ASSERT(oft_frame_stream_read(&reader, &received_data, &received_length) == -1);

    oft_frame_stream_destroy(&reader);
    close(fds[0]);
}

static void test_frame_stream_read_overlong_varint_fails(void) {
    /* OFT_MAX_VARINT_BYTES continuation bytes with no terminator exceeds the maximum varint size. */
    int fds[2];
    TEST_ASSERT(pipe(fds) == 0);

    uint8_t data[5];
    memset(data, 0xFF, sizeof(data));
    TEST_ASSERT(write(fds[1], data, sizeof(data)) == (ssize_t)sizeof(data));
    close(fds[1]);

    oft_frame_stream reader;
    oft_frame_stream_init_plain(&reader, fds[0]);
    uint8_t *received_data;
    size_t received_length;
    TEST_ASSERT(oft_frame_stream_read(&reader, &received_data, &received_length) == -1);

    oft_frame_stream_destroy(&reader);
    close(fds[0]);
}

static void test_frame_stream_read_truncated_payload_fails(void) {
    /* A valid length prefix declaring 10 bytes, but the writer closes after only 2. */
    int fds[2];
    TEST_ASSERT(pipe(fds) == 0);

    uint8_t data[] = {10, 1, 2};
    TEST_ASSERT(write(fds[1], data, sizeof(data)) == (ssize_t)sizeof(data));
    close(fds[1]);

    oft_frame_stream reader;
    oft_frame_stream_init_plain(&reader, fds[0]);
    uint8_t *received_data;
    size_t received_length;
    TEST_ASSERT(oft_frame_stream_read(&reader, &received_data, &received_length) == -1);

    oft_frame_stream_destroy(&reader);
    close(fds[0]);
}

static void test_frame_stream_write_after_peer_closes_fails(void) {
    /* Closing the read end out from under the writer forces the write() itself to fail (EPIPE),
     * covering the write-failure branch distinct from a successful write. SIGPIPE is ignored
     * elsewhere in the library (see oft_connection.c), but this test doesn't depend on that -
     * write()'s -1/EPIPE return is what oft_frame_stream_write actually checks. */
    int fds[2];
    TEST_ASSERT(pipe(fds) == 0);
    close(fds[0]);

    oft_frame_stream writer;
    oft_frame_stream_init_plain(&writer, fds[1]);
    uint8_t payload[] = {'x'};
    TEST_ASSERT(oft_frame_stream_write(&writer, payload, sizeof(payload)) != 0);

    oft_frame_stream_destroy(&writer);
    close(fds[1]);
}

/* ---- oft_event tests ---- */

static void test_event_poll_before_signal_returns_zero(void) {
    oft_event event;
    oft_event_init(&event);

    int result = -1;
    TEST_ASSERT(oft_event_poll(&event, &result) == 0);

    oft_event_destroy(&event);
}

static void test_event_poll_after_signal_returns_result(void) {
    oft_event event;
    oft_event_init(&event);
    oft_event_signal(&event, 42);

    int result = -1;
    TEST_ASSERT(oft_event_poll(&event, &result) != 0);
    TEST_ASSERT(result == 42);

    oft_event_destroy(&event);
}

/* ---- oft_peer tests ---- */

typedef struct {
    oft_peer *peer;
    SSL_CTX *ssl_ctx;
} test_listening_peer;

static test_listening_peer make_listening_peer(const char *info) {
    test_listening_peer result;
    result.ssl_ctx = test_create_peer_context();

    oft_peer_options options;
    memset(&options, 0, sizeof(options));
    options.info = info;
    options.ssl_ctx = result.ssl_ctx;
    options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;

    result.peer = oft_peer_create(&options);

    char error_buffer[256];
    if (oft_peer_listen(result.peer, "127.0.0.1", 0, error_buffer, sizeof(error_buffer)) != OFT_OK) {
        fprintf(stderr, "oft_peer_listen failed: %s\n", error_buffer);
    }

    return result;
}

static void destroy_listening_peer(test_listening_peer *peer) {
    oft_peer_close(peer->peer);
    SSL_CTX_free(peer->ssl_ctx);
}

typedef struct {
    oft_peer *peer;
    SSL_CTX *ssl_ctx;
} test_outbound_peer;

static test_outbound_peer make_outbound_peer(const char *info, long idle_timeout_ms) {
    test_outbound_peer result;
    result.ssl_ctx = test_create_peer_context();

    oft_peer_options options;
    memset(&options, 0, sizeof(options));
    options.info = info;
    options.ssl_ctx = result.ssl_ctx;
    options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;
    options.idle_timeout_ms = idle_timeout_ms;

    result.peer = oft_peer_create(&options);
    return result;
}

static void destroy_outbound_peer(test_outbound_peer *peer) {
    oft_peer_close(peer->peer);
    SSL_CTX_free(peer->ssl_ctx);
}

static void test_peer_send_reuses_connection(void) {
    test_listening_peer server = make_listening_peer("server");
    test_outbound_peer client = make_outbound_peer("client", 0);

    message_capture capture;
    message_capture_init(&capture);
    oft_peer_set_received_callback(server.peer, on_peer_message_capture, &capture);

    uint16_t port = (uint16_t)oft_peer_local_port(server.peer);
    char error_buffer[256];

    oft_connection *connection1;
    uint64_t message_id1;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"first", 5, 0, &connection1, &message_id1, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection1, message_id1) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, "first", 5) == 0);

    oft_connection *connection2;
    uint64_t message_id2;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"second", 6, 0, &connection2, &message_id2, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection2, message_id2) == OFT_OK);

    /* Same underlying connection returned both times: the second send reused the cached outbound
     * connection rather than dialing a new one. */
    TEST_ASSERT(connection1 == connection2);

    message_capture_destroy(&capture);
    destroy_outbound_peer(&client);
    destroy_listening_peer(&server);
}

static void test_peer_eviction_disconnects_idle_connections(void) {
    test_listening_peer server = make_listening_peer("server");
    /* idle_timeout_ms must comfortably exceed how long the initial send itself can take (connect +
     * handshake + one round trip), or eviction can race the send and disconnect the connection
     * before it ever finishes. */
    test_outbound_peer client = make_outbound_peer("client", 3000);

    uint16_t port = (uint16_t)oft_peer_local_port(server.peer);
    char error_buffer[256];
    oft_connection *connection;
    uint64_t message_id;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"hi", 2, 0, &connection, &message_id, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection, message_id) == OFT_OK);

    closed_capture closed;
    closed_capture_init(&closed);
    oft_connection_set_disconnected_callback(connection, on_closed_capture, &closed);

    /* 120 rather than 20: the connection only becomes an eviction candidate once oft_peer's fixed,
     * non-configurable 30-second grace period has elapsed, and eviction itself is only ever checked
     * on a further fixed, non-configurable 30-second interval on top of that (see oft_peer.h's own
     * documentation). */
    TEST_ASSERT(closed_capture_wait(&closed, 120) == 0);

    closed_capture_destroy(&closed);
    destroy_outbound_peer(&client);
    destroy_listening_peer(&server);
}

static void test_peer_eviction_disconnects_idle_inbound_connections(void) {
    SSL_CTX *ssl_ctx = test_create_peer_context();

    oft_peer_options options;
    memset(&options, 0, sizeof(options));
    options.info = "listener";
    options.ssl_ctx = ssl_ctx;
    options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;
    options.idle_timeout_ms = 200;

    oft_peer *peer = oft_peer_create(&options);
    char error_buffer[256];
    TEST_ASSERT(oft_peer_listen(peer, "127.0.0.1", 0, error_buffer, sizeof(error_buffer)) == OFT_OK);

    /* A peer only ever supports DUAL_AUTHENTICATION (see OFT_SECURITY_MODE_SERVER_AUTHENTICATION's
     * own documentation for why), so the raw client dialing into it below must present its own
     * certificate too - a peer-context ssl_ctx, not a client-only one. */
    SSL_CTX *client_ctx = test_create_peer_context();
    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;

    uint16_t port = (uint16_t)oft_peer_local_port(peer);
    oft_connection *connection = oft_connect("127.0.0.1", port, &connect_options, client_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(connection != NULL);

    closed_capture closed;
    closed_capture_init(&closed);
    oft_connection_set_disconnected_callback(connection, on_closed_capture, &closed);

    /* 120 rather than 10: the connection only becomes an eviction candidate once oft_peer's fixed,
     * non-configurable 30-second grace period has elapsed, and eviction itself is only ever checked
     * on a further fixed, non-configurable 30-second interval on top of that - with generous margin
     * for background timer drift under concurrent test-suite load (see oft_peer.h's own
     * documentation). */
    TEST_ASSERT(closed_capture_wait(&closed, 120) == 0);

    closed_capture_destroy(&closed);
    oft_connection_close(connection);
    SSL_CTX_free(client_ctx);
    oft_peer_close(peer);
    SSL_CTX_free(ssl_ctx);
}

static void test_peer_eviction_disconnects_excess_connections_beyond_max_count(void) {
    test_listening_peer server_a = make_listening_peer("serverA");
    test_listening_peer server_b = make_listening_peer("serverB");

    SSL_CTX *client_ctx = test_create_peer_context();
    oft_peer_options options;
    memset(&options, 0, sizeof(options));
    options.info = "client";
    options.ssl_ctx = client_ctx;
    options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;
    /* Long enough that idle/lifetime eviction never kicks in on its own - only the
     * max_connection_count-driven "evict the oldest" path below should ever disconnect anything. */
    options.idle_timeout_ms = 60000;
    options.max_connection_lifetime_ms = 60000;
    options.max_connection_count = 1;

    oft_peer *client = oft_peer_create(&options);

    uint16_t port_a = (uint16_t)oft_peer_local_port(server_a.peer);
    uint16_t port_b = (uint16_t)oft_peer_local_port(server_b.peer);

    char error_buffer[256];
    oft_connection *connection_a;
    uint64_t message_id_a;
    TEST_ASSERT(oft_peer_send(client, "127.0.0.1", port_a, (const uint8_t *)"hi", 2, 0, &connection_a, &message_id_a, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection_a, message_id_a) == OFT_OK);

    closed_capture closed;
    closed_capture_init(&closed);
    oft_connection_set_disconnected_callback(connection_a, on_closed_capture, &closed);

    /* Connecting to a second host, beyond max_connection_count of 1, makes connection_a the
     * evictable "oldest" connection at the next eviction cycle even though it's still well within
     * both its idle timeout and max lifetime. */
    oft_connection *connection_b;
    uint64_t message_id_b;
    TEST_ASSERT(oft_peer_send(client, "127.0.0.1", port_b, (const uint8_t *)"hi", 2, 0, &connection_b, &message_id_b, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection_b, message_id_b) == OFT_OK);

    /* 120 rather than 10: connection_a only becomes an eviction candidate once oft_peer's fixed,
     * non-configurable 30-second grace period has elapsed since it finished sending, and eviction
     * itself is only ever checked on a further fixed, non-configurable 30-second interval on top of
     * that - with generous margin for background timer drift under concurrent test-suite load (see
     * oft_peer.h's own documentation). */
    TEST_ASSERT(closed_capture_wait(&closed, 120) == 0);

    closed_capture_destroy(&closed);
    oft_peer_close(client);
    SSL_CTX_free(client_ctx);
    destroy_listening_peer(&server_a);
    destroy_listening_peer(&server_b);
}

static void test_peer_outbound_only_has_no_local_port(void) {
    test_outbound_peer client = make_outbound_peer("client", 0);

    TEST_ASSERT(oft_peer_local_port(client.peer) == 0);

    oft_peer_stop_listening(client.peer);

    destroy_outbound_peer(&client);
}

static void test_peer_message_delivered_on_inbound_connection(void) {
    test_listening_peer listening_peer = make_listening_peer("listener");
    test_outbound_peer caller = make_outbound_peer("caller", 0);

    message_capture received;
    message_capture_init(&received);
    oft_peer_set_received_callback(listening_peer.peer, on_peer_message_capture, &received);

    uint16_t port = (uint16_t)oft_peer_local_port(listening_peer.peer);
    char error_buffer[256];
    oft_connection *connection;
    uint64_t message_id;
    TEST_ASSERT(oft_peer_send(caller.peer, "127.0.0.1", port, (const uint8_t *)"hello listener", 14, 0, &connection, &message_id, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection, message_id) == OFT_OK);

    TEST_ASSERT(message_capture_wait(&received, 10) == 0);
    TEST_ASSERT(memcmp(received.data, "hello listener", 14) == 0);

    message_capture_destroy(&received);
    destroy_outbound_peer(&caller);
    destroy_listening_peer(&listening_peer);
}

static void test_peer_rekey_rekeys_outbound_and_inbound_connections(void) {
    test_listening_peer server = make_listening_peer("server");
    test_outbound_peer client = make_outbound_peer("client", 0);

    uint16_t port = (uint16_t)oft_peer_local_port(server.peer);
    char error_buffer[256];
    oft_connection *connection;
    uint64_t message_id;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"hello", 5, 0, &connection, &message_id, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection, message_id) == OFT_OK);

    TEST_ASSERT(oft_peer_rekey(client.peer) == OFT_OK);
    TEST_ASSERT(oft_peer_rekey(server.peer) == OFT_OK);

    message_capture capture;
    message_capture_init(&capture);
    oft_peer_set_received_callback(server.peer, on_peer_message_capture, &capture);

    oft_connection *connection2;
    uint64_t message_id2;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"post-rekey", 10, 0, &connection2, &message_id2, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection2, message_id2) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, "post-rekey", 10) == 0);

    message_capture_destroy(&capture);
    destroy_outbound_peer(&client);
    destroy_listening_peer(&server);
}

static void test_peer_rekey_no_connections_succeeds(void) {
    test_outbound_peer client = make_outbound_peer("client", 0);

    TEST_ASSERT(oft_peer_rekey(client.peer) == OFT_OK);

    destroy_outbound_peer(&client);
}

static void test_peer_drop_disconnects_outbound_and_inbound_connections(void) {
    test_listening_peer server = make_listening_peer("server");
    test_outbound_peer client = make_outbound_peer("client", 0);

    uint16_t port = (uint16_t)oft_peer_local_port(server.peer);
    char error_buffer[256];
    oft_connection *connection;
    uint64_t message_id;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"hi", 2, 0, &connection, &message_id, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection, message_id) == OFT_OK);

    closed_capture closed;
    closed_capture_init(&closed);
    oft_connection_set_disconnected_callback(connection, on_closed_capture, &closed);

    oft_peer_drop(client.peer);

    TEST_ASSERT(closed_capture_wait(&closed, 10) == 0);

    closed_capture_destroy(&closed);
    destroy_outbound_peer(&client);
    destroy_listening_peer(&server);
}

static void test_peer_drop_peer_remains_usable_afterward(void) {
    test_listening_peer server = make_listening_peer("server");
    test_outbound_peer client = make_outbound_peer("client", 0);

    uint16_t port = (uint16_t)oft_peer_local_port(server.peer);
    char error_buffer[256];
    oft_connection *connection;
    uint64_t message_id;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"first", 5, 0, &connection, &message_id, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection, message_id) == OFT_OK);

    oft_peer_drop(client.peer);

    oft_connection *connection2;
    uint64_t message_id2;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"second", 6, 0, &connection2, &message_id2, error_buffer, sizeof(error_buffer)) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(connection2, message_id2) == OFT_OK);

    destroy_outbound_peer(&client);
    destroy_listening_peer(&server);
}

static void test_peer_drop_no_connections_does_not_crash(void) {
    test_outbound_peer client = make_outbound_peer("client", 0);

    oft_peer_drop(client.peer);

    destroy_outbound_peer(&client);
}

static void test_peer_create_server_authentication_mode_fails(void) {
    oft_peer_options options;
    memset(&options, 0, sizeof(options));
    options.info = "peer";
    /* Not a valid mode for a peer: a peer has no client/server delineation, so it cannot express a
     * one-sided authentication requirement (use OFT_SECURITY_MODE_DUAL_AUTHENTICATION instead). */
    options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    TEST_ASSERT(oft_peer_create(&options) == NULL);
}

static void test_peer_listen_fails_when_dual_authentication_mode_missing_ssl_ctx(void) {
    oft_peer_options options;
    memset(&options, 0, sizeof(options));
    options.info = "peer";
    options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION; /* requires an ssl_ctx, deliberately left NULL */

    oft_peer *peer = oft_peer_create(&options);

    char error_buffer[256];
    TEST_ASSERT(oft_peer_listen(peer, "127.0.0.1", 0, error_buffer, sizeof(error_buffer)) == OFT_ERROR);

    oft_peer_close(peer);
}

static void test_peer_send_to_unreachable_host_fails(void) {
    test_outbound_peer client = make_outbound_peer("client", 0);

    int probe_fd = socket(AF_INET, SOCK_STREAM, 0);
    TEST_ASSERT(probe_fd >= 0);
    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    TEST_ASSERT(bind(probe_fd, (struct sockaddr *)&address, sizeof(address)) == 0);
    struct sockaddr_in bound;
    socklen_t bound_len = sizeof(bound);
    getsockname(probe_fd, (struct sockaddr *)&bound, &bound_len);
    uint16_t port = ntohs(bound.sin_port);
    close(probe_fd);

    char error_buffer[256];
    oft_connection *connection;
    uint64_t message_id;
    TEST_ASSERT(oft_peer_send(client.peer, "127.0.0.1", port, (const uint8_t *)"hi", 2, 0, &connection, &message_id, error_buffer, sizeof(error_buffer)) == OFT_ERROR);

    destroy_outbound_peer(&client);
}

static void test_peer_close_called_twice_is_idempotent(void) {
    test_outbound_peer client = make_outbound_peer("client", 0);

    oft_peer_close(client.peer);
    oft_peer_close(client.peer);
    SSL_CTX_free(client.ssl_ctx);
}

/* ---- Trusted mode tests (see Docs/OFT.md §9) ---- */

/* Dials a raw, TLS-less TCP connection to 127.0.0.1:port. Returns the connected fd, or -1. */
static int raw_connect(uint16_t port) {
    int fd = socket(AF_INET, SOCK_STREAM, 0);
    if (fd < 0) {
        return -1;
    }

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_port = htons(port);
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    if (connect(fd, (struct sockaddr *)&address, sizeof(address)) != 0) {
        close(fd);
        return -1;
    }

    return fd;
}

static void test_host_server_authentication_mode_without_ssl_ctx_fails(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;

    char error_buffer[256];
    TEST_ASSERT(oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer)) == NULL);
}

static void test_host_trusted_without_ssl_ctx_succeeds(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_close(listener);
}

static void test_host_close_called_twice_is_idempotent(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);

    oft_listener_close(listener);
    oft_listener_close(listener);
}

static void test_host_binds_ipv6_loopback(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    char error_buffer[256];
    oft_listener *listener = oft_host("::1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    TEST_ASSERT(oft_listener_local_port(listener) != 0);

    oft_listener_close(listener);
}

static void test_trusted_connection_establishes_and_exchanges_messages(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(server_connection, on_message_capture, &capture);

    const char *payload = "hello over plain tcp";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(client_connection, message_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, payload, strlen(payload)) == 0);

    message_capture_destroy(&capture);
    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
}

static void test_trusted_no_tls_session_identity_has_no_certificate(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);

    TEST_ASSERT(oft_connection_identity(client_connection)->certificate == NULL);
    TEST_ASSERT(oft_connection_identity(server_connection)->certificate == NULL);

    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
}

static void test_rekey_on_trusted_connection_is_noop(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);

    TEST_ASSERT(oft_connection_rekey(client_connection) == OFT_OK);

    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
}

static void test_trusted_hail_is_exchanged_directly_over_raw_tcp(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    int fd = raw_connect((uint16_t)oft_listener_local_port(listener));
    TEST_ASSERT(fd >= 0);

    /* No TLS handshake happened above: the hail is written as the very first bytes on the raw TCP
     * stream, immediately after connecting. */
    oft_frame_stream stream;
    oft_frame_stream_init_plain(&stream, fd);

    oft_hail our_hail;
    oft_hail_init(&our_hail, "oft/1", "raw-client");
    oft_buffer hail_buffer;
    oft_buffer_init(&hail_buffer);
    TEST_ASSERT(oft_hail_encode(&our_hail, &hail_buffer) == 0);
    oft_hail_free(&our_hail);
    TEST_ASSERT(oft_frame_stream_write(&stream, hail_buffer.data, hail_buffer.length) == 0);
    oft_buffer_free(&hail_buffer);

    uint8_t *received_data;
    size_t received_length;
    TEST_ASSERT(oft_frame_stream_read(&stream, &received_data, &received_length) == 1);

    oft_hail received_hail;
    TEST_ASSERT(oft_hail_decode(received_data, received_length, &received_hail) == 0);
    free(received_data);
    TEST_ASSERT(strcmp(received_hail.version, "oft/1") == 0);
    oft_hail_free(&received_hail);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);
    TEST_ASSERT(strcmp(oft_connection_identity(server_connection)->info, "raw-client") == 0);

    oft_frame_stream_destroy(&stream);
    close(fd);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
}

static void test_incompatible_hail_version_rejected(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    int fd = raw_connect((uint16_t)oft_listener_local_port(listener));
    TEST_ASSERT(fd >= 0);

    oft_frame_stream stream;
    oft_frame_stream_init_plain(&stream, fd);

    oft_hail our_hail;
    oft_hail_init(&our_hail, "oft/999", "rogue");
    oft_buffer hail_buffer;
    oft_buffer_init(&hail_buffer);
    TEST_ASSERT(oft_hail_encode(&our_hail, &hail_buffer) == 0);
    oft_hail_free(&our_hail);
    TEST_ASSERT(oft_frame_stream_write(&stream, hail_buffer.data, hail_buffer.length) == 0);
    oft_buffer_free(&hail_buffer);

    /* The server rejects the mismatched version before ever invoking the connected callback. */
    TEST_ASSERT(connection_capture_wait(&accepted, 2) == NULL);
    connection_capture_destroy(&accepted);

    oft_frame_stream_destroy(&stream);
    close(fd);
    oft_listener_close(listener);
}

static void test_malformed_hail_rejected(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    int fd = raw_connect((uint16_t)oft_listener_local_port(listener));
    TEST_ASSERT(fd >= 0);

    oft_frame_stream stream;
    oft_frame_stream_init_plain(&stream, fd);

    /* A valid tag (field 1, length-delimited) followed by a truncated length varint: not a valid
     * encoded Hail, so oft_hail_decode must reject it. */
    uint8_t garbage[] = {tag_byte(1, 2), 0x80};
    TEST_ASSERT(oft_frame_stream_write(&stream, garbage, sizeof(garbage)) == 0);

    TEST_ASSERT(connection_capture_wait(&accepted, 2) == NULL);
    connection_capture_destroy(&accepted);

    oft_frame_stream_destroy(&stream);
    close(fd);
    oft_listener_close(listener);
}

static void test_peer_eviction_skips_connections_with_pending_inbound_data(void) {
    oft_peer_options options;
    memset(&options, 0, sizeof(options));
    options.info = "listener";
    options.security_mode = OFT_SECURITY_MODE_TRUSTED;
    options.idle_timeout_ms = 100;

    /* Longer than the wait below (which must now cover the fixed 30-second eviction grace period
     * plus the fixed 30-second eviction check interval, with generous margin for background timer
     * drift under concurrent test-suite load): without this, a liveness Poll frame (default
     * interval 1000ms) would arrive on the raw socket mid-wait and be mistaken by the recv()-based
     * EOF check below for the connection closing. */
    options.poll_interval_ms = 150000;

    oft_peer *peer = oft_peer_create(&options);
    char error_buffer[256];
    TEST_ASSERT(oft_peer_listen(peer, "127.0.0.1", 0, error_buffer, sizeof(error_buffer)) == OFT_OK);

    int fd = raw_connect((uint16_t)oft_peer_local_port(peer));
    TEST_ASSERT(fd >= 0);

    oft_frame_stream stream;
    oft_frame_stream_init_plain(&stream, fd);

    oft_hail our_hail;
    oft_hail_init(&our_hail, "oft/1", "raw-client");
    oft_buffer hail_buffer;
    oft_buffer_init(&hail_buffer);
    TEST_ASSERT(oft_hail_encode(&our_hail, &hail_buffer) == 0);
    oft_hail_free(&our_hail);
    TEST_ASSERT(oft_frame_stream_write(&stream, hail_buffer.data, hail_buffer.length) == 0);
    oft_buffer_free(&hail_buffer);

    uint8_t *received_data;
    size_t received_length;
    TEST_ASSERT(oft_frame_stream_read(&stream, &received_data, &received_length) == 1); /* server's hail */
    free(received_data);

    /* A non-final chunk (control = priority(0) + 4) leaves an in-progress, never-completed inbound
     * message on the server side - oft_connection_has_pending_data() must report true for it, and
     * the peer's eviction sweep must never disconnect it while that's the case, no matter how many
     * eviction check cycles elapse. */
    oft_packet chunk_packet;
    oft_packet_init(&chunk_packet, 4, (const uint8_t *)"partial", 7);
    oft_buffer packet_buffer;
    oft_buffer_init(&packet_buffer);
    TEST_ASSERT(oft_packet_encode(&chunk_packet, &packet_buffer) == 0);
    oft_packet_free(&chunk_packet);
    TEST_ASSERT(oft_frame_stream_write(&stream, packet_buffer.data, packet_buffer.length) == 0);
    oft_buffer_free(&packet_buffer);

    TEST_ASSERT(oft_frame_stream_read(&stream, &received_data, &received_length) == 1); /* Receipt for the chunk */
    free(received_data);

    usleep(500000); /* comfortably more than idle_timeout_ms; well under the fixed 30-second eviction check interval, so this doesn't need to wait out a whole cycle to prove pending data blocks eviction */

    struct pollfd still_open_poll = { .fd = fd, .events = POLLIN };
    TEST_ASSERT(poll(&still_open_poll, 1, 100) == 0); /* no EOF/HUP: still connected */

    /* Completing the message (control 0: final chunk) lets it become eviction-eligible again. A
     * genuine Completion packet's data is never empty (see Docs/OFT.md §4) - control 0 is the value
     * proto3 default-value omission could elide, and an empty-data Completion here would serialize
     * identically to Poll's bare zero-length frame, so this uses a non-empty final chunk like the
     * real send path always does. */
    oft_packet final_packet;
    oft_packet_init(&final_packet, 0, (const uint8_t *)"end", 3);
    oft_buffer final_buffer;
    oft_buffer_init(&final_buffer);
    TEST_ASSERT(oft_packet_encode(&final_packet, &final_buffer) == 0);
    oft_packet_free(&final_packet);
    TEST_ASSERT(oft_frame_stream_write(&stream, final_buffer.data, final_buffer.length) == 0);
    oft_buffer_free(&final_buffer);

    TEST_ASSERT(oft_frame_stream_read(&stream, &received_data, &received_length) == 1); /* Receipt for the final chunk */
    free(received_data);

    /* 120s rather than 5: the connection only becomes an eviction candidate once oft_peer's fixed,
     * non-configurable 30-second grace period has elapsed since HasPendingData cleared, and eviction
     * itself is only ever checked on a further fixed, non-configurable 30-second interval on top of
     * that - with generous margin for background timer drift under concurrent test-suite load (see
     * oft_peer.h's own documentation). */
    struct pollfd now_evicted_poll = { .fd = fd, .events = POLLIN };
    TEST_ASSERT(poll(&now_evicted_poll, 1, 120000) > 0);
    uint8_t probe;
    TEST_ASSERT(recv(fd, &probe, 1, 0) == 0); /* clean EOF: the server closed it once idle */

    oft_frame_stream_destroy(&stream);
    close(fd);
    oft_peer_close(peer);
}

/* ---- Security mode tests (see Docs/OFT.md §9) ---- */

static void test_secure_no_ssl_ctx_configured_connection_establishes_and_exchanges_messages(void) {
    /* Secure mode needs no SSL_CTX from either side: the host generates its own throwaway identity
     * internally, and the connecting side accepts it unconditionally. */
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SECURE;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SECURE;

    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(server_connection, on_message_capture, &capture);

    const char *payload = "hello under secure mode";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(client_connection, message_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, payload, strlen(payload)) == 0);

    message_capture_destroy(&capture);
    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
}

static void test_secure_configured_ssl_ctx_is_ignored(void) {
    /* A caller-supplied ssl_ctx is meaningless under SECURE mode (nothing validates it), so hosting
     * must succeed even though this context is never actually presented. */
    SSL_CTX *unused_ctx = test_create_server_context();
    TEST_ASSERT(unused_ctx != NULL);

    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SECURE;

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, unused_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);

    oft_listener_close(listener);
    SSL_CTX_free(unused_ctx);
}

static void test_dual_authentication_connect_without_ssl_ctx_fails(void) {
    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;

    int probe_fd = socket(AF_INET, SOCK_STREAM, 0);
    TEST_ASSERT(probe_fd >= 0);

    struct sockaddr_in address;
    memset(&address, 0, sizeof(address));
    address.sin_family = AF_INET;
    address.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    TEST_ASSERT(bind(probe_fd, (struct sockaddr *)&address, sizeof(address)) == 0);

    struct sockaddr_in bound;
    socklen_t bound_len = sizeof(bound);
    getsockname(probe_fd, (struct sockaddr *)&bound, &bound_len);
    uint16_t port = ntohs(bound.sin_port);
    close(probe_fd);

    char error_buffer[256];
    oft_connection *connection = oft_connect("127.0.0.1", port, &connect_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(connection == NULL);
}

static void test_dual_authentication_both_sides_present_certificates_connection_establishes_and_exchanges_messages(void) {
    SSL_CTX *server_ctx = test_create_peer_context();
    SSL_CTX *client_ctx = test_create_peer_context();
    TEST_ASSERT(server_ctx != NULL && client_ctx != NULL);

    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, server_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_DUAL_AUTHENTICATION;

    oft_connection *client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(listener), &connect_options, client_ctx,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(client_connection != NULL);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);

    message_capture capture;
    message_capture_init(&capture);
    oft_connection_set_received_callback(server_connection, on_message_capture, &capture);

    const char *payload = "hello under mutual tls";
    uint64_t message_id;
    TEST_ASSERT(oft_connection_send(client_connection, (const uint8_t *)payload, strlen(payload), 0, &message_id) == OFT_OK);
    TEST_ASSERT(oft_connection_wait(client_connection, message_id) == OFT_OK);
    TEST_ASSERT(message_capture_wait(&capture, 10) == 0);
    TEST_ASSERT(memcmp(capture.data, payload, strlen(payload)) == 0);

    message_capture_destroy(&capture);
    oft_connection_close(client_connection);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
    SSL_CTX_free(server_ctx);
    SSL_CTX_free(client_ctx);
}

/* ---- Liveness polling tests (see Docs/OFT.md §10) ---- */

static void test_poll_keeps_idle_connection_alive_beyond_poll_timeout(void) {
    test_pair pair;
    memset(&pair, 0, sizeof(pair));

    pair.server_ssl_ctx = test_create_server_context();
    pair.client_ssl_ctx = test_create_client_context();
    TEST_ASSERT(pair.server_ssl_ctx != NULL && pair.client_ssl_ctx != NULL);

    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;
    host_options.poll_interval_ms = 50;
    host_options.poll_timeout_ms = 200;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    pair.listener = oft_host("127.0.0.1", 0, &host_options, pair.server_ssl_ctx, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(pair.listener != NULL);
    oft_listener_set_connected_callback(pair.listener, on_connection_established, &accepted);

    oft_connect_options connect_options;
    memset(&connect_options, 0, sizeof(connect_options));
    connect_options.info = "client";
    connect_options.security_mode = OFT_SECURITY_MODE_SERVER_AUTHENTICATION;
    connect_options.poll_interval_ms = 50;
    connect_options.poll_timeout_ms = 200;

    pair.client_connection = oft_connect(
            "127.0.0.1", (uint16_t)oft_listener_local_port(pair.listener), &connect_options, pair.client_ssl_ctx,
            error_buffer, sizeof(error_buffer));
    TEST_ASSERT(pair.client_connection != NULL);

    pair.server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(pair.server_connection != NULL);

    closed_capture closed;
    closed_capture_init(&closed);
    oft_connection_set_disconnected_callback(pair.server_connection, on_closed_capture, &closed);

    /* No application traffic at all in either direction for well beyond poll_timeout_ms: if the
     * background Poll packets weren't keeping the connection alive, the watchdog would have already
     * closed it. */
    usleep(500000);

    pthread_mutex_lock(&closed.mutex);
    int was_closed = closed.closed;
    pthread_mutex_unlock(&closed.mutex);
    TEST_ASSERT(!was_closed);

    closed_capture_destroy(&closed);
    destroy_pair(&pair);
}

static void test_poll_closes_connection_when_peer_goes_silent(void) {
    oft_host_options host_options;
    memset(&host_options, 0, sizeof(host_options));
    host_options.info = "server";
    host_options.security_mode = OFT_SECURITY_MODE_TRUSTED;
    host_options.poll_interval_ms = 50;
    host_options.poll_timeout_ms = 200;

    connection_capture accepted;
    connection_capture_init(&accepted);

    char error_buffer[256];
    oft_listener *listener = oft_host("127.0.0.1", 0, &host_options, NULL, error_buffer, sizeof(error_buffer));
    TEST_ASSERT(listener != NULL);
    oft_listener_set_connected_callback(listener, on_connection_established, &accepted);

    int fd = raw_connect((uint16_t)oft_listener_local_port(listener));
    TEST_ASSERT(fd >= 0);

    oft_frame_stream stream;
    oft_frame_stream_init_plain(&stream, fd);

    oft_hail our_hail;
    oft_hail_init(&our_hail, "oft/1", "silent-client");
    oft_buffer hail_buffer;
    oft_buffer_init(&hail_buffer);
    TEST_ASSERT(oft_hail_encode(&our_hail, &hail_buffer) == 0);
    oft_hail_free(&our_hail);
    TEST_ASSERT(oft_frame_stream_write(&stream, hail_buffer.data, hail_buffer.length) == 0);
    oft_buffer_free(&hail_buffer);

    uint8_t *received_data;
    size_t received_length;
    TEST_ASSERT(oft_frame_stream_read(&stream, &received_data, &received_length) == 1);
    free(received_data);

    oft_connection *server_connection = connection_capture_wait(&accepted, 10);
    connection_capture_destroy(&accepted);
    TEST_ASSERT(server_connection != NULL);

    closed_capture closed;
    closed_capture_init(&closed);
    oft_connection_set_disconnected_callback(server_connection, on_closed_capture, &closed);

    /* The raw client above never sends another byte (no Poll, nothing) after the hail: the server
     * side must notice via its liveness watchdog and close on its own. */
    TEST_ASSERT(closed_capture_wait(&closed, 10) == 0);

    closed_capture_destroy(&closed);
    oft_frame_stream_destroy(&stream);
    close(fd);
    oft_connection_close(server_connection);
    oft_listener_close(listener);
}

int main(void) {
    printf("Open Frame Transport - C tests\n");

    RUN_TEST(test_establish_exchanges_info_as_hail);
    RUN_TEST(test_identity_server_authentication_client_sees_server_certificate_identity);
    RUN_TEST(test_connection_validation_server_authentication_sees_identity_certificate_and_chain);
    RUN_TEST(test_connection_validation_trusted_mode_sees_no_certificate_or_chain);
    RUN_TEST(test_connection_validation_returns_zero_connect_fails);
    RUN_TEST(test_remote_endpoint_returns_the_peers_actual_address);
    RUN_TEST(test_disconnected_callback_reassigned_to_null_ignores_notification);
    RUN_TEST(test_is_connected_true_until_disconnected);
    RUN_TEST(test_is_connected_false_after_remote_disconnect);
    RUN_TEST(test_send_small_message_delivered_as_unit);
    RUN_TEST(test_send_empty_payload_delivered_as_empty_message);
    RUN_TEST(test_send_large_message_split_and_reassembled);
    RUN_TEST(test_send_one_byte_over_packet_size_split_with_minimal_final_chunk);
    RUN_TEST(test_higher_priority_interrupts_lower_priority);
    RUN_TEST(test_cancel_before_start_never_delivered);
    RUN_TEST(test_cancel_after_start_connection_stays_healthy);
    RUN_TEST(test_send_after_close_fails);
    RUN_TEST(test_send_negative_priority_fails);
    RUN_TEST(test_wait_unknown_message_id_fails);
    RUN_TEST(test_cancel_unknown_message_id_is_a_no_op);
    RUN_TEST(test_remote_endpoint_returns_ipv6_address);
    RUN_TEST(test_disconnected_callback_assigned_after_close_with_none_set_still_receives_it);
    RUN_TEST(test_rekey_from_client);
    RUN_TEST(test_rekey_from_server);
    RUN_TEST(test_rekey_simultaneous_does_not_deadlock);
    RUN_TEST(test_rekey_interval_automatically_rekeys);
    RUN_TEST(test_connect_nothing_listening_fails);
    RUN_TEST(test_connect_dns_resolution_failure_fails);
    RUN_TEST(test_host_dns_resolution_failure_fails);
    RUN_TEST(test_host_bind_failure_when_port_already_in_use);
    RUN_TEST(test_default_options_establish_trusted_connection);
    RUN_TEST(test_connect_handshake_failure_does_not_leak_socket);
    RUN_TEST(test_connect_received_never_misses_a_message_sent_immediately);
    RUN_TEST(test_listener_connected_callback_attached_after_accept_still_receives_it);
    RUN_TEST(test_listener_close_does_not_affect_already_accepted_connections);
    RUN_TEST(test_wire_hail_decode_empty_input_fills_defaults);
    RUN_TEST(test_wire_hail_decode_truncated_tag_fails);
    RUN_TEST(test_wire_hail_decode_overlong_varint_fails);
    RUN_TEST(test_wire_hail_decode_length_delimited_overrun_fails);
    RUN_TEST(test_wire_hail_decode_truncated_length_varint_fails);
    RUN_TEST(test_wire_hail_decode_info_field_truncated_length_fails);
    RUN_TEST(test_wire_hail_encode_field_requiring_multibyte_varint_roundtrips);
    RUN_TEST(test_wire_hail_decode_skips_unknown_fields);
    RUN_TEST(test_wire_skip_field_rejects_invalid_wire_type);
    RUN_TEST(test_wire_skip_field_fixed32_truncated_fails);
    RUN_TEST(test_wire_skip_field_fixed64_truncated_fails);
    RUN_TEST(test_wire_packet_decode_truncated_fails);
    RUN_TEST(test_wire_packet_decode_skips_unknown_field);
    RUN_TEST(test_wire_packet_decode_data_field_truncated_length_fails);
    RUN_TEST(test_wire_packet_decode_skip_field_rejects_invalid_wire_type);
    RUN_TEST(test_wire_packet_encode_large_data_roundtrips);
    RUN_TEST(test_wire_buffer_append_grows_capacity);
    RUN_TEST(test_wire_buffer_append_zero_length_is_a_no_op);
    RUN_TEST(test_frame_stream_write_large_payload_roundtrips);
    RUN_TEST(test_frame_stream_read_truncated_after_continuation_byte_fails);
    RUN_TEST(test_frame_stream_read_overlong_varint_fails);
    RUN_TEST(test_frame_stream_read_truncated_payload_fails);
    RUN_TEST(test_frame_stream_write_after_peer_closes_fails);
    RUN_TEST(test_event_poll_before_signal_returns_zero);
    RUN_TEST(test_event_poll_after_signal_returns_result);
    RUN_TEST(test_peer_send_reuses_connection);
    RUN_TEST(test_peer_eviction_disconnects_idle_connections);
    RUN_TEST(test_peer_eviction_disconnects_idle_inbound_connections);
    RUN_TEST(test_peer_eviction_disconnects_excess_connections_beyond_max_count);
    RUN_TEST(test_peer_outbound_only_has_no_local_port);
    RUN_TEST(test_peer_message_delivered_on_inbound_connection);
    RUN_TEST(test_peer_rekey_rekeys_outbound_and_inbound_connections);
    RUN_TEST(test_peer_rekey_no_connections_succeeds);
    RUN_TEST(test_peer_drop_disconnects_outbound_and_inbound_connections);
    RUN_TEST(test_peer_drop_peer_remains_usable_afterward);
    RUN_TEST(test_peer_drop_no_connections_does_not_crash);
    RUN_TEST(test_peer_create_server_authentication_mode_fails);
    RUN_TEST(test_peer_listen_fails_when_dual_authentication_mode_missing_ssl_ctx);
    RUN_TEST(test_peer_send_to_unreachable_host_fails);
    RUN_TEST(test_peer_close_called_twice_is_idempotent);

    RUN_TEST(test_host_server_authentication_mode_without_ssl_ctx_fails);
    RUN_TEST(test_host_trusted_without_ssl_ctx_succeeds);
    RUN_TEST(test_host_close_called_twice_is_idempotent);
    RUN_TEST(test_host_binds_ipv6_loopback);
    RUN_TEST(test_trusted_connection_establishes_and_exchanges_messages);
    RUN_TEST(test_trusted_no_tls_session_identity_has_no_certificate);
    RUN_TEST(test_rekey_on_trusted_connection_is_noop);
    RUN_TEST(test_trusted_hail_is_exchanged_directly_over_raw_tcp);
    RUN_TEST(test_incompatible_hail_version_rejected);
    RUN_TEST(test_malformed_hail_rejected);
    RUN_TEST(test_peer_eviction_skips_connections_with_pending_inbound_data);
    RUN_TEST(test_secure_no_ssl_ctx_configured_connection_establishes_and_exchanges_messages);
    RUN_TEST(test_secure_configured_ssl_ctx_is_ignored);
    RUN_TEST(test_dual_authentication_connect_without_ssl_ctx_fails);
    RUN_TEST(test_dual_authentication_both_sides_present_certificates_connection_establishes_and_exchanges_messages);
    RUN_TEST(test_poll_keeps_idle_connection_alive_beyond_poll_timeout);
    RUN_TEST(test_poll_closes_connection_when_peer_goes_silent);

    if (g_failures == 0) {
        printf("All tests passed.\n");
        return 0;
    }

    printf("%d assertion(s) failed.\n", g_failures);
    return 1;
}
