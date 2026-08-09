//! A Rust implementation of the [Open Frame Transport (OFT)](../../../README.md) protocol. See
//! `../../../Docs/Architecture.md` for how this port's components relate to the other
//! implementations.

mod buffered_slot;
mod connection;
mod connector;
mod ephemeral;
mod error;
mod establish;
mod frame;
mod identity;
mod listener;
mod options;
mod peer;
mod security_mode;
mod send_handle;
mod stream;
mod wire;

pub use connection::{Connection, Tag};
pub use connector::connect;
pub use error::OftError;
pub use identity::Identity;
pub use listener::{host, Listener};
pub use options::{ConnectionOptions, ConnectionValidationCallback, OwnedIdentity, PeerOptions};
pub use peer::Peer;
pub use security_mode::SecurityMode;
pub use send_handle::{SendFailure, SendHandle};
