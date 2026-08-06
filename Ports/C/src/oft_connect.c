#include "oft/oft.h"
#include "oft_connection_internal.h"

#include <errno.h>
#include <netdb.h>
#include <stdio.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

static const oft_connect_options *resolve_options(const oft_connect_options *options, oft_connect_options *defaults) {
    if (options) {
        return options;
    }

    memset(defaults, 0, sizeof(*defaults));
    return defaults;
}

oft_connection *oft_connect(
        const char *host, uint16_t port, const oft_connect_options *options, SSL_CTX *ssl_ctx,
        oft_connection_established_callback on_established, void *on_established_user_data,
        char *error_buffer, size_t error_buffer_size) {
    oft_connect_options defaults;
    options = resolve_options(options, &defaults);

    if ((options->security_mode == OFT_SECURITY_MODE_AUTHENTICATION ||
         options->security_mode == OFT_SECURITY_MODE_DUAL_AUTHENTICATION) && !ssl_ctx) {
        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size,
                     "ssl_ctx is required when security_mode is OFT_SECURITY_MODE_AUTHENTICATION or OFT_SECURITY_MODE_DUAL_AUTHENTICATION");
        }

        return NULL;
    }

    char port_str[16];
    snprintf(port_str, sizeof(port_str), "%u", (unsigned)port);

    struct addrinfo hints;
    memset(&hints, 0, sizeof(hints));
    hints.ai_family = AF_UNSPEC;
    hints.ai_socktype = SOCK_STREAM;

    struct addrinfo *resolved;
    int gai_result = getaddrinfo(host, port_str, &hints, &resolved);
    if (gai_result != 0) {
        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size, "%s", gai_strerror(gai_result));
        }

        return NULL;
    }

    int fd = -1;
    for (struct addrinfo *addr = resolved; addr; addr = addr->ai_next) {
        fd = socket(addr->ai_family, addr->ai_socktype, addr->ai_protocol);
        if (fd < 0) {
            continue;
        }

        if (connect(fd, addr->ai_addr, addr->ai_addrlen) == 0) {
            break;
        }

        close(fd);
        fd = -1;
    }

    freeaddrinfo(resolved);

    if (fd < 0) {
        if (error_buffer) {
            snprintf(error_buffer, error_buffer_size, "failed to connect: %s", strerror(errno));
        }

        return NULL;
    }

    oft_connection *connection = oft_connection_establish_as_client(fd, host, ssl_ctx, options, error_buffer, error_buffer_size);
    if (!connection) {
        close(fd);
        return NULL;
    }

    if (on_established) {
        on_established(connection, on_established_user_data);
    }

    /* Started only now, after on_established has had a chance to register its own callbacks:
     * starting any earlier risks the receive thread delivering (and discarding, for lack of a
     * callback) this connection's first inbound message before the caller ever gets to see it -
     * see oft_connection_established_callback's own comment. */
    oft_connection_start_processing(connection);
    return connection;
}
