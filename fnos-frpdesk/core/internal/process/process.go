package process

import (
	"bufio"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/YM040923/FrpDesk-fnos/internal/model"
)

const (
	Stopped  = "stopped"
	Starting = "starting"
	Running  = "running"
	Stopping = "stopping"
	Failed   = "failed"
	Exited   = "exited"
)

type managed struct {
	model.Instance
	cmd           *exec.Cmd
	logs          []model.LogLine
	source        string
	stopping      bool
	retries       int
	recoveryTimer *time.Timer
}
type Manager struct {
	mu         sync.Mutex
	frpc       string
	configDir  string
	logDir     string
	items      map[string]*managed
	maxRetries int
	statePath  string
	desired    map[string]bool
}

func New(frpc, configDir, logDir, statePath string) *Manager {
	return &Manager{frpc: frpc, configDir: configDir, logDir: logDir, items: map[string]*managed{}, maxRetries: 5, statePath: statePath, desired: loadDesired(statePath)}
}
func (m *Manager) Ensure(id string)    { m.mu.Lock(); defer m.mu.Unlock(); m.ensure(id) }
func (m *Manager) SetFrpc(path string) { m.mu.Lock(); m.frpc = path; m.mu.Unlock() }
func (m *Manager) WantsStart(id string) bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.desired[id]
}
func (m *Manager) List() []model.Instance {
	m.mu.Lock()
	defer m.mu.Unlock()
	out := make([]model.Instance, 0, len(m.items))
	for _, x := range m.items {
		out = append(out, x.Instance)
	}
	sort.Slice(out, func(i, j int) bool { return out[i].ProfileID < out[j].ProfileID })
	return out
}
func (m *Manager) Get(id string) (model.Instance, bool) {
	m.mu.Lock()
	defer m.mu.Unlock()
	x, ok := m.items[id]
	if !ok {
		return model.Instance{}, false
	}
	return x.Instance, true
}

func (m *Manager) Start(id, source string, recover bool) (model.Instance, error) {
	m.mu.Lock()
	x := m.ensure(id)
	if x.cmd != nil && x.Status == Running {
		out := x.Instance
		m.mu.Unlock()
		return out, nil
	}
	if x.Status == Starting {
		out := x.Instance
		m.mu.Unlock()
		return out, fmt.Errorf("实例正在启动中")
	}
	if err := m.setDesiredLocked(id, recover); err != nil {
		out := m.failLocked(x, err)
		m.mu.Unlock()
		return out, err
	}
	x.source = source
	x.Desired = recover
	x.stopping = false
	x.Status = Starting
	x.Error = ""
	x.Retries = 0
	x.ConfigPath = filepath.Join(m.configDir, id+".toml")
	x.LogPath = filepath.Join(m.logDir, id+".log")
	if err := os.MkdirAll(m.configDir, 0o700); err != nil {
		out := m.failLocked(x, err)
		m.mu.Unlock()
		return out, err
	}
	if err := os.WriteFile(x.ConfigPath, []byte(source), 0o600); err != nil {
		out := m.failLocked(x, err)
		m.mu.Unlock()
		return out, err
	}
	frpc := m.frpc
	if frpc == "" {
		err := fmt.Errorf("未找到内置 Linux frpc")
		out := m.failLocked(x, err)
		m.mu.Unlock()
		return out, err
	}
	config := x.ConfigPath
	m.mu.Unlock()
	instance, err := m.launch(id, frpc, config)
	if err != nil {
		m.mu.Lock()
		y := m.items[id]
		if y != nil && (y.stopping || !y.Desired || y.cmd == nil) {
			if y.cmd != nil && y.cmd.Process != nil {
				_ = y.cmd.Process.Kill()
			}
			y.cmd = nil
			y.Status = Stopped
			y.PID = 0
		}
		m.mu.Unlock()
	}
	return instance, err
}

func (m *Manager) launch(id, frpc, config string) (model.Instance, error) {
	cmd := exec.Command(frpc, "-c", config)
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return m.fail(id, err)
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		return m.fail(id, err)
	}
	if err := cmd.Start(); err != nil {
		return m.fail(id, err)
	}
	m.mu.Lock()
	x := m.ensure(id)
	x.cmd = cmd
	x.PID = cmd.Process.Pid
	x.Status = Running
	x.StartedAt = time.Now().UTC()
	x.Error = ""
	x.Diagnostic = nil
	out := x.Instance
	m.mu.Unlock()
	go m.capture(id, stdout)
	go m.capture(id, stderr)
	go m.wait(id, cmd)
	return out, nil
}
func (m *Manager) Stop(id string) error {
	m.mu.Lock()
	x, ok := m.items[id]
	if !ok {
		m.mu.Unlock()
		return nil
	}
	x.Desired = false
	x.stopping = true
	stateErr := m.setDesiredLocked(id, false)
	if x.recoveryTimer != nil {
		x.recoveryTimer.Stop()
		x.recoveryTimer = nil
	}
	cmd := x.cmd
	x.Status = Stopping
	m.mu.Unlock()
	if cmd != nil && cmd.Process != nil {
		_ = cmd.Process.Kill()
	}
	return stateErr
}
func (m *Manager) StopAll() {
	for _, x := range m.List() {
		_ = m.Stop(x.ProfileID)
	}
}
func (m *Manager) Shutdown() {
	for _, item := range m.List() {
		m.mu.Lock()
		x := m.items[item.ProfileID]
		var cmd *exec.Cmd
		if x != nil {
			x.stopping = true
			x.Status = Stopping
			cmd = x.cmd
		}
		m.mu.Unlock()
		if cmd != nil && cmd.Process != nil {
			_ = cmd.Process.Kill()
		}
	}
}
func (m *Manager) Restart(id, source string) (model.Instance, error) {
	_ = m.Stop(id)
	time.Sleep(100 * time.Millisecond)
	return m.Start(id, source, true)
}
func (m *Manager) Reload(id, source string) (model.Instance, error) {
	m.mu.Lock()
	x, ok := m.items[id]
	frpc := m.frpc
	config := filepath.Join(m.configDir, id+".toml")
	if ok {
		x.source = source
		x.ConfigPath = config
	}
	cmd := x.cmd
	m.mu.Unlock()
	if !ok || cmd == nil {
		_ = os.WriteFile(config, []byte(source), 0o600)
		result, _ := m.Get(id)
		return result, nil
	}
	_ = os.WriteFile(config, []byte(source), 0o600)
	if err := runCommand(frpc, "reload", "--api-timeout", "3s", "-c", config); err != nil {
		return m.Restart(id, source)
	}
	result, _ := m.Get(id)
	return result, nil
}
func (m *Manager) Status(id string) ([]string, error) {
	m.mu.Lock()
	x, ok := m.items[id]
	frpc := m.frpc
	config := ""
	if ok {
		config = x.ConfigPath
	}
	m.mu.Unlock()
	if !ok || config == "" {
		return nil, fmt.Errorf("实例不存在")
	}
	return commandOutput(frpc, "status", "--api-timeout", "3s", "-c", config)
}
func (m *Manager) Remove(id string) { _ = m.Stop(id); m.mu.Lock(); delete(m.items, id); m.mu.Unlock() }
func (m *Manager) Logs(id string) []model.LogLine {
	m.mu.Lock()
	x := m.ensure(id)
	out := append([]model.LogLine(nil), x.logs...)
	path := x.LogPath
	m.mu.Unlock()
	if len(out) > 0 {
		return out
	}
	return readLog(path)
}
func (m *Manager) ClearLogs(id string) error {
	m.mu.Lock()
	x := m.ensure(id)
	path := x.LogPath
	if path == "" {
		path = filepath.Join(m.logDir, id+".log")
		x.LogPath = path
	}
	x.logs = nil
	m.mu.Unlock()
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return err
	}
	return os.WriteFile(path, nil, 0o600)
}

func (m *Manager) wait(id string, cmd *exec.Cmd) {
	err := cmd.Wait()
	code := cmd.ProcessState.ExitCode()
	m.mu.Lock()
	x := m.ensure(id)
	if x.cmd != cmd {
		m.mu.Unlock()
		return
	}
	x.cmd = nil
	x.PID = 0
	x.StoppedAt = time.Now().UTC()
	var cp *int
	cp = &code
	x.ExitCode = cp
	if x.stopping || !x.Desired {
		x.Status = Stopped
		x.stopping = false
		m.mu.Unlock()
		return
	}
	x.Status = Failed
	x.Error = fmt.Sprintf("frpc 已退出，代码 %d", code)
	x.Diagnostic = &model.Diagnostic{Summary: "frpc 意外退出", Detail: recent(x.logs), Severity: "error", ExitCode: cp, FailureAt: time.Now().UTC()}
	x.Retries++
	retry := x.Retries <= m.maxRetries
	delay := recoveryDelay(x.Retries)
	source := x.source
	frpc := m.frpc
	config := x.ConfigPath
	if retry {
		x.Status = Starting
		x.Error = fmt.Sprintf("将在 %s 后自动恢复", delay)
	}
	m.mu.Unlock()
	if !retry {
		return
	}
	timer := time.AfterFunc(delay, func() {
		m.mu.Lock()
		y := m.items[id]
		if y == nil || !y.Desired || y.stopping {
			m.mu.Unlock()
			return
		}
		m.mu.Unlock()
		if _, e := m.launch(id, frpc, config); e != nil {
			m.mu.Lock()
			if z := m.items[id]; z != nil && z.Desired {
				z.source = source
			}
			m.mu.Unlock()
		}
	})
	m.mu.Lock()
	if x2 := m.items[id]; x2 != nil {
		x2.recoveryTimer = timer
	}
	m.mu.Unlock()
	_ = err
}
func (m *Manager) capture(id string, r io.Reader) {
	reader := bufio.NewReader(r)
	for {
		line, err := reader.ReadBytes('\n')
		if len(line) > 0 {
			m.append(id, strings.TrimRight(string(line), "\r\n"))
		}
		if err != nil {
			break
		}
	}
}
func (m *Manager) append(id, line string) {
	m.mu.Lock()
	defer m.mu.Unlock()
	x := m.ensure(id)
	item := model.LogLine{At: time.Now().UTC(), Line: line}
	x.logs = append(x.logs, item)
	if len(x.logs) > 500 {
		x.logs = x.logs[len(x.logs)-500:]
	}
	if x.LogPath != "" {
		_ = os.MkdirAll(filepath.Dir(x.LogPath), 0o700)
		f, e := os.OpenFile(x.LogPath, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
		if e == nil {
			_, _ = fmt.Fprintf(f, "%s %s\n", item.At.Format(time.RFC3339), line)
			_ = f.Close()
		}
	}
}
func (m *Manager) ensure(id string) *managed {
	if x := m.items[id]; x != nil {
		return x
	}
	x := &managed{Instance: model.Instance{ProfileID: id, Status: Stopped}, logs: nil}
	x.LogPath = filepath.Join(m.logDir, id+".log")
	m.items[id] = x
	return x
}

func loadDesired(path string) map[string]bool {
	active := map[string]bool{}
	b, err := os.ReadFile(path)
	if err == nil {
		_ = json.Unmarshal(b, &active)
	}
	return active
}

func (m *Manager) setDesiredLocked(id string, active bool) error {
	next := make(map[string]bool, len(m.desired)+1)
	for key, value := range m.desired {
		next[key] = value
	}
	if active {
		next[id] = true
	} else {
		delete(next, id)
	}
	b, err := json.Marshal(next)
	if err != nil {
		return err
	}
	if err = os.MkdirAll(filepath.Dir(m.statePath), 0o700); err != nil {
		return err
	}
	tmp := m.statePath + ".tmp"
	if err = os.WriteFile(tmp, b, 0o600); err != nil {
		return err
	}
	if err = os.Rename(tmp, m.statePath); err != nil {
		_ = os.Remove(tmp)
		return err
	}
	m.desired = next
	return nil
}
func (m *Manager) fail(id string, err error) (model.Instance, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	x := m.ensure(id)
	out := m.failLocked(x, err)
	return out, err
}
func (m *Manager) failLocked(x *managed, err error) model.Instance {
	x.Status = Failed
	x.Error = err.Error()
	x.Diagnostic = &model.Diagnostic{Summary: "启动失败", Detail: err.Error(), Severity: "error", FailureAt: time.Now().UTC()}
	return x.Instance
}

func recoveryDelay(retry int) time.Duration {
	if v := os.Getenv("FRPDESK_RECOVERY_DELAY_MS"); v != "" {
		var n int64
		if _, e := fmt.Sscan(v, &n); e == nil && n >= 0 {
			return time.Duration(n) * time.Millisecond
		}
	}
	switch retry {
	case 1:
		return 30 * time.Second
	case 2:
		return time.Minute
	case 3:
		return 2 * time.Minute
	default:
		return 4 * time.Minute
	}
}
func runCommand(name string, args ...string) error {
	_, err := commandOutput(name, args...)
	return err
}
func commandOutput(name string, args ...string) ([]string, error) {
	if name == "" {
		return nil, fmt.Errorf("frpc 未配置")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 8*time.Second)
	defer cancel()
	cmd := exec.CommandContext(ctx, name, args...)
	b, e := cmd.CombinedOutput()
	if ctx.Err() != nil {
		return nil, ctx.Err()
	}
	lines := strings.Split(strings.TrimSpace(string(b)), "\n")
	if e != nil {
		return lines, e
	}
	return lines, nil
}
func recent(lines []model.LogLine) string {
	start := len(lines) - 3
	if start < 0 {
		start = 0
	}
	out := make([]string, 0, 3)
	for _, x := range lines[start:] {
		if strings.TrimSpace(x.Line) != "" {
			out = append(out, x.Line)
		}
	}
	return strings.Join(out, " | ")
}
func readLog(path string) []model.LogLine {
	f, e := os.Open(path)
	if e != nil {
		return nil
	}
	defer f.Close()
	var out []model.LogLine
	s := bufio.NewScanner(f)
	for s.Scan() {
		line := s.Text()
		at := time.Now().UTC()
		if ts, rest, ok := strings.Cut(line, " "); ok {
			if t, e := time.Parse(time.RFC3339, ts); e == nil {
				at = t
				line = rest
			}
		}
		out = append(out, model.LogLine{At: at, Line: line})
		if len(out) > 500 {
			out = out[len(out)-500:]
		}
	}
	return out
}
