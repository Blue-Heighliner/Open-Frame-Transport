use crate::connection::{self, Connection};
use crate::error::OftError;
use crate::establish::exchange_hail;
use crate::options::ConnectionOptions;
use crate::security_mode::SecurityMode;
use crate::stream::{build_client_config, Stream};
use rustls::pki_types::ServerName;
use rustls::ClientConnection;
use std::net::TcpStream;

/// Dials an outbound connection to `host:port`, performs the TLS handshake (unless
/// `SecurityMode::Trusted`) and hail exchange, and returns an established connection. `options`
/// defaults to `SecurityMode::Secure` with default timing/size settings if omitted.
pub fn connect(host: &str, port: u16, options: Option<ConnectionOptions>) -> Result<Connection, OftError> {
    let options = options.unwrap_or_default();
    let tcp = TcpStream::connect((host, port))?;
    let _ = tcp.set_nodelay(true);

    let mut stream = match options.security_mode {
        SecurityMode::Trusted => Stream::Plain(tcp),
        _ => {
            let config = build_client_config(&options, host)?;
            let server_name = ServerName::try_from(host.to_string()).map_err(|_| OftError::ValidationRejected(format!("invalid host name '{host}'")))?;
            let conn = ClientConnection::new(config, server_name)?;
            Stream::TlsClient(rustls::StreamOwned::new(conn, tcp))
        }
    };

    let identity = exchange_hail(&mut stream, &options)?;

    Ok(connection::spawn(
        stream,
        identity,
        options.max_packet_data_size,
        options.poll_interval.unwrap_or(connection::DEFAULT_POLL_INTERVAL),
        options.poll_timeout.unwrap_or(connection::DEFAULT_POLL_TIMEOUT),
        options.rekey_interval,
    ))
}
