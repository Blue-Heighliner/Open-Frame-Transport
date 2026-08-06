#ifndef OFT_WIRE_H
#define OFT_WIRE_H

#include <stddef.h>
#include <stdint.h>

/*
 * A minimal, hand-written encoder/decoder for exactly the two messages defined in ../../OFT.proto
 * (Hail and Packet) - not a general-purpose protobuf library. The wire format produced and consumed
 * here is standard protobuf binary encoding, so it interoperates with the C# and Java
 * implementations, which use the real protobuf runtime.
 */

/* A dynamically-growable byte buffer used to build outgoing messages. */
typedef struct {
    uint8_t *data;
    size_t length;
    size_t capacity;
} oft_buffer;

void oft_buffer_init(oft_buffer *buffer);
void oft_buffer_free(oft_buffer *buffer);
int oft_buffer_append(oft_buffer *buffer, const uint8_t *data, size_t length);

typedef struct {
    char *version;
    char *info;
} oft_hail;

typedef struct {
    uint32_t control;
    uint8_t *data;
    size_t length;
} oft_packet;

/* Takes ownership of nothing; copies version/info. */
void oft_hail_init(oft_hail *hail, const char *version, const char *info);
void oft_hail_free(oft_hail *hail);
int oft_hail_encode(const oft_hail *hail, oft_buffer *out);

/* On success (0), out->version and out->info are newly allocated and must be freed via oft_hail_free. */
int oft_hail_decode(const uint8_t *data, size_t length, oft_hail *out);

/* Copies data. */
void oft_packet_init(oft_packet *packet, uint32_t control, const uint8_t *data, size_t length);
void oft_packet_free(oft_packet *packet);
int oft_packet_encode(const oft_packet *packet, oft_buffer *out);

/* On success (0), out->data is newly allocated (or NULL if empty) and must be freed via oft_packet_free. */
int oft_packet_decode(const uint8_t *data, size_t length, oft_packet *out);

#endif
