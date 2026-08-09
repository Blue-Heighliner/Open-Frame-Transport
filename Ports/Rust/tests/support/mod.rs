#![allow(dead_code)]

use oft::{connect, host, Connection, ConnectionOptions, Listener, OwnedIdentity};
use rustls::RootCertStore;
use std::net::TcpListener as StdTcpListener;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

pub fn reserve_free_port() -> u16 {
    StdTcpListener::bind("127.0.0.1:0").unwrap().local_addr().unwrap().port()
}

pub fn wait_until(mut predicate: impl FnMut() -> bool, timeout: Duration) -> bool {
    let deadline = Instant::now() + timeout;
    loop {
        if predicate() {
            return true;
        }
        if Instant::now() >= deadline {
            return false;
        }
        std::thread::sleep(Duration::from_millis(5));
    }
}

/// A throwaway self-signed identity, usable both as one side's own certificate and as the trust
/// anchor for validating it (mirrors every other port's own test-certificate helper).
pub fn generate_test_identity() -> (OwnedIdentity, RootCertStore) {
    let (identity, cert) = generate_test_identity_and_cert();
    (identity, root_store_trusting(&cert))
}

/// Same as `generate_test_identity`, but also returns the raw certificate so a caller needing two
/// *separate* `RootCertStore`s that both trust it (e.g. dual-authentication tests, where both
/// sides build their own store) can call `root_store_trusting` again without regenerating a
/// second, mismatched identity.
pub fn generate_test_identity_and_cert() -> (OwnedIdentity, rustls::pki_types::CertificateDer<'static>) {
    // Tests connect via the "127.0.0.1"/"::1" IP literal, not a DNS name, so the SAN must cover
    // those too - real webpki-based validation (unlike SecurityMode::Secure's own
    // accept-unconditionally verifier) checks the presented name against these.
    let certified_key = rcgen::generate_simple_self_signed(vec!["localhost".to_string(), "127.0.0.1".to_string(), "::1".to_string()]).unwrap();
    let cert_der = rustls::pki_types::CertificateDer::from(certified_key.cert.der().to_vec());
    let key_der = rustls::pki_types::PrivateKeyDer::Pkcs8(rustls::pki_types::PrivatePkcs8KeyDer::from(certified_key.signing_key.serialize_der()));
    (Arc::new((vec![cert_der.clone()], key_der)), cert_der)
}

pub fn root_store_trusting(cert: &rustls::pki_types::CertificateDer<'static>) -> RootCertStore {
    let mut roots = RootCertStore::empty();
    roots.add(cert.clone()).unwrap();
    roots
}

pub struct Pair {
    pub client: Connection,
    pub server: Connection,
    pub listener: Listener,
}

impl Pair {
    pub fn close(&self) {
        self.client.close();
        self.server.close();
        self.listener.close();
    }
}

/// Establishes a client/server connection pair over real loopback TCP/TLS, mirroring the other
/// ports' own `OftTestHarness.establish()`/`establish_pair()` helpers.
pub fn establish(options: ConnectionOptions) -> Pair {
    let listener = host("127.0.0.1", 0, Some(options.clone())).unwrap();
    let port = listener.local_endpoint().port();

    let server_slot: Arc<Mutex<Option<Connection>>> = Arc::new(Mutex::new(None));
    let received = Arc::new(AtomicBool::new(false));
    let slot_for_handler = server_slot.clone();
    let received_for_handler = received.clone();
    listener.set_connected_handler(Some(Arc::new(move |connection: Connection| {
        *slot_for_handler.lock().unwrap() = Some(connection);
        received_for_handler.store(true, Ordering::SeqCst);
    })));

    let client = connect("127.0.0.1", port, Some(options)).unwrap();

    assert!(wait_until(|| received.load(Ordering::SeqCst), Duration::from_secs(10)), "server never accepted the connection");
    let server = server_slot.lock().unwrap().take().unwrap();

    Pair { client, server, listener }
}
