// Command codexcw is the example CLI for the codexcw library.
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
