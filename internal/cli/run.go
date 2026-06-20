package cli

import (
	"context"
	"fmt"
	"io"
	"strings"

	"github.com/spf13/cobra"

	"github.com/c3-oss/codexcw"
)

type runFlags struct {
	codexBin              string
	dir                   string
	model                 string
	sandbox               string
	approval              string
	outputLastMessagePath string
	config                []string
	persistent            bool
	requireGitRepo        bool
	ignoreUserConfig      bool
	ignoreRules           bool
}

func newRunCmd() *cobra.Command {
	flags := runFlags{
		codexBin: "codex",
		sandbox:  string(codexcw.SandboxReadOnly),
		approval: string(codexcw.ApprovalNever),
	}

	cmd := &cobra.Command{
		Use:   "run [prompt]",
		Short: "Run codex exec --json and print the final agent message",
		Args:  cobra.ArbitraryArgs,
		RunE: func(cmd *cobra.Command, args []string) error {
			return runCodex(cmd.Context(), cmd, flags, args)
		},
	}

	cmd.Flags().StringVar(&flags.codexBin, "codex-bin", flags.codexBin, "codex executable path")
	cmd.Flags().StringVarP(&flags.dir, "cd", "C", "", "working directory passed to codex")
	cmd.Flags().StringVarP(&flags.model, "model", "m", "", "model passed to codex")
	cmd.Flags().StringVar(&flags.sandbox, "sandbox", flags.sandbox, "sandbox mode (read-only, workspace-write, danger-full-access)")
	cmd.Flags().StringVar(&flags.approval, "approval", flags.approval, "approval policy (untrusted, on-request, never)")
	cmd.Flags().StringArrayVarP(&flags.config, "config", "c", nil, "codex config override as key=value")
	cmd.Flags().StringVarP(&flags.outputLastMessagePath, "output-last-message", "o", "", "file where codex writes the last agent message")
	cmd.Flags().BoolVar(&flags.persistent, "persistent", false, "persist Codex session rollout files")
	cmd.Flags().BoolVar(&flags.requireGitRepo, "require-git-repo", false, "let Codex enforce the git repository check")
	cmd.Flags().BoolVar(&flags.ignoreUserConfig, "ignore-user-config", false, "do not load CODEX_HOME/config.toml")
	cmd.Flags().BoolVar(&flags.ignoreRules, "ignore-rules", false, "do not load execpolicy .rules files")

	return cmd
}

func runCodex(ctx context.Context, cmd *cobra.Command, flags runFlags, args []string) error {
	prompt, err := promptFromArgsOrStdin(cmd.InOrStdin(), args)
	if err != nil {
		return err
	}

	req := codexcw.Request{
		Prompt:                prompt,
		Dir:                   flags.dir,
		Model:                 flags.model,
		Sandbox:               codexcw.SandboxMode(flags.sandbox),
		Approval:              codexcw.ApprovalPolicy(flags.approval),
		Config:                parseConfigOverrides(flags.config),
		Persistent:            flags.persistent,
		RequireGitRepo:        flags.requireGitRepo,
		IgnoreUserConfig:      flags.ignoreUserConfig,
		IgnoreRules:           flags.ignoreRules,
		OutputLastMessagePath: flags.outputLastMessagePath,
	}

	errOut := cmd.ErrOrStderr()
	runner := codexcw.New(codexcw.WithExecutable(flags.codexBin))
	result, err := runner.Run(ctx, req, codexcw.WithHandler(codexcw.HandlerFunc(func(_ context.Context, event codexcw.Event) error {
		writeProgress(errOut, event)
		return nil
	})))
	if err != nil {
		return err
	}
	if result.FinalMessage != "" {
		_, err = fmt.Fprintln(cmd.OutOrStdout(), result.FinalMessage)
	}
	return err
}

func promptFromArgsOrStdin(in io.Reader, args []string) (string, error) {
	if len(args) > 0 {
		return strings.Join(args, " "), nil
	}
	data, err := io.ReadAll(in)
	if err != nil {
		return "", err
	}
	prompt := strings.TrimSpace(string(data))
	if prompt == "" {
		return "", codexcw.ErrPromptRequired
	}
	return prompt, nil
}

func parseConfigOverrides(values []string) []codexcw.ConfigOverride {
	overrides := make([]codexcw.ConfigOverride, 0, len(values))
	for _, value := range values {
		key, val, ok := strings.Cut(value, "=")
		if !ok {
			overrides = append(overrides, codexcw.ConfigOverride{Value: value})
			continue
		}
		overrides = append(overrides, codexcw.ConfigOverride{Key: key, Value: val})
	}
	return overrides
}

func writeProgress(out io.Writer, event codexcw.Event) {
	switch {
	case event.ThreadStarted != nil:
		fmt.Fprintf(out, "thread %s started\n", event.ThreadStarted.ThreadID)
	case event.ItemStarted != nil && event.ItemStarted.Item.Type == codexcw.ItemCommandExecution:
		fmt.Fprintf(out, "running: %s\n", event.ItemStarted.Item.Command)
	case event.ItemCompleted != nil && event.ItemCompleted.Item.Type == codexcw.ItemCommandExecution:
		item := event.ItemCompleted.Item
		if item.ExitCode != nil {
			fmt.Fprintf(out, "command %s: exit %d\n", item.Status, *item.ExitCode)
			return
		}
		fmt.Fprintf(out, "command %s\n", item.Status)
	case event.ItemCompleted != nil && event.ItemCompleted.Item.Type == codexcw.ItemFileChange:
		fmt.Fprintf(out, "file change %s\n", event.ItemCompleted.Item.Status)
	case event.TurnCompleted != nil:
		fmt.Fprintf(out, "completed: input=%d output=%d reasoning=%d\n",
			event.TurnCompleted.Usage.InputTokens,
			event.TurnCompleted.Usage.OutputTokens,
			event.TurnCompleted.Usage.ReasoningOutputTokens,
		)
	case event.TurnFailed != nil:
		fmt.Fprintf(out, "failed: %s\n", event.TurnFailed.Error.Message)
	case event.Error != nil:
		fmt.Fprintf(out, "error: %s\n", event.Error.Message)
	}
}
