#include "oft_event_buffer.h"

#include <stdlib.h>

void oft_event_buffer_init(oft_event_buffer *buffer, oft_event_buffer_free_item free_item) {
    pthread_mutex_init(&buffer->mutex, NULL);
    buffer->head = NULL;
    buffer->tail = NULL;
    buffer->ever_attached = 0;
    buffer->dispatch = NULL;
    buffer->user_data = NULL;
    buffer->free_item = free_item;
}

void oft_event_buffer_destroy(oft_event_buffer *buffer) {
    pthread_mutex_lock(&buffer->mutex);
    oft_event_buffer_node *node = buffer->head;
    buffer->head = NULL;
    buffer->tail = NULL;
    pthread_mutex_unlock(&buffer->mutex);

    while (node) {
        oft_event_buffer_node *next = node->next;
        if (buffer->free_item) {
            buffer->free_item(node->item);
        }

        free(node);
        node = next;
    }

    pthread_mutex_destroy(&buffer->mutex);
}

void oft_event_buffer_raise(oft_event_buffer *buffer, void *item) {
    pthread_mutex_lock(&buffer->mutex);

    if (!buffer->ever_attached) {
        oft_event_buffer_node *node = malloc(sizeof(oft_event_buffer_node));
        if (!node) {
            pthread_mutex_unlock(&buffer->mutex);
            if (buffer->free_item) {
                buffer->free_item(item);
            }

            return;
        }

        node->item = item;
        node->next = NULL;

        if (buffer->tail) {
            buffer->tail->next = node;
        } else {
            buffer->head = node;
        }

        buffer->tail = node;
        pthread_mutex_unlock(&buffer->mutex);
        return;
    }

    oft_event_buffer_dispatch dispatch = buffer->dispatch;
    void *user_data = buffer->user_data;
    pthread_mutex_unlock(&buffer->mutex);

    if (dispatch) {
        dispatch(user_data, item);
    } else if (buffer->free_item) {
        buffer->free_item(item);
    }
}

void oft_event_buffer_attach(oft_event_buffer *buffer, oft_event_buffer_dispatch dispatch, void *user_data) {
    pthread_mutex_lock(&buffer->mutex);

    oft_event_buffer_node *backlog = NULL;
    oft_event_buffer_dispatch flush_dispatch = NULL;
    void *flush_user_data = NULL;

    if (!buffer->ever_attached && dispatch) {
        buffer->ever_attached = 1;
        backlog = buffer->head;
        buffer->head = NULL;
        buffer->tail = NULL;
        flush_dispatch = dispatch;
        flush_user_data = user_data;
    }

    buffer->dispatch = dispatch;
    buffer->user_data = user_data;
    pthread_mutex_unlock(&buffer->mutex);

    while (backlog) {
        oft_event_buffer_node *next = backlog->next;
        flush_dispatch(flush_user_data, backlog->item);
        free(backlog);
        backlog = next;
    }
}
