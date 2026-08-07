#ifndef OFT_EVENT_BUFFER_H
#define OFT_EVENT_BUFFER_H

#include <pthread.h>

/* Called with an item previously passed to oft_event_buffer_raise(), once a dispatch target is
 * attached to receive it - either immediately (already attached) or later, as part of the backlog
 * flush when the first dispatch target ever attaches. */
typedef void (*oft_event_buffer_dispatch)(void *user_data, void *item);

/* Called to release an item that will never be dispatched (the buffer is destroyed, or an item is
 * raised while attached to a NULL dispatch target) - e.g. to free() it or anything it owns. May be
 * NULL if items need no cleanup (e.g. a non-owning pointer). */
typedef void (*oft_event_buffer_free_item)(void *item);

typedef struct oft_event_buffer_node {
    void *item;
    struct oft_event_buffer_node *next;
} oft_event_buffer_node;

/*
 * A single-target buffer of opaque items that holds onto everything raised via
 * oft_event_buffer_raise() until oft_event_buffer_attach() is first called with a non-NULL dispatch
 * function, at which point the backlog is flushed, in order, to that dispatch function before it
 * becomes the live target for all future raises. Mirrors the same core guarantee as the C#/Java
 * reference implementations' buffered handler-slot types (OftBufferedHandlerSlot<THandler> /
 * BufferedHandlerSlot<H>), adapted for C's plain-callback idiom in place of a handler object: at
 * most one dispatch target is ever live here, and a later oft_event_buffer_attach() call always
 * replaces it (matching oft_connection_set_received_callback()'s "there is only ever one"
 * semantics) - only the *first* such call (the one that transitions the buffer out of pure
 * buffering) triggers a flush.
 *
 * Thread-safe: oft_event_buffer_raise() and oft_event_buffer_attach() may be called concurrently
 * from any thread.
 */
typedef struct {
    pthread_mutex_t mutex;
    oft_event_buffer_node *head;
    oft_event_buffer_node *tail;
    int ever_attached;
    oft_event_buffer_dispatch dispatch;
    void *user_data;
    oft_event_buffer_free_item free_item;
} oft_event_buffer;

void oft_event_buffer_init(oft_event_buffer *buffer, oft_event_buffer_free_item free_item);

/* Discards (and, via free_item, frees) anything still buffered for lack of a dispatch target. */
void oft_event_buffer_destroy(oft_event_buffer *buffer);

/*
 * Raises item (ownership transfers to the buffer/dispatch target). If a dispatch target is already
 * attached, calls it synchronously on the caller's thread (or frees item via free_item if the
 * current target is NULL); otherwise appends item to the backlog for a later flush.
 */
void oft_event_buffer_raise(oft_event_buffer *buffer, void *item);

/*
 * Attaches dispatch/user_data as the buffer's live target, replacing any previous one. If this is
 * the first time this buffer has ever been attached to a non-NULL dispatch function, first flushes
 * the entire backlog to it, in order.
 */
void oft_event_buffer_attach(oft_event_buffer *buffer, oft_event_buffer_dispatch dispatch, void *user_data);

#endif
