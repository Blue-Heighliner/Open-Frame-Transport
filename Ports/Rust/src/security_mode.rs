/// The security mode a connection is established under (see `Docs/OFT.md` §9).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum SecurityMode {
    /// Skips TLS entirely - the hail exchange happens directly on the raw TCP connection. No
    /// confidentiality, integrity, or authentication; rekeying is a no-op (there's no TLS session).
    Trusted,
    /// The default. TLS provides confidentiality and integrity but no authentication of either
    /// side - the accepting side uses a throwaway certificate it generates internally, and the
    /// connecting side accepts whatever certificate it's presented with unconditionally.
    Secure,
    /// Traditional one-way TLS: the accepting side must supply a real certificate, which the
    /// connecting side validates normally. Not valid for a `Peer` - use `DualAuthentication`.
    ServerAuthentication,
    /// Mutual TLS: everything `ServerAuthentication` requires, plus the connecting side must also
    /// supply its own certificate. The only authenticating mode a `Peer` supports.
    DualAuthentication,
}
