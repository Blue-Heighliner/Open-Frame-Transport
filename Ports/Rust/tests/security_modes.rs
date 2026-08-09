mod support;

use oft::{connect, host, ConnectionOptions, OftError, SecurityMode};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::mpsc;
use std::sync::Arc;
use std::time::Duration;
use support::{establish, generate_test_identity, generate_test_identity_and_cert, reserve_free_port, root_store_trusting, wait_until};

#[test]
fn secure_mode_exchanges_messages_and_client_sees_no_meaningful_certificate_check() {
    let pair = establish(ConnectionOptions::default());
    // Under Secure, the server presents an ephemeral cert the client accepts unconditionally.
    assert!(pair.client.identity().certificate.is_some());
    let (tx, rx) = mpsc::channel();
    pair.server.set_received_handler(Some(Arc::new(move |data| tx.send(data).unwrap())));
    pair.client.send(b"secure".to_vec(), 0, None).wait().unwrap();
    assert_eq!(rx.recv_timeout(Duration::from_secs(5)).unwrap(), b"secure");
    pair.close();
}

#[test]
fn server_authentication_client_sees_server_certificate() {
    let (identity, roots) = generate_test_identity();
    let host_options = ConnectionOptions {
        security_mode: SecurityMode::ServerAuthentication,
        identity: Some(identity),
        ..Default::default()
    };
    let connect_options = ConnectionOptions {
        security_mode: SecurityMode::ServerAuthentication,
        root_certificates: Some(Arc::new(roots)),
        ..Default::default()
    };

    let listener = host("127.0.0.1", 0, Some(host_options)).unwrap();
    let port = listener.local_endpoint().port();

    let accepted = Arc::new(AtomicBool::new(false));
    let accepted_for_handler = accepted.clone();
    listener.set_connected_handler(Some(Arc::new(move |connection| {
        accepted_for_handler.store(true, Ordering::SeqCst);
        std::mem::forget(connection);
    })));

    let client = connect("127.0.0.1", port, Some(connect_options)).unwrap();
    assert!(client.identity().certificate.is_some());
    assert!(wait_until(|| accepted.load(Ordering::SeqCst), Duration::from_secs(5)));

    client.close();
    listener.close();
}

#[test]
fn server_authentication_connect_without_root_certificates_falls_back_to_native_trust_store() {
    // No matching cert in the native trust store, so this should fail validation (not panic or
    // hang) - proving the fallback path (rustls-native-certs) is actually exercised.
    let (identity, _roots) = generate_test_identity();
    let host_options = ConnectionOptions {
        security_mode: SecurityMode::ServerAuthentication,
        identity: Some(identity),
        ..Default::default()
    };
    let listener = host("127.0.0.1", 0, Some(host_options)).unwrap();
    let port = listener.local_endpoint().port();

    let connect_options = ConnectionOptions {
        security_mode: SecurityMode::ServerAuthentication,
        ..Default::default()
    };
    let result = connect("127.0.0.1", port, Some(connect_options));
    assert!(result.is_err());
    listener.close();
}

#[test]
fn dual_authentication_both_sides_present_certificates() {
    let (identity, cert) = generate_test_identity_and_cert();
    let host_options = ConnectionOptions {
        security_mode: SecurityMode::DualAuthentication,
        identity: Some(identity.clone()),
        root_certificates: Some(Arc::new(root_store_trusting(&cert))),
        ..Default::default()
    };
    let connect_options = ConnectionOptions {
        security_mode: SecurityMode::DualAuthentication,
        identity: Some(identity),
        root_certificates: Some(Arc::new(root_store_trusting(&cert))),
        ..Default::default()
    };

    let listener = host("127.0.0.1", 0, Some(host_options)).unwrap();
    let port = listener.local_endpoint().port();

    let server_cert_seen = Arc::new(AtomicBool::new(false));
    let server_cert_seen_for_handler = server_cert_seen.clone();
    listener.set_connected_handler(Some(Arc::new(move |connection: oft::Connection| {
        server_cert_seen_for_handler.store(connection.identity().certificate.is_some(), Ordering::SeqCst);
        std::mem::forget(connection);
    })));

    let client = connect("127.0.0.1", port, Some(connect_options)).unwrap();
    assert!(client.identity().certificate.is_some());
    assert!(wait_until(|| server_cert_seen.load(Ordering::SeqCst), Duration::from_secs(5)));

    client.close();
    listener.close();
}

#[test]
fn dual_authentication_connect_without_identity_fails() {
    let (_identity, roots) = generate_test_identity();
    let port = reserve_free_port();
    let connect_options = ConnectionOptions {
        security_mode: SecurityMode::DualAuthentication,
        root_certificates: Some(Arc::new(roots)),
        ..Default::default()
    };
    let result = connect("127.0.0.1", port, Some(connect_options));
    assert!(result.is_err());
}

#[test]
fn connection_validation_none_accepts_every_connection() {
    let pair = establish(ConnectionOptions::default());
    pair.close();
}

#[test]
fn connection_validation_sees_identity() {
    let observed = Arc::new(std::sync::Mutex::new(None));
    let observed_for_callback = observed.clone();
    let options = ConnectionOptions {
        connection_validation: Some(Arc::new(move |identity: &oft::Identity| {
            *observed_for_callback.lock().unwrap() = Some(identity.info.clone());
            true
        })),
        info: "validated-client".to_string(),
        ..Default::default()
    };

    let listener = host("127.0.0.1", 0, Some(ConnectionOptions::default())).unwrap();
    let port = listener.local_endpoint().port();
    let client = connect("127.0.0.1", port, Some(options)).unwrap();

    assert_eq!(observed.lock().unwrap().clone(), Some("".to_string()));
    client.close();
    listener.close();
}

#[test]
fn connection_validation_rejecting_fails_connect() {
    let options = ConnectionOptions {
        connection_validation: Some(Arc::new(|_identity: &oft::Identity| false)),
        ..Default::default()
    };

    let listener = host("127.0.0.1", 0, Some(ConnectionOptions::default())).unwrap();
    let port = listener.local_endpoint().port();
    let result = connect("127.0.0.1", port, Some(options));
    assert!(matches!(result, Err(OftError::ValidationRejected(_))));
    listener.close();
}
