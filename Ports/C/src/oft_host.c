#include "oft/oft.h"
#include "oft_connection_internal.h"
#include "oft_ephemeral_ssl_ctx.h"
#include "oft_event_buffer.h"

#include <arpa/inet.h>
#include <errno.h>
#include <netdb.h>
#include <netinet/in.h>
#include <poll.h>
#include <pthread.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

typedef struct {
    struct oft_listener *listener;
    int fd;
} oft_accept_task;

typedef struct {
    oft_listener *listener;
    oft_connection *connection;
} oft_connected_buffer_item;

/* The dispatch target attached to connected_buffer below. */
typedef struct {
    oft_connected_callback callback;
    void *user_data;
} oft_connected_target;

struct oft_listener {
    oft_host_options options;
    char *info_copy;
    SSL_CTX *ssl_ctx;
    int owns_ssl_ctx; /* set only under OFT_SECURITY_MODE_SECURE, which resolves one throwaway
                        * certificate for this listener's whole lifetime rather than one per
                        * accepted connection - see oft_host()'s own doc comment. */

    int listen_fd;
    int wakeup_pipe[2]; /* [0] = read end, [1] = write end; see oft_listener_close() */
    uint16_t local_port;
    pthread_t accept_thread;

    /* Holds every accepted connection until oft_listener_set_connected_callback() is first called
     * with a non-NULL callback, then flushes that backlog to it before becoming its live target -
     * see oft_event_buffer's own doc comment. This is what makes calling
     * oft_listener_set_connected_callback() after oft_host() already returned the listener always
     * safe, with no accept-before-subscribe race to guard against. connected_target is the live
     * target attached to connected_buffer - embedded rather than heap-allocated since there is only
     * ever one at a time. */
    oft_event_buffer connected_buffer;
    oft_connected_target connected_target;

    int closed;
};

/* Discards (by closing) an accepted connection nobody ever assigned a callback to receive - matches
 * oft_event_buffer's own "discard on destroy" contract. */
static void free_connected_buffer_item(void *item) {
    oft_connected_buffer_item *accepted = item;
    oft_connection_close(accepted->connection);
    free(accepted);
}

static void dispatch_connected_buffer_item(void *user_data, void *item) {
    oft_connected_target *target = user_data;
    oft_connected_buffer_item *accepted = item;
    if (target->callback) {
        target->callback(accepted->listener, accepted->connection, target->user_data);
    } else {
        oft_connection_close(accepted->connection);
    }

    free(accepted);
}

/* Runs on a short-lived, detached thread per accepted connection so a slow TLS handshake from one
 * peer never delays accepting the next. */
static void *handle_accepted(void *arg) {
    oft_accept_task *task = arg;
    oft_listener *listener = task->listener;
    int fd = task->fd;
    free(task);

    char error_buffer[256];
    oft_connection *connection = oft_connection_establish_as_server(fd, listener->ssl_ctx, &listener->options, error_buffer, sizeof(error_buffer));
    if (!connection) {
        close(fd);
        return NULL;
    }

    /* Safe to start processing immediately, before raising the connected notification below:
     * connected_buffer buffers it, so a caller reacting to it - even one that hasn't called
     * oft_connection_set_received_callback()/oft_connection_set_disconnected_callback() on it yet -
     * never misses this connection's own buffered received/disconnected notifications either (see
     * oft_connection's received_buffer/disconnected_buffer). */
    oft_connection_start_processing(connection);

    oft_connected_buffer_item *item = malloc(sizeof(oft_connected_buffer_item));
    if (!item) {
        oft_connection_close(connection);
        return NULL;
    }

    item->listener = listener;
    item->connection = connection;
    oft_event_buffer_raise(&listener->connected_buffer, item);

    return NULL;
}

/*
 * Waits on both the listening socket and a dedicated wakeup pipe via poll(), rather than calling
 * accept() directly: closing a file descriptor that another thread is blocked in accept() on is
 * not a reliable way to unblock it. Writing a byte to the pipe from oft_listener_close() is.
 */
static void *accept_loop(void *arg) {
    oft_listener *listener = arg;

    while (1) {
        struct pollfd fds[2];
        fds[0].fd = listener->listen_fd;
        fds[0].events = POLLIN;
        fds[0].revents = 0;
        fds[1].fd = listener->wakeup_pipe[0];
        fds[1].events = POLLIN;
        fds[1].revents = 0;

        int poll_result = poll(fds, 2, -1);
        if (poll_result < 0) {
            if (errno == EINTR) {
                continue;
            }

            return NULL;
        }

        if (fds[1].revents & POLLIN) {
            /* Woken up by oft_listener_close(). */
            return NULL;
        }

        if (!(fds[0].revents & POLLIN)) {
            continue;
        }

        struct sockaddr_storage address;
        socklen_t address_length = sizeof(address);
        int fd = accept(listener->listen_fd, (struct sockaddr *)&address, &address_length);
        if (fd < 0) {
            if (errno == EINTR || errno == EAGAIN || errno == EWOULDBLOCK) {
                continue;
            }

            return NULL;
        }

        oft_accept_task *task = malloc(sizeof(oft_accept_task));
        if (!task) {
            close(fd);
            continue;
        }

        task->listener = listener;
        task->fd = fd;

        pthread_t thread;
        if (pthread_create(&thread, NULL, handle_accepted, task) != 0) {
            free(task);
            close(fd);
            continue;
        }

        pthread_detach(thread);
    }
}

static const oft_host_options *resolve_options(const oft_host_options *options, oft_host_options *defaults) {
    if (options) {
        return options;
    }

    memset(defaults, 0, sizeof(*defaults));
    return defaults;
}

oft_listener *oft_host(
        const char *bind_host, uint16_t bind_port, const oft_host_options *options, SSL_CTX *ssl_ctx,
        char *error_buffer, size_t error_buffer_size) {
    oft_host_options defaults;
    options = resolve_options(options, &defaults);

    int owns_ssl_ctx = 0;
    if (options->security_mode == OFT_SECURITY_MODE_SERVER_AUTHENTICATION || options->security_mode == OFT_SECURITY_MODE_DUAL_AUTHENTICATION) {
        if (!ssl_ctx) {
            if (error_buffer) {
                snprintf(error_buffer, error_buffer_size,
                         "ssl_ctx is required when security_mode is OFT_SECURITY_MODE_SERVER_AUTHENTICATION or OFT_SECURITY_MODE_DUAL_AUTHENTICATION");
            }

            return NULL;
        }
    } else if (options->security_mode == OFT_SECURITY_MODE_SECURE) {
        /* Resolved once per listener rather than once per accepted connection: nothing validates
         * this certificate under SECURE mode, so one throwaway identity reused for the listener's
         * whole lifetime is both correct and far cheaper than generating a fresh RSA keypair on
         * every single inbound connection. */
        ssl_ctx = oft_ephemeral_ssl_ctx_create_server();
        if (!ssl_ctx) {
            if (error_buffer) {
                snprintf(error_buffer, error_buffer_size, "failed to create ephemeral SSL_CTX");
            }

            return NULL;
        }

        owns_ssl_ctx = 1;
    }

    oft_listener *listener = calloc(1, sizeof(oft_listener));
    if (!listener) {
        if (owns_ssl_ctx) {
            SSL_CTX_free(ssl_ctx);
        }

        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size, "out of memory");
        }

        return NULL;
    }

    listener->options = *options;
    listener->info_copy = strdup(options->info ? options->info : "");
    listener->options.info = listener->info_copy;
    listener->ssl_ctx = ssl_ctx;
    listener->owns_ssl_ctx = owns_ssl_ctx;
    listener->listen_fd = -1;
    oft_event_buffer_init(&listener->connected_buffer, free_connected_buffer_item);

    char port_str[16];
    snprintf(port_str, sizeof(port_str), "%u", (unsigned)bind_port);

    struct addrinfo hints;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;
    hints.ai_flags = AI_PASSIVE;

    struct addrinfo *resolved;
    int gai_result = getaddrinfo(bind_host ? bind_host : "0.0.0.0", port_str, &hints, &resolved);
    if (gai_result != 0) {
        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size, "%s", gai_strerror(gai_result));
        }

        oft_listener_close(listener);
        return NULL;
    }

    int fd = -1;
    for (struct addrinfo *addr = resolved; addr; addr = addr->ai_next) {
        fd = socket(addr->ai_family, addr->ai_socktype, addr->ai_protocol);
        if (fd < 0) {
            continue;
        }

        int reuse = 1;
        setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse));

        if (bind(fd, addr->ai_addr, addr->ai_addrlen) == 0) {
            struct sockaddr_storage bound;
            socklen_t bound_length = sizeof(bound);
            if (getsockname(fd, (struct sockaddr *)&bound, &bound_length) == 0) {
                if (bound.ss_family == AF_INET) {
                    listener->local_port = ntohs(((struct sockaddr_in *)&bound)->sin_port);
                } else if (bound.ss_family == AF_INET6) {
                    listener->local_port = ntohs(((struct sockaddr_in6 *)&bound)->sin6_port);
                }
            }

            break;
        }

        close(fd);
        fd = -1;
    }

    freeaddrinfo(resolved);

    if (fd < 0) {
        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size, "failed to bind: %s", strerror(errno));
        }

        oft_listener_close(listener);
        return NULL;
    }

    if (listen(fd, 128) != 0) {
        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size, "failed to listen: %s", strerror(errno));
        }

        close(fd);
        oft_listener_close(listener);
        return NULL;
    }

    if (pipe(listener->wakeup_pipe) != 0) {
        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size, "failed to create wakeup pipe: %s", strerror(errno));
        }

        close(fd);
        oft_listener_close(listener);
        return NULL;
    }

    listener->listen_fd = fd;
    pthread_create(&listener->accept_thread, NULL, accept_loop, listener);
    return listener;
}

int oft_listener_local_port(oft_listener *listener) {
    return (int)listener->local_port;
}

void oft_listener_set_connected_callback(oft_listener *listener, oft_connected_callback callback, void *user_data) {
    listener->connected_target.callback = callback;
    listener->connected_target.user_data = user_data;
    oft_event_buffer_attach(&listener->connected_buffer, callback ? dispatch_connected_buffer_item : NULL, &listener->connected_target);
}

void oft_listener_close(oft_listener *listener) {
    if (listener->closed) {
        return;
    }

    listener->closed = 1;

    if (listener->listen_fd >= 0) {
        char byte = 0;
        ssize_t written = write(listener->wakeup_pipe[1], &byte, 1);
        (void)written;

        pthread_join(listener->accept_thread, NULL);

        close(listener->listen_fd);
        close(listener->wakeup_pipe[0]);
        close(listener->wakeup_pipe[1]);
    }

    /* Safe now: the accept thread (the only thread that ever raises onto connected_buffer) has
     * fully exited, or was never started. */
    oft_event_buffer_destroy(&listener->connected_buffer);

    if (listener->owns_ssl_ctx) {
        SSL_CTX_free(listener->ssl_ctx);
    }

    free(listener->info_copy);
    free(listener);
}
