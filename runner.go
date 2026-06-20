package codexcw

import (
	"bufio"
	"bytes"
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

const (
	defaultExecutable  = "codex"
	defaultEventBuffer = 1024
	defaultStderrLimit = 1 << 20
	defaultScanMax     = 64 << 20
)

var runCounter atomic.Uint64

// Runner starts codex exec processes and decodes their JSONL event streams.
type Runner struct {
	executable      string
	env             []string
	eventBuffer     int
	stderrLimit     int
	scanMaxBytes    int
	defaultSandbox  SandboxMode
	defaultApproval ApprovalPolicy
	now             func() time.Time
}

// Option configures a Runner.
type Option func(*Runner)

// WithExecutable changes the codex executable path. It is the primary test seam.
func WithExecutable(path string) Option {
	return func(r *Runner) {
		if path != "" {
			r.executable = path
		}
	}
}

// WithEnv appends environment variables to every child process.
func WithEnv(env ...string) Option {
	return func(r *Runner) {
		r.env = append(r.env, env...)
	}
}

// WithEventBuffer changes the per-session event channel buffer.
func WithEventBuffer(n int) Option {
	return func(r *Runner) {
		if n > 0 {
			r.eventBuffer = n
		}
	}
}

// WithStderrLimit changes the captured stderr tail size in bytes.
func WithStderrLimit(n int) Option {
	return func(r *Runner) {
		if n > 0 {
			r.stderrLimit = n
		}
	}
}

// WithScanMaxBytes changes the maximum accepted JSONL line length.
func WithScanMaxBytes(n int) Option {
	return func(r *Runner) {
		if n > 0 {
			r.scanMaxBytes = n
		}
	}
}

// WithDefaultSandbox changes the default sandbox mode.
func WithDefaultSandbox(mode SandboxMode) Option {
	return func(r *Runner) {
		if mode != "" {
			r.defaultSandbox = mode
		}
	}
}

// WithDefaultApproval changes the default approval policy.
func WithDefaultApproval(policy ApprovalPolicy) Option {
	return func(r *Runner) {
		if policy != "" {
			r.defaultApproval = policy
		}
	}
}

// New creates a Runner with safe automation defaults.
func New(opts ...Option) *Runner {
	r := &Runner{
		executable:      defaultExecutable,
		eventBuffer:     defaultEventBuffer,
		stderrLimit:     defaultStderrLimit,
		scanMaxBytes:    defaultScanMax,
		defaultSandbox:  SandboxReadOnly,
		defaultApproval: ApprovalNever,
		now:             time.Now,
	}
	for _, opt := range opts {
		opt(r)
	}
	return r
}

// Handler receives decoded events as they stream from Codex.
type Handler interface {
	// HandleCodexEvent processes one decoded event.
	HandleCodexEvent(context.Context, Event) error
}

// HandlerFunc adapts a function to Handler.
type HandlerFunc func(context.Context, Event) error

// HandleCodexEvent implements Handler.
func (f HandlerFunc) HandleCodexEvent(ctx context.Context, event Event) error {
	return f(ctx, event)
}

type runConfig struct {
	handler Handler
}

// RunOption configures one run.
type RunOption func(*runConfig)

// WithHandler registers an event handler. A handler error cancels the process.
func WithHandler(handler Handler) RunOption {
	return func(c *runConfig) {
		c.handler = handler
	}
}

// Result summarizes a completed codex exec invocation.
type Result struct {
	// RunID is the wrapper-assigned run id.
	RunID string

	// ThreadID is the Codex thread id once known.
	ThreadID string

	// FinalMessage is the last completed agent_message text.
	FinalMessage string

	// Usage is the last turn.completed usage payload.
	Usage Usage

	// Events contains every decoded event retained by Wait.
	Events []Event

	// Stderr is the captured stderr tail.
	Stderr string

	// StartedAt is the local time when collection started.
	StartedAt time.Time

	// FinishedAt is the local time when the process finished.
	FinishedAt time.Time
}

type sessionOutcome struct {
	result *Result
	err    error
}

// Session represents one running codex exec process.
type Session struct {
	// ID is the wrapper-assigned run id.
	ID string

	events chan Event
	cancel context.CancelFunc
	done   chan sessionOutcome

	mu       sync.Mutex
	threadID string
	waited   bool
	outcome  sessionOutcome
}

// Events streams decoded events until the process exits.
func (s *Session) Events() <-chan Event {
	return s.events
}

// ThreadID returns the Codex thread id once thread.started has arrived.
func (s *Session) ThreadID() string {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.threadID
}

// Cancel stops the child process through the run context.
func (s *Session) Cancel() error {
	s.cancel()
	return nil
}

// Wait waits for the process to exit and returns the final result.
func (s *Session) Wait() (*Result, error) {
	s.mu.Lock()
	if s.waited {
		outcome := s.outcome
		s.mu.Unlock()
		return outcome.result, outcome.err
	}
	s.mu.Unlock()

	outcome := <-s.done

	s.mu.Lock()
	s.waited = true
	s.outcome = outcome
	s.mu.Unlock()

	return outcome.result, outcome.err
}

// Start launches one codex exec process and returns immediately.
func (r *Runner) Start(ctx context.Context, req Request, opts ...RunOption) (*Session, error) {
	if ctx == nil {
		ctx = context.Background()
	}

	cfg := runConfig{}
	for _, opt := range opts {
		opt(&cfg)
	}

	args, stdin, cleanup, err := r.prepare(req)
	if err != nil {
		return nil, err
	}
	if cleanup != nil {
		defer func() {
			if err != nil {
				cleanup()
			}
		}()
	}

	runCtx, cancel := context.WithCancel(ctx)
	// #nosec G204 -- launching the configured Codex executable is the wrapper boundary.
	cmd := exec.CommandContext(runCtx, r.executable, args...)
	cmd.Stdin = stdin
	cmd.Env = append(os.Environ(), append(r.env, req.Env...)...)

	stdout, err := cmd.StdoutPipe()
	if err != nil {
		cancel()
		return nil, err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		cancel()
		return nil, err
	}

	runID := newRunID()
	session := &Session{
		ID:     runID,
		events: make(chan Event, r.eventBuffer),
		cancel: cancel,
		done:   make(chan sessionOutcome, 1),
	}

	if err = cmd.Start(); err != nil {
		cancel()
		if cleanup != nil {
			cleanup()
		}
		return nil, err
	}

	stderrTail := newTailBuffer(r.stderrLimit)
	stderrDone := make(chan struct{})
	go func() {
		_, _ = io.Copy(stderrTail, stderr)
		close(stderrDone)
	}()

	go r.collect(runCtx, session, cmd, stdout, stderrTail, stderrDone, cleanup, cfg.handler)

	return session, nil
}

// Run starts one process, drains its event stream, and waits for completion.
func (r *Runner) Run(ctx context.Context, req Request, opts ...RunOption) (*Result, error) {
	session, err := r.Start(ctx, req, opts...)
	if err != nil {
		return nil, err
	}
	for range session.Events() {
	}
	return session.Wait()
}

func (r *Runner) collect(
	ctx context.Context,
	session *Session,
	cmd *exec.Cmd,
	stdout io.Reader,
	stderrTail *tailBuffer,
	stderrDone <-chan struct{},
	cleanup func(),
	handler Handler,
) {
	startedAt := r.now()
	var (
		events       []Event
		lastEvent    *Event
		finalMessage string
		usage        Usage
		threadID     string
		runErr       error
	)

	scanner := bufio.NewScanner(stdout)
	scanner.Buffer(make([]byte, 0, 64*1024), r.scanMaxBytes)

	line := 0
	for scanner.Scan() {
		line++
		raw := bytes.TrimSpace(scanner.Bytes())
		if len(raw) == 0 {
			continue
		}
		event, err := decodeEvent(raw, session.ID, threadID, r.now())
		if err != nil {
			runErr = &DecodeError{Line: line, Raw: append([]byte(nil), raw...), Err: err}
			session.cancel()
			break
		}
		if event.ThreadStarted != nil {
			threadID = event.ThreadStarted.ThreadID
			event.ThreadID = threadID
			session.setThreadID(threadID)
		}
		if event.ThreadID == "" {
			event.ThreadID = threadID
		}
		if event.ItemCompleted != nil && event.ItemCompleted.Item.Type == ItemAgentMessage {
			finalMessage = event.ItemCompleted.Item.Text
		}
		if event.TurnCompleted != nil {
			usage = event.TurnCompleted.Usage
		}

		eventCopy := event
		lastEvent = &eventCopy
		events = append(events, event)

		select {
		case session.events <- event:
		case <-ctx.Done():
			runErr = ctx.Err()
			session.cancel()
		}
		if runErr != nil {
			break
		}

		if handler != nil {
			if err := handler.HandleCodexEvent(ctx, event); err != nil {
				runErr = &HandlerError{Err: err}
				session.cancel()
				break
			}
		}
	}

	if err := scanner.Err(); err != nil && runErr == nil {
		runErr = &DecodeError{Line: line + 1, Err: err}
		session.cancel()
	}

	waitErr := cmd.Wait()
	<-stderrDone
	if cleanup != nil {
		cleanup()
	}

	finishedAt := r.now()
	result := &Result{
		RunID:        session.ID,
		ThreadID:     threadID,
		FinalMessage: finalMessage,
		Usage:        usage,
		Events:       events,
		Stderr:       stderrTail.String(),
		StartedAt:    startedAt,
		FinishedAt:   finishedAt,
	}

	if runErr == nil {
		runErr = classifyProcessError(ctx, waitErr, result.Stderr, lastEvent)
	}
	if runErr == nil {
		runErr = classifyCodexEvent(lastEvent)
	}

	close(session.events)
	session.done <- sessionOutcome{result: result, err: runErr}
}

func (s *Session) setThreadID(threadID string) {
	s.mu.Lock()
	s.threadID = threadID
	s.mu.Unlock()
}

func classifyProcessError(ctx context.Context, waitErr error, stderr string, lastEvent *Event) error {
	if waitErr == nil {
		return nil
	}
	if ctxErr := ctx.Err(); ctxErr != nil {
		return ctxErr
	}

	var exitErr *exec.ExitError
	if errors.As(waitErr, &exitErr) {
		return &ExitError{
			Code:      exitErr.ExitCode(),
			Stderr:    stderr,
			LastEvent: lastEvent,
			Err:       waitErr,
		}
	}
	return waitErr
}

func classifyCodexEvent(lastEvent *Event) error {
	if lastEvent == nil {
		return nil
	}
	if lastEvent.Error != nil || lastEvent.TurnFailed != nil {
		return &CodexError{Event: *lastEvent}
	}
	return nil
}

func newRunID() string {
	return fmt.Sprintf("run-%d-%d", time.Now().UnixNano(), runCounter.Add(1))
}

func (r *Runner) prepare(req Request) (_ []string, _ io.Reader, cleanup func(), err error) {
	if err := validateRequest(req); err != nil {
		return nil, nil, nil, err
	}

	schemaPath := req.OutputSchemaPath
	if len(req.OutputSchema) > 0 {
		file, err := os.CreateTemp("", "codexcw-schema-*.json")
		if err != nil {
			return nil, nil, nil, err
		}
		schemaPath = file.Name()
		cleanup = func() {
			_ = os.Remove(file.Name())
		}
		if _, err := file.Write(req.OutputSchema); err != nil {
			_ = file.Close()
			cleanup()
			return nil, nil, nil, err
		}
		if err := file.Close(); err != nil {
			cleanup()
			return nil, nil, nil, err
		}
	}

	args := []string{"exec"}
	if req.ResumeID != "" || req.ResumeLast {
		args = append(args, "resume")
	}

	args = r.appendCommonArgs(args, req, schemaPath)

	if req.ResumeLast {
		args = append(args, "--last")
	}
	if req.ResumeAll {
		args = append(args, "--all")
	}

	if req.ResumeID != "" {
		args = append(args, req.ResumeID)
	}
	args = append(args, "-")

	return args, promptReader(req), cleanup, nil
}

func validateRequest(req Request) error {
	if req.Prompt == "" && req.Stdin == nil {
		return ErrPromptRequired
	}
	if len(req.OutputSchema) > 0 && req.OutputSchemaPath != "" {
		return fmt.Errorf("%w: output schema path and inline schema are mutually exclusive", ErrInvalidRequest)
	}
	resume := req.ResumeID != "" || req.ResumeLast
	if req.ResumeID != "" && req.ResumeLast {
		return fmt.Errorf("%w: resume id and resume last are mutually exclusive", ErrInvalidRequest)
	}
	if req.ResumeAll && !resume {
		return fmt.Errorf("%w: resume all requires resume id or resume last", ErrInvalidRequest)
	}
	if resume {
		if req.Dir != "" || len(req.AddDirs) > 0 || req.Profile != "" {
			return fmt.Errorf("%w: dir, add dirs, and profile are not supported by codex exec resume", ErrInvalidRequest)
		}
	}
	return nil
}

func (r *Runner) appendCommonArgs(args []string, req Request, schemaPath string) []string {
	resume := req.ResumeID != "" || req.ResumeLast

	args = append(args, "--json")
	if !resume {
		args = append(args, "--color", "never")
	}
	if req.StrictConfig {
		args = append(args, "--strict-config")
	}
	if req.Model != "" {
		args = append(args, "-m", req.Model)
	}
	if req.Profile != "" && !resume {
		args = append(args, "-p", req.Profile)
	}
	for _, feature := range req.Enable {
		args = append(args, "--enable", feature)
	}
	for _, feature := range req.Disable {
		args = append(args, "--disable", feature)
	}
	for _, image := range req.Images {
		args = append(args, "-i", image)
	}
	if !req.RequireGitRepo {
		args = append(args, "--skip-git-repo-check")
	}
	if !req.Persistent {
		args = append(args, "--ephemeral")
	}
	if req.IgnoreUserConfig {
		args = append(args, "--ignore-user-config")
	}
	if req.IgnoreRules {
		args = append(args, "--ignore-rules")
	}
	if req.DangerouslyBypassSandbox {
		args = append(args, "--dangerously-bypass-approvals-and-sandbox")
	} else {
		sandbox := req.Sandbox
		if sandbox == "" {
			sandbox = r.defaultSandbox
		}
		if resume {
			args = append(args, "-c", fmt.Sprintf("sandbox_mode=%q", string(sandbox)))
		} else {
			args = append(args, "--sandbox", string(sandbox))
		}
		approval := req.Approval
		if approval == "" {
			approval = r.defaultApproval
		}
		args = append(args, "-c", fmt.Sprintf("approval_policy=%q", string(approval)))
	}
	if req.DangerouslyBypassHooks {
		args = append(args, "--dangerously-bypass-hook-trust")
	}
	if !resume {
		if req.Dir != "" {
			args = append(args, "-C", req.Dir)
		}
		for _, dir := range req.AddDirs {
			args = append(args, "--add-dir", dir)
		}
	}
	if schemaPath != "" {
		args = append(args, "--output-schema", schemaPath)
	}
	if req.OutputLastMessagePath != "" {
		args = append(args, "-o", req.OutputLastMessagePath)
	}
	for _, override := range req.Config {
		args = append(args, "-c", override.String())
	}
	return args
}

func promptReader(req Request) io.Reader {
	switch {
	case req.Prompt != "" && req.Stdin != nil:
		return io.MultiReader(
			strings.NewReader(req.Prompt),
			strings.NewReader("\n\n<stdin>\n"),
			req.Stdin,
			strings.NewReader("\n</stdin>\n"),
		)
	case req.Prompt != "":
		return strings.NewReader(req.Prompt)
	default:
		return req.Stdin
	}
}

type tailBuffer struct {
	mu    sync.Mutex
	limit int
	data  []byte
}

func newTailBuffer(limit int) *tailBuffer {
	return &tailBuffer{limit: limit}
}

func (b *tailBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()

	if b.limit <= 0 {
		return len(p), nil
	}
	b.data = append(b.data, p...)
	if len(b.data) > b.limit {
		b.data = append([]byte(nil), b.data[len(b.data)-b.limit:]...)
	}
	return len(p), nil
}

func (b *tailBuffer) String() string {
	b.mu.Lock()
	defer b.mu.Unlock()
	return string(b.data)
}
