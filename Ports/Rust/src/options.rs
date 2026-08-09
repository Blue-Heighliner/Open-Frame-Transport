use crate::identity::Identity;
use crate::security_mode::SecurityMode;
use rustls::RootCertStore;
use rustls_pki_types::{CertificateDer, PrivateKeyDer};
use std::ops::{Deref, DerefMut};
use std::sync::Arc;
use std::time::Duration;

/// Validates a fully-established connection. Invoked once per connection, after the OFT hail
/// exchange completes (see `Docs/OFT.md` §3), for every security mode - including
/// `SecurityMode::Trusted`, where `identity.certificate` is always `None`. Return `true` to accept
/// the connection, `false` to reject it (in which case `connect()`/`host()` fails).
pub type ConnectionValidationCallback = Arc<dyn Fn(&Identity) -> bool + Send + Sync>;

/// This side's own certificate chain + private key, used when this side must present a
/// certificate to the peer (see `ConnectionOptions::identity`). Wrapped in one `Arc` (rather than
/// requiring `PrivateKeyDer` itself to be cheaply `Clone`) so `ConnectionOptions` stays cheaply
/// cloneable regardless of the key material's own type.
pub type OwnedIdentity = Arc<(Vec<CertificateDer<'static>>, PrivateKeyDer<'static>)>;

/// Options for an individual connection, used both to connect (`connect()`) and to host
/// (`host()`).
#[derive(Clone)]
pub struct ConnectionOptions {
    /// Opaque, application-controlled data sent to the peer in this side's hail (see
    /// `Docs/OFT.md` §3).
    pub info: String,

    /// The security mode this connection is established under (see `Docs/OFT.md` §9). Default:
    /// `SecurityMode::Secure`.
    pub security_mode: SecurityMode,

    /// This side's own certificate chain + private key - required when hosting under
    /// `SecurityMode::ServerAuthentication`/`DualAuthentication`, or connecting under
    /// `SecurityMode::DualAuthentication`. Ignored under `Trusted`/`Secure`.
    pub identity: Option<OwnedIdentity>,

    /// Trust store used to validate the peer's certificate - required when hosting under
    /// `SecurityMode::DualAuthentication`. When connecting under `SecurityMode::ServerAuthentication`
    /// and left `None`, falls back to the platform's native trust store (see
    /// `rustls-native-certs`). Ignored under `Trusted`/`Secure`.
    pub root_certificates: Option<Arc<RootCertStore>>,

    /// The maximum number of payload bytes carried in a single packet's data field. `0` = default
    /// (1024).
    pub max_packet_data_size: usize,

    /// When set, the connection automatically rekeys its TLS session on this interval. `None` =
    /// disabled, or ignored entirely when `security_mode` is `SecurityMode::Trusted`.
    pub rekey_interval: Option<Duration>,

    /// How often the connection sends an empty Poll frame to the peer as a liveness signal, once
    /// established (see `Docs/OFT.md` §10). `None` = default (1 second).
    pub poll_interval: Option<Duration>,

    /// How long the connection may go without receiving anything at all from the peer before it
    /// assumes the peer is unreachable and closes itself (see `Docs/OFT.md` §10). `None` = default
    /// (5 seconds).
    pub poll_timeout: Option<Duration>,

    /// An optional callback used to validate a fully-established connection (see
    /// `ConnectionValidationCallback`'s own documentation). `None` = every connection is accepted.
    pub connection_validation: Option<ConnectionValidationCallback>,
}

impl Default for ConnectionOptions {
    fn default() -> Self {
        ConnectionOptions {
            info: String::new(),
            security_mode: SecurityMode::Secure,
            identity: None,
            root_certificates: None,
            max_packet_data_size: 0,
            rekey_interval: None,
            poll_interval: None,
            poll_timeout: None,
            connection_validation: None,
        }
    }
}

/// Options for a `Peer`, covering both its outbound and inbound connections at once.
#[derive(Clone, Default)]
pub struct PeerOptions {
    pub connection: ConnectionOptions,

    /// How long a connection may sit idle (no send or receive) before it is automatically
    /// disconnected. `None` = default (2 hours). Eviction is only ever checked on a fixed,
    /// non-configurable 30-second interval, and a connection only ever becomes a candidate once it
    /// has had no pending data for a further fixed, non-configurable 30-second grace period - so a
    /// value below that combined ~30-60 second floor has no effect beyond it.
    pub idle_timeout: Option<Duration>,

    /// The maximum total lifetime of a connection before it is automatically disconnected,
    /// regardless of activity. `None` = default (1 day). Subject to the same ~30-60 second floor
    /// as `idle_timeout` above.
    pub max_connection_lifetime: Option<Duration>,

    /// The maximum number of connections this peer keeps at once. When exceeded, the oldest
    /// connections (by when they were established) are disconnected first, skipping any with
    /// pending data. `0` = default (16).
    pub max_connection_count: usize,
}

impl Deref for PeerOptions {
    type Target = ConnectionOptions;

    fn deref(&self) -> &ConnectionOptions {
        &self.connection
    }
}

impl DerefMut for PeerOptions {
    fn deref_mut(&mut self) -> &mut ConnectionOptions {
        &mut self.connection
    }
}
