//! A bounded buffer that retains only the trailing bytes written to it.

use std::sync::Mutex;

/// Keeps at most `limit` trailing bytes, mirroring captured stderr tails.
pub(crate) struct TailBuffer {
    limit: usize,
    data: Mutex<Vec<u8>>,
}

impl TailBuffer {
    pub(crate) fn new(limit: usize) -> Self {
        TailBuffer {
            limit,
            data: Mutex::new(Vec::new()),
        }
    }

    pub(crate) fn write(&self, chunk: &[u8]) {
        if self.limit == 0 {
            return;
        }
        let mut data = self.data.lock().expect("tail buffer poisoned");
        data.extend_from_slice(chunk);
        if data.len() > self.limit {
            let start = data.len() - self.limit;
            data.drain(0..start);
        }
    }

    pub(crate) fn snapshot(&self) -> String {
        let data = self.data.lock().expect("tail buffer poisoned");
        String::from_utf8_lossy(&data).into_owned()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_only_trailing_bytes() {
        let tail = TailBuffer::new(4);
        tail.write(b"0123456789");
        assert_eq!(tail.snapshot(), "6789");
    }

    #[test]
    fn zero_limit_discards_everything() {
        let tail = TailBuffer::new(0);
        tail.write(b"data");
        assert_eq!(tail.snapshot(), "");
    }
}
