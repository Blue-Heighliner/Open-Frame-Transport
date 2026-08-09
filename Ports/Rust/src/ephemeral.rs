//! Ephemeral self-signed identity generation for `SecurityMode::Secure` - see `Docs/OFT.md` §9.
//! Resolved once per listener/peer (not once per connection): generating a fresh keypair is
//! expensive enough that doing it per connection would meaningfully slow down or destabilize
//! connection establishment under load, matching every other port's own documented approach.

use crate::error::OftError;
use rustls::pki_types::{CertificateDer, PrivateKeyDer, PrivatePkcs8KeyDer};

pub(crate) type EphemeralIdentity = (Vec<CertificateDer<'static>>, PrivateKeyDer<'static>);

pub(crate) fn generate_ephemeral_identity() -> Result<EphemeralIdentity, OftError> {
    let certified_key = rcgen::generate_simple_self_signed(vec!["localhost".to_string()])
        .map_err(|err| OftError::ValidationRejected(format!("failed to generate ephemeral identity: {err}")))?;

    let cert_der = CertificateDer::from(certified_key.cert.der().to_vec());
    let key_der = PrivateKeyDer::Pkcs8(PrivatePkcs8KeyDer::from(certified_key.signing_key.serialize_der()));

    Ok((vec![cert_der], key_der))
}
