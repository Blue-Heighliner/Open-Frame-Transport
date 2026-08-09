mod support;

use oft::{connect, host, ConnectionOptions};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use std::time::Duration;
use support::{reserve_free_port, wait_until};

#[test]
fn host_with_default_options_and_connect_succeed() {
    let listener = host("127.0.0.1", 0, None).unwrap();
    let port = listener.local_endpoint().port();
    let client = connect("127.0.0.1", port, None).unwrap();
    assert!(client.is_connected());
    client.close();
    listener.close();
}

#[test]
fn connect_to_nothing_listening_fails() {
    let port = reserve_free_port();
    let result = connect("127.0.0.1", port, None);
    assert!(result.is_err());
}

#[test]
fn connected_handler_assigned_after_accept_still_receives_it() {
    let listener = host("127.0.0.1", 0, None).unwrap();
    let port = listener.local_endpoint().port();
    let client = connect("127.0.0.1", port, None).unwrap();

    // Give the accept loop a moment to actually accept and raise the (as-yet-unassigned)
    // connected notification, proving it gets buffered rather than lost.
    std::thread::sleep(Duration::from_millis(100));

    let accepted = Arc::new(AtomicBool::new(false));
    let accepted_for_handler = accepted.clone();
    listener.set_connected_handler(Some(Arc::new(move |connection| {
        accepted_for_handler.store(true, Ordering::SeqCst);
        std::mem::forget(connection);
    })));

    assert!(wait_until(|| accepted.load(Ordering::SeqCst), Duration::from_secs(5)));
    client.close();
    listener.close();
}

#[test]
fn listener_close_does_not_affect_already_accepted_connections() {
    let listener = host("127.0.0.1", 0, None).unwrap();
    let port = listener.local_endpoint().port();

    let accepted: Arc<std::sync::Mutex<Option<oft::Connection>>> = Arc::new(std::sync::Mutex::new(None));
    let accepted_for_handler = accepted.clone();
    listener.set_connected_handler(Some(Arc::new(move |connection| {
        *accepted_for_handler.lock().unwrap() = Some(connection);
    })));

    let client = connect("127.0.0.1", port, None).unwrap();
    assert!(wait_until(|| accepted.lock().unwrap().is_some(), Duration::from_secs(5)));

    listener.close();

    let server = accepted.lock().unwrap().take().unwrap();
    assert!(server.is_connected());
    server.send(b"still alive".to_vec(), 0, None).wait().unwrap();

    client.close();
    server.close();
}

#[test]
fn local_endpoint_reports_bound_port() {
    let listener = host("127.0.0.1", 0, None).unwrap();
    assert_ne!(listener.local_endpoint().port(), 0);
    listener.close();
}

#[test]
fn ipv6_loopback_binds_and_connects() {
    let listener = host("::1", 0, Some(ConnectionOptions { security_mode: oft::SecurityMode::Trusted, ..Default::default() })).unwrap();
    let port = listener.local_endpoint().port();
    let client = connect("::1", port, Some(ConnectionOptions { security_mode: oft::SecurityMode::Trusted, ..Default::default() })).unwrap();
    assert!(client.is_connected());
    client.close();
    listener.close();
}
