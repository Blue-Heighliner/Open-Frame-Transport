#include "oft_wire.h"

#include <stdlib.h>
#include <string.h>

void oft_buffer_init(oft_buffer *buffer) {
    buffer->data = NULL;
    buffer->length = 0;
    buffer->capacity = 0;
}

void oft_buffer_free(oft_buffer *buffer) {
    free(buffer->data);
    buffer->data = NULL;
    buffer->length = 0;
    buffer->capacity = 0;
}

int oft_buffer_append(oft_buffer *buffer, const uint8_t *data, size_t length) {
    if (length == 0) {
        return 0;
    }

    if (buffer->length + length > buffer->capacity) {
        size_t new_capacity = buffer->capacity == 0 ? 64 : buffer->capacity;
        while (new_capacity < buffer->length + length) {
            new_capacity *= 2;
        }

        uint8_t *new_data = realloc(buffer->data, new_capacity);
        if (!new_data) {
            return -1;
        }

        buffer->data = new_data;
        buffer->capacity = new_capacity;
    }

    memcpy(buffer->data + buffer->length, data, length);
    buffer->length += length;
    return 0;
}

static int append_varint(oft_buffer *buffer, uint64_t value) {
    uint8_t bytes[10];
    size_t count = 0;

    do {
        uint8_t b = (uint8_t)(value & 0x7F);
        value >>= 7;
        if (value != 0) {
            b |= 0x80;
        }

        bytes[count++] = b;
    } while (value != 0);

    return oft_buffer_append(buffer, bytes, count);
}

static int append_tag(oft_buffer *buffer, uint32_t field_number, uint32_t wire_type) {
    return append_varint(buffer, ((uint64_t)field_number << 3) | wire_type);
}

static int append_length_delimited(oft_buffer *buffer, uint32_t field_number, const uint8_t *data, size_t length) {
    if (append_tag(buffer, field_number, 2) != 0) {
        return -1;
    }

    if (append_varint(buffer, length) != 0) {
        return -1;
    }

    return length > 0 ? oft_buffer_append(buffer, data, length) : 0;
}

static char *dup_range(const char *data, size_t length) {
    char *result = malloc(length + 1);
    if (!result) {
        return NULL;
    }

    if (length > 0) {
        memcpy(result, data, length);
    }

    result[length] = '\0';
    return result;
}

/* A read-only cursor over an encoded message, used to parse tag/value pairs one at a time. */
typedef struct {
    const uint8_t *data;
    size_t length;
    size_t pos;
} oft_reader;

static int reader_read_varint(oft_reader *reader, uint64_t *out) {
    uint64_t result = 0;
    int shift = 0;

    while (reader->pos < reader->length) {
        uint8_t b = reader->data[reader->pos++];
        result |= (uint64_t)(b & 0x7F) << shift;
        if ((b & 0x80) == 0) {
            *out = result;
            return 0;
        }

        shift += 7;
        if (shift > 63) {
            return -1;
        }
    }

    return -1;
}

static int reader_read_length_delimited(oft_reader *reader, const uint8_t **out_data, size_t *out_length) {
    uint64_t length;
    if (reader_read_varint(reader, &length) != 0) {
        return -1;
    }

    if (length > reader->length - reader->pos) {
        return -1;
    }

    *out_data = reader->data + reader->pos;
    *out_length = (size_t)length;
    reader->pos += (size_t)length;
    return 0;
}

static int reader_skip_field(oft_reader *reader, uint32_t wire_type) {
    if (wire_type == 0) {
        uint64_t value;
        return reader_read_varint(reader, &value);
    }

    if (wire_type == 2) {
        const uint8_t *data;
        size_t length;
        return reader_read_length_delimited(reader, &data, &length);
    }

    if (wire_type == 5) {
        if (reader->length - reader->pos < 4) {
            return -1;
        }

        reader->pos += 4;
        return 0;
    }

    if (wire_type == 1) {
        if (reader->length - reader->pos < 8) {
            return -1;
        }

        reader->pos += 8;
        return 0;
    }

    return -1;
}

void oft_hail_init(oft_hail *hail, const char *version, const char *info) {
    hail->version = dup_range(version, strlen(version));
    hail->info = dup_range(info, strlen(info));
}

void oft_hail_free(oft_hail *hail) {
    free(hail->version);
    free(hail->info);
    hail->version = NULL;
    hail->info = NULL;
}

int oft_hail_encode(const oft_hail *hail, oft_buffer *out) {
    const char *version = hail->version ? hail->version : "";
    const char *info = hail->info ? hail->info : "";

    if (append_length_delimited(out, 1, (const uint8_t *)version, strlen(version)) != 0) {
        return -1;
    }

    return append_length_delimited(out, 2, (const uint8_t *)info, strlen(info));
}

int oft_hail_decode(const uint8_t *data, size_t length, oft_hail *out) {
    oft_reader reader = {data, length, 0};
    out->version = NULL;
    out->info = NULL;

    while (reader.pos < reader.length) {
        uint64_t tag;
        if (reader_read_varint(&reader, &tag) != 0) {
            goto fail;
        }

        uint32_t field_number = (uint32_t)(tag >> 3);
        uint32_t wire_type = (uint32_t)(tag & 0x7);

        if (field_number == 1 && wire_type == 2) {
            const uint8_t *field_data;
            size_t field_length;
            if (reader_read_length_delimited(&reader, &field_data, &field_length) != 0) {
                goto fail;
            }

            free(out->version);
            out->version = dup_range((const char *)field_data, field_length);
        } else if (field_number == 2 && wire_type == 2) {
            const uint8_t *field_data;
            size_t field_length;
            if (reader_read_length_delimited(&reader, &field_data, &field_length) != 0) {
                goto fail;
            }

            free(out->info);
            out->info = dup_range((const char *)field_data, field_length);
        } else if (reader_skip_field(&reader, wire_type) != 0) {
            goto fail;
        }
    }

    if (!out->version) {
        out->version = dup_range("", 0);
    }

    if (!out->info) {
        out->info = dup_range("", 0);
    }

    return 0;

fail:
    free(out->version);
    free(out->info);
    out->version = NULL;
    out->info = NULL;
    return -1;
}

void oft_packet_init(oft_packet *packet, uint32_t control, const uint8_t *data, size_t length) {
    packet->control = control;
    packet->length = length;
    if (length > 0) {
        packet->data = malloc(length);
        if (packet->data) {
            memcpy(packet->data, data, length);
        }
    } else {
        packet->data = NULL;
    }
}

void oft_packet_free(oft_packet *packet) {
    free(packet->data);
    packet->data = NULL;
    packet->length = 0;
}

int oft_packet_encode(const oft_packet *packet, oft_buffer *out) {
    /*
     * Matches plain proto3 default-value omission - control is only emitted when nonzero, exactly
     * like the real protobuf runtimes the C#/Java ports use do for a field left at its default
     * value. This is safe because control 0 (Completion) is the only control value
     * that could ever be omitted this way, and a Completion packet is only ever the final chunk of
     * a message too large to fit in one packet (see Docs/OFT.md §4), so its data field is always
     * non-empty and alone always forces a nonzero-length frame, so it can never collide with Poll's
     * bare zero-length frame (Docs/OFT.md §4, §10) even with control omitted. Every other control
     * value is itself nonzero and is always emitted.
     */
    if (packet->control != 0) {
        if (append_tag(out, 1, 0) != 0) {
            return -1;
        }

        if (append_varint(out, packet->control) != 0) {
            return -1;
        }
    }

    if (packet->length > 0) {
        return append_length_delimited(out, 2, packet->data, packet->length);
    }

    return 0;
}

int oft_packet_decode(const uint8_t *data, size_t length, oft_packet *out) {
    oft_reader reader = {data, length, 0};
    out->control = 0;
    out->data = NULL;
    out->length = 0;

    while (reader.pos < reader.length) {
        uint64_t tag;
        if (reader_read_varint(&reader, &tag) != 0) {
            goto fail;
        }

        uint32_t field_number = (uint32_t)(tag >> 3);
        uint32_t wire_type = (uint32_t)(tag & 0x7);

        if (field_number == 1 && wire_type == 0) {
            uint64_t value;
            if (reader_read_varint(&reader, &value) != 0) {
                goto fail;
            }

            out->control = (uint32_t)value;
        } else if (field_number == 2 && wire_type == 2) {
            const uint8_t *field_data;
            size_t field_length;
            if (reader_read_length_delimited(&reader, &field_data, &field_length) != 0) {
                goto fail;
            }

            free(out->data);
            out->data = NULL;
            out->length = field_length;
            if (field_length > 0) {
                out->data = malloc(field_length);
                if (!out->data) {
                    goto fail;
                }

                memcpy(out->data, field_data, field_length);
            }
        } else if (reader_skip_field(&reader, wire_type) != 0) {
            goto fail;
        }
    }

    return 0;

fail:
    free(out->data);
    out->data = NULL;
    out->length = 0;
    return -1;
}
