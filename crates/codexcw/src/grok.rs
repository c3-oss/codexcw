//! Grok Build headless argument construction.

use std::io::Write;

use crate::args::{nonempty, prompt_bytes, Prepared};
use crate::error::Error;
use crate::request::{ApprovalPolicy, Request, SandboxMode};

/// Validates a request and builds its Grok Build headless invocation.
pub(crate) fn prepare_grok(
    req: &Request,
    default_sandbox: SandboxMode,
    default_approval: ApprovalPolicy,
) -> Result<Prepared, Error> {
    validate_grok_request(req)?;

    let mut prompt_file = tempfile::Builder::new()
        .prefix("codexcw-grok-prompt-")
        .suffix(".md")
        .tempfile()
        .map_err(|err| Error::Process(err.to_string()))?;
    prompt_file
        .write_all(&prompt_bytes(req))
        .map_err(|err| Error::Process(err.to_string()))?;
    prompt_file
        .flush()
        .map_err(|err| Error::Process(err.to_string()))?;
    let prompt_path = prompt_file.path().to_string_lossy().into_owned();

    let mut schema = req.output_schema.clone().filter(|value| !value.is_empty());
    if let Some(path) = req
        .output_schema_path
        .as_deref()
        .filter(|path| !path.is_empty())
    {
        schema = Some(std::fs::read(path).map_err(|err| Error::Process(err.to_string()))?);
    }

    let resume = req.is_resume();
    let mut args = vec![
        "--no-auto-update".to_string(),
        "--output-format".to_string(),
        "streaming-messages-json".to_string(),
        "--verbatim".to_string(),
        "--prompt-file".to_string(),
        prompt_path,
    ];

    if let Some(dir) = nonempty(&req.dir) {
        args.push("--cwd".to_string());
        args.push(dir);
    }
    if let Some(model) = nonempty(&req.model) {
        args.push("--model".to_string());
        args.push(model);
    }
    if let Some(profile) = nonempty(&req.profile) {
        args.push("--agent".to_string());
        args.push(profile);
    }

    if req.dangerously_bypass_sandbox {
        args.extend([
            "--sandbox".to_string(),
            "off".to_string(),
            "--permission-mode".to_string(),
            "bypassPermissions".to_string(),
        ]);
    } else {
        if !resume || req.sandbox.is_some() {
            let sandbox = req.sandbox.unwrap_or(default_sandbox);
            args.push("--sandbox".to_string());
            args.push(grok_sandbox(sandbox).to_string());
        }

        let permission = nonempty(&req.permission_mode)
            .unwrap_or_else(|| grok_approval(req.approval.unwrap_or(default_approval)).to_string());
        args.push("--permission-mode".to_string());
        args.push(permission);
    }

    for rule in &req.allowed_tools {
        args.push("--allow".to_string());
        args.push(rule.clone());
    }
    for rule in &req.disallowed_tools {
        args.push("--deny".to_string());
        args.push(rule.clone());
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
        stdin: Vec::new(),
        temp_files: vec![prompt_file],
        current_dir: None,
    })
}

fn validate_grok_request(req: &Request) -> Result<(), Error> {
    if req.prompt.is_empty() && req.stdin.is_none() {
        return Err(Error::PromptRequired);
    }
    let inline_schema = req
        .output_schema
        .as_ref()
        .is_some_and(|value| !value.is_empty());
    let schema_path = req
        .output_schema_path
        .as_deref()
        .is_some_and(|path| !path.is_empty());
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
    if req.approval.is_some()
        && req
            .permission_mode
            .as_deref()
            .is_some_and(|mode| !mode.is_empty())
    {
        return Err(Error::invalid(
            "approval and permission mode are mutually exclusive for the grok agent",
        ));
    }

    let unsupported: [(bool, &str); 12] = [
        (!req.add_dirs.is_empty(), "add dirs"),
        (!req.images.is_empty(), "images"),
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
                .is_some_and(|path| !path.is_empty()),
            "output last message path",
        ),
        (req.dangerously_bypass_hooks, "dangerously bypass hooks"),
        (req.resume_all, "resume all"),
    ];
    for (set, name) in unsupported {
        if set {
            return Err(Error::invalid(format!(
                "{name} is not supported by the grok agent"
            )));
        }
    }
    Ok(())
}

fn grok_sandbox(sandbox: SandboxMode) -> &'static str {
    match sandbox {
        SandboxMode::ReadOnly => "read-only",
        SandboxMode::WorkspaceWrite => "workspace",
        SandboxMode::DangerFullAccess => "off",
    }
}

fn grok_approval(approval: ApprovalPolicy) -> &'static str {
    match approval {
        ApprovalPolicy::Never => "dontAsk",
        ApprovalPolicy::Untrusted | ApprovalPolicy::OnRequest => "default",
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn defaults() -> (SandboxMode, ApprovalPolicy) {
        (SandboxMode::ReadOnly, ApprovalPolicy::Never)
    }

    #[test]
    fn builds_safe_defaults_and_prompt_file() {
        let (sandbox, approval) = defaults();
        let prepared = prepare_grok(&Request::new("diga oi"), sandbox, approval).unwrap();

        for want in [
            "--no-auto-update",
            "--output-format",
            "streaming-messages-json",
            "--verbatim",
            "--prompt-file",
            "--sandbox",
            "read-only",
            "--permission-mode",
            "dontAsk",
        ] {
            assert!(prepared.args.contains(&want.to_string()), "missing {want}");
        }
        let path = prepared.args[prepared
            .args
            .iter()
            .position(|arg| arg == "--prompt-file")
            .unwrap()
            + 1]
        .clone();
        assert_eq!(std::fs::read_to_string(path).unwrap(), "diga oi");
        assert_eq!(prepared.temp_files.len(), 1);
    }

    #[test]
    fn builds_advanced_args_and_wraps_stdin() {
        let (sandbox, approval) = defaults();
        let prepared = prepare_grok(
            &Request {
                prompt: "prompt".to_string(),
                stdin: Some(b"extra".to_vec()),
                dir: Some("/work".to_string()),
                model: Some("grok-4.6".to_string()),
                profile: Some("reviewer".to_string()),
                sandbox: Some(SandboxMode::WorkspaceWrite),
                permission_mode: Some("acceptEdits".to_string()),
                allowed_tools: vec!["Bash(git *)".to_string()],
                disallowed_tools: vec!["WebSearch".to_string()],
                output_schema: Some(br#"{"type":"object"}"#.to_vec()),
                resume_id: Some("sess-9".to_string()),
                persistent: true,
                ..Default::default()
            },
            sandbox,
            approval,
        )
        .unwrap();

        for want in [
            "--cwd",
            "/work",
            "--model",
            "grok-4.6",
            "--agent",
            "reviewer",
            "--sandbox",
            "workspace",
            "--permission-mode",
            "acceptEdits",
            "--allow",
            "Bash(git *)",
            "--deny",
            "WebSearch",
            "--json-schema",
            r#"{"type":"object"}"#,
            "--resume",
            "sess-9",
        ] {
            assert!(prepared.args.contains(&want.to_string()), "missing {want}");
        }
        let prompt_path = &prepared.args[prepared
            .args
            .iter()
            .position(|arg| arg == "--prompt-file")
            .unwrap()
            + 1];
        assert_eq!(
            std::fs::read_to_string(prompt_path).unwrap(),
            "prompt\n\n<stdin>\nextra\n</stdin>\n"
        );
    }

    #[test]
    fn resume_uses_saved_sandbox_unless_explicit() {
        let (sandbox, approval) = defaults();
        let prepared = prepare_grok(
            &Request {
                prompt: "continue".to_string(),
                resume_last: true,
                ..Default::default()
            },
            sandbox,
            approval,
        )
        .unwrap();
        assert!(!prepared.args.contains(&"--sandbox".to_string()));
        assert!(prepared.args.contains(&"--continue".to_string()));
    }

    #[test]
    fn rejects_unsupported_and_conflicting_fields() {
        let (sandbox, approval) = defaults();
        for req in [
            Request::default(),
            Request {
                prompt: "x".to_string(),
                add_dirs: vec!["/other".to_string()],
                ..Default::default()
            },
            Request {
                prompt: "x".to_string(),
                approval: Some(ApprovalPolicy::Never),
                permission_mode: Some("dontAsk".to_string()),
                ..Default::default()
            },
        ] {
            assert!(prepare_grok(&req, sandbox, approval).is_err());
        }
    }
}
