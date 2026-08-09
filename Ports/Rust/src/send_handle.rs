use std::sync::{Arc, Condvar, Mutex};
use std::time::Duration;

/// Why a queued send never delivered.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SendFailure {
    /// Cancelled via `SendHandle::cancel` before it completed (see `Docs/OFT.md` §7).
    Cancelled,
    /// The connection closed before this message was fully delivered and acknowledged.
    Disconnected,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum Outcome {
    Pending,
    Delivered,
    Failed(SendFailure),
}

pub(crate) struct Completion {
    state: Mutex<Outcome>,
    condvar: Condvar,
}

impl Completion {
    pub(crate) fn new() -> Arc<Self> {
        Arc::new(Completion {
            state: Mutex::new(Outcome::Pending),
            condvar: Condvar::new(),
        })
    }

    pub(crate) fn complete(&self, outcome: Result<(), SendFailure>) {
        let mut guard = self.state.lock().unwrap();
        if *guard == Outcome::Pending {
            *guard = match outcome {
                Ok(()) => Outcome::Delivered,
                Err(failure) => Outcome::Failed(failure),
            };
            self.condvar.notify_all();
        }
    }
}

/// Returned by `Connection::send`/`Peer::send`: lets a caller wait for delivery or cancel a
/// previously queued send.
pub struct SendHandle {
    pub(crate) completion: Arc<Completion>,
    pub(crate) cancel: Arc<dyn Fn() + Send + Sync>,
}

impl SendHandle {
    /// Blocks until the send is fully delivered and acknowledged, cancelled, or the connection
    /// disconnects.
    pub fn wait(&self) -> Result<(), SendFailure> {
        let guard = self.completion.state.lock().unwrap();
        let guard = self
            .completion
            .condvar
            .wait_while(guard, |state| *state == Outcome::Pending)
            .unwrap();
        match *guard {
            Outcome::Pending => unreachable!(),
            Outcome::Delivered => Ok(()),
            Outcome::Failed(failure) => Err(failure),
        }
    }

    /// Like `wait`, but returns `None` instead of blocking indefinitely if `timeout` elapses
    /// first.
    pub fn wait_timeout(&self, timeout: Duration) -> Option<Result<(), SendFailure>> {
        let guard = self.completion.state.lock().unwrap();
        let (guard, result) = self
            .completion
            .condvar
            .wait_timeout_while(guard, timeout, |state| *state == Outcome::Pending)
            .unwrap();
        if result.timed_out() {
            return None;
        }

        Some(match *guard {
            Outcome::Pending => unreachable!(),
            Outcome::Delivered => Ok(()),
            Outcome::Failed(failure) => Err(failure),
        })
    }

    /// Cancels this send (see `Docs/OFT.md` §7): immediately if it hasn't started, or by sending a
    /// Cancellation packet if it has already begun. A no-op if it has already completed.
    pub fn cancel(&self) {
        (self.cancel)();
    }
}
