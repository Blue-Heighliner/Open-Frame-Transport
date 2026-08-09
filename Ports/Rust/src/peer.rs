use crate::buffered_slot::BufferedSlot;
use crate::connection::{Connection, Tag};
use crate::connector::connect;
use crate::error::OftError;
use crate::identity::Identity;
use crate::listener::{host, Listener};
use crate::options::{ConnectionOptions, PeerOptions};
use crate::security_mode::SecurityMode;
use crate::send_handle::SendHandle;
use std::collections::HashMap;
use std::net::SocketAddr;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

const DEFAULT_IDLE_TIMEOUT: Duration = Duration::from_secs(2 * 60 * 60);
const DEFAULT_MAX_CONNECTION_LIFETIME: Duration = Duration::from_secs(24 * 60 * 60);
const DEFAULT_MAX_CONNECTION_COUNT: usize = 16;

/// How long a connection must have had no pending data (see `Connection::has_pending_data`)
/// before it becomes eligible for automatic eviction (idle, lifetime, or excess-count based) at
/// all - a fixed value, not configurable, giving the underlying TLS/TCP layers time to actually
/// flush and acknowledge everything after the last application-level message completes.
pub(crate) const EVICTION_GRACE_PERIOD: Duration = Duration::from_secs(30);

/// How often eviction checks connections against `idle_timeout`/`max_connection_lifetime`/
/// `max_connection_count` - a fixed value, not configurable. Combined with `EVICTION_GRACE_PERIOD`
/// above, neither of those two duration-based options can take effect any sooner than roughly
/// 30-60 seconds after it's reached, regardless of how much shorter either is configured to be.
pub(crate) const EVICTION_CHECK_INTERVAL: Duration = Duration::from_secs(30);

struct TrackedConnection {
    connection: Connection,
    pending_data_cleared_at: Mutex<Option<Instant>>,
}

struct Shared {
    connection_options: ConnectionOptions,
    idle_timeout: Duration,
    max_connection_lifetime: Duration,
    max_connection_count: usize,
    outbound: Mutex<HashMap<(String, u16), Arc<TrackedConnection>>>,
    inbound: Mutex<Vec<Arc<TrackedConnection>>>,
    listener: Mutex<Option<Listener>>,
    received_slot: BufferedSlot<(Identity, Vec<u8>)>,
    acknowledged_handler: Mutex<Option<Arc<dyn Fn(Identity, Tag) + Send + Sync>>>,
    closed: AtomicBool,
    eviction_stop: Arc<(Mutex<bool>, Condvar)>,
    eviction_thread: Mutex<Option<JoinHandle<()>>>,
}

/// Sending a message to a `host:port` transparently reuses an existing connection or creates and
/// caches a new one; it may optionally also listen for inbound connections, folding them into the
/// same pool. There is no way to enumerate or individually address the connections it holds -
/// `rekey()`/`drop()` act on all of them at once - and it exposes only a single received-message
/// notification covering every connection, identifying which one a message arrived on via the
/// message's own `Identity`. It has no disconnected or connected notification of its own:
/// connection lifecycle (establishing, reconnecting, evicting) is this peer's own implementation
/// detail, deliberately not surfaced.
pub struct Peer {
    shared: Arc<Shared>,
}

impl Peer {
    pub fn new(options: Option<PeerOptions>) -> Result<Peer, OftError> {
        let options = options.unwrap_or_default();
        if options.security_mode == SecurityMode::ServerAuthentication {
            return Err(OftError::ValidationRejected(
                "SecurityMode::ServerAuthentication is not valid for a Peer - a peer has no client/server delineation, so it cannot express a one-sided authentication requirement; use SecurityMode::DualAuthentication instead."
                    .to_string(),
            ));
        }

        let idle_timeout = options.idle_timeout.unwrap_or(DEFAULT_IDLE_TIMEOUT);
        let max_connection_lifetime = options.max_connection_lifetime.unwrap_or(DEFAULT_MAX_CONNECTION_LIFETIME);
        let max_connection_count = if options.max_connection_count == 0 { DEFAULT_MAX_CONNECTION_COUNT } else { options.max_connection_count };

        let shared = Arc::new(Shared {
            connection_options: options.connection,
            idle_timeout,
            max_connection_lifetime,
            max_connection_count,
            outbound: Mutex::new(HashMap::new()),
            inbound: Mutex::new(Vec::new()),
            listener: Mutex::new(None),
            received_slot: BufferedSlot::new(),
            acknowledged_handler: Mutex::new(None),
            closed: AtomicBool::new(false),
            eviction_stop: Arc::new((Mutex::new(false), Condvar::new())),
            eviction_thread: Mutex::new(None),
        });

        let eviction_shared = shared.clone();
        let handle = thread::spawn(move || loop {
            let (lock, condvar) = &*eviction_shared.eviction_stop;
            let guard = lock.lock().unwrap();
            let (guard, timeout_result) = condvar.wait_timeout(guard, EVICTION_CHECK_INTERVAL).unwrap();
            if *guard {
                return;
            }
            drop(guard);
            let _ = timeout_result;
            run_eviction(&eviction_shared);
        });
        *shared.eviction_thread.lock().unwrap() = Some(handle);

        Ok(Peer { shared })
    }

    pub fn local_endpoint(&self) -> Option<SocketAddr> {
        self.shared.listener.lock().unwrap().as_ref().map(|l| l.local_endpoint())
    }

    /// Assigns the (single) callback invoked for every message received on any connection this
    /// peer holds, both inbound and outbound. `identity` is only for identifying which connection
    /// a message arrived on. Same buffering guarantee as `Connection::set_received_handler`.
    pub fn set_received_handler(&self, handler: Option<Arc<dyn Fn(Identity, Vec<u8>) + Send + Sync>>) {
        let wrapped = handler.map(|handler| {
            let wrapped: Arc<dyn Fn((Identity, Vec<u8>)) + Send + Sync> = Arc::new(move |(identity, data)| handler(identity, data));
            wrapped
        });
        self.shared.received_slot.set_handler(wrapped);
    }

    /// Assigns the (single) callback invoked whenever data sent via `send()` with a `Some(tag)`
    /// has been fully delivered and acknowledged on the connection it was sent on. Not buffered -
    /// see `Connection::set_acknowledged_handler`'s own documentation for why that's safe.
    pub fn set_acknowledged_handler(&self, handler: Option<Arc<dyn Fn(Identity, Tag) + Send + Sync>>) {
        *self.shared.acknowledged_handler.lock().unwrap() = handler;
    }

    /// Starts listening for inbound connections on `bind_host:bind_port`. A peer that never calls
    /// this only ever makes outbound connections. Calling this again replaces any previously
    /// started listener (stopping it first).
    pub fn listen(&self, bind_host: &str, bind_port: u16) -> Result<(), OftError> {
        if self.shared.closed.load(Ordering::SeqCst) {
            return Err(OftError::Disconnected);
        }

        if let Some(previous) = self.shared.listener.lock().unwrap().take() {
            previous.close();
        }

        let listener = host(bind_host, bind_port, Some(self.shared.connection_options.clone()))?;
        let wiring_shared = self.shared.clone();
        listener.set_connected_handler(Some(Arc::new(move |connection: Connection| {
            let tracked = Arc::new(TrackedConnection {
                connection: connection.clone(),
                pending_data_cleared_at: Mutex::new(None),
            });
            wiring_shared.inbound.lock().unwrap().push(tracked.clone());
            wire_tracking(&wiring_shared, tracked);
        })));

        *self.shared.listener.lock().unwrap() = Some(listener);
        Ok(())
    }

    /// Stops listening for new inbound connections. Already-established connections are left
    /// open.
    pub fn stop_listening(&self) {
        if let Some(listener) = self.shared.listener.lock().unwrap().take() {
            listener.close();
        }
    }

    /// Sends a message to `host:port`, reusing a cached connection if one already exists, or
    /// creating and caching a new one otherwise. `tag`, if present, is referenced later via
    /// `acknowledged_handler` once this specific message has been fully delivered and
    /// acknowledged.
    pub fn send(&self, host_: &str, port: u16, data: Vec<u8>, priority: u32, tag: Option<Tag>) -> Result<SendHandle, OftError> {
        if self.shared.closed.load(Ordering::SeqCst) {
            return Err(OftError::Disconnected);
        }

        let tracked = self.get_or_connect(host_, port)?;
        Ok(tracked.connection.send(data, priority, tag))
    }

    /// Requests a rekey (see `Connection::rekey`) on every connection this peer currently holds,
    /// both outbound and inbound. Connections established after this call is issued are
    /// unaffected.
    pub fn rekey(&self) -> Result<(), OftError> {
        if self.shared.closed.load(Ordering::SeqCst) {
            return Err(OftError::Disconnected);
        }
        for tracked in all_tracked(&self.shared) {
            let _ = tracked.connection.rekey();
        }
        Ok(())
    }

    /// Disconnects every connection this peer currently holds, both outbound and inbound. The
    /// peer itself is left usable - a subsequent `send()` creates and caches a new outbound
    /// connection as usual, and, if listening, new inbound connections keep being accepted.
    pub fn drop(&self) -> Result<(), OftError> {
        if self.shared.closed.load(Ordering::SeqCst) {
            return Err(OftError::Disconnected);
        }
        for tracked in all_tracked(&self.shared) {
            tracked.connection.disconnect();
        }
        Ok(())
    }

    /// `true` until this peer is closed, after which it is permanently `false` and every other
    /// member raises `OftError::Disconnected` (except `is_connected` itself).
    pub fn is_connected(&self) -> bool {
        !self.shared.closed.load(Ordering::SeqCst)
    }

    /// Stops listening (if applicable), closes every connection this peer holds, and stops its
    /// background eviction thread. Safe to call more than once.
    pub fn close(&self) {
        if self.shared.closed.swap(true, Ordering::SeqCst) {
            return;
        }

        {
            let (lock, condvar) = &*self.shared.eviction_stop;
            *lock.lock().unwrap() = true;
            condvar.notify_all();
        }
        if let Some(handle) = self.shared.eviction_thread.lock().unwrap().take() {
            let _ = handle.join();
        }

        self.stop_listening();

        for tracked in all_tracked(&self.shared) {
            tracked.connection.close();
        }
    }

    /// Runs one eviction pass immediately, bypassing the real fixed 30s/30s background schedule -
    /// exposed only so tests can exercise eviction logic quickly and deterministically.
    #[doc(hidden)]
    pub fn run_eviction_now(&self) {
        run_eviction(&self.shared);
    }

    #[doc(hidden)]
    pub fn tracked_connection_count(&self) -> usize {
        self.shared.outbound.lock().unwrap().len() + self.shared.inbound.lock().unwrap().len()
    }

    fn get_or_connect(&self, host_: &str, port: u16) -> Result<Arc<TrackedConnection>, OftError> {
        // Held across the entire find-or-connect sequence, including the blocking connect() call
        // itself - this serializes all outbound connection establishment through this peer
        // (whether to the same or different hosts), trading a little parallelism for a simpler,
        // still-correct implementation (the same documented tradeoff the C port makes).
        let mut outbound = self.shared.outbound.lock().unwrap();
        let key = (host_.to_string(), port);
        if let Some(tracked) = outbound.get(&key) {
            return Ok(tracked.clone());
        }

        let connection = connect(host_, port, Some(self.shared.connection_options.clone()))?;
        let tracked = Arc::new(TrackedConnection {
            connection,
            pending_data_cleared_at: Mutex::new(None),
        });
        outbound.insert(key, tracked.clone());
        drop(outbound);
        wire_tracking(&self.shared, tracked.clone());
        Ok(tracked)
    }
}

fn wire_tracking(shared: &Arc<Shared>, tracked: Arc<TrackedConnection>) {
    let identity = tracked.connection.identity().clone();

    let received_shared = shared.clone();
    let received_identity = identity.clone();
    tracked.connection.set_received_handler(Some(Arc::new(move |data| {
        received_shared.received_slot.raise((received_identity.clone(), data));
    })));

    let ack_shared = shared.clone();
    let ack_identity = identity.clone();
    tracked.connection.set_acknowledged_handler(Some(Arc::new(move |tag| {
        if let Some(handler) = ack_shared.acknowledged_handler.lock().unwrap().as_ref() {
            handler(ack_identity.clone(), tag);
        }
    })));

    let untrack_shared = shared.clone();
    let connection_for_untrack = tracked.connection.clone();
    tracked.connection.set_disconnected_handler(Some(Arc::new(move |_reason| {
        untrack(&untrack_shared, &connection_for_untrack);
    })));
}

fn untrack(shared: &Arc<Shared>, connection: &Connection) {
    {
        let mut outbound = shared.outbound.lock().unwrap();
        let key = outbound
            .iter()
            .find(|(_, tracked)| same_connection(&tracked.connection, connection))
            .map(|(key, _)| key.clone());
        if let Some(key) = key {
            outbound.remove(&key);
            return;
        }
    }

    let mut inbound = shared.inbound.lock().unwrap();
    inbound.retain(|tracked| !same_connection(&tracked.connection, connection));
}

fn same_connection(a: &Connection, b: &Connection) -> bool {
    Arc::ptr_eq(&a.shared, &b.shared)
}

fn all_tracked(shared: &Arc<Shared>) -> Vec<Arc<TrackedConnection>> {
    let outbound: Vec<_> = shared.outbound.lock().unwrap().values().cloned().collect();
    let inbound: Vec<_> = shared.inbound.lock().unwrap().clone();
    outbound.into_iter().chain(inbound).collect()
}

fn run_eviction(shared: &Arc<Shared>) {
    let now = Instant::now();
    let tracked_list = all_tracked(shared);

    let mut candidates: Vec<Arc<TrackedConnection>> = Vec::new();
    for tracked in &tracked_list {
        // A connection with pending/unacknowledged data is never auto-disconnected here,
        // regardless of which eviction condition it would otherwise meet.
        if tracked.connection.has_pending_data() {
            *tracked.pending_data_cleared_at.lock().unwrap() = None;
            continue;
        }

        let cleared_at = {
            let mut guard = tracked.pending_data_cleared_at.lock().unwrap();
            if guard.is_none() {
                *guard = Some(now);
            }
            guard.unwrap()
        };

        if now.duration_since(cleared_at) < EVICTION_GRACE_PERIOD {
            continue;
        }

        candidates.push(tracked.clone());
    }

    let mut should_evict: Vec<Arc<TrackedConnection>> = Vec::new();
    for tracked in &candidates {
        let connection = &tracked.connection;
        let last_sent = connection.last_sent_at();
        let last_received = connection.last_received_at();
        let last_activity = last_sent.max(last_received);
        let idle_for = std::time::SystemTime::now().duration_since(last_activity).unwrap_or_default();
        let age = std::time::SystemTime::now().duration_since(connection.connected_at()).unwrap_or_default();

        if idle_for > shared.idle_timeout || age > shared.max_connection_lifetime {
            should_evict.push(tracked.clone());
        }
    }

    let remaining = tracked_list.len().saturating_sub(should_evict.len());
    if remaining > shared.max_connection_count {
        let excess = remaining - shared.max_connection_count;
        let mut eligible: Vec<&Arc<TrackedConnection>> = candidates.iter().filter(|tracked| !should_evict.iter().any(|e| Arc::ptr_eq(e, tracked))).collect();
        eligible.sort_by_key(|tracked| tracked.connection.connected_at());
        for tracked in eligible.into_iter().take(excess) {
            should_evict.push(tracked.clone());
        }
    }

    for tracked in should_evict {
        tracked.connection.disconnect();
    }
}
