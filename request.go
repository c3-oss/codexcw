package codexcw

import "io"

// SandboxMode controls the sandbox policy passed to codex exec.
type SandboxMode string

const (
	SandboxReadOnly         SandboxMode = "read-only"
	SandboxWorkspaceWrite   SandboxMode = "workspace-write"
	SandboxDangerFullAccess SandboxMode = "danger-full-access"
)

// ApprovalPolicy controls Codex approval behavior through config overrides.
type ApprovalPolicy string

const (
	ApprovalUntrusted ApprovalPolicy = "untrusted"
	ApprovalOnRequest ApprovalPolicy = "on-request"
	ApprovalNever     ApprovalPolicy = "never"
)

// ConfigOverride is passed as one -c key=value override.
type ConfigOverride struct {
	Key   string
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
	Prompt string
	Stdin  io.Reader

	Dir     string
	AddDirs []string
	Images  []string

	Model   string
	Profile string

	Sandbox  SandboxMode
	Approval ApprovalPolicy

	Config  []ConfigOverride
	Enable  []string
	Disable []string

	StrictConfig     bool
	Persistent       bool
	IgnoreUserConfig bool
	IgnoreRules      bool
	RequireGitRepo   bool

	OutputSchemaPath         string
	OutputSchema             []byte
	OutputLastMessagePath    string
	DangerouslyBypassSandbox bool
	DangerouslyBypassHooks   bool

	Env []string

	ResumeID   string
	ResumeLast bool
	ResumeAll  bool
}
