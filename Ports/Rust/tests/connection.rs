mod support;

use oft::{ConnectionOptions, SecurityMode};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::sync::Arc;
use std::time::Duration;
use support::{establish, wait_until, Pair};

fn trusted_options() -> ConnectionOptions {
    ConnectionOptions {
        security_mode: SecurityMode::Trusted,
        ..Default::default()
    }
}

#[test]
fn hail_info_is_exchanged() {
    let pair = establish(ConnectionOptions {
        info: "client-info".to_string(),
        ..trusted_options()
    });
    assert_eq!(pair.server.identity().info, "client-info");
    pair.close();
}

#[test]
fn trusted_identity_has_no_certificate() {
    let pair = establish(trusted_options());
    assert!(pair.client.identity().certificate.is_none());
    assert!(pair.server.identity().certificate.is_none());
    pair.close();
}

#[test]
fn small_message_is_delivered() {
    let pair = establish(trusted_options());
    let (tx, rx) = mpsc::channel();
    pair.server.set_received_handler(Some(Arc::new(move |data| tx.send(data).unwrap())));

    pair.client.send(b"hello".to_vec(), 0, None).wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), b"hello");
    pair.close();
}

#[test]
fn empty_message_is_delivered() {
    let pair = establish(trusted_options());
    let (tx, rx) = mpsc::channel();
    pair.server.set_received_handler(Some(Arc::new(move |data| tx.send(data).unwrap())));

    pair.client.send(Vec::new(), 0, None).wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), Vec::<u8>::new());
    pair.close();
}

#[test]
fn large_message_is_split_and_reassembled() {
    let pair = establish(ConnectionOptions {
        max_packet_data_size: 16,
        ..trusted_options()
    });
    let (tx, rx) = mpsc::channel();
    pair.server.set_received_handler(Some(Arc::new(move |data| tx.send(data).unwrap())));

    let payload = vec![0xABu8; 500];
    pair.client.send(payload.clone(), 0, None).wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), payload);
    pair.close();
}

#[test]
fn one_byte_over_packet_size_splits_with_minimal_final_chunk() {
    let pair = establish(ConnectionOptions {
        max_packet_data_size: 16,
        ..trusted_options()
    });
    let (tx, rx) = mpsc::channel();
    pair.server.set_received_handler(Some(Arc::new(move |data| tx.send(data).unwrap())));

    let payload = vec![0x11u8; 17];
    pair.client.send(payload.clone(), 0, None).wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), payload);
    pair.close();
}

#[test]
fn higher_priority_interrupts_lower_priority() {
    let pair = establish(ConnectionOptions {
        max_packet_data_size: 8,
        ..trusted_options()
    });
    let received: Arc<std::sync::Mutex<Vec<Vec<u8>>>> = Arc::new(std::sync::Mutex::new(Vec::new()));
    let received_for_handler = received.clone();
    pair.server.set_received_handler(Some(Arc::new(move |data| received_for_handler.lock().unwrap().push(data))));

    let low = vec![b'a'; 2000];
    let high = vec![b'b'; 8];
    let low_handle = pair.client.send(low.clone(), 0, None);
    let high_handle = pair.client.send(high.clone(), 10, None);

    low_handle.wait().unwrap();
    high_handle.wait().unwrap();

    let order = received.lock().unwrap().clone();
    assert_eq!(order.len(), 2);
    assert_eq!(order[0], high, "the higher-priority message should have been delivered first");
    assert_eq!(order[1], low);
    pair.close();
}

#[test]
fn cancel_before_start_never_delivered() {
    let pair = establish(ConnectionOptions {
        max_packet_data_size: 16,
        ..trusted_options()
    });
    let (tx, rx) = mpsc::channel();
    pair.server.set_received_handler(Some(Arc::new(move |data| tx.send(data).unwrap())));

    // Queue a large higher-priority multi-packet message first so the cancelled one (lower
    // priority, also multi-packet) never gets a turn before we cancel it. Different priorities
    // also avoid colliding on the receiver's single per-priority-channel reassembly buffer
    // (Docs/OFT.md §4.4 assumes at most one in-progress message per priority channel at a time).
    let blocker_payload = vec![0u8; 100_000];
    let cancelled_payload = vec![b'n'; 64];
    let blocker = pair.client.send(blocker_payload.clone(), 5, None);
    let handle = pair.client.send(cancelled_payload.clone(), 0, None);
    handle.cancel();

    assert_eq!(handle.wait(), Err(oft::SendFailure::Cancelled));
    blocker.wait().unwrap(); // loopback is fast enough that this may well finish before we check

    // The cancelled payload must never show up among whatever the blocker (and only the blocker)
    // delivered.
    let received = rx.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(received, blocker_payload);
    assert_ne!(received, cancelled_payload);
    assert!(rx.try_recv().is_err(), "nothing besides the blocker should ever have been delivered");
    pair.close();
}

#[test]
fn send_with_tag_raises_acknowledged_handler() {
    let pair = establish(trusted_options());
    let (tx, rx) = mpsc::channel();
    pair.client.set_acknowledged_handler(Some(Arc::new(move |tag: oft::Tag| {
        let tag = tag.downcast::<u32>().unwrap();
        tx.send(*tag).unwrap();
    })));

    pair.client.send(b"hi".to_vec(), 0, Some(Box::new(42u32))).wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), 42u32);
    pair.close();
}

#[test]
fn send_without_tag_never_raises_acknowledged_handler() {
    let pair = establish(trusted_options());
    let raised = Arc::new(AtomicBool::new(false));
    let raised_for_handler = raised.clone();
    pair.client.set_acknowledged_handler(Some(Arc::new(move |_tag| raised_for_handler.store(true, Ordering::SeqCst))));

    pair.client.send(b"hi".to_vec(), 0, None).wait().unwrap();
    std::thread::sleep(Duration::from_millis(200));
    assert!(!raised.load(Ordering::SeqCst));
    pair.close();
}

#[test]
fn disconnected_handler_is_notified() {
    let pair = establish(trusted_options());
    let disconnected = Arc::new(AtomicBool::new(false));
    let disconnected_for_handler = disconnected.clone();
    pair.server.set_disconnected_handler(Some(Arc::new(move |_reason| disconnected_for_handler.store(true, Ordering::SeqCst))));

    pair.client.disconnect();
    assert!(wait_until(|| disconnected.load(Ordering::SeqCst), Duration::from_secs(5)));
    pair.listener.close();
}

#[test]
fn is_connected_false_after_close() {
    let pair = establish(trusted_options());
    assert!(pair.client.is_connected());
    pair.client.close();
    assert!(!pair.client.is_connected());
    pair.server.close();
    pair.listener.close();
}

#[test]
fn send_after_close_fails() {
    let pair = establish(trusted_options());
    pair.client.close();
    let handle = pair.client.send(b"hi".to_vec(), 0, None);
    assert_eq!(handle.wait(), Err(oft::SendFailure::Disconnected));
    pair.server.close();
    pair.listener.close();
}

#[test]
fn rekey_on_trusted_connection_is_noop() {
    let pair = establish(trusted_options());
    pair.client.rekey().unwrap(); // must not error, even though there's no TLS session
    pair.client.send(b"still works".to_vec(), 0, None).wait().unwrap();
    pair.close();
}

#[test]
fn has_pending_data_reflects_in_flight_sends() {
    let pair = establish(ConnectionOptions {
        max_packet_data_size: 8,
        ..trusted_options()
    });
    assert!(!pair.client.has_pending_data());

    // Large enough (relative to max_packet_data_size) that the transfer reliably takes longer than
    // wait_until's poll interval, even over a fast loopback connection - otherwise it can finish
    // before the very first poll ever observes has_pending_data() as true.
    let handle = pair.client.send(vec![0u8; 200_000], 0, None);
    assert!(wait_until(|| pair.client.has_pending_data(), Duration::from_secs(2)));

    handle.wait().unwrap();
    assert!(wait_until(|| !pair.client.has_pending_data(), Duration::from_secs(2)));
    pair.close();
}

#[test]
fn secure_pair_helper_smoke() {
    let pair: Pair = establish(ConnectionOptions::default());
    let (tx, rx) = mpsc::channel();
    pair.server.set_received_handler(Some(Arc::new(move |data| tx.send(data).unwrap())));
    pair.client.send(b"over-tls".to_vec(), 0, None).wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), b"over-tls");
    pair.close();
}
