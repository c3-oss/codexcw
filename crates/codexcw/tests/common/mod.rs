//! Shared test helper: a fake `codex` executable that records args/stdin and
//! emits a fixed JSONL stream. Mirrors `writeFakeCodex` from the Go suite.

#![cfg(unix)]

use std::fs;
use std::os::unix::fs::PermissionsExt;
use std::path::{Path, PathBuf};

/// A fake codex script plus the capture-file paths it writes to.
pub struct FakeCodex {
    _dir: tempfile::TempDir,
    pub path: PathBuf,
    pub args_file: PathBuf,
    pub stdin_file: PathBuf,
}

impl FakeCodex {
    pub fn executable(&self) -> &str {
        self.path.to_str().unwrap()
    }
}

/// Writes a fake `codex` shell script whose body is appended after a
/// `record_args` helper, returning its paths.
pub fn write_fake_codex(body: &str) -> FakeCodex {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("codex");
    let args_file = dir.path().join("args.txt");
    let stdin_file = dir.path().join("stdin.txt");

    let script = format!(
        "#!/bin/sh\n\
set -eu\n\
record_args() {{\n\
  if [ \"${{CODEXCW_ARGS_FILE:-}}\" != \"\" ]; then\n\
    : > \"$CODEXCW_ARGS_FILE\"\n\
    for arg in \"$@\"; do\n\
      printf '%s\\n' \"$arg\" >> \"$CODEXCW_ARGS_FILE\"\n\
    done\n\
  fi\n\
}}\n\
{body}"
    );

    fs::write(&path, script).unwrap();
    let mut perms = fs::metadata(&path).unwrap().permissions();
    perms.set_mode(0o755);
    fs::set_permissions(&path, perms).unwrap();

    FakeCodex {
        _dir: dir,
        path,
        args_file,
        stdin_file,
    }
}

/// Reads a recorded args file into one string per argument.
pub fn read_args(path: &Path) -> Vec<String> {
    let content = fs::read_to_string(path).unwrap();
    content.trim().split('\n').map(|s| s.to_string()).collect()
}
