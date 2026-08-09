use std::future::Future;
use std::pin::Pin;
use std::sync::{Arc, Condvar, Mutex};
use std::task::{Context, Poll, Waker};
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

struct State {
    outcome: Outcome,
    /// Woken once `outcome` leaves `Pending`, for `SendHandle`'s `Future` impl - `None` until
    /// something has actually polled it (blocking callers using `wait`/`wait_timeout` never touch
    /// this at all, since they block on `condvar` instead).
    waker: Option<Waker>,
}

pub(crate) struct Completion {
    state: Mutex<State>,
    condvar: Condvar,
}

impl Completion {
    pub(crate) fn new() -> Arc<Self> {
        Arc::new(Completion {
            state: Mutex::new(State { outcome: Outcome::Pending, waker: None }),
            condvar: Condvar::new(),
        })
    }

    pub(crate) fn complete(&self, outcome: Result<(), SendFailure>) {
        let mut guard = self.state.lock().unwrap();
        if guard.outcome == Outcome::Pending {
            guard.outcome = match outcome {
                Ok(()) => Outcome::Delivered,
                Err(failure) => Outcome::Failed(failure),
            };
            self.condvar.notify_all();
            if let Some(waker) = guard.waker.take() {
                waker.wake();
            }
        }
    }
}

/// Returned by `Connection::send`/`Peer::send`: lets a caller wait for delivery or cancel a
/// previously queued send. Both a blocking API (`wait`/`wait_timeout`) and, since this crate
/// stays runtime-agnostic and brings in no async executor of its own (see `Docs/Rust.md`'s
/// "Concurrency model" section), a hand-rolled `Future` impl are available on the same handle -
/// `.await` it directly under any executor (tokio, `async-std`, `pollster`, ...) instead of
/// calling `wait()`, without this crate needing to depend on one itself.
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
            .wait_while(guard, |state| state.outcome == Outcome::Pending)
            .unwrap();
        match guard.outcome {
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
            .wait_timeout_while(guard, timeout, |state| state.outcome == Outcome::Pending)
            .unwrap();
        if result.timed_out() {
            return None;
        }

        Some(match guard.outcome {
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

impl Future for SendHandle {
    type Output = Result<(), SendFailure>;

    fn poll(self: Pin<&mut Self>, cx: &mut Context<'_>) -> Poll<Self::Output> {
        let mut guard = self.completion.state.lock().unwrap();
        match guard.outcome {
            Outcome::Pending => {
                guard.waker = Some(cx.waker().clone());
                Poll::Pending
            }
            Outcome::Delivered => Poll::Ready(Ok(())),
            Outcome::Failed(failure) => Poll::Ready(Err(failure)),
        }
    }
}
