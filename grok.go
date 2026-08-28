package codexcw

import (
	"fmt"
	"io"
	"os"
)

func (r *Runner) prepareGrok(req Request) (_ []string, _ io.Reader, cleanup func(), err error) {
	if err := validateGrokRequest(req); err != nil {
		return nil, nil, nil, err
	}

	prompt, err := io.ReadAll(promptReader(req))
	if err != nil {
		return nil, nil, nil, err
	}
	promptFile, err := os.CreateTemp("", "codexcw-grok-prompt-*.md")
	if err != nil {
		return nil, nil, nil, err
	}
	cleanup = func() {
		_ = os.Remove(promptFile.Name())
	}
	if _, err := promptFile.Write(prompt); err != nil {
		_ = promptFile.Close()
		cleanup()
		return nil, nil, nil, err
	}
	if err := promptFile.Close(); err != nil {
		cleanup()
		return nil, nil, nil, err
	}

	schema := req.OutputSchema
	if req.OutputSchemaPath != "" {
		schema, err = os.ReadFile(req.OutputSchemaPath)
		if err != nil {
			cleanup()
			return nil, nil, nil, err
		}
	}

	args := []string{
		"--no-auto-update",
		"--output-format", "streaming-messages-json",
		"--verbatim",
		"--prompt-file", promptFile.Name(),
	}
	if req.Dir != "" {
		args = append(args, "--cwd", req.Dir)
	}
	if req.Model != "" {
		args = append(args, "--model", req.Model)
	}
	if req.Profile != "" {
		args = append(args, "--agent", req.Profile)
	}

	resume := req.ResumeID != "" || req.ResumeLast
	if req.DangerouslyBypassSandbox {
		args = append(
			args,
			"--sandbox", "off",
			"--permission-mode", "bypassPermissions",
		)
	} else {
		if !resume || req.Sandbox != "" {
			sandbox := req.Sandbox
			if sandbox == "" {
				sandbox = r.defaultSandbox
			}
			args = append(args, "--sandbox", grokSandbox(sandbox))
		}
		permission := string(req.PermissionMode)
		if permission == "" {
			approval := req.Approval
			if approval == "" {
				approval = r.defaultApproval
			}
			permission = grokApproval(approval)
		}
		args = append(args, "--permission-mode", permission)
	}

	for _, tool := range req.AllowedTools {
		args = append(args, "--allow", tool)
	}
	for _, tool := range req.DisallowedTools {
		args = append(args, "--deny", tool)
	}
	if len(schema) > 0 {
		args = append(args, "--json-schema", string(schema))
	}
	if req.ResumeID != "" {
		args = append(args, "--resume", req.ResumeID)
	}
	if req.ResumeLast {
		args = append(args, "--continue")
	}

	return args, nil, cleanup, nil
}

func validateGrokRequest(req Request) error {
	if req.Prompt == "" && req.Stdin == nil {
		return ErrPromptRequired
	}
	if len(req.OutputSchema) > 0 && req.OutputSchemaPath != "" {
		return fmt.Errorf("%w: output schema path and inline schema are mutually exclusive", ErrInvalidRequest)
	}
	if req.ResumeID != "" && req.ResumeLast {
		return fmt.Errorf("%w: resume id and resume last are mutually exclusive", ErrInvalidRequest)
	}
	if req.Approval != "" && req.PermissionMode != "" {
		return fmt.Errorf(
			"%w: approval and permission mode are mutually exclusive for the grok agent",
			ErrInvalidRequest,
		)
	}

	unsupported := []struct {
		set  bool
		name string
	}{
		{len(req.AddDirs) > 0, "add dirs"},
		{len(req.Images) > 0, "images"},
		{len(req.Config) > 0, "config overrides"},
		{len(req.Enable) > 0, "enable flags"},
		{len(req.Disable) > 0, "disable flags"},
		{req.StrictConfig, "strict config"},
		{req.IgnoreUserConfig, "ignore user config"},
		{req.IgnoreRules, "ignore rules"},
		{req.RequireGitRepo, "require git repo"},
		{req.OutputLastMessagePath != "", "output last message path"},
		{req.DangerouslyBypassHooks, "dangerously bypass hooks"},
		{req.ResumeAll, "resume all"},
	}
	for _, field := range unsupported {
		if field.set {
			return fmt.Errorf(
				"%w: %s is not supported by the grok agent",
				ErrInvalidRequest,
				field.name,
			)
		}
	}
	return nil
}

func grokSandbox(sandbox SandboxMode) string {
	switch sandbox {
	case SandboxWorkspaceWrite:
		return "workspace"
	case SandboxDangerFullAccess:
		return "off"
	default:
		return "read-only"
	}
}

func grokApproval(approval ApprovalPolicy) string {
	if approval == ApprovalNever {
		return "dontAsk"
	}
	return "default"
}
