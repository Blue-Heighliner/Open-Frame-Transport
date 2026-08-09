use std::fmt;

/// Errors raised by this crate's blocking API.
#[derive(Debug)]
pub enum OftError {
    /// Raised by `Connection::send`/`::rekey` and `Peer::send`/`::rekey` once the connection/peer
    /// is permanently disconnected - the equivalent of the other ports' `OftDisconnectedException`.
    Disconnected,
    /// A previously queued send was cancelled via `SendHandle::cancel` before it completed.
    Cancelled,
    /// The remote side's `connection_validation` callback rejected the connection, or the hail
    /// exchange failed (incompatible/malformed version).
    ValidationRejected(String),
    Io(std::io::Error),
    Tls(rustls::Error),
}

impl fmt::Display for OftError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            OftError::Disconnected => write!(f, "the connection is no longer connected"),
            OftError::Cancelled => write!(f, "the send was cancelled"),
            OftError::ValidationRejected(reason) => write!(f, "connection rejected: {reason}"),
            OftError::Io(err) => write!(f, "{err}"),
            OftError::Tls(err) => write!(f, "{err}"),
        }
    }
}

impl std::error::Error for OftError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            OftError::Io(err) => Some(err),
            OftError::Tls(err) => Some(err),
            _ => None,
        }
    }
}

impl From<std::io::Error> for OftError {
    fn from(err: std::io::Error) -> Self {
        OftError::Io(err)
    }
}

impl From<rustls::Error> for OftError {
    fn from(err: rustls::Error) -> Self {
        OftError::Tls(err)
    }
}
