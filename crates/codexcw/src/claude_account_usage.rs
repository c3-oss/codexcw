//! Account usage windows from Claude Code's `/usage` command.

use std::process::Stdio;
use std::time::Duration;

use serde::Deserialize;
use tokio::io::AsyncWriteExt;
use tokio::process::Command;
use tokio::time::timeout;

use crate::Error;

const REQUEST_TIMEOUT: Duration = Duration::from_secs(10);

/// Configures one Claude account usage lookup.
#[derive(Clone, Debug, Default)]
pub struct ClaudeAccountUsageRequest {
    /// Claude executable path. Defaults to `claude`.
    pub executable: Option<String>,
    /// Environment variables for the Claude child process.
    pub env: Vec<(String, String)>,
    /// Command timeout. Defaults to 10 seconds.
    pub timeout: Option<Duration>,
}

/// Account usage reported by Claude Code's `/usage` command.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ClaudeAccountUsage {
    /// Human-readable `/usage` report.
    pub report: String,
    /// Parsed percentage-based usage windows.
    pub windows: Vec<ClaudeAccountUsageWindow>,
    /// Raw JSON result line emitted by Claude Code.
    pub raw: String,
}

/// One percentage-based Claude account usage window.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct ClaudeAccountUsageWindow {
    /// Window label, such as `Current session`.
    pub label: String,
    /// Percentage of the window already used.
    pub used_percent: f64,
    /// Human-readable reset description emitted by Claude Code.
    pub resets_at: String,
}

#[derive(Deserialize)]
struct ClaudeUsageResponse {
    #[serde(default)]
    is_error: bool,
    #[serde(default)]
    result: String,
    #[serde(default)]
    errors: Vec<String>,
}

/// Reads Claude account usage through Claude Code's `/usage` command.
pub async fn get_claude_account_usage(
    req: ClaudeAccountUsageRequest,
) -> Result<ClaudeAccountUsage, Error> {
    let executable = req
        .executable
        .as_deref()
        .filter(|value| !value.is_empty())
        .unwrap_or("claude");
    let duration = req.timeout.unwrap_or(REQUEST_TIMEOUT);
    let mut child = Command::new(executable)
        .args(["-p", "--output-format", "json", "--no-session-persistence"])
        .envs(req.env)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .kill_on_drop(true)
        .spawn()
        .map_err(|err| Error::Process(format!("start claude account usage: {err}")))?;

    let mut stdin = child
        .stdin
        .take()
        .ok_or_else(|| Error::Process("open claude account usage stdin".to_string()))?;
    stdin
        .write_all(b"/usage")
        .await
        .map_err(|err| Error::Process(format!("write claude account usage: {err}")))?;
    stdin
        .shutdown()
        .await
        .map_err(|err| Error::Process(format!("close claude account usage stdin: {err}")))?;
    drop(stdin);

    let output = timeout(duration, child.wait_with_output())
        .await
        .map_err(|_| Error::Process("claude account usage command timed out".to_string()))?
        .map_err(|err| Error::Process(format!("wait for claude account usage: {err}")))?;
    let stderr = String::from_utf8_lossy(&output.stderr).trim().to_string();
    if !output.status.success() {
        return Err(Error::Exit {
            code: output.status.code().unwrap_or(-1),
            stderr,
            last_event: None,
        });
    }

    let raw = String::from_utf8_lossy(&output.stdout).trim().to_string();
    let response: ClaudeUsageResponse = serde_json::from_str(&raw).map_err(|err| {
        let suffix = if stderr.is_empty() {
            String::new()
        } else {
            format!(": {stderr}")
        };
        Error::Process(format!("decode claude account usage: {err}{suffix}"))
    })?;
    if response.is_error {
        let detail = if !response.result.is_empty() {
            response.result
        } else if !response.errors.is_empty() {
            response.errors.join("; ")
        } else {
            "unknown error".to_string()
        };
        return Err(Error::Process(format!(
            "claude account usage failed: {detail}"
        )));
    }

    Ok(ClaudeAccountUsage {
        windows: parse_usage_windows(&response.result),
        report: response.result,
        raw,
    })
}

fn parse_usage_windows(report: &str) -> Vec<ClaudeAccountUsageWindow> {
    report.lines().filter_map(parse_usage_window).collect()
}

fn parse_usage_window(line: &str) -> Option<ClaudeAccountUsageWindow> {
    let (label, details) = line.split_once(':')?;
    let percent_end = details.find("% used")?;
    let used_percent = details[..percent_end]
        .split_whitespace()
        .last()?
        .parse()
        .ok()?;
    let resets_at = details
        .split_once("resets ")
        .map(|(_, value)| value.trim().to_string())
        .unwrap_or_default();
    Some(ClaudeAccountUsageWindow {
        label: label.trim().to_string(),
        used_percent,
        resets_at,
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_percentage_windows_and_ignores_report_details() {
        let report = "Usage report\n\
            Current session: 13% used · resets Jul 16 at 3:50pm (America/Sao_Paulo)\n\
            Current week (all models): 5.5% used · resets Jul 18 at 9am\n\
            Last 24h · 3405 requests";

        assert_eq!(
            parse_usage_windows(report),
            vec![
                ClaudeAccountUsageWindow {
                    label: "Current session".to_string(),
                    used_percent: 13.0,
                    resets_at: "Jul 16 at 3:50pm (America/Sao_Paulo)".to_string(),
                },
                ClaudeAccountUsageWindow {
                    label: "Current week (all models)".to_string(),
                    used_percent: 5.5,
                    resets_at: "Jul 18 at 9am".to_string(),
                },
            ]
        );
    }
}
