#include "oft_event.h"

void oft_event_init(oft_event *event) {
    pthread_mutex_init(&event->mutex, NULL);
    pthread_cond_init(&event->cond, NULL);
    event->done = 0;
    event->result = 0;
}

void oft_event_destroy(oft_event *event) {
    pthread_mutex_destroy(&event->mutex);
    pthread_cond_destroy(&event->cond);
}

void oft_event_signal(oft_event *event, int result) {
    pthread_mutex_lock(&event->mutex);
    if (!event->done) {
        event->done = 1;
        event->result = result;
        pthread_cond_broadcast(&event->cond);
    }
    pthread_mutex_unlock(&event->mutex);
}

int oft_event_wait(oft_event *event) {
    pthread_mutex_lock(&event->mutex);
    while (!event->done) {
        pthread_cond_wait(&event->cond, &event->mutex);
    }
    int result = event->result;
    pthread_mutex_unlock(&event->mutex);
    return result;
}

int oft_event_poll(oft_event *event, int *out_result) {
    pthread_mutex_lock(&event->mutex);
    int done = event->done;
    if (done) {
        *out_result = event->result;
    }
    pthread_mutex_unlock(&event->mutex);
    return done;
}
