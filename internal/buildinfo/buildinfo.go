// Package buildinfo exposes version metadata stamped at link time.
//
// Defaults match a local `go build` invocation. CI builds may override these
// via -ldflags -X.
package buildinfo

var (
	Version   = "dev"
	Commit    = "none"
	BuildDate = "unknown"
)
