#ifndef OFT_FRAME_H
#define OFT_FRAME_H

#include <openssl/ssl.h>
#include <pthread.h>
#include <stddef.h>
#include <stdint.h>

/*
 * Reads and writes length-delimited messages on either an SSL connection or a plain TCP file
 * descriptor (see Docs/OFT.md §9) using a standard protobuf varint length prefix, as described in
 * Docs/OFT.md §2. Writes are serialized against concurrent callers so that a Receipt written from
 * the receive loop can never interleave with a partially written message from the send loop.
 */
typedef struct {
    int is_ssl;
    SSL *ssl; /* used when is_ssl */
    int fd;   /* used when !is_ssl */
    pthread_mutex_t write_mutex;
} oft_frame_stream;

void oft_frame_stream_init(oft_frame_stream *stream, SSL *ssl);

/* Initializes a stream over a plain TCP file descriptor, with no TLS layered on top. */
void oft_frame_stream_init_plain(oft_frame_stream *stream, int fd);

void oft_frame_stream_destroy(oft_frame_stream *stream);

/* Writes a single message, prefixed with its varint-encoded length. Returns 0 on success, -1 on error. */
int oft_frame_stream_write(oft_frame_stream *stream, const uint8_t *data, size_t length);

/*
 * Reads a single message. Returns 1 with *out_data and *out_length set (caller must free
 * *out_data, unless length is 0, in which case it's NULL) on success, 0 on a clean EOF at a
 * message boundary, or -1 on error.
 */
int oft_frame_stream_read(oft_frame_stream *stream, uint8_t **out_data, size_t *out_length);

#endif
