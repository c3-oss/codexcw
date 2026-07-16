package codexcw

import (
	"bufio"
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

const (
	accountUsageInitTimeout    = 8 * time.Second
	accountUsageRequestTimeout = 10 * time.Second
)

// AccountUsageRequest configures one Codex account usage lookup.
type AccountUsageRequest struct {
	// Executable is the codex executable path. Defaults to "codex".
	Executable string

	// Env appends environment variables for the Codex app-server child process.
	Env map[string]string

	// Timeout bounds each account read request. Values <= 0 use the 10s default.
	Timeout time.Duration
}

// AccountUsage is the account limits and credits reported by Codex app-server.
type AccountUsage struct {
	// Account is the authenticated account when Codex reports it.
	Account *AccountUsageAccount

	// TokenUsage is the account token-usage summary when Codex reports it.
	TokenUsage *AccountTokenUsage

	// RateLimits is the primary account rate-limit payload.
	RateLimits AccountRateLimits

	// RateLimitsByLimitID contains additional named rate-limit payloads.
	RateLimitsByLimitID map[string]AccountRateLimits

	// RawRateLimits is the raw JSON-RPC result for account/rateLimits/read.
	RawRateLimits json.RawMessage

	// RawTokenUsage is the raw JSON-RPC result for account/usage/read when available.
	RawTokenUsage json.RawMessage

	// RawAccount is the raw JSON-RPC result for account/read when available.
	RawAccount json.RawMessage
}

// AccountUsageAccount is the authenticated account reported by Codex.
type AccountUsageAccount struct {
	// Type is the account type, such as "chatgpt" or "apikey".
	Type string

	// Email is the ChatGPT account email when available.
	Email string

	// PlanType is the ChatGPT plan type when available.
	PlanType string

	// RequiresOpenAIAuth is true when Codex reports that OpenAI auth is required.
	RequiresOpenAIAuth bool
}

// AccountRateLimits is one Codex rate-limit set.
type AccountRateLimits struct {
	// LimitID is the optional machine identifier for this limit.
	LimitID string

	// LimitName is the optional display name for this limit.
	LimitName string

	// Primary is the short rolling usage window when available.
	Primary *AccountRateLimitWindow

	// Secondary is the longer usage window when available.
	Secondary *AccountRateLimitWindow

	// Credits is the account credit balance when available.
	Credits *AccountCredits

	// IndividualLimit is the account spend or credit control limit when available.
	IndividualLimit *AccountSpendLimit

	// PlanType is the plan type associated with this limit set.
	PlanType string

	// RateLimitReachedType describes which limit was reached when Codex reports it.
	RateLimitReachedType string
}

// AccountRateLimitWindow is one account usage window.
type AccountRateLimitWindow struct {
	// UsedPercent is the percentage of the window already used.
	UsedPercent float64

	// WindowDurationMinutes is the window duration in minutes when available.
	WindowDurationMinutes int

	// ResetsAt is a Unix timestamp in seconds when available.
	ResetsAt int64
}

// AccountCredits is the Codex credit balance snapshot.
type AccountCredits struct {
	// HasCredits reports whether the account has a credit bucket.
	HasCredits bool

	// Unlimited reports whether credits are unlimited.
	Unlimited bool

	// Balance is the remaining credit balance when available.
	Balance string
}

// AccountSpendLimit is an individual spend or credit-control limit.
type AccountSpendLimit struct {
	// Limit is the configured limit when available.
	Limit float64

	// Used is the consumed amount when available.
	Used float64

	// RemainingPercent is the remaining percentage when available.
	RemainingPercent float64

	// ResetsAt is a Unix timestamp in seconds when available.
	ResetsAt int64
}

// AccountTokenUsage is the account token-usage summary reported by Codex.
type AccountTokenUsage struct {
	// Summary contains aggregate account token-usage metrics.
	Summary AccountTokenUsageSummary

	// DailyUsageBuckets contains per-day token usage when available.
	DailyUsageBuckets []AccountTokenUsageDailyBucket
}

// AccountTokenUsageSummary contains aggregate account token-usage metrics.
type AccountTokenUsageSummary struct {
	// LifetimeTokens is the total lifetime token count when available.
	LifetimeTokens string

	// PeakDailyTokens is the peak daily token count when available.
	PeakDailyTokens string

	// LongestRunningTurnSeconds is the longest running turn duration when available.
	LongestRunningTurnSeconds string

	// CurrentStreakDays is the current usage streak length when available.
	CurrentStreakDays string

	// LongestStreakDays is the longest usage streak length when available.
	LongestStreakDays string
}

// AccountTokenUsageDailyBucket is one daily account token-usage bucket.
type AccountTokenUsageDailyBucket struct {
	// StartDate is the bucket start date.
	StartDate string

	// Tokens is the token count for the bucket.
	Tokens string
}

// GetAccountUsage reads Codex account usage and limits through codex app-server.
func GetAccountUsage(ctx context.Context, req AccountUsageRequest) (*AccountUsage, error) {
	executable := strings.TrimSpace(req.Executable)
	if executable == "" {
		executable = string(AgentCodex)
	}
	requestTimeout := req.Timeout
	if requestTimeout <= 0 {
		requestTimeout = accountUsageRequestTimeout
	}

	// #nosec G204 -- launching the configured Codex executable is the wrapper boundary.
	cmd := exec.CommandContext(ctx, executable, "-s", "read-only", "-a", "untrusted", "app-server", "--stdio")
	cmd.Env = accountUsageEnv(req.Env)
	// Bound teardown when killed child processes keep the pipes open.
	cmd.WaitDelay = time.Second

	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, fmt.Errorf("codex app-server stdin: %w", err)
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, fmt.Errorf("codex app-server stdout: %w", err)
	}
	stderr := newTailBuffer(defaultStderrLimit)
	cmd.Stderr = stderr

	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("start codex app-server: %w", err)
	}
	defer func() {
		_ = cmd.Process.Kill()
		_ = cmd.Wait()
	}()

	client := newAccountUsageRPCClient(stdin, stdout)

	if _, err := client.request(ctx, "initialize", map[string]any{
		"clientInfo": map[string]string{
			"name":    "codexcw",
			"version": "0.1.0",
		},
	}, accountUsageInitTimeout); err != nil {
		return nil, fmt.Errorf("initialize codex app-server: %w%s", err, stderrSuffix(stderr.String()))
	}
	if err := client.notify("initialized", map[string]any{}); err != nil {
		return nil, fmt.Errorf("notify codex app-server initialized: %w", err)
	}

	rawRateLimits, err := client.request(ctx, "account/rateLimits/read", nil, requestTimeout)
	if err != nil {
		return nil, fmt.Errorf("read codex account rate limits: %w%s", err, stderrSuffix(stderr.String()))
	}
	var rateLimits accountRateLimitsResponse
	if err := json.Unmarshal(rawRateLimits, &rateLimits); err != nil {
		return nil, fmt.Errorf("decode codex account rate limits: %w", err)
	}

	usage := &AccountUsage{
		RateLimits:          rateLimits.RateLimits,
		RateLimitsByLimitID: rateLimits.RateLimitsByLimitID,
		RawRateLimits:       append(json.RawMessage(nil), rawRateLimits...),
	}
	if usage.RateLimitsByLimitID == nil {
		usage.RateLimitsByLimitID = map[string]AccountRateLimits{}
	}

	// account/usage/read and account/read are optional: a JSON-RPC error
	// response means the data is absent, while transport failures abort.
	rawTokenUsage, err := client.request(ctx, "account/usage/read", nil, requestTimeout)
	switch {
	case err == nil:
		var tokenUsage AccountTokenUsage
		if err := json.Unmarshal(rawTokenUsage, &tokenUsage); err != nil {
			return nil, fmt.Errorf("decode codex account token usage: %w", err)
		}
		usage.TokenUsage = &tokenUsage
		usage.RawTokenUsage = append(json.RawMessage(nil), rawTokenUsage...)
	case !isAccountUsageRPCError(err):
		return nil, fmt.Errorf("read codex account token usage: %w%s", err, stderrSuffix(stderr.String()))
	}

	rawAccount, err := client.request(ctx, "account/read", map[string]any{}, requestTimeout)
	switch {
	case err == nil:
		var account accountResponse
		if err := json.Unmarshal(rawAccount, &account); err != nil {
			return nil, fmt.Errorf("decode codex account: %w", err)
		}
		usage.Account = account.Account
		if usage.Account != nil {
			usage.Account.RequiresOpenAIAuth = account.RequiresOpenAIAuth
		}
		usage.RawAccount = append(json.RawMessage(nil), rawAccount...)
	case !isAccountUsageRPCError(err):
		return nil, fmt.Errorf("read codex account: %w%s", err, stderrSuffix(stderr.String()))
	}

	return usage, nil
}

// accountUsageRPCError is a JSON-RPC error response from codex app-server.
type accountUsageRPCError struct {
	Code    int
	Message string
}

func (e *accountUsageRPCError) Error() string {
	if e.Message != "" {
		return e.Message
	}
	return fmt.Sprintf("codex app-server JSON-RPC error %d", e.Code)
}

func isAccountUsageRPCError(err error) bool {
	var rpcErr *accountUsageRPCError
	return errors.As(err, &rpcErr)
}

type accountUsageRPCClient struct {
	nextID int
	stdin  io.Writer
	lines  <-chan accountUsageLine
}

type accountUsageLine struct {
	line []byte
	err  error
}

func newAccountUsageRPCClient(stdin io.Writer, stdout io.Reader) *accountUsageRPCClient {
	// Buffered so trailing server chatter rarely blocks the reader goroutine
	// once the caller stops receiving.
	lines := make(chan accountUsageLine, 16)
	go func() {
		defer close(lines)
		reader := bufio.NewReader(stdout)
		for {
			line, err := reader.ReadBytes('\n')
			if err != nil {
				if !errors.Is(err, io.EOF) {
					lines <- accountUsageLine{err: err}
				} else if len(bytes.TrimSpace(line)) > 0 {
					lines <- accountUsageLine{line: line}
				}
				return
			}
			lines <- accountUsageLine{line: line}
		}
	}()
	return &accountUsageRPCClient{stdin: stdin, lines: lines}
}

func (c *accountUsageRPCClient) request(ctx context.Context, method string, params any, timeout time.Duration) (json.RawMessage, error) {
	c.nextID++
	id := c.nextID
	payload := map[string]any{
		"id":     id,
		"method": method,
	}
	if params != nil {
		payload["params"] = params
	}
	if err := writeJSONLine(c.stdin, payload); err != nil {
		return nil, err
	}

	requestCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	for {
		select {
		case <-requestCtx.Done():
			return nil, requestCtx.Err()
		case next, ok := <-c.lines:
			if !ok {
				return nil, errors.New("codex app-server closed stdout")
			}
			if next.err != nil {
				return nil, next.err
			}
			var message accountUsageRPCMessage
			if err := json.Unmarshal(bytes.TrimSpace(next.line), &message); err != nil {
				return nil, err
			}
			// Skip notifications and server-initiated requests.
			if message.Method != "" {
				continue
			}
			if message.ID == nil || *message.ID != id {
				continue
			}
			if message.Error != nil {
				return nil, &accountUsageRPCError{Code: message.Error.Code, Message: message.Error.Message}
			}
			if len(message.Result) == 0 {
				return nil, errors.New("codex app-server JSON-RPC response missing result")
			}
			return message.Result, nil
		}
	}
}

func (c *accountUsageRPCClient) notify(method string, params any) error {
	payload := map[string]any{
		"method": method,
		"params": map[string]any{},
	}
	if params != nil {
		payload["params"] = params
	}
	return writeJSONLine(c.stdin, payload)
}

type accountUsageRPCMessage struct {
	ID     *int            `json:"id"`
	Method string          `json:"method"`
	Result json.RawMessage `json:"result"`
	Error  *struct {
		Code    int    `json:"code"`
		Message string `json:"message"`
	} `json:"error"`
}

func writeJSONLine(w io.Writer, payload any) error {
	data, err := json.Marshal(payload)
	if err != nil {
		return err
	}
	data = append(data, '\n')
	_, err = w.Write(data)
	return err
}

func accountUsageEnv(overrides map[string]string) []string {
	env := os.Environ()
	for key, value := range overrides {
		env = append(env, key+"="+value)
	}
	codexHome, overridden := overrides["CODEX_HOME"]
	if !overridden {
		codexHome = os.Getenv("CODEX_HOME")
	}
	if strings.TrimSpace(codexHome) == "" {
		if home, err := os.UserHomeDir(); err == nil && home != "" {
			env = append(env, "CODEX_HOME="+filepath.Join(home, ".codex"))
		}
	}
	return env
}

func stderrSuffix(stderr string) string {
	stderr = strings.TrimSpace(stderr)
	if stderr == "" {
		return ""
	}
	return ": " + stderr
}

type accountRateLimitsResponse struct {
	RateLimits          AccountRateLimits
	RateLimitsByLimitID map[string]AccountRateLimits
}

func (r *accountRateLimitsResponse) UnmarshalJSON(data []byte) error {
	var wire struct {
		RateLimits               AccountRateLimits            `json:"rateLimits"`
		RateLimitsSnake          AccountRateLimits            `json:"rate_limits"`
		RateLimitsByLimitID      map[string]AccountRateLimits `json:"rateLimitsByLimitId"`
		RateLimitsByLimitIDSnake map[string]AccountRateLimits `json:"rate_limits_by_limit_id"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	r.RateLimits = wire.RateLimits
	if r.RateLimits == (AccountRateLimits{}) {
		r.RateLimits = wire.RateLimitsSnake
	}
	r.RateLimitsByLimitID = wire.RateLimitsByLimitID
	if r.RateLimitsByLimitID == nil {
		r.RateLimitsByLimitID = wire.RateLimitsByLimitIDSnake
	}
	return nil
}

// UnmarshalJSON accepts Codex rate-limit fields in camelCase or snake_case.
func (r *AccountRateLimits) UnmarshalJSON(data []byte) error {
	var wire struct {
		LimitID                   string                  `json:"limitId"`
		LimitIDSnake              string                  `json:"limit_id"`
		LimitName                 string                  `json:"limitName"`
		LimitNameSnake            string                  `json:"limit_name"`
		Primary                   *AccountRateLimitWindow `json:"primary"`
		Secondary                 *AccountRateLimitWindow `json:"secondary"`
		Credits                   *AccountCredits         `json:"credits"`
		IndividualLimit           *AccountSpendLimit      `json:"individualLimit"`
		IndividualLimitSnake      *AccountSpendLimit      `json:"individual_limit"`
		PlanType                  string                  `json:"planType"`
		PlanTypeSnake             string                  `json:"plan_type"`
		RateLimitReachedType      string                  `json:"rateLimitReachedType"`
		RateLimitReachedTypeSnake string                  `json:"rate_limit_reached_type"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	r.LimitID = firstNonEmpty(wire.LimitID, wire.LimitIDSnake)
	r.LimitName = firstNonEmpty(wire.LimitName, wire.LimitNameSnake)
	r.Primary = wire.Primary
	r.Secondary = wire.Secondary
	r.Credits = wire.Credits
	r.IndividualLimit = wire.IndividualLimit
	if r.IndividualLimit == nil {
		r.IndividualLimit = wire.IndividualLimitSnake
	}
	r.PlanType = firstNonEmpty(wire.PlanType, wire.PlanTypeSnake)
	r.RateLimitReachedType = firstNonEmpty(wire.RateLimitReachedType, wire.RateLimitReachedTypeSnake)
	return nil
}

// UnmarshalJSON accepts Codex window fields in camelCase or snake_case.
func (w *AccountRateLimitWindow) UnmarshalJSON(data []byte) error {
	var wire struct {
		UsedPercent                flexibleFloat `json:"usedPercent"`
		UsedPercentSnake           flexibleFloat `json:"used_percent"`
		WindowDurationMinutes      flexibleInt64 `json:"windowDurationMins"`
		WindowDurationMinutesSnake flexibleInt64 `json:"window_duration_mins"`
		ResetsAt                   flexibleInt64 `json:"resetsAt"`
		ResetsAtSnake              flexibleInt64 `json:"resets_at"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	w.UsedPercent = firstNonZeroFloat(float64(wire.UsedPercent), float64(wire.UsedPercentSnake))
	w.WindowDurationMinutes = int(firstNonZeroInt64(int64(wire.WindowDurationMinutes), int64(wire.WindowDurationMinutesSnake)))
	w.ResetsAt = firstNonZeroInt64(int64(wire.ResetsAt), int64(wire.ResetsAtSnake))
	return nil
}

// UnmarshalJSON accepts Codex spend fields in camelCase or snake_case.
func (l *AccountSpendLimit) UnmarshalJSON(data []byte) error {
	var wire struct {
		Limit                 flexibleFloat `json:"limit"`
		Used                  flexibleFloat `json:"used"`
		RemainingPercent      flexibleFloat `json:"remainingPercent"`
		RemainingPercentSnake flexibleFloat `json:"remaining_percent"`
		ResetsAt              flexibleInt64 `json:"resetsAt"`
		ResetsAtSnake         flexibleInt64 `json:"resets_at"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	l.Limit = float64(wire.Limit)
	l.Used = float64(wire.Used)
	l.RemainingPercent = firstNonZeroFloat(float64(wire.RemainingPercent), float64(wire.RemainingPercentSnake))
	l.ResetsAt = firstNonZeroInt64(int64(wire.ResetsAt), int64(wire.ResetsAtSnake))
	return nil
}

type flexibleFloat float64

func (f *flexibleFloat) UnmarshalJSON(data []byte) error {
	var number float64
	if err := json.Unmarshal(data, &number); err == nil {
		*f = flexibleFloat(number)
		return nil
	}
	var text string
	if err := json.Unmarshal(data, &text); err == nil {
		var parsed float64
		if _, scanErr := fmt.Sscanf(strings.TrimSpace(text), "%f", &parsed); scanErr == nil {
			*f = flexibleFloat(parsed)
		}
		return nil
	}
	return nil
}

// UnmarshalJSON accepts both string and numeric credit balances.
func (c *AccountCredits) UnmarshalJSON(data []byte) error {
	var wire struct {
		HasCredits      bool            `json:"hasCredits"`
		HasCreditsSnake bool            `json:"has_credits"`
		Unlimited       bool            `json:"unlimited"`
		Balance         json.RawMessage `json:"balance"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	c.HasCredits = wire.HasCredits || wire.HasCreditsSnake
	c.Unlimited = wire.Unlimited
	if len(wire.Balance) > 0 {
		var text string
		if err := json.Unmarshal(wire.Balance, &text); err == nil {
			c.Balance = text
		} else {
			c.Balance = strings.TrimSpace(string(wire.Balance))
		}
	}
	return nil
}

type flexibleInt64 int64

func (i *flexibleInt64) UnmarshalJSON(data []byte) error {
	var number int64
	if err := json.Unmarshal(data, &number); err == nil {
		*i = flexibleInt64(number)
		return nil
	}
	var asFloat float64
	if err := json.Unmarshal(data, &asFloat); err == nil {
		*i = flexibleInt64(asFloat)
		return nil
	}
	var text string
	if err := json.Unmarshal(data, &text); err == nil {
		var parsed int64
		if _, scanErr := fmt.Sscanf(strings.TrimSpace(text), "%d", &parsed); scanErr == nil {
			*i = flexibleInt64(parsed)
		}
		return nil
	}
	return nil
}

type accountResponse struct {
	Account            *AccountUsageAccount
	RequiresOpenAIAuth bool
}

func (r *accountResponse) UnmarshalJSON(data []byte) error {
	var wire struct {
		Account             *AccountUsageAccount `json:"account"`
		RequiresOpenAIAuth  bool                 `json:"requiresOpenaiAuth"`
		RequiresOpenAIAuth2 bool                 `json:"requires_openai_auth"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	r.Account = wire.Account
	r.RequiresOpenAIAuth = wire.RequiresOpenAIAuth || wire.RequiresOpenAIAuth2
	return nil
}

// UnmarshalJSON accepts Codex account fields in camelCase or snake_case.
func (a *AccountUsageAccount) UnmarshalJSON(data []byte) error {
	var wire struct {
		Type          string `json:"type"`
		Email         string `json:"email"`
		PlanType      string `json:"planType"`
		PlanTypeSnake string `json:"plan_type"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	a.Type = wire.Type
	a.Email = wire.Email
	a.PlanType = firstNonEmpty(wire.PlanType, wire.PlanTypeSnake)
	return nil
}

// UnmarshalJSON accepts account token usage fields in camelCase or snake_case.
func (u *AccountTokenUsage) UnmarshalJSON(data []byte) error {
	var wire struct {
		Summary                AccountTokenUsageSummary       `json:"summary"`
		DailyUsageBuckets      []AccountTokenUsageDailyBucket `json:"dailyUsageBuckets"`
		DailyUsageBucketsSnake []AccountTokenUsageDailyBucket `json:"daily_usage_buckets"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	u.Summary = wire.Summary
	u.DailyUsageBuckets = wire.DailyUsageBuckets
	if u.DailyUsageBuckets == nil {
		u.DailyUsageBuckets = wire.DailyUsageBucketsSnake
	}
	return nil
}

// UnmarshalJSON accepts account token summary fields in camelCase or snake_case.
func (s *AccountTokenUsageSummary) UnmarshalJSON(data []byte) error {
	var wire struct {
		LifetimeTokens                 json.RawMessage `json:"lifetimeTokens"`
		LifetimeTokensSnake            json.RawMessage `json:"lifetime_tokens"`
		PeakDailyTokens                json.RawMessage `json:"peakDailyTokens"`
		PeakDailyTokensSnake           json.RawMessage `json:"peak_daily_tokens"`
		LongestRunningTurnSeconds      json.RawMessage `json:"longestRunningTurnSec"`
		LongestRunningTurnSecondsSnake json.RawMessage `json:"longest_running_turn_sec"`
		CurrentStreakDays              json.RawMessage `json:"currentStreakDays"`
		CurrentStreakDaysSnake         json.RawMessage `json:"current_streak_days"`
		LongestStreakDays              json.RawMessage `json:"longestStreakDays"`
		LongestStreakDaysSnake         json.RawMessage `json:"longest_streak_days"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	s.LifetimeTokens = firstNonEmpty(rawJSONScalarString(wire.LifetimeTokens), rawJSONScalarString(wire.LifetimeTokensSnake))
	s.PeakDailyTokens = firstNonEmpty(rawJSONScalarString(wire.PeakDailyTokens), rawJSONScalarString(wire.PeakDailyTokensSnake))
	s.LongestRunningTurnSeconds = firstNonEmpty(rawJSONScalarString(wire.LongestRunningTurnSeconds), rawJSONScalarString(wire.LongestRunningTurnSecondsSnake))
	s.CurrentStreakDays = firstNonEmpty(rawJSONScalarString(wire.CurrentStreakDays), rawJSONScalarString(wire.CurrentStreakDaysSnake))
	s.LongestStreakDays = firstNonEmpty(rawJSONScalarString(wire.LongestStreakDays), rawJSONScalarString(wire.LongestStreakDaysSnake))
	return nil
}

// UnmarshalJSON accepts account token bucket fields in camelCase or snake_case.
func (b *AccountTokenUsageDailyBucket) UnmarshalJSON(data []byte) error {
	var wire struct {
		StartDate      string          `json:"startDate"`
		StartDateSnake string          `json:"start_date"`
		Tokens         json.RawMessage `json:"tokens"`
	}
	if err := json.Unmarshal(data, &wire); err != nil {
		return err
	}
	b.StartDate = firstNonEmpty(wire.StartDate, wire.StartDateSnake)
	b.Tokens = rawJSONScalarString(wire.Tokens)
	return nil
}

func rawJSONScalarString(data json.RawMessage) string {
	data = bytes.TrimSpace(data)
	if len(data) == 0 || bytes.Equal(data, []byte("null")) {
		return ""
	}
	var text string
	if err := json.Unmarshal(data, &text); err == nil {
		return text
	}
	decoder := json.NewDecoder(bytes.NewReader(data))
	decoder.UseNumber()
	var number json.Number
	if err := decoder.Decode(&number); err == nil {
		return number.String()
	}
	return string(data)
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if value != "" {
			return value
		}
	}
	return ""
}

func firstNonZeroFloat(values ...float64) float64 {
	for _, value := range values {
		if value != 0 {
			return value
		}
	}
	return 0
}

func firstNonZeroInt64(values ...int64) int64 {
	for _, value := range values {
		if value != 0 {
			return value
		}
	}
	return 0
}
