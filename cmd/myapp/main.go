// Command myapp is the placeholder entrypoint of this template.
//
// Replace this package with your own binary's name via scripts/setup.sh
// after creating a new repo from the template.
package main

import (
	"os"

	"github.com/c3-oss/go-template/internal/cli"
)

func main() {
	if err := cli.Execute(); err != nil {
		os.Exit(1)
	}
}
