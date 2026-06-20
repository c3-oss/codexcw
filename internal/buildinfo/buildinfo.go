// Package buildinfo exposes version metadata stamped at link time.
//
// Defaults match a local `go build` invocation. Release builds (GoReleaser,
// Docker, CI) override these via -ldflags -X.
package buildinfo

var (
	Version   = "dev"
	Commit    = "none"
	BuildDate = "unknown"
)
