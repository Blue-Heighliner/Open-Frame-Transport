use std::sync::{Arc, Mutex};

type Handler<T> = Arc<dyn Fn(T) + Send + Sync>;

/// A single-slot callback that buffers everything raised before the *first-ever* handler is
/// assigned, then flushes it in order - the same pattern used by every other port
/// (`OftBufferedHandlerSlot`/`BufferedHandlerSlot`/`oft_event_buffer`) to avoid a
/// connect/disconnect/receive message-loss race between a connection/listener becoming live and
/// the caller getting a chance to assign a handler for it. See `Docs/Architecture.md`'s "Buffered
/// notifications" section.
pub(crate) struct BufferedSlot<T> {
    state: Mutex<State<T>>,
}

enum State<T> {
    Buffering(Vec<T>),
    Assigned(Option<Handler<T>>),
}

impl<T: Send + 'static> BufferedSlot<T> {
    pub(crate) fn new() -> Self {
        BufferedSlot {
            state: Mutex::new(State::Buffering(Vec::new())),
        }
    }

    pub(crate) fn raise(&self, value: T) {
        let mut guard = self.state.lock().unwrap();
        match &mut *guard {
            State::Buffering(buffer) => buffer.push(value),
            State::Assigned(Some(handler)) => {
                let handler = handler.clone();
                drop(guard);
                handler(value);
            }
            State::Assigned(None) => {}
        }
    }

    pub(crate) fn set_handler(&self, handler: Option<Handler<T>>) {
        let buffered: Vec<T> = {
            let mut guard = self.state.lock().unwrap();
            let previous = std::mem::replace(&mut *guard, State::Assigned(handler.clone()));
            match previous {
                State::Buffering(buffer) => buffer,
                State::Assigned(_) => Vec::new(),
            }
        };

        if let Some(handler) = handler {
            for value in buffered {
                handler(value);
            }
        }
    }
}
