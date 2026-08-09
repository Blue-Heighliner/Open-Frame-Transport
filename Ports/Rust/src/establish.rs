use crate::error::OftError;
use crate::frame::{write_frame, FrameReader};
use crate::identity::Identity;
use crate::options::ConnectionOptions;
use crate::stream::Stream;
use crate::wire::{decode_hail, encode_hail, Hail};
use std::io::Read;
use std::time::Duration;

pub(crate) const PROTOCOL_VERSION: &str = "oft/1";

/// Writes this side's hail, reads the peer's, validates it, and returns the resulting `Identity`.
/// Blocking - performed synchronously as part of `connect()`/accepting a connection, before this
/// connection's background I/O thread ever starts.
pub(crate) fn exchange_hail(stream: &mut Stream, options: &ConnectionOptions) -> Result<Identity, OftError> {
    let our_hail = encode_hail(&Hail {
        version: PROTOCOL_VERSION.to_string(),
        info: options.info.clone(),
    });
    write_frame(stream, &our_hail)?;

    let peer_hail = read_one_frame(stream)?;
    let peer_hail = decode_hail(&peer_hail).map_err(|_| OftError::ValidationRejected("malformed hail".to_string()))?;
    if peer_hail.version != PROTOCOL_VERSION {
        return Err(OftError::ValidationRejected(format!("incompatible OFT protocol version '{}'", peer_hail.version)));
    }

    let identity = Identity {
        address: stream.peer_addr()?,
        certificate: stream.peer_certificate(),
        info: peer_hail.info,
    };

    if let Some(validation) = &options.connection_validation
        && !validation(&identity)
    {
        return Err(OftError::ValidationRejected("rejected by connection_validation".to_string()));
    }

    Ok(identity)
}

fn read_one_frame(stream: &mut Stream) -> Result<Vec<u8>, OftError> {
    let mut reader = FrameReader::new();
    let mut buf = [0u8; 4096];
    stream.set_read_timeout(Some(Duration::from_secs(30)))?;

    loop {
        if let Some(frame) = reader.try_take_frame().map_err(|_| OftError::ValidationRejected("malformed hail frame".to_string()))? {
            return Ok(frame);
        }

        let n = stream.read(&mut buf)?;
        if n == 0 {
            return Err(OftError::ValidationRejected("connection closed during hail exchange".to_string()));
        }
        reader.feed(&buf[..n]);
    }
}
