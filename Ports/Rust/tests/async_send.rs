mod support;

use oft::ConnectionOptions;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;
use support::{establish, wait_until};

/// `SendHandle` implements `std::future::Future` (see its own doc comment) in addition to its
/// blocking `wait()`/`wait_timeout()` - this crate brings in no async runtime of its own, so
/// `pollster` (a minimal "block one future to completion" executor, not a full runtime) stands in
/// here for whatever real executor an application would actually use (tokio, `async-std`, ...).
#[test]
fn send_handle_can_be_awaited_under_any_executor() {
    let pair = establish(ConnectionOptions::default());

    let received = Arc::new(AtomicBool::new(false));
    let received_for_handler = received.clone();
    pair.server.set_received_handler(Some(Arc::new(move |data: Vec<u8>| {
        assert_eq!(data, b"hello via await");
        received_for_handler.store(true, Ordering::SeqCst);
    })));

    let handle = pair.client.send(b"hello via await".to_vec(), 0, None);
    pollster::block_on(handle).expect("send should be delivered and acknowledged");

    assert!(wait_until(|| received.load(Ordering::SeqCst), Duration::from_secs(10)), "message was never received");

    pair.close();
}

/// A cancelled send's `Future` resolves to `Err(SendFailure::Cancelled)`, exactly like `wait()`
/// does for a blocking caller - the two APIs observe the same underlying `Completion`.
#[test]
fn send_handle_future_reports_cancellation() {
    let pair = establish(ConnectionOptions::default());

    let handle = pair.client.send(vec![0u8; 1], 0, None);
    handle.cancel();

    let result = pollster::block_on(handle);
    assert!(result.is_err());

    pair.close();
}
