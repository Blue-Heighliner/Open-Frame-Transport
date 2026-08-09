use crate::error::OftError;
use crate::options::ConnectionOptions;
use crate::security_mode::SecurityMode;
use rustls::client::danger::{HandshakeSignatureValid, ServerCertVerified, ServerCertVerifier};
use rustls::crypto::CryptoProvider;
use rustls::pki_types::{CertificateDer, ServerName, UnixTime};
use rustls::server::danger::{ClientCertVerified, ClientCertVerifier};
use rustls::server::WebPkiClientVerifier;
use rustls::{
    ClientConfig, ClientConnection, DigitallySignedStruct, RootCertStore, ServerConfig, ServerConnection,
    SignatureScheme,
};
use std::io::{self, Read, Write};
use std::net::TcpStream;
use std::sync::{Arc, Once};
use std::time::Duration;

static INSTALL_CRYPTO_PROVIDER: Once = Once::new();

pub(crate) fn ensure_crypto_provider() {
    INSTALL_CRYPTO_PROVIDER.call_once(|| {
        let _ = CryptoProvider::install_default(rustls::crypto::aws_lc_rs::default_provider());
    });
}

/// Accepts any server certificate unconditionally - used only under `SecurityMode::Secure`, where
/// the accepting side's certificate is a throwaway identity with nothing meaningful to validate it
/// against (see `Docs/OFT.md` §9).
#[derive(Debug)]
struct AcceptAnyServerCert(Arc<CryptoProvider>);

impl ServerCertVerifier for AcceptAnyServerCert {
    fn verify_server_cert(
        &self,
        _end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _server_name: &ServerName<'_>,
        _ocsp_response: &[u8],
        _now: UnixTime,
    ) -> Result<ServerCertVerified, rustls::Error> {
        Ok(ServerCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        _message: &[u8],
        _cert: &CertificateDer<'_>,
        _dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        Ok(HandshakeSignatureValid::assertion())
    }

    fn verify_tls13_signature(
        &self,
        _message: &[u8],
        _cert: &CertificateDer<'_>,
        _dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        Ok(HandshakeSignatureValid::assertion())
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.0.signature_verification_algorithms.supported_schemes()
    }
}

/// Accepts any client certificate unconditionally, requiring one be presented but not validating
/// it against a trust store - used only under `SecurityMode::Secure` when a `Peer` accepts an
/// inbound connection (the accepting side never requires a client certificate under `Secure`, so
/// this only actually matters if a caller-configured client happens to present one anyway).
#[derive(Debug)]
struct AcceptAnyClientCert(Arc<CryptoProvider>);

impl ClientCertVerifier for AcceptAnyClientCert {
    fn offer_client_auth(&self) -> bool {
        false
    }

    fn client_auth_mandatory(&self) -> bool {
        false
    }

    fn root_hint_subjects(&self) -> &[rustls::DistinguishedName] {
        &[]
    }

    fn verify_client_cert(
        &self,
        _end_entity: &CertificateDer<'_>,
        _intermediates: &[CertificateDer<'_>],
        _now: UnixTime,
    ) -> Result<ClientCertVerified, rustls::Error> {
        Ok(ClientCertVerified::assertion())
    }

    fn verify_tls12_signature(
        &self,
        _message: &[u8],
        _cert: &CertificateDer<'_>,
        _dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        Ok(HandshakeSignatureValid::assertion())
    }

    fn verify_tls13_signature(
        &self,
        _message: &[u8],
        _cert: &CertificateDer<'_>,
        _dss: &DigitallySignedStruct,
    ) -> Result<HandshakeSignatureValid, rustls::Error> {
        Ok(HandshakeSignatureValid::assertion())
    }

    fn supported_verify_schemes(&self) -> Vec<SignatureScheme> {
        self.0.signature_verification_algorithms.supported_schemes()
    }
}

pub(crate) fn build_client_config(options: &ConnectionOptions, target_host: &str) -> Result<Arc<ClientConfig>, OftError> {
    ensure_crypto_provider();
    let provider = CryptoProvider::get_default()
        .cloned()
        .unwrap_or_else(|| Arc::new(rustls::crypto::aws_lc_rs::default_provider()));

    let builder = ClientConfig::builder_with_protocol_versions(&[&rustls::version::TLS13]);

    let builder = match options.security_mode {
        SecurityMode::Secure => builder
            .dangerous()
            .with_custom_certificate_verifier(Arc::new(AcceptAnyServerCert(provider))),
        SecurityMode::ServerAuthentication | SecurityMode::DualAuthentication => {
            let roots = match &options.root_certificates {
                Some(roots) => roots.as_ref().clone(),
                None => native_root_store()?,
            };
            builder.with_root_certificates(roots)
        }
        SecurityMode::Trusted => unreachable!("Trusted mode never builds a TLS config"),
    };

    let config = if options.security_mode == SecurityMode::DualAuthentication {
        let (chain, key) = options
            .identity
            .as_ref()
            .ok_or_else(|| OftError::ValidationRejected("DualAuthentication requires ConnectionOptions::identity".into()))?
            .as_ref();
        builder.with_client_auth_cert(chain.clone(), key.clone_key())?
    } else {
        builder.with_no_client_auth()
    };

    let _ = target_host;
    Ok(Arc::new(config))
}

pub(crate) fn build_server_config(
    options: &ConnectionOptions,
    ephemeral: Option<&(Vec<CertificateDer<'static>>, rustls::pki_types::PrivateKeyDer<'static>)>,
) -> Result<Arc<ServerConfig>, OftError> {
    ensure_crypto_provider();
    let provider = CryptoProvider::get_default()
        .cloned()
        .unwrap_or_else(|| Arc::new(rustls::crypto::aws_lc_rs::default_provider()));

    let builder = ServerConfig::builder_with_protocol_versions(&[&rustls::version::TLS13]);

    let builder = match options.security_mode {
        SecurityMode::Secure => builder.with_client_cert_verifier(Arc::new(AcceptAnyClientCert(provider))),
        SecurityMode::ServerAuthentication => builder.with_no_client_auth(),
        SecurityMode::DualAuthentication => {
            let roots = options
                .root_certificates
                .clone()
                .ok_or_else(|| OftError::ValidationRejected("DualAuthentication hosting requires ConnectionOptions::root_certificates".into()))?;
            let verifier = WebPkiClientVerifier::builder(roots)
                .build()
                .map_err(|err| OftError::ValidationRejected(err.to_string()))?;
            builder.with_client_cert_verifier(verifier)
        }
        SecurityMode::Trusted => unreachable!("Trusted mode never builds a TLS config"),
    };

    let (chain, key) = if options.security_mode == SecurityMode::Secure {
        ephemeral.expect("Secure-mode hosting always resolves an ephemeral identity before building a server config")
    } else {
        options
            .identity
            .as_ref()
            .ok_or_else(|| OftError::ValidationRejected(format!("{:?} hosting requires ConnectionOptions::identity", options.security_mode)))?
            .as_ref()
    };

    let config = builder.with_single_cert(chain.clone(), key.clone_key())?;
    Ok(Arc::new(config))
}

fn native_root_store() -> Result<RootCertStore, OftError> {
    let mut store = RootCertStore::empty();
    let native = rustls_native_certs::load_native_certs();
    if let Some(err) = native.errors.into_iter().next() {
        return Err(OftError::Io(io::Error::other(err.to_string())));
    }
    for cert in native.certs {
        let _ = store.add(cert);
    }
    Ok(store)
}

/// Owns the raw socket plus, for non-`Trusted` modes, the TLS connection state - the single
/// concrete type this port's connection engine reads/writes through regardless of security mode.
pub(crate) enum Stream {
    Plain(TcpStream),
    TlsClient(rustls::StreamOwned<ClientConnection, TcpStream>),
    TlsServer(rustls::StreamOwned<ServerConnection, TcpStream>),
}

impl Read for Stream {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        match self {
            Stream::Plain(s) => s.read(buf),
            Stream::TlsClient(s) => s.read(buf),
            Stream::TlsServer(s) => s.read(buf),
        }
    }
}

impl Write for Stream {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        match self {
            Stream::Plain(s) => s.write(buf),
            Stream::TlsClient(s) => s.write(buf),
            Stream::TlsServer(s) => s.write(buf),
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        match self {
            Stream::Plain(s) => s.flush(),
            Stream::TlsClient(s) => s.flush(),
            Stream::TlsServer(s) => s.flush(),
        }
    }
}

impl Stream {
    pub(crate) fn set_read_timeout(&self, timeout: Option<Duration>) -> io::Result<()> {
        match self {
            Stream::Plain(s) => s.set_read_timeout(timeout),
            Stream::TlsClient(s) => s.sock.set_read_timeout(timeout),
            Stream::TlsServer(s) => s.sock.set_read_timeout(timeout),
        }
    }

    pub(crate) fn shutdown(&self) {
        let sock = match self {
            Stream::Plain(s) => s,
            Stream::TlsClient(s) => &s.sock,
            Stream::TlsServer(s) => &s.sock,
        };
        let _ = sock.shutdown(std::net::Shutdown::Both);
    }

    /// A clone of the underlying socket, kept only so `disconnect()` can call `shutdown()` on it
    /// from a different thread than the one blocked in `read()` - shutting down any clone of a
    /// socket immediately unblocks a `read()` on any other clone of the same socket, unlike
    /// merely closing one clone, which doesn't reliably interrupt a concurrent blocking read on
    /// another thread's clone.
    pub(crate) fn try_clone_socket(&self) -> io::Result<TcpStream> {
        match self {
            Stream::Plain(s) => s.try_clone(),
            Stream::TlsClient(s) => s.sock.try_clone(),
            Stream::TlsServer(s) => s.sock.try_clone(),
        }
    }

    pub(crate) fn peer_addr(&self) -> io::Result<std::net::SocketAddr> {
        match self {
            Stream::Plain(s) => s.peer_addr(),
            Stream::TlsClient(s) => s.sock.peer_addr(),
            Stream::TlsServer(s) => s.sock.peer_addr(),
        }
    }

    pub(crate) fn peer_certificate(&self) -> Option<CertificateDer<'static>> {
        let certs = match self {
            Stream::Plain(_) => None,
            Stream::TlsClient(s) => s.conn.peer_certificates(),
            Stream::TlsServer(s) => s.conn.peer_certificates(),
        }?;
        certs.first().cloned()
    }

    pub(crate) fn refresh_traffic_keys(&mut self) -> Result<(), OftError> {
        match self {
            Stream::Plain(_) => Ok(()), // Trusted mode: no TLS session to rekey - a documented no-op.
            Stream::TlsClient(s) => Ok(s.conn.refresh_traffic_keys()?),
            Stream::TlsServer(s) => Ok(s.conn.refresh_traffic_keys()?),
        }
    }
}
