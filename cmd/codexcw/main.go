// Command myapp is the placeholder entrypoint of this template.
//
// Replace this package with your own binary's name via scripts/setup.sh
// after creating a new repo from the template.
package main

import (
	"fmt"
	"os"

	"github.com/c3-oss/codexcw/internal/cli"
)

func main() {
	if err := cli.Execute(); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}
}
