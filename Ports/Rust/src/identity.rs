use rustls_pki_types::CertificateDer;
use std::net::SocketAddr;

/// Describes a connection's remote side: its endpoint, TLS certificate (if any), and the opaque
/// hail `info` it sent.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Identity {
    pub address: SocketAddr,
    /// Present only if the remote side presented a TLS certificate - `None` under
    /// `SecurityMode::Trusted`, and also `None` on the accepting side of a connection established
    /// under a mode that never requests a certificate from the connecting side (see
    /// `SecurityMode::DualAuthentication`).
    pub certificate: Option<CertificateDer<'static>>,
    pub info: String,
}
