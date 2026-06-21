//! Building `codex exec` arguments and the stdin payload from a [`Request`].

use std::io::Write;

use tempfile::NamedTempFile;

use crate::error::Error;
use crate::request::{ApprovalPolicy, Request, SandboxMode};

/// A prepared command: argv, stdin bytes, and the temporary schema file (if any).
pub(crate) struct Prepared {
    pub args: Vec<String>,
    pub stdin: Vec<u8>,
    /// Kept alive so the inline output-schema file exists for the run, then
    /// deleted on drop.
    pub schema_temp: Option<NamedTempFile>,
}

/// Validates a request and builds its `codex exec` invocation.
pub(crate) fn prepare(
    req: &Request,
    default_sandbox: SandboxMode,
    default_approval: ApprovalPolicy,
) -> Result<Prepared, Error> {
    validate_request(req)?;

    let mut schema_temp = None;
    let mut schema_path = req.output_schema_path.clone().unwrap_or_default();
    if let Some(bytes) = &req.output_schema {
        if !bytes.is_empty() {
            let mut file = tempfile::Builder::new()
                .prefix("codexcw-schema-")
                .suffix(".json")
                .tempfile()
                .map_err(|err| Error::Process(err.to_string()))?;
            file.write_all(bytes)
                .map_err(|err| Error::Process(err.to_string()))?;
            file.flush()
                .map_err(|err| Error::Process(err.to_string()))?;
            schema_path = file.path().to_string_lossy().into_owned();
            schema_temp = Some(file);
        }
    }

    let mut args = vec!["exec".to_string()];
    if req.is_resume() {
        args.push("resume".to_string());
    }

    append_common_args(
        &mut args,
        req,
        &schema_path,
        default_sandbox,
        default_approval,
    );

    if req.resume_last {
        args.push("--last".to_string());
    }
    if req.resume_all {
        args.push("--all".to_string());
    }
    if let Some(id) = &req.resume_id {
        if !id.is_empty() {
            args.push(id.clone());
        }
    }
    args.push("-".to_string());

    Ok(Prepared {
        args,
        stdin: prompt_bytes(req),
        schema_temp,
    })
}

pub(crate) fn validate_request(req: &Request) -> Result<(), Error> {
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
    let resume = req.is_resume();
    let resume_id = req.resume_id.as_deref().is_some_and(|id| !id.is_empty());
    if resume_id && req.resume_last {
        return Err(Error::invalid(
            "resume id and resume last are mutually exclusive",
        ));
    }
    if req.resume_all && !resume {
        return Err(Error::invalid(
            "resume all requires resume id or resume last",
        ));
    }
    if resume {
        let dir = req.dir.as_deref().is_some_and(|d| !d.is_empty());
        let profile = req.profile.as_deref().is_some_and(|p| !p.is_empty());
        if dir || !req.add_dirs.is_empty() || profile {
            return Err(Error::invalid(
                "dir, add dirs, and profile are not supported by codex exec resume",
            ));
        }
    }
    Ok(())
}

fn append_common_args(
    args: &mut Vec<String>,
    req: &Request,
    schema_path: &str,
    default_sandbox: SandboxMode,
    default_approval: ApprovalPolicy,
) {
    let resume = req.is_resume();

    args.push("--json".to_string());
    if !resume {
        args.push("--color".to_string());
        args.push("never".to_string());
    }
    if req.strict_config {
        args.push("--strict-config".to_string());
    }
    if let Some(model) = nonempty(&req.model) {
        args.push("-m".to_string());
        args.push(model);
    }
    if !resume {
        if let Some(profile) = nonempty(&req.profile) {
            args.push("-p".to_string());
            args.push(profile);
        }
    }
    for feature in &req.enable {
        args.push("--enable".to_string());
        args.push(feature.clone());
    }
    for feature in &req.disable {
        args.push("--disable".to_string());
        args.push(feature.clone());
    }
    for image in &req.images {
        args.push("-i".to_string());
        args.push(image.clone());
    }
    if !req.require_git_repo {
        args.push("--skip-git-repo-check".to_string());
    }
    if !req.persistent {
        args.push("--ephemeral".to_string());
    }
    if req.ignore_user_config {
        args.push("--ignore-user-config".to_string());
    }
    if req.ignore_rules {
        args.push("--ignore-rules".to_string());
    }
    if req.dangerously_bypass_sandbox {
        args.push("--dangerously-bypass-approvals-and-sandbox".to_string());
    } else {
        let sandbox = req.sandbox.unwrap_or(default_sandbox);
        if resume {
            args.push("-c".to_string());
            args.push(format!("sandbox_mode=\"{}\"", sandbox.as_str()));
        } else {
            args.push("--sandbox".to_string());
            args.push(sandbox.as_str().to_string());
        }
        let approval = req.approval.unwrap_or(default_approval);
        args.push("-c".to_string());
        args.push(format!("approval_policy=\"{}\"", approval.as_str()));
    }
    if req.dangerously_bypass_hooks {
        args.push("--dangerously-bypass-hook-trust".to_string());
    }
    if !resume {
        if let Some(dir) = nonempty(&req.dir) {
            args.push("-C".to_string());
            args.push(dir);
        }
        for dir in &req.add_dirs {
            args.push("--add-dir".to_string());
            args.push(dir.clone());
        }
    }
    if !schema_path.is_empty() {
        args.push("--output-schema".to_string());
        args.push(schema_path.to_string());
    }
    if let Some(path) = nonempty(&req.output_last_message_path) {
        args.push("-o".to_string());
        args.push(path);
    }
    for override_ in &req.config {
        args.push("-c".to_string());
        args.push(override_.as_arg());
    }
}

fn prompt_bytes(req: &Request) -> Vec<u8> {
    match (!req.prompt.is_empty(), req.stdin.as_ref()) {
        (true, Some(stdin)) => {
            let mut out = Vec::with_capacity(req.prompt.len() + stdin.len() + 24);
            out.extend_from_slice(req.prompt.as_bytes());
            out.extend_from_slice(b"\n\n<stdin>\n");
            out.extend_from_slice(stdin);
            out.extend_from_slice(b"\n</stdin>\n");
            out
        }
        (true, None) => req.prompt.clone().into_bytes(),
        (false, Some(stdin)) => stdin.clone(),
        (false, None) => Vec::new(),
    }
}

fn nonempty(value: &Option<String>) -> Option<String> {
    value.as_ref().filter(|v| !v.is_empty()).cloned()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::request::ConfigOverride;

    fn defaults() -> (SandboxMode, ApprovalPolicy) {
        (SandboxMode::ReadOnly, ApprovalPolicy::Never)
    }

    #[test]
    fn builds_safe_defaults() {
        let (s, a) = defaults();
        let prepared = prepare(&Request::new("diga oi"), s, a).unwrap();
        let args = &prepared.args;
        assert!(args.contains(&"exec".to_string()));
        assert!(args.contains(&"--json".to_string()));
        assert!(args.contains(&"--color".to_string()));
        assert!(args.contains(&"never".to_string()));
        assert!(args.contains(&"--skip-git-repo-check".to_string()));
        assert!(args.contains(&"--ephemeral".to_string()));
        assert!(args.contains(&"--sandbox".to_string()));
        assert!(args.contains(&"read-only".to_string()));
        assert!(args.contains(&r#"approval_policy="never""#.to_string()));
        assert_eq!(args.last().unwrap(), "-");
        assert_eq!(prepared.stdin, b"diga oi");
    }

    #[test]
    fn builds_advanced_args() {
        let (s, a) = defaults();
        let req = Request {
            prompt: "prompt".to_string(),
            stdin: Some(b"extra".to_vec()),
            dir: Some("/work".to_string()),
            add_dirs: vec!["/other".to_string()],
            images: vec!["image.png".to_string()],
            model: Some("gpt-test".to_string()),
            profile: Some("work".to_string()),
            sandbox: Some(SandboxMode::WorkspaceWrite),
            approval: Some(ApprovalPolicy::OnRequest),
            config: vec![
                ConfigOverride::new("foo.bar", "\"baz\""),
                ConfigOverride::new("", "raw=true"),
            ],
            enable: vec!["feature-a".to_string()],
            disable: vec!["feature-b".to_string()],
            strict_config: true,
            ignore_user_config: true,
            ignore_rules: true,
            output_schema: Some(br#"{"type":"object"}"#.to_vec()),
            output_last_message_path: Some("last.txt".to_string()),
            dangerously_bypass_hooks: true,
            ..Default::default()
        };
        let prepared = prepare(&req, s, a).unwrap();
        let args = &prepared.args;

        assert_eq!(prepared.stdin, b"prompt\n\n<stdin>\nextra\n</stdin>\n");

        let schema_index = args.iter().position(|a| a == "--output-schema").unwrap();
        let schema_bytes = std::fs::read(&args[schema_index + 1]).unwrap();
        assert_eq!(schema_bytes, br#"{"type":"object"}"#);

        for want in [
            "exec",
            "--json",
            "--color",
            "never",
            "--strict-config",
            "-m",
            "gpt-test",
            "-p",
            "work",
            "--enable",
            "feature-a",
            "--disable",
            "feature-b",
            "-i",
            "image.png",
            "--skip-git-repo-check",
            "--ephemeral",
            "--ignore-user-config",
            "--ignore-rules",
            "--sandbox",
            "workspace-write",
            "-c",
            r#"approval_policy="on-request""#,
            "--dangerously-bypass-hook-trust",
            "-C",
            "/work",
            "--add-dir",
            "/other",
            "-o",
            "last.txt",
            r#"foo.bar="baz""#,
            "raw=true",
            "-",
        ] {
            assert!(args.contains(&want.to_string()), "missing arg: {want}");
        }
    }

    #[test]
    fn builds_resume_args() {
        let (s, a) = defaults();
        let req = Request {
            prompt: "continue".to_string(),
            resume_id: Some("thread-id".to_string()),
            resume_all: true,
            persistent: true,
            sandbox: Some(SandboxMode::DangerFullAccess),
            approval: Some(ApprovalPolicy::Untrusted),
            ..Default::default()
        };
        let prepared = prepare(&req, s, a).unwrap();
        let args = &prepared.args;
        assert_eq!(args[0], "exec");
        assert_eq!(args[1], "resume");
        assert!(args.contains(&"--all".to_string()));
        assert!(args.contains(&"thread-id".to_string()));
        assert!(args.contains(&r#"sandbox_mode="danger-full-access""#.to_string()));
        assert!(args.contains(&r#"approval_policy="untrusted""#.to_string()));
        assert!(!args.contains(&"--ephemeral".to_string()));
        assert_eq!(args.last().unwrap(), "-");
    }

    #[test]
    fn validation_table() {
        let (_s, _a) = defaults();
        assert!(matches!(
            validate_request(&Request::default()),
            Err(Error::PromptRequired)
        ));
        assert!(matches!(
            validate_request(&Request {
                prompt: "x".to_string(),
                output_schema_path: Some("schema.json".to_string()),
                output_schema: Some(b"{}".to_vec()),
                ..Default::default()
            }),
            Err(Error::InvalidRequest(_))
        ));
        assert!(matches!(
            validate_request(&Request {
                prompt: "x".to_string(),
                resume_id: Some("id".to_string()),
                resume_last: true,
                ..Default::default()
            }),
            Err(Error::InvalidRequest(_))
        ));
        assert!(matches!(
            validate_request(&Request {
                prompt: "x".to_string(),
                resume_all: true,
                ..Default::default()
            }),
            Err(Error::InvalidRequest(_))
        ));
        assert!(matches!(
            validate_request(&Request {
                prompt: "x".to_string(),
                resume_last: true,
                dir: Some(".".to_string()),
                ..Default::default()
            }),
            Err(Error::InvalidRequest(_))
        ));
    }
}
