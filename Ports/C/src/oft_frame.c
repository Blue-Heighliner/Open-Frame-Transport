#include "oft_frame.h"

#include <stdlib.h>
#include <unistd.h>

#define OFT_MAX_VARINT_BYTES 5

void oft_frame_stream_init(oft_frame_stream *stream, SSL *ssl) {
    stream->is_ssl = 1;
    stream->ssl = ssl;
    stream->fd = -1;
    pthread_mutex_init(&stream->write_mutex, NULL);
}

void oft_frame_stream_init_plain(oft_frame_stream *stream, int fd) {
    stream->is_ssl = 0;
    stream->ssl = NULL;
    stream->fd = fd;
    pthread_mutex_init(&stream->write_mutex, NULL);
}

void oft_frame_stream_destroy(oft_frame_stream *stream) {
    pthread_mutex_destroy(&stream->write_mutex);
}

/* Returns the number of bytes read (> 0), 0 on clean EOF, or -1 on error - mirroring SSL_read's
 * return convention regardless of which underlying transport is in use. */
static int stream_read(oft_frame_stream *stream, uint8_t *buffer, size_t length) {
    if (stream->is_ssl) {
        return SSL_read(stream->ssl, buffer, (int)length);
    }

    ssize_t n = read(stream->fd, buffer, length);
    return n < 0 ? -1 : (int)n;
}

/* Returns the number of bytes written (> 0 on success), or <= 0 on error - mirroring SSL_write's
 * return convention regardless of which underlying transport is in use. */
static int stream_write(oft_frame_stream *stream, const uint8_t *data, size_t length) {
    if (stream->is_ssl) {
        return SSL_write(stream->ssl, data, (int)length);
    }

    ssize_t n = write(stream->fd, data, length);
    return n < 0 ? -1 : (int)n;
}

static int stream_read_exact(oft_frame_stream *stream, uint8_t *buffer, size_t length) {
    size_t total = 0;
    while (total < length) {
        int n = stream_read(stream, buffer + total, length - total);
        if (n <= 0) {
            return -1;
        }

        total += (size_t)n;
    }

    return 0;
}

/* Returns 0 on success (with *out_value set), 1 on clean EOF before any byte was read, -1 on error. */
static int read_varint32(oft_frame_stream *stream, uint32_t *out_value) {
    uint32_t result = 0;
    int shift = 0;

    for (int i = 0; i < OFT_MAX_VARINT_BYTES; i++) {
        uint8_t b;
        int n = stream_read(stream, &b, 1);
        if (n == 0) {
            return i == 0 ? 1 : -1;
        }

        if (n < 0) {
            return -1;
        }

        result |= (uint32_t)(b & 0x7F) << shift;
        if ((b & 0x80) == 0) {
            *out_value = result;
            return 0;
        }

        shift += 7;
    }

    return -1;
}

int oft_frame_stream_read(oft_frame_stream *stream, uint8_t **out_data, size_t *out_length) {
    uint32_t length = 0;
    int varint_result = read_varint32(stream, &length);
    if (varint_result == 1) {
        *out_data = NULL;
        *out_length = 0;
        return 0;
    }

    if (varint_result != 0) {
        return -1;
    }

    uint8_t *buffer = NULL;
    if (length > 0) {
        buffer = malloc(length);
        if (!buffer) {
            return -1;
        }

        if (stream_read_exact(stream, buffer, length) != 0) {
            free(buffer);
            return -1;
        }
    }

    *out_data = buffer;
    *out_length = length;
    return 1;
}

int oft_frame_stream_write(oft_frame_stream *stream, const uint8_t *data, size_t length) {
    uint8_t varint[OFT_MAX_VARINT_BYTES];
    size_t count = 0;
    uint32_t value = (uint32_t)length;

    do {
        uint8_t b = (uint8_t)(value & 0x7F);
        value >>= 7;
        if (value != 0) {
            b |= 0x80;
        }

        varint[count++] = b;
    } while (value != 0);

    int result = 0;
    pthread_mutex_lock(&stream->write_mutex);
    if (stream_write(stream, varint, count) <= 0) {
        result = -1;
    } else if (length > 0 && stream_write(stream, data, length) <= 0) {
        result = -1;
    }
    pthread_mutex_unlock(&stream->write_mutex);
    return result;
}
