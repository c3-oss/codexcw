//! JSONL line decoding into [`Event`] values.

use std::time::SystemTime;

use serde::Deserialize;
use serde_json::value::RawValue;

use crate::event::{
    CodexErrorEvent, ErrorPayload, Event, EventKind, EventPayload, FileChange, Item, ItemKind,
    Usage,
};

#[derive(Deserialize)]
struct WireEvent {
    #[serde(rename = "type")]
    kind: Option<String>,
    #[serde(default)]
    thread_id: String,
    #[serde(default)]
    usage: Usage,
    #[serde(default)]
    item: Option<Box<RawValue>>,
    #[serde(default)]
    error: Option<Box<RawValue>>,
    #[serde(default)]
    message: String,
}

#[derive(Deserialize)]
struct WireItem {
    #[serde(default)]
    id: String,
    #[serde(default, rename = "type")]
    kind: String,
    #[serde(default)]
    status: String,
    #[serde(default)]
    text: String,
    #[serde(default)]
    message: String,
    #[serde(default)]
    command: String,
    #[serde(default)]
    aggregated_output: String,
    #[serde(default)]
    exit_code: Option<i32>,
    #[serde(default)]
    changes: Vec<FileChange>,
}

/// Decodes one JSONL line into an [`Event`].
///
/// Returns a human-readable message on malformed input; the caller wraps it
/// into a line-numbered decode error.
pub(crate) fn decode_event(
    line: &[u8],
    run_id: &str,
    thread_id: &str,
    now: SystemTime,
) -> Result<Event, String> {
    let raw = String::from_utf8_lossy(line).into_owned();

    let wire: WireEvent = serde_json::from_slice(line).map_err(|err| err.to_string())?;
    let kind = wire.kind.unwrap_or_default();
    if kind.is_empty() {
        return Err("missing event type".to_string());
    }

    let mut event = Event {
        kind: EventKind::from_wire(&kind),
        run_id: run_id.to_string(),
        thread_id: thread_id.to_string(),
        received_at: now,
        raw,
        payload: EventPayload::Other,
    };

    event.payload = match event.kind {
        EventKind::ThreadStarted => {
            event.thread_id = wire.thread_id.clone();
            EventPayload::ThreadStarted {
                thread_id: wire.thread_id,
            }
        }
        EventKind::TurnStarted => EventPayload::TurnStarted,
        EventKind::TurnCompleted => EventPayload::TurnCompleted { usage: wire.usage },
        EventKind::TurnFailed => EventPayload::TurnFailed {
            error: decode_event_error(wire.error.as_deref()),
            usage: wire.usage,
        },
        EventKind::ItemStarted => EventPayload::ItemStarted(decode_item(wire.item.as_deref())?),
        EventKind::ItemCompleted => EventPayload::ItemCompleted(decode_item(wire.item.as_deref())?),
        EventKind::Error => {
            let raw_error = wire
                .error
                .as_deref()
                .map(|value| value.get().to_string())
                .unwrap_or_default();
            let mut message = wire.message;
            if message.is_empty() && !raw_error.is_empty() {
                message = raw_error.clone();
            }
            EventPayload::Error(CodexErrorEvent {
                message,
                raw: raw_error,
            })
        }
        EventKind::Other(_) => EventPayload::Other,
    };

    Ok(event)
}

fn decode_item(raw: Option<&RawValue>) -> Result<Item, String> {
    let raw = raw.ok_or_else(|| "missing item payload".to_string())?;
    let text = raw.get();
    if text.is_empty() {
        return Err("missing item payload".to_string());
    }
    let wire: WireItem = serde_json::from_str(text).map_err(|err| err.to_string())?;
    Ok(Item {
        id: wire.id,
        kind: ItemKind::from_wire(&wire.kind),
        status: wire.status,
        raw: text.to_string(),
        text: wire.text,
        message: wire.message,
        command: wire.command,
        aggregated_output: wire.aggregated_output,
        exit_code: wire.exit_code,
        changes: wire.changes,
    })
}

fn decode_event_error(raw: Option<&RawValue>) -> ErrorPayload {
    let Some(raw) = raw else {
        return ErrorPayload::default();
    };
    let text = raw.get().to_string();
    if text.is_empty() {
        return ErrorPayload::default();
    }

    #[derive(Deserialize)]
    struct Inner {
        #[serde(default)]
        message: String,
    }

    let mut message = serde_json::from_str::<Inner>(&text)
        .map(|inner| inner.message)
        .unwrap_or_default();
    if message.is_empty() {
        message = text.clone();
    }
    ErrorPayload { message, raw: text }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn decodes_thread_started_and_backfills_thread_id() {
        let event = decode_event(
            br#"{"type":"thread.started","thread_id":"t-1"}"#,
            "run-x",
            "",
            SystemTime::UNIX_EPOCH,
        )
        .unwrap();
        assert_eq!(event.kind, EventKind::ThreadStarted);
        assert_eq!(event.thread_id, "t-1");
        assert_eq!(event.run_id, "run-x");
    }

    #[test]
    fn decodes_agent_message_item() {
        let event = decode_event(
            br#"{"type":"item.completed","item":{"id":"i0","type":"agent_message","text":"Oi."}}"#,
            "run-x",
            "t-1",
            SystemTime::UNIX_EPOCH,
        )
        .unwrap();
        let item = event.item_completed().unwrap();
        assert_eq!(item.kind, ItemKind::AgentMessage);
        assert_eq!(item.text, "Oi.");
        assert_eq!(event.thread_id, "t-1");
    }

    #[test]
    fn decodes_command_execution_exit_code() {
        let event = decode_event(
            br#"{"type":"item.completed","item":{"id":"i0","type":"command_execution","command":"false","exit_code":7,"status":"failed"}}"#,
            "run-x",
            "",
            SystemTime::UNIX_EPOCH,
        )
        .unwrap();
        let item = event.item_completed().unwrap();
        assert_eq!(item.exit_code, Some(7));
        assert_eq!(item.status, "failed");
    }

    #[test]
    fn decodes_collab_tool_call_kind() {
        let event = decode_event(
            br#"{"type":"item.started","item":{"id":"i0","type":"collab_tool_call","tool":"wait","sender_thread_id":"t-parent","receiver_thread_ids":[],"agents_states":{},"status":"in_progress"}}"#,
            "run-x",
            "t-parent",
            SystemTime::UNIX_EPOCH,
        )
        .unwrap();
        let item = event.item_started().unwrap();
        assert_eq!(item.kind, ItemKind::CollabToolCall);
        assert_eq!(item.status, "in_progress");
        assert!(item.raw.contains("\"tool\":\"wait\""));
    }

    #[test]
    fn missing_type_is_an_error() {
        let err = decode_event(b"{}", "run", "", SystemTime::UNIX_EPOCH).unwrap_err();
        assert_eq!(err, "missing event type");
    }

    #[test]
    fn invalid_json_is_an_error() {
        let err = decode_event(b"not-json", "run", "", SystemTime::UNIX_EPOCH).unwrap_err();
        assert!(!err.is_empty());
    }

    #[test]
    fn turn_failed_error_message() {
        let event = decode_event(
            br#"{"type":"turn.failed","error":{"message":"turn broke"}}"#,
            "run",
            "",
            SystemTime::UNIX_EPOCH,
        )
        .unwrap();
        match event.payload {
            EventPayload::TurnFailed { error, .. } => assert_eq!(error.message, "turn broke"),
            other => panic!("unexpected payload: {other:?}"),
        }
    }
}
