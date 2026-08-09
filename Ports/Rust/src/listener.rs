use crate::buffered_slot::BufferedSlot;
use crate::connection::{self, Connection};
use crate::ephemeral::generate_ephemeral_identity;
use crate::error::OftError;
use crate::establish::exchange_hail;
use crate::options::ConnectionOptions;
use crate::security_mode::SecurityMode;
use crate::stream::{build_server_config, Stream};
use rustls::{ServerConfig, ServerConnection};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};

struct Shared {
    local_addr: SocketAddr,
    connected_slot: BufferedSlot<Connection>,
    closed: AtomicBool,
    accept_thread: Mutex<Option<JoinHandle<()>>>,
}

/// A listener returned by `host()`, notifying the caller of each accepted, fully-established
/// inbound connection. Closing it only stops accepting new ones - already-accepted connections
/// keep running. There's no way to reopen a closed listener; `host()` a fresh one instead.
pub struct Listener {
    shared: Arc<Shared>,
}

impl Listener {
    pub fn local_endpoint(&self) -> SocketAddr {
        self.shared.local_addr
    }

    /// Assigns the (single) callback invoked for each newly accepted, fully-established inbound
    /// connection. See `Docs/Architecture.md`'s "Buffered notifications" section for why
    /// assigning any time after `host()` returns is always safe.
    pub fn set_connected_handler(&self, handler: Option<Arc<dyn Fn(Connection) + Send + Sync>>) {
        self.shared.connected_slot.set_handler(handler);
    }

    /// Stops accepting new connections and returns once the accept loop has fully stopped.
    /// Already-accepted connections are left running. Safe to call more than once.
    pub fn close(&self) {
        if self.shared.closed.swap(true, Ordering::SeqCst) {
            return;
        }

        // A blocking accept() has no std-library timeout/interrupt mechanism; connecting to
        // ourselves is what reliably unblocks it so the accept loop can observe `closed` and exit.
        let _ = TcpStream::connect(self.shared.local_addr);

        if let Some(handle) = self.shared.accept_thread.lock().unwrap().take() {
            let _ = handle.join();
        }
    }
}

/// Starts listening for inbound connections on `bind_host:bind_port`. `options` defaults to
/// `SecurityMode::Secure` with default timing/size settings if omitted.
pub fn host(bind_host: &str, bind_port: u16, options: Option<ConnectionOptions>) -> Result<Listener, OftError> {
    let options = options.unwrap_or_default();
    let tcp = TcpListener::bind((bind_host, bind_port))?;
    let local_addr = tcp.local_addr()?;

    // Resolved once per listener, not once per accepted connection - see `ephemeral.rs`.
    let ephemeral = if options.security_mode == SecurityMode::Secure {
        Some(generate_ephemeral_identity()?)
    } else {
        None
    };

    let server_config: Option<Arc<ServerConfig>> = if options.security_mode != SecurityMode::Trusted {
        Some(build_server_config(&options, ephemeral.as_ref())?)
    } else {
        None
    };

    let shared = Arc::new(Shared {
        local_addr,
        connected_slot: BufferedSlot::new(),
        closed: AtomicBool::new(false),
        accept_thread: Mutex::new(None),
    });

    let accept_shared = shared.clone();
    let handle = thread::spawn(move || {
        while let Ok((sock, _addr)) = tcp.accept() {
            if accept_shared.closed.load(Ordering::SeqCst) {
                break;
            }

            let options = options.clone();
            let server_config = server_config.clone();
            let shared_for_conn = accept_shared.clone();
            thread::spawn(move || {
                if let Ok(connection) = accept_one(sock, &options, server_config) {
                    shared_for_conn.connected_slot.raise(connection);
                }
                // A handshake/hail failure just drops the connection silently, matching
                // every other port's own accept-loop behavior.
            });
        }
    });

    *shared.accept_thread.lock().unwrap() = Some(handle);
    Ok(Listener { shared })
}

fn accept_one(sock: TcpStream, options: &ConnectionOptions, server_config: Option<Arc<ServerConfig>>) -> Result<Connection, OftError> {
    let mut stream = match options.security_mode {
        SecurityMode::Trusted => Stream::Plain(sock),
        _ => {
            let conn = ServerConnection::new(server_config.expect("non-Trusted modes always resolve a server config"))?;
            Stream::TlsServer(rustls::StreamOwned::new(conn, sock))
        }
    };

    let identity = exchange_hail(&mut stream, options)?;

    Ok(connection::spawn(
        stream,
        identity,
        options.max_packet_data_size,
        options.poll_interval.unwrap_or(connection::DEFAULT_POLL_INTERVAL),
        options.poll_timeout.unwrap_or(connection::DEFAULT_POLL_TIMEOUT),
        options.rekey_interval,
    ))
}
