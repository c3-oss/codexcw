package codexcw

import (
	"context"
	"sync"
)

const defaultMaxConcurrent = 4

// RunEvent is one event multiplexed from RunMany.
type RunEvent struct {
	// RunID is the wrapper-assigned run id.
	RunID string

	// Index is the request index in the RunMany input slice.
	Index int

	// Event is the decoded agent event.
	Event Event
}

// GroupResult is the result for one request passed to RunMany.
type GroupResult struct {
	// Index is the request index in the RunMany input slice.
	Index int

	// RunID is the wrapper-assigned run id.
	RunID string

	// Result is set when the run started.
	Result *Result

	// Err is set when the run failed.
	Err error
}

// Group represents a batch of running agent processes.
type Group struct {
	events <-chan RunEvent
	cancel context.CancelFunc
	done   chan []GroupResult
}

// ManyOption configures RunMany.
type ManyOption func(*manyConfig)

type manyConfig struct {
	maxConcurrent int
	eventBuffer   int
	runOptions    []RunOption
}

// WithMaxConcurrent limits how many agent processes run at once.
func WithMaxConcurrent(n int) ManyOption {
	return func(c *manyConfig) {
		if n > 0 {
			c.maxConcurrent = n
		}
	}
}

// WithRunEventBuffer changes the multiplexed event channel buffer.
func WithRunEventBuffer(n int) ManyOption {
	return func(c *manyConfig) {
		if n > 0 {
			c.eventBuffer = n
		}
	}
}

// WithRunOptions applies RunOptions to each run in the group.
func WithRunOptions(opts ...RunOption) ManyOption {
	return func(c *manyConfig) {
		c.runOptions = append(c.runOptions, opts...)
	}
}

// RunMany starts N agent processes with bounded concurrency.
func (r *Runner) RunMany(ctx context.Context, reqs []Request, opts ...ManyOption) (*Group, error) {
	if ctx == nil {
		ctx = context.Background()
	}
	cfg := manyConfig{
		maxConcurrent: defaultMaxConcurrent,
		eventBuffer:   r.eventBuffer,
	}
	for _, opt := range opts {
		opt(&cfg)
	}
	if cfg.maxConcurrent <= 0 {
		cfg.maxConcurrent = defaultMaxConcurrent
	}

	groupCtx, cancel := context.WithCancel(ctx)
	events := make(chan RunEvent, cfg.eventBuffer)
	group := &Group{
		events: events,
		cancel: cancel,
		done:   make(chan []GroupResult, 1),
	}

	go r.runMany(groupCtx, reqs, cfg, events, group.done)

	return group, nil
}

// Events streams multiplexed events until every run has finished.
func (g *Group) Events() <-chan RunEvent {
	return g.events
}

// Cancel stops all active and pending runs.
func (g *Group) Cancel() error {
	g.cancel()
	return nil
}

// Wait returns every run result. If any run failed, it also returns GroupError.
func (g *Group) Wait() ([]GroupResult, error) {
	results := <-g.done
	for _, result := range results {
		if result.Err != nil {
			return results, &GroupError{Results: results}
		}
	}
	return results, nil
}

func (r *Runner) runMany(
	ctx context.Context,
	reqs []Request,
	cfg manyConfig,
	events chan<- RunEvent,
	done chan<- []GroupResult,
) {
	defer close(events)

	results := make([]GroupResult, len(reqs))
	sem := make(chan struct{}, cfg.maxConcurrent)
	var wg sync.WaitGroup

	for i, req := range reqs {
		if ctx.Err() != nil {
			results[i] = GroupResult{Index: i, Err: ctx.Err()}
			continue
		}

		sem <- struct{}{}
		wg.Add(1)
		go func(index int, request Request) {
			defer wg.Done()
			defer func() { <-sem }()

			session, err := r.Start(ctx, request, cfg.runOptions...)
			if err != nil {
				results[index] = GroupResult{Index: index, Err: err}
				return
			}

			for event := range session.Events() {
				select {
				case events <- RunEvent{RunID: session.ID, Index: index, Event: event}:
				case <-ctx.Done():
					_ = session.Cancel()
				}
			}

			result, err := session.Wait()
			runID := session.ID
			if result != nil {
				runID = result.RunID
			}
			results[index] = GroupResult{
				Index:  index,
				RunID:  runID,
				Result: result,
				Err:    err,
			}
		}(i, req)
	}

	wg.Wait()
	done <- results
}
