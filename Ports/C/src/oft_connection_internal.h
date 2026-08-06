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
 * if configured). Not started automatically by the establish functions above: callers must invoke
 * this only after they've finished their own bookkeeping (adding the connection to their tracked
 * list and invoking their connected callback, if any), so that no inbound message can be delivered
 * before a callback reacting to that has had a chance to call
 * oft_connection_set_received_callback().
 */
void oft_connection_start_processing(oft_connection *connection);

#endif
