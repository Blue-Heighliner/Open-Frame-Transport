mod support;

use oft::{ConnectionOptions, OwnedIdentity, Peer, PeerOptions, SecurityMode};
use rustls::pki_types::CertificateDer;
use std::sync::mpsc;
use std::sync::Arc;
use std::time::Duration;
use support::{generate_test_identity_and_cert, reserve_free_port, root_store_trusting, wait_until};

/// Every `Peer` in a given test must share the *same* identity/cert (not one generated per call) -
/// each side validates the other's certificate under `DualAuthentication`, so two independently
/// generated self-signed certs would never trust one another.
fn shared_test_identity() -> (OwnedIdentity, CertificateDer<'static>) {
    generate_test_identity_and_cert()
}

fn make_peer_options(identity: &OwnedIdentity, cert: &CertificateDer<'static>) -> PeerOptions {
    PeerOptions {
        connection: ConnectionOptions {
            security_mode: SecurityMode::DualAuthentication,
            identity: Some(identity.clone()),
            root_certificates: Some(Arc::new(root_store_trusting(cert))),
            ..Default::default()
        },
        ..Default::default()
    }
}

#[test]
fn create_with_server_authentication_mode_fails() {
    let result = Peer::new(Some(PeerOptions {
        connection: ConnectionOptions {
            security_mode: SecurityMode::ServerAuthentication,
            ..Default::default()
        },
        ..Default::default()
    }));
    assert!(result.is_err());
}

#[test]
fn send_reuses_connection_across_calls() {
    let (identity, cert) = shared_test_identity();
    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let caller = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    caller.send("127.0.0.1", port, b"first".to_vec(), 0, None).unwrap().wait().unwrap();
    caller.send("127.0.0.1", port, b"second".to_vec(), 0, None).unwrap().wait().unwrap();

    assert!(wait_until(|| listener_peer.tracked_connection_count() == 1, Duration::from_secs(5)));

    caller.close();
    listener_peer.close();
}

#[test]
fn received_delivers_with_identity() {
    let (identity, cert) = shared_test_identity();
    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let (tx, rx) = mpsc::channel();
    listener_peer.set_received_handler(Some(Arc::new(move |identity, data| {
        tx.send((identity.info, data)).unwrap();
    })));

    let mut caller_options = make_peer_options(&identity, &cert);
    caller_options.connection.info = "caller".to_string();
    let caller = Peer::new(Some(caller_options)).unwrap();
    caller.send("127.0.0.1", port, b"hello listener".to_vec(), 0, None).unwrap().wait().unwrap();

    let (info, data) = rx.recv_timeout(Duration::from_secs(5)).unwrap();
    assert_eq!(info, "caller");
    assert_eq!(data, b"hello listener");

    caller.close();
    listener_peer.close();
}

#[test]
fn send_with_tag_raises_delivery_status_handler_with_tag_ending_in_acknowledged() {
    let (identity, cert) = shared_test_identity();
    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let caller = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    let (tx, rx) = mpsc::channel();
    caller.set_delivery_status_handler(Some(Arc::new(move |tag: &oft::Tag, status| {
        if status == oft::DeliveryStatus::Acknowledged {
            tx.send(*tag.downcast_ref::<u32>().unwrap()).unwrap();
        }
    })));

    caller.send("127.0.0.1", port, b"hi".to_vec(), 0, Some(Box::new(7u32))).unwrap().wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), 7u32);

    caller.close();
    listener_peer.close();
}

#[test]
fn send_without_tag_never_raises_delivery_status_handler() {
    let (identity, cert) = shared_test_identity();
    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let caller = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    let raised = Arc::new(std::sync::atomic::AtomicBool::new(false));
    let raised_for_handler = raised.clone();
    caller.set_delivery_status_handler(Some(Arc::new(move |_tag, _status| {
        raised_for_handler.store(true, std::sync::atomic::Ordering::SeqCst);
    })));

    caller.send("127.0.0.1", port, b"hi".to_vec(), 0, None).unwrap().wait().unwrap();
    std::thread::sleep(Duration::from_millis(200));
    assert!(!raised.load(std::sync::atomic::Ordering::SeqCst));

    caller.close();
    listener_peer.close();
}

#[test]
fn outbound_only_peer_has_no_local_endpoint() {
    let (identity, cert) = shared_test_identity();
    let peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    assert!(peer.local_endpoint().is_none());
    peer.stop_listening(); // no-op when never listening
    peer.close();
}

#[test]
fn send_to_unreachable_host_fails() {
    let (identity, cert) = shared_test_identity();
    let peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    let port = reserve_free_port();
    let result = peer.send("127.0.0.1", port, b"hi".to_vec(), 0, None);
    assert!(result.is_err());
    peer.close();
}

#[test]
fn rekey_rekeys_outbound_and_inbound_connections() {
    let (identity, cert) = shared_test_identity();
    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let (tx, rx) = mpsc::channel();
    listener_peer.set_received_handler(Some(Arc::new(move |_identity, data| tx.send(data).unwrap())));

    let caller = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    caller.send("127.0.0.1", port, b"hello".to_vec(), 0, None).unwrap().wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), b"hello");

    caller.rekey().unwrap();
    listener_peer.rekey().unwrap();

    caller.send("127.0.0.1", port, b"post-rekey".to_vec(), 0, None).unwrap().wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), b"post-rekey");

    caller.close();
    listener_peer.close();
}

#[test]
fn drop_disconnects_outbound_and_inbound_connections() {
    let (identity, cert) = shared_test_identity();
    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let caller = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    caller.send("127.0.0.1", port, b"hi".to_vec(), 0, None).unwrap().wait().unwrap();
    assert!(wait_until(|| listener_peer.tracked_connection_count() == 1, Duration::from_secs(5)));

    caller.drop().unwrap();

    assert!(wait_until(|| listener_peer.tracked_connection_count() == 0, Duration::from_secs(5)));

    caller.close();
    listener_peer.close();
}

#[test]
fn drop_peer_remains_usable_afterward() {
    let (identity, cert) = shared_test_identity();
    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let caller = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    caller.send("127.0.0.1", port, b"first".to_vec(), 0, None).unwrap().wait().unwrap();

    caller.drop().unwrap();
    std::thread::sleep(Duration::from_millis(100));

    assert!(caller.is_connected());
    caller.send("127.0.0.1", port, b"second".to_vec(), 0, None).unwrap().wait().unwrap();

    caller.close();
    listener_peer.close();
}

#[test]
fn close_called_twice_is_idempotent() {
    let (identity, cert) = shared_test_identity();
    let peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    peer.close();
    peer.close();
}

#[test]
fn is_connected_false_after_close() {
    let (identity, cert) = shared_test_identity();
    let peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    assert!(peer.is_connected());
    peer.close();
    assert!(!peer.is_connected());
}

#[test]
fn send_after_close_fails() {
    let (identity, cert) = shared_test_identity();
    let peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    peer.close();
    assert!(peer.send("127.0.0.1", 12345, b"hi".to_vec(), 0, None).is_err());
}

#[test]
fn rekey_after_close_fails() {
    let (identity, cert) = shared_test_identity();
    let peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    peer.close();
    assert!(peer.rekey().is_err());
}

#[test]
fn listen_after_close_fails() {
    let (identity, cert) = shared_test_identity();
    let peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    peer.close();
    assert!(peer.listen("127.0.0.1", 0).is_err());
}

// ---- Eviction: drive run_eviction_now() directly, bypassing the real fixed 30s/30s schedule. ----

#[test]
fn eviction_disconnects_idle_connections_past_grace_period() {
    let (identity, cert) = shared_test_identity();
    let mut options = make_peer_options(&identity, &cert);
    options.idle_timeout = Some(Duration::from_millis(1));

    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let peer = Peer::new(Some(options)).unwrap();
    peer.send("127.0.0.1", port, b"hi".to_vec(), 0, None).unwrap().wait().unwrap();

    std::thread::sleep(Duration::from_millis(50));

    // Run eviction twice: the first pass only starts the grace-period clock (has_pending_data just
    // cleared), the second - after the grace period constant has elapsed - actually evicts.
    peer.run_eviction_now();
    std::thread::sleep(Duration::from_secs(31));
    peer.run_eviction_now();

    assert!(wait_until(|| peer.tracked_connection_count() == 0, Duration::from_secs(5)));

    peer.close();
    listener_peer.close();
}

#[test]
fn eviction_skips_connections_within_grace_period() {
    let (identity, cert) = shared_test_identity();
    let mut options = make_peer_options(&identity, &cert);
    options.idle_timeout = Some(Duration::from_millis(1));

    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let peer = Peer::new(Some(options)).unwrap();
    peer.send("127.0.0.1", port, b"hi".to_vec(), 0, None).unwrap().wait().unwrap();

    peer.run_eviction_now(); // pending_data_cleared_at is set to "now" on this first pass
    assert_eq!(peer.tracked_connection_count(), 1);

    peer.close();
    listener_peer.close();
}

#[test]
fn eviction_skips_connections_with_pending_data() {
    let (identity, cert) = shared_test_identity();
    let mut options = make_peer_options(&identity, &cert);
    options.idle_timeout = Some(Duration::from_millis(1));
    options.max_packet_data_size = 8;

    let listener_peer = Peer::new(Some(make_peer_options(&identity, &cert))).unwrap();
    listener_peer.listen("127.0.0.1", 0).unwrap();
    let port = listener_peer.local_endpoint().unwrap().port();

    let peer = Peer::new(Some(options)).unwrap();
    let handle = peer.send("127.0.0.1", port, vec![0u8; 4000], 0, None).unwrap();

    peer.run_eviction_now();
    assert_eq!(peer.tracked_connection_count(), 1);

    handle.wait().unwrap();
    peer.close();
    listener_peer.close();
}
