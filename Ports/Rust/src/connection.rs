use crate::buffered_slot::BufferedSlot;
use crate::error::OftError;
use crate::frame::{write_frame, FrameReader};
use crate::identity::Identity;
use crate::send_handle::{Completion, SendFailure, SendHandle};
use crate::stream::Stream;
use crate::wire::{decode_packet, encode_packet, Packet};
use std::collections::{HashMap, VecDeque};
use std::io::{self, Read};
use std::net::TcpStream;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{self, Receiver, Sender};
use std::sync::{Arc, Mutex};
use std::thread::JoinHandle;
use std::time::{Duration, Instant, SystemTime};

const DEFAULT_MAX_PACKET_DATA_SIZE: usize = 1024;
pub(crate) const DEFAULT_POLL_INTERVAL: Duration = Duration::from_secs(1);
pub(crate) const DEFAULT_POLL_TIMEOUT: Duration = Duration::from_secs(5);
const READ_TIMEOUT_CAP: Duration = Duration::from_millis(100);

const CONTROL_COMPLETION: u32 = 0;
const CONTROL_CANCELLATION: u32 = 1;
const CONTROL_RECEIPT: u32 = 2;
const CONTROL_UNIT: u32 = 3;

/// An opaque, application-controlled value attached to a `send()`, referenced later via
/// `delivery_status_handler` each time that message's delivery status changes.
pub type Tag = Box<dyn std::any::Any + Send>;

/// A lifecycle stage of a tagged send, reported via `Connection::set_delivery_status_handler`/
/// `Peer::set_delivery_status_handler`. Every tagged send passes through `Queued`, `Sending`, then
/// either `Cancelled` or `Sent` followed by `Acknowledged`; `Interrupted`/`Resumed` pairs may occur
/// any number of times in between `Sending` and `Sent`, for a multi-packet send that a
/// higher-priority send preempts (see `Docs/OFT.md` §6) - a single-packet send can never be
/// interrupted, since there is nothing between its first and only packet for another send to
/// interleave with. `Cancelled` can only occur before `Sent`: once a send's final packet has
/// actually been written, cancelling it can no longer prevent delivery, so it always proceeds to
/// `Sent`/`Acknowledged` instead.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DeliveryStatus {
    /// The send has been queued and is waiting its turn - reported once `send()` returns, not
    /// necessarily synchronously before it does.
    Queued,
    /// The send's first packet has started transmitting.
    Sending,
    /// A higher-priority send has preempted this one before it finished (see `Docs/OFT.md` §6); it
    /// remains queued and eventually resumes.
    Interrupted,
    /// Transmission has resumed after an `Interrupted` preemption.
    Resumed,
    /// The send's final packet has been written, but not yet acknowledged.
    Sent,
    /// The send's final packet has been acknowledged (a `Receipt` - see `Docs/OFT.md` §4.1): the
    /// send is now fully delivered. This is the terminal status for a send that isn't cancelled.
    Acknowledged,
    /// The send was cancelled (see `Docs/OFT.md` §7) before its final packet was written. This is
    /// the terminal status for a cancelled send.
    Cancelled,
}

/// Raises `status` on `shared`'s delivery-status handler with `tag`, if `tag` is `Some`. A free
/// function rather than an `Engine` method so it can be called with a `&PendingMessage` borrowed
/// from `Engine::in_progress` without conflicting with a concurrent `&Engine` borrow.
fn raise_delivery_status(shared: &Shared, tag: &Option<Tag>, status: DeliveryStatus) {
    if let Some(tag) = tag {
        if let Some(handler) = shared.delivery_status_handler.lock().unwrap().as_ref() {
            handler(tag, status);
        }
    }
}

enum Work {
    Send {
        message_id: u64,
        data: Vec<u8>,
        priority: u32,
        tag: Option<Tag>,
        completion: Arc<Completion>,
    },
    Cancel(u64),
    Rekey,
    Disconnect,
}

struct PendingMessage {
    data: Vec<u8>,
    priority: u32,
    tag: Option<Tag>,
    completion: Arc<Completion>,
    started: bool,
    bytes_sent: usize,
    cancel_requested: bool,
    /// Whether this message was preempted by a higher-priority send since it last sent a packet -
    /// set when `DeliveryStatus::Interrupted` is raised, cleared (after raising
    /// `DeliveryStatus::Resumed`) once it's picked to send again.
    was_interrupted: bool,
}

pub(crate) struct Shared {
    identity: Identity,
    connected_at: SystemTime,
    last_sent_at: Mutex<SystemTime>,
    last_received_at: Mutex<SystemTime>,
    received_slot: BufferedSlot<Vec<u8>>,
    disconnected_slot: BufferedSlot<Option<String>>,
    delivery_status_handler: Mutex<Option<Arc<dyn Fn(&Tag, DeliveryStatus) + Send + Sync>>>,
    closed: AtomicBool,
    has_pending_data: AtomicBool,
    work_tx: Mutex<Option<Sender<Work>>>,
    io_thread: Mutex<Option<JoinHandle<()>>>,
    next_message_id: AtomicU64,
    /// A clone of the connection's own socket, used only so `disconnect()` can shut it down
    /// immediately from the calling thread - see `Stream::try_clone_socket`'s own docs for why
    /// this matters (a blocking `read()` on the I/O thread would otherwise only notice a
    /// disconnect request on its next read-timeout tick, up to `READ_TIMEOUT_CAP` later).
    shutdown_socket: Option<TcpStream>,
}

/// A single established connection, produced by `connect()`, `host()`'s accepted-connection
/// callback, or `Peer`.
#[derive(Clone)]
pub struct Connection {
    pub(crate) shared: Arc<Shared>,
}

impl Connection {
    pub fn identity(&self) -> &Identity {
        &self.shared.identity
    }

    pub fn connected_at(&self) -> SystemTime {
        self.shared.connected_at
    }

    pub fn last_sent_at(&self) -> SystemTime {
        *self.shared.last_sent_at.lock().unwrap()
    }

    pub fn last_received_at(&self) -> SystemTime {
        *self.shared.last_received_at.lock().unwrap()
    }

    /// Assigns the (single) callback invoked for every fully-received application message.
    /// Assigning any time after this connection was returned is always safe, even if the peer
    /// sent something the instant the connection was up - see `Docs/Architecture.md`'s "Buffered
    /// notifications" section.
    pub fn set_received_handler(&self, handler: Option<Arc<dyn Fn(Vec<u8>) + Send + Sync>>) {
        self.shared.received_slot.set_handler(handler);
    }

    /// Assigns the (single) callback invoked once this connection permanently disconnects, with a
    /// human-readable reason if it closed due to an error (`None` for a clean/requested
    /// disconnect).
    pub fn set_disconnected_handler(&self, handler: Option<Arc<dyn Fn(Option<String>) + Send + Sync>>) {
        self.shared.disconnected_slot.set_handler(handler);
    }

    /// Assigns the (single) callback invoked whenever data sent via `send()` with a `Some(tag)`
    /// changes delivery status (see `DeliveryStatus` for the full lifecycle), with a reference to
    /// that same tag and its new status. Called multiple times per send, once per status it passes
    /// through. Deliberately *not* buffered, unlike `received_handler`/`disconnected_handler` - see
    /// `Docs/Architecture.md`'s own note on why that's safe (it can only ever be triggered by the
    /// caller's own `send()` call).
    pub fn set_delivery_status_handler(&self, handler: Option<Arc<dyn Fn(&Tag, DeliveryStatus) + Send + Sync>>) {
        *self.shared.delivery_status_handler.lock().unwrap() = handler;
    }

    /// Sends `data` at the given `priority` (see `Docs/OFT.md` §5-§7). `tag`, if present, is
    /// handed back via `delivery_status_handler` as this send passes through each
    /// `DeliveryStatus`.
    pub fn send(&self, data: Vec<u8>, priority: u32, tag: Option<Tag>) -> SendHandle {
        let completion = Completion::new();
        if self.shared.closed.load(Ordering::SeqCst) {
            completion.complete(Err(SendFailure::Disconnected));
            return SendHandle {
                completion,
                cancel: Arc::new(|| {}),
            };
        }

        let message_id = self.shared.next_message_id.fetch_add(1, Ordering::SeqCst);
        let work_tx = self.shared.work_tx.lock().unwrap().clone();
        if let Some(tx) = &work_tx {
            let _ = tx.send(Work::Send {
                message_id,
                data,
                priority,
                tag,
                completion: completion.clone(),
            });
        } else {
            completion.complete(Err(SendFailure::Disconnected));
        }

        let cancel_tx = work_tx.clone();
        SendHandle {
            completion,
            cancel: Arc::new(move || {
                if let Some(tx) = &cancel_tx {
                    let _ = tx.send(Work::Cancel(message_id));
                }
            }),
        }
    }

    /// Requests a TLS 1.3 rekey (see `Docs/OFT.md` §8). A no-op if this connection was
    /// established with `SecurityMode::Trusted` - there's no TLS session to rekey.
    pub fn rekey(&self) -> Result<(), OftError> {
        if self.shared.closed.load(Ordering::SeqCst) {
            return Err(OftError::Disconnected);
        }
        if let Some(tx) = self.shared.work_tx.lock().unwrap().as_ref() {
            let _ = tx.send(Work::Rekey);
        }
        Ok(())
    }

    pub fn has_pending_data(&self) -> bool {
        self.shared.has_pending_data.load(Ordering::SeqCst)
    }

    pub fn is_connected(&self) -> bool {
        !self.shared.closed.load(Ordering::SeqCst)
    }

    /// Requests an immediate teardown and returns without waiting for the connection's background
    /// I/O thread to finish, which happens shortly afterward on its own.
    pub fn disconnect(&self) {
        if let Some(tx) = self.shared.work_tx.lock().unwrap().take() {
            let _ = tx.send(Work::Disconnect);
        }
        // Unblocks the I/O thread's blocking read() immediately rather than waiting for its next
        // read-timeout tick - see `shutdown_socket`'s own docs.
        if let Some(socket) = &self.shared.shutdown_socket {
            let _ = socket.shutdown(std::net::Shutdown::Both);
        }
    }

    /// Like `disconnect`, but waits for the background I/O thread to fully stop before returning.
    pub fn close(&self) {
        self.disconnect();
        if let Some(handle) = self.shared.io_thread.lock().unwrap().take() {
            let _ = handle.join();
        }
    }
}

pub(crate) fn spawn(stream: Stream, identity: Identity, max_packet_data_size: usize, poll_interval: Duration, poll_timeout: Duration, rekey_interval: Option<Duration>) -> Connection {
    let (work_tx, work_rx) = mpsc::channel();
    let now = SystemTime::now();
    let shutdown_socket = stream.try_clone_socket().ok();

    let shared = Arc::new(Shared {
        identity,
        connected_at: now,
        last_sent_at: Mutex::new(now),
        last_received_at: Mutex::new(now),
        received_slot: BufferedSlot::new(),
        disconnected_slot: BufferedSlot::new(),
        delivery_status_handler: Mutex::new(None),
        closed: AtomicBool::new(false),
        has_pending_data: AtomicBool::new(false),
        work_tx: Mutex::new(Some(work_tx)),
        io_thread: Mutex::new(None),
        next_message_id: AtomicU64::new(0),
        shutdown_socket,
    });

    let engine_shared = shared.clone();
    let max_packet_data_size = if max_packet_data_size == 0 { DEFAULT_MAX_PACKET_DATA_SIZE } else { max_packet_data_size };

    let handle = std::thread::spawn(move || {
        let mut engine = Engine {
            stream,
            shared: engine_shared,
            work_rx,
            frame_reader: FrameReader::new(),
            max_packet_data_size,
            poll_interval,
            poll_timeout,
            rekey_interval,
            outstanding_turn: false,
            queue: VecDeque::new(),
            in_progress: HashMap::new(),
            previous_send: None,
            inbound_channels: HashMap::new(),
            awaiting_completion: None,
            next_poll_send: Instant::now() + poll_interval,
            last_inbound_activity: Instant::now(),
            next_rekey: rekey_interval.map(|interval| Instant::now() + interval),
        };
        engine.run();
    });

    *shared.io_thread.lock().unwrap() = Some(handle);
    Connection { shared }
}

struct Engine {
    stream: Stream,
    shared: Arc<Shared>,
    work_rx: Receiver<Work>,
    frame_reader: FrameReader,
    max_packet_data_size: usize,
    poll_interval: Duration,
    poll_timeout: Duration,
    rekey_interval: Option<Duration>,
    outstanding_turn: bool,
    /// FIFO order messages were submitted in, used to break priority ties.
    queue: VecDeque<u64>,
    in_progress: HashMap<u64, PendingMessage>,
    /// The message a packet was most recently sent for, so a change in which message gets picked
    /// (other than that message finishing) can be recognized as a priority interruption (see
    /// `Docs/OFT.md` §6) rather than ordinary packet-by-packet progress on the same message.
    previous_send: Option<u64>,
    /// Per-priority-channel reassembly buffer for an in-progress multi-packet inbound message.
    inbound_channels: HashMap<u32, Vec<u8>>,
    /// The outcome to apply to a message's `SendHandle` once its most recently sent packet (a
    /// `Unit`, a multi-packet message's final `Completion` chunk, or a `Cancellation`) is actually
    /// acknowledged by a `Receipt` - a message only counts as delivered once acknowledged
    /// (Docs/OFT.md §4.1), not merely once its bytes are written to the socket.
    awaiting_completion: Option<(Result<(), SendFailure>, Arc<Completion>, Option<Tag>)>,
    next_poll_send: Instant,
    last_inbound_activity: Instant,
    next_rekey: Option<Instant>,
}

impl Engine {
    fn run(&mut self) {
        let mut read_buf = [0u8; 8192];

        loop {
            let now = Instant::now();
            let poll_timeout_deadline = self.last_inbound_activity + self.poll_timeout;
            let mut deadline = self.next_poll_send.min(poll_timeout_deadline);
            if let Some(next_rekey) = self.next_rekey {
                deadline = deadline.min(next_rekey);
            }

            let timeout = deadline.saturating_duration_since(now).min(READ_TIMEOUT_CAP).max(Duration::from_millis(1));
            let _ = self.stream.set_read_timeout(Some(timeout));

            match self.stream.read(&mut read_buf) {
                Ok(0) => {
                    self.teardown(None);
                    return;
                }
                Ok(n) => {
                    self.last_inbound_activity = Instant::now();
                    self.frame_reader.feed(&read_buf[..n]);
                    loop {
                        match self.frame_reader.try_take_frame() {
                            Ok(Some(frame)) => {
                                if let Err(err) = self.dispatch_frame(frame) {
                                    self.teardown(Some(err.to_string()));
                                    return;
                                }
                            }
                            Ok(None) => break,
                            Err(err) => {
                                self.teardown(Some(err.to_string()));
                                return;
                            }
                        }
                    }
                }
                Err(err) if err.kind() == io::ErrorKind::WouldBlock || err.kind() == io::ErrorKind::TimedOut => {}
                Err(err) => {
                    self.teardown(Some(err.to_string()));
                    return;
                }
            }

            if Instant::now() >= self.last_inbound_activity + self.poll_timeout {
                self.teardown(Some("peer went silent past poll_timeout".to_string()));
                return;
            }

            while let Ok(work) = self.work_rx.try_recv() {
                match work {
                    Work::Send { message_id, data, priority, tag, completion } => {
                        self.queue.push_back(message_id);
                        self.in_progress.insert(
                            message_id,
                            PendingMessage {
                                data,
                                priority,
                                tag,
                                completion,
                                started: false,
                                bytes_sent: 0,
                                cancel_requested: false,
                                was_interrupted: false,
                            },
                        );
                        self.shared.has_pending_data.store(true, Ordering::SeqCst);

                        // Raised here, from this connection's own I/O thread, rather than
                        // synchronously inside `Connection::send()` on the caller's thread.
                        let message = self.in_progress.get(&message_id).unwrap();
                        raise_delivery_status(&self.shared, &message.tag, DeliveryStatus::Queued);
                    }
                    Work::Cancel(id) => self.cancel(id),
                    Work::Rekey => {
                        let _ = self.stream.refresh_traffic_keys();
                    }
                    Work::Disconnect => {
                        self.teardown(None);
                        return;
                    }
                }
            }

            if Instant::now() >= self.next_poll_send {
                self.next_poll_send = Instant::now() + self.poll_interval;
                if write_frame(&mut self.stream, &[]).is_err() {
                    self.teardown(Some("failed writing poll frame".to_string()));
                    return;
                }
            }

            if let Some(next_rekey) = self.next_rekey {
                if Instant::now() >= next_rekey {
                    let _ = self.stream.refresh_traffic_keys();
                    self.next_rekey = self.rekey_interval.map(|interval| Instant::now() + interval);
                }
            }

            if !self.outstanding_turn {
                if let Err(err) = self.try_send_next() {
                    self.teardown(Some(err.to_string()));
                    return;
                }
            }
        }
    }

    fn cancel(&mut self, message_id: u64) {
        let Some(message) = self.in_progress.get_mut(&message_id) else {
            return;
        };

        let is_unit = message.data.len() <= self.max_packet_data_size;
        if !message.started {
            let message = self.in_progress.remove(&message_id).unwrap();
            self.queue.retain(|id| *id != message_id);
            message.completion.complete(Err(SendFailure::Cancelled));
            raise_delivery_status(&self.shared, &message.tag, DeliveryStatus::Cancelled);
        } else if is_unit {
            // Already dispatched and atomic - nothing left to cancel (Docs/OFT.md §7).
        } else {
            message.cancel_requested = true;
        }
    }

    fn dispatch_frame(&mut self, frame: Vec<u8>) -> Result<(), OftError> {
        if frame.is_empty() {
            return Ok(()); // Poll (Docs/OFT.md §4, §10) - liveness timestamp already updated by the caller.
        }

        let packet = decode_packet(&frame).map_err(|_| OftError::ValidationRejected("malformed packet".to_string()))?;
        self.handle_packet(packet)
    }

    fn handle_packet(&mut self, packet: Packet) -> Result<(), OftError> {
        match packet.control {
            CONTROL_RECEIPT => {
                self.outstanding_turn = false;
                if let Some((outcome, completion, tag)) = self.awaiting_completion.take() {
                    completion.complete(outcome);
                    match outcome {
                        Ok(()) => raise_delivery_status(&self.shared, &tag, DeliveryStatus::Acknowledged),
                        Err(SendFailure::Cancelled) => raise_delivery_status(&self.shared, &tag, DeliveryStatus::Cancelled),
                        Err(SendFailure::Disconnected) => {}
                    }
                }
            }
            CONTROL_CANCELLATION => {
                if let Some(priority) = self.highest_pending_inbound_channel() {
                    self.inbound_channels.remove(&priority);
                }
                self.send_receipt()?;
            }
            CONTROL_COMPLETION => {
                let mut data = if let Some(priority) = self.highest_pending_inbound_channel() {
                    self.inbound_channels.remove(&priority).unwrap_or_default()
                } else {
                    Vec::new()
                };
                data.extend_from_slice(&packet.data);
                self.deliver_received(data);
                self.send_receipt()?;
            }
            CONTROL_UNIT => {
                self.deliver_received(packet.data);
                self.send_receipt()?;
            }
            control => {
                let priority = control - 4;
                self.inbound_channels.entry(priority).or_default().extend_from_slice(&packet.data);
                self.send_receipt()?;
            }
        }
        Ok(())
    }

    fn highest_pending_inbound_channel(&self) -> Option<u32> {
        self.inbound_channels.keys().max().copied()
    }

    fn deliver_received(&mut self, data: Vec<u8>) {
        *self.shared.last_received_at.lock().unwrap() = SystemTime::now();
        self.shared.received_slot.raise(data);
    }

    fn send_receipt(&mut self) -> Result<(), OftError> {
        let encoded = encode_packet(&Packet { control: CONTROL_RECEIPT, data: Vec::new() });
        write_frame(&mut self.stream, &encoded)?;
        Ok(())
    }

    fn try_send_next(&mut self) -> Result<(), OftError> {
        // Highest-priority in-progress-or-queued multi-packet message. Ties broken by: an
        // already-started message (continue the channel already in flight) beats a not-yet-started
        // one, and among equally-eligible candidates the earliest-queued (`self.queue`'s own order)
        // wins - `Iterator::max_by_key` alone isn't enough here since it resolves ties toward the
        // *last* equal element, which would starve an earlier-queued same-priority message forever.
        let mut best_multipacket: Option<u64> = None;
        for id in &self.queue {
            let Some(message) = self.in_progress.get(id) else { continue };
            if message.data.len() <= self.max_packet_data_size {
                continue;
            }

            let better = match best_multipacket.and_then(|best_id| self.in_progress.get(&best_id)) {
                None => true,
                Some(best) => message.priority > best.priority || (message.priority == best.priority && message.started && !best.started),
            };
            if better {
                best_multipacket = Some(*id);
            }
        }

        // Highest-priority queued Unit message not yet started (earliest-queued breaks ties).
        let mut best_unit: Option<u64> = None;
        for id in &self.queue {
            let Some(message) = self.in_progress.get(id) else { continue };
            if message.data.len() > self.max_packet_data_size || message.started {
                continue;
            }

            let better = match best_unit.and_then(|best_id| self.in_progress.get(&best_id)) {
                None => true,
                Some(best) => message.priority > best.priority,
            };
            if better {
                best_unit = Some(*id);
            }
        }

        let multipacket_priority = best_multipacket.and_then(|id| self.in_progress.get(&id)).map(|m| m.priority);
        let unit_priority = best_unit.and_then(|id| self.in_progress.get(&id)).map(|m| m.priority);

        let send_unit = match (unit_priority, multipacket_priority) {
            (Some(up), Some(mp)) => up >= mp,
            (Some(_), None) => true,
            _ => false,
        };

        let chosen_id = if send_unit { best_unit } else { best_multipacket };
        if let Some(id) = chosen_id {
            self.raise_sending_interrupted_resumed(id);
        }

        if send_unit {
            if let Some(id) = best_unit {
                self.send_unit(id)?;
            }
        } else if let Some(id) = best_multipacket {
            self.send_multipacket_chunk(id)?;
        }

        Ok(())
    }

    /// Raises `DeliveryStatus::Interrupted` for `self.previous_send` if `chosen_id` picked a
    /// different message than last time (see `previous_send`'s own doc comment), and
    /// `DeliveryStatus::Sending`/`DeliveryStatus::Resumed` for `chosen_id` itself, as appropriate.
    /// Does not update `previous_send` itself - the caller does that once it knows whether the send
    /// that follows finishes the message.
    fn raise_sending_interrupted_resumed(&mut self, chosen_id: u64) {
        if let Some(previous_id) = self.previous_send {
            if previous_id != chosen_id {
                if let Some(previous) = self.in_progress.get_mut(&previous_id) {
                    previous.was_interrupted = true;
                    raise_delivery_status(&self.shared, &previous.tag, DeliveryStatus::Interrupted);
                }
            }
        }

        let message = self.in_progress.get_mut(&chosen_id).unwrap();
        if !message.started {
            raise_delivery_status(&self.shared, &message.tag, DeliveryStatus::Sending);
        } else if message.was_interrupted {
            message.was_interrupted = false;
            raise_delivery_status(&self.shared, &message.tag, DeliveryStatus::Resumed);
        }
    }

    fn send_unit(&mut self, message_id: u64) -> Result<(), OftError> {
        let message = self.in_progress.remove(&message_id).unwrap();
        self.queue.retain(|id| *id != message_id);

        let encoded = encode_packet(&Packet {
            control: CONTROL_UNIT,
            data: message.data,
        });
        write_frame(&mut self.stream, &encoded)?;
        *self.shared.last_sent_at.lock().unwrap() = SystemTime::now();
        self.outstanding_turn = true;
        self.update_has_pending_data();
        // A Unit is always fully sent in one packet, so it's always removed from in_progress above
        // - nothing left for a later send to interrupt.
        self.previous_send = None;

        raise_delivery_status(&self.shared, &message.tag, DeliveryStatus::Sent);

        // Not completed yet - only once its Receipt actually arrives (Docs/OFT.md §4.1); see
        // CONTROL_RECEIPT's own handling in handle_packet.
        self.awaiting_completion = Some((Ok(()), message.completion, message.tag));
        Ok(())
    }

    fn send_multipacket_chunk(&mut self, message_id: u64) -> Result<(), OftError> {
        let message = self.in_progress.get_mut(&message_id).unwrap();

        if message.cancel_requested {
            let message = self.in_progress.remove(&message_id).unwrap();
            self.queue.retain(|id| *id != message_id);
            let encoded = encode_packet(&Packet {
                control: CONTROL_CANCELLATION,
                data: Vec::new(),
            });
            write_frame(&mut self.stream, &encoded)?;
            *self.shared.last_sent_at.lock().unwrap() = SystemTime::now();
            self.outstanding_turn = true;
            self.update_has_pending_data();
            self.previous_send = None;
            self.awaiting_completion = Some((Err(SendFailure::Cancelled), message.completion, message.tag));
            return Ok(());
        }

        message.started = true;
        let remaining = message.data.len() - message.bytes_sent;
        let chunk_size = remaining.min(self.max_packet_data_size);
        let is_last = remaining == chunk_size;
        let chunk = message.data[message.bytes_sent..message.bytes_sent + chunk_size].to_vec();
        let control = if is_last { CONTROL_COMPLETION } else { message.priority + 4 };
        message.bytes_sent += chunk_size;

        let encoded = encode_packet(&Packet { control, data: chunk });
        write_frame(&mut self.stream, &encoded)?;
        *self.shared.last_sent_at.lock().unwrap() = SystemTime::now();
        self.outstanding_turn = true;
        self.update_has_pending_data();

        if is_last {
            raise_delivery_status(&self.shared, &self.in_progress[&message_id].tag, DeliveryStatus::Sent);

            let message = self.in_progress.remove(&message_id).unwrap();
            self.queue.retain(|id| *id != message_id);
            self.update_has_pending_data();
            self.previous_send = None;
            self.awaiting_completion = Some((Ok(()), message.completion, message.tag));
        } else {
            self.previous_send = Some(message_id);
        }

        Ok(())
    }

    fn update_has_pending_data(&self) {
        self.shared.has_pending_data.store(!self.in_progress.is_empty(), Ordering::SeqCst);
    }

    fn teardown(&mut self, reason: Option<String>) {
        if self.shared.closed.swap(true, Ordering::SeqCst) {
            return;
        }

        self.stream.shutdown();
        *self.shared.work_tx.lock().unwrap() = None;

        for id in self.queue.drain(..).collect::<Vec<_>>() {
            if let Some(message) = self.in_progress.remove(&id) {
                message.completion.complete(Err(SendFailure::Disconnected));
            }
        }
        self.shared.has_pending_data.store(false, Ordering::SeqCst);

        // A message whose final packet was already written but whose Receipt will now never
        // arrive (the connection is closing) never delivered - it just never got a chance to be
        // cancelled either, so this is a disconnect, not a cancellation.
        if let Some((_, completion, _)) = self.awaiting_completion.take() {
            completion.complete(Err(SendFailure::Disconnected));
        }

        self.shared.disconnected_slot.raise(reason);
    }
}
