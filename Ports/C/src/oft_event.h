#ifndef OFT_EVENT_H
#define OFT_EVENT_H

#include <pthread.h>

/* A single-shot, thread-safe completion signal carrying a small integer result, roughly analogous
 * to a future. Used for correlating a sent packet with its Receipt, and for message/rekey
 * completion. */
typedef struct {
    pthread_mutex_t mutex;
    pthread_cond_t cond;
    int done;
    int result;
} oft_event;

void oft_event_init(oft_event *event);
void oft_event_destroy(oft_event *event);

/* Signals the event with the given result. Only the first call has any effect. */
void oft_event_signal(oft_event *event, int result);

/* Blocks until the event is signaled, and returns its result. */
int oft_event_wait(oft_event *event);

/* Returns non-zero if the event has already been signaled, writing its result to *out_result. */
int oft_event_poll(oft_event *event, int *out_result);

#endif
