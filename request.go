package codexcw

import "io"

// SandboxMode controls the Codex sandbox or mapped Grok sandbox profile.
type SandboxMode string

const (
	// SandboxReadOnly lets the selected agent inspect files without write access.
	SandboxReadOnly SandboxMode = "read-only"

	// SandboxWorkspaceWrite lets the selected agent write inside the workspace.
	SandboxWorkspaceWrite SandboxMode = "workspace-write"

	// SandboxDangerFullAccess removes agent sandbox filesystem restrictions.
	SandboxDangerFullAccess SandboxMode = "danger-full-access"
)

// ApprovalPolicy controls Codex approvals or mapped Grok permissions.
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

// Request describes one selected-agent invocation.
type Request struct {
	// Prompt is the user instruction sent to the selected agent.
	Prompt string

	// Stdin is additional prompt input when Prompt is empty or extra context
	// when Prompt is set.
	Stdin io.Reader

	// Dir is the selected agent's working directory.
	Dir string

	// AddDirs grants the selected agent access to additional directories.
	AddDirs []string

	// Images are attached to the initial Codex prompt.
	Images []string

	// Model overrides the selected agent's model for this run.
	Model string

	// Profile selects a Codex config profile or Grok agent.
	Profile string

	// Sandbox controls the Codex or Grok sandbox policy.
	Sandbox SandboxMode

	// Approval controls Codex or Grok approval behavior.
	Approval ApprovalPolicy

	// PermissionMode controls the Claude or Grok permission mode.
	PermissionMode PermissionMode

	// AllowedTools lists tool patterns Claude or Grok may use without prompting.
	AllowedTools []string

	// DisallowedTools lists tool patterns denied to Claude or Grok.
	DisallowedTools []string

	// Config contains raw Codex -c config overrides.
	Config []ConfigOverride

	// Enable contains feature flags passed with --enable.
	Enable []string

	// Disable contains feature flags passed with --disable.
	Disable []string

	// StrictConfig makes Codex reject unrecognized config fields.
	StrictConfig bool

	// Persistent keeps session data on disk. Grok sessions are always persisted.
	Persistent bool

	// IgnoreUserConfig skips CODEX_HOME/config.toml.
	IgnoreUserConfig bool

	// IgnoreRules skips user and project execpolicy .rules files.
	IgnoreRules bool

	// RequireGitRepo lets Codex enforce its Git repository check.
	RequireGitRepo bool

	// OutputSchemaPath points to a JSON Schema file for the final response.
	OutputSchemaPath string

	// OutputSchema contains inline JSON Schema text for the final response.
	OutputSchema []byte

	// OutputLastMessagePath asks Codex to write the final message to a file.
	OutputLastMessagePath string

	// DangerouslyBypassSandbox passes the selected agent's full bypass flag.
	DangerouslyBypassSandbox bool

	// DangerouslyBypassHooks runs enabled hooks without persisted trust.
	DangerouslyBypassHooks bool

	// Env appends environment variables for the selected agent process.
	Env []string

	// ResumeID resumes a specific agent session or thread id.
	ResumeID string

	// ResumeLast resumes the selected agent's most recent session.
	ResumeLast bool

	// ResumeAll disables Codex's cwd filtering while resuming.
	ResumeAll bool
}
