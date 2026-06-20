package codexcw

import "io"

// SandboxMode controls the sandbox policy passed to codex exec.
type SandboxMode string

const (
	// SandboxReadOnly lets Codex inspect files without write access.
	SandboxReadOnly SandboxMode = "read-only"

	// SandboxWorkspaceWrite lets Codex write inside the configured workspace.
	SandboxWorkspaceWrite SandboxMode = "workspace-write"

	// SandboxDangerFullAccess removes Codex sandbox filesystem restrictions.
	SandboxDangerFullAccess SandboxMode = "danger-full-access"
)

// ApprovalPolicy controls Codex approval behavior through config overrides.
type ApprovalPolicy string

const (
	// ApprovalUntrusted asks before commands outside Codex's trusted set.
	ApprovalUntrusted ApprovalPolicy = "untrusted"

	// ApprovalOnRequest lets Codex work in the sandbox and request approval.
	ApprovalOnRequest ApprovalPolicy = "on-request"

	// ApprovalNever prevents interactive approval prompts.
	ApprovalNever ApprovalPolicy = "never"
)

// ConfigOverride is passed as one -c key=value override.
type ConfigOverride struct {
	// Key is the config path before the equals sign.
	Key string

	// Value is the config value after the equals sign.
	Value string
}

// String returns the exact key=value argument expected by codex -c.
func (c ConfigOverride) String() string {
	if c.Key == "" {
		return c.Value
	}
	return c.Key + "=" + c.Value
}

// Request describes one codex exec invocation.
type Request struct {
	// Prompt is the user instruction sent to Codex.
	Prompt string

	// Stdin is additional prompt input when Prompt is empty or extra context
	// when Prompt is set.
	Stdin io.Reader

	// Dir is passed to codex exec as --cd.
	Dir string

	// AddDirs grants Codex access to additional directories.
	AddDirs []string

	// Images are attached to the initial Codex prompt.
	Images []string

	// Model overrides the Codex model for this run.
	Model string

	// Profile selects a Codex config profile.
	Profile string

	// Sandbox controls the Codex sandbox policy.
	Sandbox SandboxMode

	// Approval controls the Codex approval policy through -c.
	Approval ApprovalPolicy

	// Config contains raw Codex -c config overrides.
	Config []ConfigOverride

	// Enable contains feature flags passed with --enable.
	Enable []string

	// Disable contains feature flags passed with --disable.
	Disable []string

	// StrictConfig makes Codex reject unrecognized config fields.
	StrictConfig bool

	// Persistent keeps Codex rollout files on disk.
	Persistent bool

	// IgnoreUserConfig skips CODEX_HOME/config.toml.
	IgnoreUserConfig bool

	// IgnoreRules skips user and project execpolicy .rules files.
	IgnoreRules bool

	// RequireGitRepo lets Codex enforce its Git repository check.
	RequireGitRepo bool

	// OutputSchemaPath points to a JSON Schema file for the final response.
	OutputSchemaPath string

	// OutputSchema is written to a temporary JSON Schema file for the run.
	OutputSchema []byte

	// OutputLastMessagePath asks Codex to write the final message to a file.
	OutputLastMessagePath string

	// DangerouslyBypassSandbox passes Codex's full bypass flag.
	DangerouslyBypassSandbox bool

	// DangerouslyBypassHooks runs enabled hooks without persisted trust.
	DangerouslyBypassHooks bool

	// Env appends environment variables for the Codex child process.
	Env []string

	// ResumeID resumes a specific Codex thread id.
	ResumeID string

	// ResumeLast resumes the most recent Codex thread.
	ResumeLast bool

	// ResumeAll disables Codex's cwd filtering while resuming.
	ResumeAll bool
}
