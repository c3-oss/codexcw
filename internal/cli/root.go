// Package cli wires the Cobra command tree for codexcw.
package cli

import (
	"github.com/spf13/cobra"

	"github.com/c3-oss/codexcw/internal/logging"
)

func newRootCmd() *cobra.Command {
	var logLevel string

	cmd := &cobra.Command{
		Use:           "codexcw",
		Short:         "codexcw runs Codex CLI non-interactively from Go",
		SilenceUsage:  true,
		SilenceErrors: true,
		PersistentPreRunE: func(_ *cobra.Command, _ []string) error {
			return logging.Configure(logLevel)
		},
	}

	cmd.PersistentFlags().StringVar(&logLevel, "log-level", "info", "log level (debug, info, warn, error)")

	cmd.AddCommand(newVersionCmd())
	cmd.AddCommand(newRunCmd())

	return cmd
}

// Execute runs the root command and returns any error encountered.
func Execute() error {
	return newRootCmd().Execute()
}
