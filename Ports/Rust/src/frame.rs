use crate::wire::encode_varint;
use std::io::{self, Write};

/// Accumulates bytes fed in from successive (possibly partial) socket reads and extracts complete
/// varint-length-delimited frames (see `Docs/OFT.md` §2) as they become available. Kept as
/// persistent state (not local variables scoped to one read call) so a frame can be assembled
/// across many short reads without losing already-consumed bytes - this port's single I/O thread
/// per connection (see `connection.rs`) uses a short read timeout to also service outbound work,
/// so any given `read()` call may return with less than a full frame, or even mid-varint.
#[derive(Default)]
pub(crate) struct FrameReader {
    buffer: Vec<u8>,
}

impl FrameReader {
    pub(crate) fn new() -> Self {
        FrameReader::default()
    }

    pub(crate) fn feed(&mut self, data: &[u8]) {
        self.buffer.extend_from_slice(data);
    }

    /// Returns `Some(frame_bytes)` and removes that frame's bytes (length prefix included) from
    /// the internal buffer if a complete frame is available; `Some(empty vec)` for a bare
    /// zero-length frame (`Docs/OFT.md` §4, §10 - a `Poll`); `None` if more data is needed.
    ///
    /// # Errors
    /// Returns an error if the length prefix is malformed (overlong varint).
    pub(crate) fn try_take_frame(&mut self) -> Result<Option<Vec<u8>>, io::Error> {
        let mut pos = 0usize;
        let mut shift = 0u32;
        let mut length: u64 = 0;

        loop {
            if pos >= self.buffer.len() {
                return Ok(None); // length prefix not fully received yet
            }

            let byte = self.buffer[pos];
            pos += 1;
            length |= ((byte & 0x7F) as u64) << shift;
            if byte & 0x80 == 0 {
                break;
            }

            shift += 7;
            if shift > 63 {
                return Err(io::Error::new(io::ErrorKind::InvalidData, "overlong varint length prefix"));
            }
        }

        let length = length as usize;
        if self.buffer.len() - pos < length {
            return Ok(None); // frame body not fully received yet
        }

        let frame = self.buffer[pos..pos + length].to_vec();
        self.buffer.drain(0..pos + length);
        Ok(Some(frame))
    }
}

pub(crate) fn write_frame<W: Write>(writer: &mut W, data: &[u8]) -> io::Result<()> {
    let mut framed = Vec::with_capacity(data.len() + 5);
    encode_varint(data.len() as u64, &mut framed);
    framed.extend_from_slice(data);
    writer.write_all(&framed)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn single_frame_across_multiple_feeds() {
        let mut reader = FrameReader::new();
        reader.feed(&[5]); // length prefix
        assert_eq!(reader.try_take_frame().unwrap(), None);
        reader.feed(b"hel");
        assert_eq!(reader.try_take_frame().unwrap(), None);
        reader.feed(b"lo");
        assert_eq!(reader.try_take_frame().unwrap(), Some(b"hello".to_vec()));
    }

    #[test]
    fn zero_length_frame_is_poll() {
        let mut reader = FrameReader::new();
        reader.feed(&[0]);
        assert_eq!(reader.try_take_frame().unwrap(), Some(Vec::new()));
    }

    #[test]
    fn multiple_frames_back_to_back() {
        let mut reader = FrameReader::new();
        reader.feed(&[5, b'h', b'e', b'l', b'l', b'o', 0, 3, b'b', b'y', b'e']);
        assert_eq!(reader.try_take_frame().unwrap(), Some(b"hello".to_vec()));
        assert_eq!(reader.try_take_frame().unwrap(), Some(Vec::new()));
        assert_eq!(reader.try_take_frame().unwrap(), Some(b"bye".to_vec()));
        assert_eq!(reader.try_take_frame().unwrap(), None);
    }

    #[test]
    fn overlong_varint_errors() {
        let mut reader = FrameReader::new();
        reader.feed(&[0x80u8; 11]);
        assert!(reader.try_take_frame().is_err());
    }

    #[test]
    fn write_frame_roundtrips_through_reader() {
        let mut buf = Vec::new();
        write_frame(&mut buf, b"hello").unwrap();
        let mut reader = FrameReader::new();
        reader.feed(&buf);
        assert_eq!(reader.try_take_frame().unwrap(), Some(b"hello".to_vec()));
    }
}
