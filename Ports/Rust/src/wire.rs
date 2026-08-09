//! Hand-written encoder/decoder for exactly the two messages defined in `../../OFT.proto` (`Hail`
//! and `Packet`) - not a general-purpose protobuf library. The wire format produced and consumed
//! here is standard protobuf binary encoding, so it interoperates with the C#/Java implementations
//! (which use a real protobuf runtime) and the C port (which hand-rolls the same thing). Both
//! messages are small and fixed (two fields each), so this keeps the crate dependency-free for the
//! wire layer specifically - see `AGENTS.md`'s "Dependencies" section for when hand-rolling is
//! appropriate.

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Hail {
    pub version: String,
    pub info: String,
}

#[derive(Debug, Clone, PartialEq, Eq, Default)]
pub struct Packet {
    pub control: u32,
    pub data: Vec<u8>,
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct WireError;

pub fn encode_varint(mut value: u64, out: &mut Vec<u8>) {
    loop {
        let byte = (value & 0x7F) as u8;
        value >>= 7;
        if value != 0 {
            out.push(byte | 0x80);
        } else {
            out.push(byte);
            break;
        }
    }
}

fn encode_tag(field_number: u32, wire_type: u32, out: &mut Vec<u8>) {
    encode_varint(((field_number as u64) << 3) | wire_type as u64, out);
}

fn encode_length_delimited(field_number: u32, data: &[u8], out: &mut Vec<u8>) {
    encode_tag(field_number, 2, out);
    encode_varint(data.len() as u64, out);
    out.extend_from_slice(data);
}

struct Reader<'a> {
    data: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(data: &'a [u8]) -> Self {
        Reader { data, pos: 0 }
    }

    fn has_remaining(&self) -> bool {
        self.pos < self.data.len()
    }

    fn read_varint(&mut self) -> Result<u64, WireError> {
        let mut result: u64 = 0;
        let mut shift = 0u32;

        while self.pos < self.data.len() {
            let byte = self.data[self.pos];
            self.pos += 1;
            result |= ((byte & 0x7F) as u64) << shift;
            if byte & 0x80 == 0 {
                return Ok(result);
            }

            shift += 7;
            if shift > 63 {
                return Err(WireError);
            }
        }

        Err(WireError)
    }

    fn read_length_delimited(&mut self) -> Result<&'a [u8], WireError> {
        let length = self.read_varint()? as usize;
        if length > self.data.len() - self.pos {
            return Err(WireError);
        }

        let slice = &self.data[self.pos..self.pos + length];
        self.pos += length;
        Ok(slice)
    }

    fn skip_field(&mut self, wire_type: u32) -> Result<(), WireError> {
        match wire_type {
            0 => {
                self.read_varint()?;
                Ok(())
            }
            2 => {
                self.read_length_delimited()?;
                Ok(())
            }
            5 => {
                if self.data.len() - self.pos < 4 {
                    return Err(WireError);
                }
                self.pos += 4;
                Ok(())
            }
            1 => {
                if self.data.len() - self.pos < 8 {
                    return Err(WireError);
                }
                self.pos += 8;
                Ok(())
            }
            _ => Err(WireError),
        }
    }
}

pub fn encode_hail(hail: &Hail) -> Vec<u8> {
    let mut out = Vec::new();
    encode_length_delimited(1, hail.version.as_bytes(), &mut out);
    encode_length_delimited(2, hail.info.as_bytes(), &mut out);
    out
}

pub fn decode_hail(data: &[u8]) -> Result<Hail, WireError> {
    let mut reader = Reader::new(data);
    let mut version: Option<String> = None;
    let mut info: Option<String> = None;

    while reader.has_remaining() {
        let tag = reader.read_varint()?;
        let field_number = (tag >> 3) as u32;
        let wire_type = (tag & 0x7) as u32;

        match (field_number, wire_type) {
            (1, 2) => {
                let bytes = reader.read_length_delimited()?;
                version = Some(String::from_utf8_lossy(bytes).into_owned());
            }
            (2, 2) => {
                let bytes = reader.read_length_delimited()?;
                info = Some(String::from_utf8_lossy(bytes).into_owned());
            }
            _ => reader.skip_field(wire_type)?,
        }
    }

    Ok(Hail {
        version: version.unwrap_or_default(),
        info: info.unwrap_or_default(),
    })
}

pub fn encode_packet(packet: &Packet) -> Vec<u8> {
    let mut out = Vec::new();

    /* Matches plain proto3 default-value omission - control is only emitted when nonzero, exactly
     * like the real protobuf runtimes the C#/Java ports use do for a field left at its default
     * value. This is safe because control 0 (Completion) is the only control value that could ever
     * be omitted this way, and a Completion packet is only ever the final chunk of a message too
     * large to fit in one packet (see Docs/OFT.md §4), so its data field is always non-empty and
     * alone always forces a nonzero-length frame, so it can never collide with Poll's bare
     * zero-length frame (Docs/OFT.md §4, §10). Every other control value is itself nonzero and is
     * always emitted. */
    if packet.control != 0 {
        encode_tag(1, 0, &mut out);
        encode_varint(packet.control as u64, &mut out);
    }

    if !packet.data.is_empty() {
        encode_length_delimited(2, &packet.data, &mut out);
    }

    out
}

pub fn decode_packet(data: &[u8]) -> Result<Packet, WireError> {
    let mut reader = Reader::new(data);
    let mut control: u32 = 0;
    let mut packet_data: Vec<u8> = Vec::new();

    while reader.has_remaining() {
        let tag = reader.read_varint()?;
        let field_number = (tag >> 3) as u32;
        let wire_type = (tag & 0x7) as u32;

        match (field_number, wire_type) {
            (1, 0) => {
                control = reader.read_varint()? as u32;
            }
            (2, 2) => {
                packet_data = reader.read_length_delimited()?.to_vec();
            }
            _ => reader.skip_field(wire_type)?,
        }
    }

    Ok(Packet {
        control,
        data: packet_data,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn varint_roundtrip() {
        for value in [0u64, 1, 127, 128, 300, u32::MAX as u64, u64::MAX] {
            let mut buf = Vec::new();
            encode_varint(value, &mut buf);
            let mut reader = Reader::new(&buf);
            assert_eq!(reader.read_varint().unwrap(), value);
        }
    }

    #[test]
    fn hail_roundtrip() {
        let hail = Hail {
            version: "oft/1".to_string(),
            info: "hello".to_string(),
        };
        let encoded = encode_hail(&hail);
        let decoded = decode_hail(&encoded).unwrap();
        assert_eq!(hail, decoded);
    }

    #[test]
    fn hail_multibyte_varint_length_roundtrip() {
        let hail = Hail {
            version: "oft/1".to_string(),
            info: "a".repeat(200),
        };
        let encoded = encode_hail(&hail);
        let decoded = decode_hail(&encoded).unwrap();
        assert_eq!(hail, decoded);
    }

    #[test]
    fn hail_default_decodes_to_empty_strings() {
        let decoded = decode_hail(&[]).unwrap();
        assert_eq!(decoded.version, "");
        assert_eq!(decoded.info, "");
    }

    #[test]
    fn hail_decode_skips_unknown_fields() {
        let hail = Hail {
            version: "oft/1".to_string(),
            info: "hello".to_string(),
        };
        let mut combined = Vec::new();
        combined.extend_from_slice(&[(10 << 3) | 0, 0x2A]); // unknown varint field
        combined.extend_from_slice(&[(11 << 3) | 2, 0x02, b'h', b'i']); // unknown length-delimited
        combined.extend_from_slice(&[(12 << 3) | 5, 0x01, 0x02, 0x03, 0x04]); // unknown fixed32
        combined.extend_from_slice(&[(13 << 3) | 1, 1, 2, 3, 4, 5, 6, 7, 8]); // unknown fixed64
        combined.extend_from_slice(&encode_hail(&hail));

        let decoded = decode_hail(&combined).unwrap();
        assert_eq!(decoded, hail);
    }

    #[test]
    fn hail_decode_rejects_invalid_wire_type() {
        let data = [((10u32 << 3) | 6) as u8]; // wire type 6 doesn't exist in protobuf
        assert!(decode_hail(&data).is_err());
    }

    #[test]
    fn hail_decode_rejects_truncated_length() {
        let data = [((1u32 << 3) | 2) as u8, 100]; // claims 100 bytes, none follow
        assert!(decode_hail(&data).is_err());
    }

    #[test]
    fn hail_decode_rejects_overlong_varint() {
        let data = [0x80u8; 11]; // never terminates within 64 bits
        assert!(decode_hail(&data).is_err());
    }

    #[test]
    fn packet_completion_with_empty_data_serializes_to_zero_bytes() {
        let packet = Packet {
            control: 0,
            data: Vec::new(),
        };
        assert_eq!(encode_packet(&packet), Vec::<u8>::new());
    }

    #[test]
    fn packet_completion_with_data_never_empty() {
        let packet = Packet {
            control: 0,
            data: b"final-chunk".to_vec(),
        };
        let encoded = encode_packet(&packet);
        assert!(!encoded.is_empty());
        let decoded = decode_packet(&encoded).unwrap();
        assert_eq!(decoded, packet);
    }

    #[test]
    fn packet_roundtrip_control_only() {
        let packet = Packet {
            control: 2,
            data: Vec::new(),
        };
        let encoded = encode_packet(&packet);
        let decoded = decode_packet(&encoded).unwrap();
        assert_eq!(decoded, packet);
    }

    #[test]
    fn packet_roundtrip_data_priority_channel() {
        let packet = Packet {
            control: 4,
            data: b"hello".to_vec(),
        };
        let encoded = encode_packet(&packet);
        let decoded = decode_packet(&encoded).unwrap();
        assert_eq!(decoded, packet);
    }

    #[test]
    fn packet_large_data_roundtrip() {
        let packet = Packet {
            control: 5,
            data: vec![0xABu8; 4096],
        };
        let encoded = encode_packet(&packet);
        let decoded = decode_packet(&encoded).unwrap();
        assert_eq!(decoded, packet);
    }

    #[test]
    fn packet_decode_rejects_truncated_data_length() {
        let data = [((2u32 << 3) | 2) as u8, 100];
        assert!(decode_packet(&data).is_err());
    }
}
