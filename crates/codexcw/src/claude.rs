//! The claude agent: `claude -p` argument building and normalization of its
//! stream-json events into the shared [`Event`] model.

use std::collections::HashMap;
use std::time::SystemTime;

use serde::Deserialize;
use serde_json::value::RawValue;

use crate::args::{nonempty, prompt_bytes, Prepared};
use crate::error::Error;
use crate::event::{
    ErrorPayload, Event, EventKind, EventPayload, FileChange, Item, ItemKind, ModelUsage, Usage,
};
use crate::request::Request;

/// Validates a request and builds its `claude -p` invocation.
pub(crate) fn prepare_claude(req: &Request) -> Result<Prepared, Error> {
    validate_claude_request(req)?;

    let mut schema = req.output_schema.clone().filter(|s| !s.is_empty());
    if let Some(path) = req.output_schema_path.as_deref().filter(|p| !p.is_empty()) {
        let data = std::fs::read(path).map_err(|err| Error::Process(err.to_string()))?;
        schema = Some(data);
    }

    let mut args = vec![
        "-p".to_string(),
        "--output-format".to_string(),
        "stream-json".to_string(),
        "--verbose".to_string(),
    ];
    if let Some(model) = nonempty(&req.model) {
        args.push("--model".to_string());
        args.push(model);
    }
    if let Some(mode) = nonempty(&req.permission_mode) {
        args.push("--permission-mode".to_string());
        args.push(mode);
    }
    for tool in &req.allowed_tools {
        args.push("--allowed-tools".to_string());
        args.push(tool.clone());
    }
    for tool in &req.disallowed_tools {
        args.push("--disallowed-tools".to_string());
        args.push(tool.clone());
    }
    if req.dangerously_bypass_sandbox {
        args.push("--dangerously-skip-permissions".to_string());
    }
    if !req.persistent {
        args.push("--no-session-persistence".to_string());
    }
    for dir in &req.add_dirs {
        args.push("--add-dir".to_string());
        args.push(dir.clone());
    }
    if let Some(schema) = schema {
        args.push("--json-schema".to_string());
        args.push(String::from_utf8_lossy(&schema).into_owned());
    }
    if let Some(id) = req.resume_id.as_deref().filter(|id| !id.is_empty()) {
        args.push("--resume".to_string());
        args.push(id.to_string());
    }
    if req.resume_last {
        args.push("--continue".to_string());
    }

    Ok(Prepared {
        args,
        stdin: prompt_bytes(req),
        schema_temp: None,
        current_dir: req.dir.clone().filter(|d| !d.is_empty()),
    })
}

pub(crate) fn validate_claude_request(req: &Request) -> Result<(), Error> {
    if req.prompt.is_empty() && req.stdin.is_none() {
        return Err(Error::PromptRequired);
    }
    let inline_schema = req.output_schema.as_ref().is_some_and(|s| !s.is_empty());
    let schema_path = req
        .output_schema_path
        .as_deref()
        .is_some_and(|p| !p.is_empty());
    if inline_schema && schema_path {
        return Err(Error::invalid(
            "output schema path and inline schema are mutually exclusive",
        ));
    }
    let resume_id = req.resume_id.as_deref().is_some_and(|id| !id.is_empty());
    if resume_id && req.resume_last {
        return Err(Error::invalid(
            "resume id and resume last are mutually exclusive",
        ));
    }

    let unsupported: [(bool, &str); 14] = [
        (!req.images.is_empty(), "images"),
        (
            req.profile.as_deref().is_some_and(|p| !p.is_empty()),
            "profile",
        ),
        (req.sandbox.is_some(), "sandbox"),
        (req.approval.is_some(), "approval"),
        (!req.config.is_empty(), "config overrides"),
        (!req.enable.is_empty(), "enable flags"),
        (!req.disable.is_empty(), "disable flags"),
        (req.strict_config, "strict config"),
        (req.ignore_user_config, "ignore user config"),
        (req.ignore_rules, "ignore rules"),
        (req.require_git_repo, "require git repo"),
        (
            req.output_last_message_path
                .as_deref()
                .is_some_and(|p| !p.is_empty()),
            "output last message path",
        ),
        (req.dangerously_bypass_hooks, "dangerously bypass hooks"),
        (req.resume_all, "resume all"),
    ];
    for (set, name) in unsupported {
        if set {
            return Err(Error::invalid(format!(
                "{name} is not supported by the claude agent"
            )));
        }
    }
    Ok(())
}

/// Normalizes `claude -p --output-format stream-json` events into the shared
/// [`Event`] model. Every emitted event keeps the original Claude JSON line in
/// its `raw` field.
#[derive(Default)]
pub(crate) struct ClaudeDecoder {
    pending: HashMap<String, Item>,
    block_indexes: HashMap<String, usize>,
    last_agent_text: String,
}

#[derive(Deserialize)]
struct WireEvent {
    #[serde(rename = "type")]
    kind: Option<String>,
    #[serde(default)]
    subtype: String,
    #[serde(default)]
    session_id: String,
    #[serde(default)]
    message: Option<WireMessage>,
    #[serde(default)]
    is_error: bool,
    #[serde(default)]
    result: Option<String>,
    #[serde(default)]
    usage: WireUsage,
    #[serde(default)]
    total_cost_usd: f64,
    #[serde(default, rename = "modelUsage")]
    model_usage: HashMap<String, ModelUsage>,
    #[serde(default)]
    tool_use_result: Option<Box<RawValue>>,
}

#[derive(Deserialize)]
struct WireMessage {
    #[serde(default)]
    id: String,
    #[serde(default)]
    content: Option<Box<RawValue>>,
}

#[derive(Deserialize)]
struct WireBlock {
    #[serde(rename = "type", default)]
    kind: String,
    #[serde(default)]
    text: String,
    #[serde(default)]
    thinking: String,
    #[serde(default)]
    id: String,
    #[serde(default)]
    name: String,
    #[serde(default)]
    input: Option<Box<RawValue>>,
    #[serde(default)]
    tool_use_id: String,
    #[serde(default)]
    content: Option<Box<RawValue>>,
    #[serde(default)]
    is_error: bool,
}

#[derive(Deserialize, Default)]
struct WireUsage {
    #[serde(default)]
    input_tokens: i64,
    #[serde(default)]
    cache_creation_input_tokens: i64,
    #[serde(default)]
    cache_read_input_tokens: i64,
    #[serde(default)]
    output_tokens: i64,
}

#[derive(Deserialize, Default)]
struct WireToolInput {
    #[serde(default)]
    command: String,
    #[serde(default)]
    file_path: String,
    #[serde(default)]
    notebook_path: String,
}

impl ClaudeDecoder {
    pub(crate) fn decode(
        &mut self,
        line: &[u8],
        run_id: &str,
        thread_id: &str,
        now: SystemTime,
    ) -> Result<Vec<Event>, String> {
        let raw = String::from_utf8_lossy(line).into_owned();
        let wire: WireEvent = serde_json::from_slice(line).map_err(|err| err.to_string())?;
        let kind = wire.kind.clone().unwrap_or_default();
        if kind.is_empty() {
            return Err("missing event type".to_string());
        }

        let mut base = Event {
            kind: EventKind::Other(kind.clone()),
            run_id: run_id.to_string(),
            thread_id: thread_id.to_string(),
            received_at: now,
            raw,
            payload: EventPayload::Other,
        };
        if !wire.session_id.is_empty() {
            base.thread_id = wire.session_id.clone();
        }

        match kind.as_str() {
            "system" if wire.subtype == "init" => {
                let mut started = base.clone();
                started.kind = EventKind::ThreadStarted;
                started.payload = EventPayload::ThreadStarted {
                    thread_id: wire.session_id.clone(),
                };
                let mut turn = base;
                turn.kind = EventKind::TurnStarted;
                turn.payload = EventPayload::TurnStarted;
                Ok(vec![started, turn])
            }
            "assistant" => self.decode_assistant(base, &wire),
            "user" => self.decode_user(base, &wire),
            "result" => Ok(self.decode_result(base, &wire)),
            _ => Ok(vec![base]),
        }
    }

    fn decode_assistant(&mut self, base: Event, wire: &WireEvent) -> Result<Vec<Event>, String> {
        let mut events = Vec::new();
        let message_id = wire.message.as_ref().map(|m| m.id.as_str()).unwrap_or("");
        for raw_block in content_blocks(wire.message.as_ref()) {
            let block: WireBlock =
                serde_json::from_str(raw_block.get()).map_err(|err| err.to_string())?;
            match block.kind.as_str() {
                "text" => {
                    self.last_agent_text = block.text.clone();
                    events.push(item_completed(
                        &base,
                        Item {
                            id: self.next_block_id(message_id),
                            kind: ItemKind::AgentMessage,
                            status: "completed".to_string(),
                            raw: raw_block.get().to_string(),
                            text: block.text,
                            ..Default::default()
                        },
                    ));
                }
                "thinking" => {
                    events.push(item_completed(
                        &base,
                        Item {
                            id: self.next_block_id(message_id),
                            kind: ItemKind::Reasoning,
                            status: "completed".to_string(),
                            raw: raw_block.get().to_string(),
                            text: block.thinking,
                            ..Default::default()
                        },
                    ));
                }
                "tool_use" => {
                    let item = tool_item(&block, raw_block.get());
                    self.pending.insert(block.id.clone(), item.clone());
                    let mut started = base.clone();
                    started.kind = EventKind::ItemStarted;
                    started.payload = EventPayload::ItemStarted(item);
                    events.push(started);
                }
                _ => {}
            }
        }
        if events.is_empty() {
            return Ok(vec![base]);
        }
        Ok(events)
    }

    fn decode_user(&mut self, base: Event, wire: &WireEvent) -> Result<Vec<Event>, String> {
        let mut events = Vec::new();
        for raw_block in content_blocks(wire.message.as_ref()) {
            let block: WireBlock =
                serde_json::from_str(raw_block.get()).map_err(|err| err.to_string())?;
            if block.kind != "tool_result" {
                continue;
            }
            let Some(mut item) = self.pending.remove(&block.tool_use_id) else {
                continue;
            };
            item.raw = raw_block.get().to_string();
            item.aggregated_output = tool_result_text(block.content.as_deref());
            item.status = if block.is_error {
                "failed".to_string()
            } else {
                "completed".to_string()
            };
            if item.kind == ItemKind::CommandExecution {
                item.exit_code = command_exit_code(wire.tool_use_result.as_deref(), &block)
                    .or((!block.is_error).then_some(0));
            }
            if item.kind == ItemKind::FileChange && !item.changes.is_empty() {
                if let Some(kind) = file_change_kind(wire.tool_use_result.as_deref()) {
                    item.changes[0].kind = kind;
                }
            }
            events.push(item_completed(&base, item));
        }
        if events.is_empty() {
            return Ok(vec![base]);
        }
        Ok(events)
    }

    fn decode_result(&mut self, base: Event, wire: &WireEvent) -> Vec<Event> {
        let result_text = wire.result.clone().unwrap_or_default();
        let usage = result_usage(wire);
        if wire.is_error {
            let message = if result_text.is_empty() {
                "claude run failed".to_string()
            } else {
                result_text
            };
            let mut failed = base.clone();
            failed.kind = EventKind::TurnFailed;
            failed.payload = EventPayload::TurnFailed {
                error: ErrorPayload {
                    message,
                    raw: base.raw,
                },
                usage,
            };
            return vec![failed];
        }

        let mut events = Vec::new();
        if !result_text.is_empty() && result_text != self.last_agent_text {
            events.push(item_completed(
                &base,
                Item {
                    id: "result".to_string(),
                    kind: ItemKind::AgentMessage,
                    status: "completed".to_string(),
                    raw: base.raw.clone(),
                    text: result_text,
                    ..Default::default()
                },
            ));
        }
        let mut completed = base;
        completed.kind = EventKind::TurnCompleted;
        completed.payload = EventPayload::TurnCompleted { usage };
        events.push(completed);
        events
    }

    fn next_block_id(&mut self, message_id: &str) -> String {
        let index = self
            .block_indexes
            .entry(message_id.to_string())
            .or_default();
        let id = block_id(message_id, *index);
        *index += 1;
        id
    }
}

fn result_usage(wire: &WireEvent) -> Usage {
    Usage {
        input_tokens: wire.usage.input_tokens,
        cache_creation_input_tokens: wire.usage.cache_creation_input_tokens,
        cached_input_tokens: wire.usage.cache_read_input_tokens,
        output_tokens: wire.usage.output_tokens,
        total_tokens: wire.usage.input_tokens
            + wire.usage.cache_creation_input_tokens
            + wire.usage.cache_read_input_tokens
            + wire.usage.output_tokens,
        total_cost_usd: wire.total_cost_usd,
        model_usage: wire.model_usage.clone(),
        reasoning_output_tokens: 0,
    }
}

fn tool_item(block: &WireBlock, raw_block: &str) -> Item {
    let input: WireToolInput = block
        .input
        .as_deref()
        .and_then(|raw| serde_json::from_str(raw.get()).ok())
        .unwrap_or_default();

    let mut item = Item {
        id: block.id.clone(),
        status: "in_progress".to_string(),
        raw: raw_block.to_string(),
        ..Default::default()
    };

    match block.name.as_str() {
        "Bash" => {
            item.kind = ItemKind::CommandExecution;
            item.command = input.command;
        }
        "Write" | "Edit" | "MultiEdit" | "NotebookEdit" => {
            item.kind = ItemKind::FileChange;
            let path = if input.file_path.is_empty() {
                input.notebook_path
            } else {
                input.file_path
            };
            let kind = if block.name == "Write" {
                "add"
            } else {
                "update"
            };
            item.changes = vec![FileChange {
                path,
                kind: kind.to_string(),
            }];
        }
        "WebSearch" => item.kind = ItemKind::WebSearch,
        name if name.starts_with("mcp__") => item.kind = ItemKind::McpToolCall,
        _ => item.kind = ItemKind::ToolCall,
    }
    item
}

fn content_blocks(message: Option<&WireMessage>) -> Vec<Box<RawValue>> {
    message
        .and_then(|m| m.content.as_deref())
        .and_then(|raw| serde_json::from_str::<Vec<Box<RawValue>>>(raw.get()).ok())
        .unwrap_or_default()
}

fn tool_result_text(raw: Option<&RawValue>) -> String {
    let Some(raw) = raw else {
        return String::new();
    };
    if let Ok(text) = serde_json::from_str::<String>(raw.get()) {
        return text;
    }

    #[derive(Deserialize)]
    struct TextBlock {
        #[serde(rename = "type", default)]
        kind: String,
        #[serde(default)]
        text: String,
    }

    if let Ok(blocks) = serde_json::from_str::<Vec<TextBlock>>(raw.get()) {
        return blocks
            .into_iter()
            .filter(|b| b.kind == "text" && !b.text.is_empty())
            .map(|b| b.text)
            .collect::<Vec<_>>()
            .join("\n");
    }
    String::new()
}

fn file_change_kind(raw: Option<&RawValue>) -> Option<String> {
    #[derive(Deserialize)]
    struct ToolUseResult {
        #[serde(rename = "type", default)]
        kind: String,
    }

    let result: ToolUseResult = serde_json::from_str(raw?.get()).ok()?;
    match result.kind.as_str() {
        "create" => Some("add".to_string()),
        "update" => Some("update".to_string()),
        _ => None,
    }
}

fn command_exit_code(raw: Option<&RawValue>, block: &WireBlock) -> Option<i32> {
    #[derive(Deserialize)]
    struct ToolUseResult {
        #[serde(default, alias = "exitCode")]
        exit_code: Option<i32>,
    }

    if let Some(raw) = raw {
        if let Ok(result) = serde_json::from_str::<ToolUseResult>(raw.get()) {
            if result.exit_code.is_some() {
                return result.exit_code;
            }
        }
        if let Ok(text) = serde_json::from_str::<String>(raw.get()) {
            if let Some(code) = exit_code_from_text(&text) {
                return Some(code);
            }
        }
    }
    exit_code_from_text(&tool_result_text(block.content.as_deref()))
}

fn exit_code_from_text(text: &str) -> Option<i32> {
    let suffix = text.rsplit_once("Exit code ")?.1;
    let code = suffix
        .split(|ch: char| !ch.is_ascii_digit() && ch != '-')
        .next()?;
    code.parse().ok()
}

fn block_id(message_id: &str, index: usize) -> String {
    if message_id.is_empty() {
        format!("block_{index}")
    } else {
        format!("{message_id}_{index}")
    }
}

fn item_completed(base: &Event, item: Item) -> Event {
    let mut event = base.clone();
    event.kind = EventKind::ItemCompleted;
    event.payload = EventPayload::ItemCompleted(item);
    event
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::request::Request;

    #[test]
    fn builds_claude_args() {
        let req = Request {
            prompt: "diga oi".to_string(),
            model: Some("haiku".to_string()),
            permission_mode: Some("acceptEdits".to_string()),
            allowed_tools: vec!["Bash(git *)".to_string()],
            disallowed_tools: vec!["WebSearch".to_string()],
            add_dirs: vec!["/other".to_string()],
            output_schema: Some(br#"{"type":"object"}"#.to_vec()),
            dangerously_bypass_sandbox: true,
            ..Default::default()
        };
        let prepared = prepare_claude(&req).unwrap();
        assert_eq!(prepared.stdin, b"diga oi");
        assert!(prepared.schema_temp.is_none());
        for want in [
            "-p",
            "--output-format",
            "stream-json",
            "--verbose",
            "--model",
            "haiku",
            "--permission-mode",
            "acceptEdits",
            "--allowed-tools",
            "Bash(git *)",
            "--disallowed-tools",
            "WebSearch",
            "--dangerously-skip-permissions",
            "--no-session-persistence",
            "--add-dir",
            "/other",
            "--json-schema",
            r#"{"type":"object"}"#,
        ] {
            assert!(
                prepared.args.contains(&want.to_string()),
                "missing arg: {want}"
            );
        }
    }

    #[test]
    fn builds_resume_args_and_current_dir() {
        let req = Request {
            prompt: "continue".to_string(),
            dir: Some("/work".to_string()),
            resume_id: Some("sess-1".to_string()),
            persistent: true,
            ..Default::default()
        };
        let prepared = prepare_claude(&req).unwrap();
        assert_eq!(prepared.current_dir.as_deref(), Some("/work"));
        let resume_index = prepared.args.iter().position(|a| a == "--resume").unwrap();
        assert_eq!(prepared.args[resume_index + 1], "sess-1");
        assert!(!prepared
            .args
            .contains(&"--no-session-persistence".to_string()));
    }

    #[test]
    fn rejects_codex_only_fields() {
        for req in [
            Request {
                prompt: "x".to_string(),
                sandbox: Some(crate::request::SandboxMode::ReadOnly),
                ..Default::default()
            },
            Request {
                prompt: "x".to_string(),
                profile: Some("work".to_string()),
                ..Default::default()
            },
            Request {
                prompt: "x".to_string(),
                images: vec!["a.png".to_string()],
                ..Default::default()
            },
            Request {
                prompt: "x".to_string(),
                resume_id: Some("id".to_string()),
                resume_all: true,
                ..Default::default()
            },
        ] {
            assert!(matches!(
                validate_claude_request(&req),
                Err(Error::InvalidRequest(_))
            ));
        }
    }

    #[test]
    fn init_becomes_thread_and_turn_started() {
        let mut decoder = ClaudeDecoder::default();
        let events = decoder
            .decode(
                br#"{"type":"system","subtype":"init","session_id":"sess-1"}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        assert_eq!(events.len(), 2);
        assert_eq!(events[0].kind, EventKind::ThreadStarted);
        assert_eq!(events[0].thread_id, "sess-1");
        assert_eq!(events[1].kind, EventKind::TurnStarted);
    }

    #[test]
    fn tool_use_and_result_pair_into_items() {
        let mut decoder = ClaudeDecoder::default();
        let started = decoder
            .decode(
                br#"{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls"}}]},"session_id":"sess-1"}"#,
                "run-x",
                "sess-1",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        let item = started[0].item_started().unwrap();
        assert_eq!(item.kind, ItemKind::CommandExecution);
        assert_eq!(item.command, "ls");
        assert_eq!(item.status, "in_progress");

        let completed = decoder
            .decode(
                br#"{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"total 0"}]},"session_id":"sess-1"}"#,
                "run-x",
                "sess-1",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        let item = completed[0].item_completed().unwrap();
        assert_eq!(item.id, "t1");
        assert_eq!(item.aggregated_output, "total 0");
        assert_eq!(item.status, "completed");
        assert_eq!(item.exit_code, Some(0));
    }

    #[test]
    fn bash_failure_extracts_exit_code() {
        let mut decoder = ClaudeDecoder::default();
        decoder
            .decode(
                br#"{"type":"assistant","message":{"id":"msg_1","content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"sh -c 'exit 7'"}}]}}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        let completed = decoder
            .decode(
                br#"{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"Exit code 7","is_error":true}]},"tool_use_result":"Error: Exit code 7"}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();

        let item = completed[0].item_completed().unwrap();
        assert_eq!(item.status, "failed");
        assert_eq!(item.exit_code, Some(7));
    }

    #[test]
    fn write_tool_becomes_file_change() {
        let mut decoder = ClaudeDecoder::default();
        let events = decoder
            .decode(
                br#"{"type":"assistant","message":{"id":"m","content":[{"type":"tool_use","id":"t2","name":"Write","input":{"file_path":"/tmp/a.txt","content":"x"}}]}}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        let item = events[0].item_started().unwrap();
        assert_eq!(item.kind, ItemKind::FileChange);
        assert_eq!(item.changes[0].path, "/tmp/a.txt");
        assert_eq!(item.changes[0].kind, "add");
    }

    #[test]
    fn error_result_becomes_turn_failed() {
        let mut decoder = ClaudeDecoder::default();
        let events = decoder
            .decode(
                br#"{"type":"result","subtype":"success","is_error":true,"result":"model gone","session_id":"s","total_cost_usd":0.02,"usage":{"input_tokens":3,"cache_creation_input_tokens":5,"cache_read_input_tokens":7,"output_tokens":11}}"#,
                "run-x",
                "s",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        assert_eq!(events.len(), 1);
        match &events[0].payload {
            EventPayload::TurnFailed { error, usage } => {
                assert_eq!(error.message, "model gone");
                assert_eq!(usage.total_tokens, 26);
                assert_eq!(usage.total_cost_usd, 0.02);
            }
            other => panic!("unexpected payload: {other:?}"),
        }
    }

    #[test]
    fn result_synthesizes_final_message_and_usage() {
        let mut decoder = ClaudeDecoder::default();
        let events = decoder
            .decode(
                br#"{"type":"result","subtype":"success","is_error":false,"result":"{\"a\":1}","session_id":"s","total_cost_usd":0.25,"usage":{"input_tokens":9,"cache_creation_input_tokens":11,"cache_read_input_tokens":40,"output_tokens":7},"modelUsage":{"claude-sonnet":{"inputTokens":9,"outputTokens":7,"cacheReadInputTokens":40,"cacheCreationInputTokens":11,"webSearchRequests":2,"costUSD":0.25,"contextWindow":200000,"maxOutputTokens":64000}}}"#,
                "run-x",
                "s",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        assert_eq!(events.len(), 2);
        let item = events[0].item_completed().unwrap();
        assert_eq!(item.kind, ItemKind::AgentMessage);
        assert_eq!(item.text, r#"{"a":1}"#);
        match &events[1].payload {
            EventPayload::TurnCompleted { usage } => {
                assert_eq!(usage.input_tokens, 9);
                assert_eq!(usage.cache_creation_input_tokens, 11);
                assert_eq!(usage.cached_input_tokens, 40);
                assert_eq!(usage.output_tokens, 7);
                assert_eq!(usage.total_tokens, 67);
                assert_eq!(usage.total_cost_usd, 0.25);
                let model = &usage.model_usage["claude-sonnet"];
                assert_eq!(model.input_tokens, 9);
                assert_eq!(model.output_tokens, 7);
                assert_eq!(model.cache_read_input_tokens, 40);
                assert_eq!(model.cache_creation_input_tokens, 11);
                assert_eq!(model.web_search_requests, 2);
                assert_eq!(model.cost_usd, 0.25);
                assert_eq!(model.context_window, 200_000);
                assert_eq!(model.max_output_tokens, 64_000);
            }
            other => panic!("unexpected payload: {other:?}"),
        }
    }

    #[test]
    fn assistant_block_ids_are_unique_across_streamed_chunks() {
        let mut decoder = ClaudeDecoder::default();
        let thinking = decoder
            .decode(
                br#"{"type":"assistant","message":{"id":"msg_1","content":[{"type":"thinking","thinking":"hmm"}]}}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        let text = decoder
            .decode(
                br#"{"type":"assistant","message":{"id":"msg_1","content":[{"type":"text","text":"Done."}]}}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();

        assert_eq!(thinking[0].item_completed().unwrap().id, "msg_1_0");
        assert_eq!(text[0].item_completed().unwrap().id, "msg_1_1");
    }

    #[test]
    fn duplicate_result_text_is_not_reemitted() {
        let mut decoder = ClaudeDecoder::default();
        decoder
            .decode(
                br#"{"type":"assistant","message":{"id":"m","content":[{"type":"text","text":"Done."}]}}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        let events = decoder
            .decode(
                br#"{"type":"result","subtype":"success","is_error":false,"result":"Done.","usage":{"input_tokens":1,"output_tokens":1}}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].kind, EventKind::TurnCompleted);
    }

    #[test]
    fn unknown_events_pass_through() {
        let mut decoder = ClaudeDecoder::default();
        let events = decoder
            .decode(
                br#"{"type":"rate_limit_event","session_id":"s"}"#,
                "run-x",
                "",
                SystemTime::UNIX_EPOCH,
            )
            .unwrap();
        assert_eq!(events.len(), 1);
        assert_eq!(
            events[0].kind,
            EventKind::Other("rate_limit_event".to_string())
        );
        assert_eq!(events[0].thread_id, "s");
    }
}
