package cli

import (
	"fmt"

	"github.com/spf13/cobra"

	"github.com/c3-oss/go-template/internal/buildinfo"
)

func newVersionCmd() *cobra.Command {
	return &cobra.Command{
		Use:   "version",
		Short: "Print build metadata (version, commit, date)",
		RunE: func(cmd *cobra.Command, _ []string) error {
			out := cmd.OutOrStdout()
			fmt.Fprintf(out, "version: %s\n", buildinfo.Version)
			fmt.Fprintf(out, "commit:  %s\n", buildinfo.Commit)
			fmt.Fprintf(out, "date:    %s\n", buildinfo.BuildDate)
			return nil
		},
	}
}
