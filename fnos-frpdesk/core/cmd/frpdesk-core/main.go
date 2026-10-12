package main

import (
	"encoding/json"

	"io"
	"log"
	"net"
	"net/http"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"

	"strings"

	"github.com/YM040923/FrpDesk-fnos/internal/config"
	"github.com/YM040923/FrpDesk-fnos/internal/model"
	"github.com/YM040923/FrpDesk-fnos/internal/process"
)

type app struct {
	store   *config.Store
	manager *process.Manager
	web     string
	data    string
	frpc    string
}

func main() {
	data := env("FRPDESK_DATA_DIR", filepath.Join(".", ".frpdesk"))
	_ = os.MkdirAll(data, 0o700)
	store, err := config.NewStore(filepath.Join(data, "profiles"))
	if err != nil {
		log.Fatal(err)
	}
	frpc := env("FRPDESK_FRPC_PATH", filepath.Join(filepath.Dir(os.Args[0]), "..", "resources", "frpc", "linux", "frpc"))
	if _, e := os.Stat(frpc); e != nil {
		frpc = env("FRPDESK_FRPC_PATH", "")
	}
	a := &app{store: store, data: data, frpc: frpc, web: env("FRPDESK_WEB_DIR", filepath.Join(filepath.Dir(os.Args[0]), "..", "web"))}
	a.manager = process.New(frpc, filepath.Join(data, "runtime"), filepath.Join(data, "logs"), filepath.Join(data, "active-instances.json"))
	for _, p := range store.List() {
		a.manager.Ensure(p.ID)
		if p.AutoStart || a.manager.WantsStart(p.ID) {
			_, _ = a.manager.Start(p.ID, p.Raw, true)
		}
	}
	ln, err := listen()
	if err != nil {
		log.Fatal(err)
	}
	defer ln.Close()
	log.Printf("FrpDesk fnOS core listening on %s", ln.Addr())
	server := &http.Server{Handler: a.handler()}
	stop := make(chan os.Signal, 1)
	signal.Notify(stop, os.Interrupt, syscall.SIGTERM)
	go func() {
		<-stop
		a.manager.Shutdown()
		_ = server.Close()
	}()
	if err := server.Serve(ln); err != nil && err != http.ErrServerClosed {
		log.Fatal(err)
	}
}
func listen() (net.Listener, error) {
	if s := os.Getenv("FRPDESK_SOCKET"); s != "" {
		_ = os.MkdirAll(filepath.Dir(s), 0o700)
		_ = os.Remove(s)
		return net.Listen("unix", s)
	}
	return net.Listen("tcp", env("FRPDESK_ADDR", "127.0.0.1:47890"))
}
func (a *app) handler() http.Handler {
	mux := http.NewServeMux()
	mux.HandleFunc("/api/health", a.health)
	mux.HandleFunc("/api/configs", a.configs)
	mux.HandleFunc("/api/configs/", a.configByID)
	mux.HandleFunc("/api/instances", a.instances)
	mux.HandleFunc("/api/instances/", a.instanceByID)
	mux.HandleFunc("/api/status", a.status)
	mux.HandleFunc("/api/diagnostics", a.diagnostics)
	mux.HandleFunc("/api/settings/fnos-autostart", a.autostart)
	mux.HandleFunc("/api/", a.notFound)
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		prefix := strings.TrimRight(env("FRPDESK_GATEWAY_PREFIX", "/app/frpdesk"), "/")
		if prefix != "" && (r.URL.Path == prefix || strings.HasPrefix(r.URL.Path, prefix+"/")) {
			r.URL.Path = strings.TrimPrefix(r.URL.Path, prefix)
			if r.URL.Path == "" {
				r.URL.Path = "/"
			}
		}
		if strings.HasPrefix(r.URL.Path, "/api/") {
			mux.ServeHTTP(w, r)
			return
		}
		a.static(w, r)
	})
}
func (a *app) static(w http.ResponseWriter, r *http.Request) {
	path := strings.TrimPrefix(filepath.Clean("/"+r.URL.Path), "/")
	if path == "" || strings.HasPrefix(path, "api/") {
		path = "index.html"
	}
	full := filepath.Join(a.web, path)
	if !within(a.web, full) {
		http.NotFound(w, r)
		return
	}
	if _, e := os.Stat(full); e != nil {
		full = filepath.Join(a.web, "index.html")
	}
	http.ServeFile(w, r, full)
}
func (a *app) health(w http.ResponseWriter, _ *http.Request) {
	jsonOut(w, map[string]any{"ok": true, "app": "FrpDesk fnOS Core"})
}
func (a *app) configs(w http.ResponseWriter, r *http.Request) {
	switch r.Method {
	case http.MethodGet:
		jsonOut(w, a.store.List())
	case http.MethodPost:
		var q struct{ Filename, Content string }
		if decode(r, &q) != nil {
			jsonErr(w, 400, "请求无效")
			return
		}
		p, e := a.store.Import(q.Filename, q.Content)
		if e != nil {
			jsonErr(w, 400, e.Error())
			return
		}
		a.manager.Ensure(p.ID)
		jsonOutStatus(w, 201, p)
	default:
		jsonErr(w, 405, "不支持此方法")
	}
}
func (a *app) configByID(w http.ResponseWriter, r *http.Request) {
	path := strings.TrimPrefix(r.URL.Path, "/api/configs/")
	parts := strings.Split(path, "/")
	if len(parts) == 2 && parts[1] == "endpoints" {
		p, ok := a.store.Get(parts[0])
		if !ok {
			jsonErr(w, 404, "配置不存在")
			return
		}
		jsonOut(w, config.Endpoints(p))
		return
	}
	if len(parts) == 2 && parts[1] == "autostart" && r.Method == http.MethodPut {
		var q struct {
			AutoStart bool `json:"autoStart"`
		}
		if decode(r, &q) != nil {
			jsonErr(w, 400, "请求无效")
			return
		}
		p, e := a.store.SetAutoStart(parts[0], q.AutoStart)
		if e != nil {
			jsonErr(w, 400, e.Error())
			return
		}
		jsonOut(w, p)
		return
	}
	if len(parts) == 2 && parts[1] == "validate" && r.Method == http.MethodPost {
		p, ok := a.store.Get(parts[0])
		if !ok {
			jsonErr(w, 404, "配置不存在")
			return
		}
		jsonOut(w, config.Validate(p))
		return
	}
	if len(parts) != 1 || parts[0] == "" {
		jsonErr(w, 404, "配置不存在")
		return
	}
	id := parts[0]
	switch r.Method {
	case http.MethodGet:
		p, ok := a.store.Get(id)
		if !ok {
			jsonErr(w, 404, "配置不存在")
			return
		}
		jsonOut(w, p)
	case http.MethodPut:
		var q struct{ Name, Content string }
		if decode(r, &q) != nil {
			jsonErr(w, 400, "请求无效")
			return
		}
		p, e := a.store.Update(id, q.Name, q.Content)
		if e != nil {
			jsonErr(w, 400, e.Error())
			return
		}
		jsonOut(w, p)
	case http.MethodDelete:
		if !a.store.Delete(id) {
			jsonErr(w, 404, "配置不存在")
			return
		}
		a.manager.Remove(id)
		w.WriteHeader(204)
	default:
		jsonErr(w, 405, "不支持此方法")
	}
}
func (a *app) instances(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodGet {
		jsonErr(w, 405, "不支持此方法")
		return
	}
	for _, p := range a.store.List() {
		a.manager.Ensure(p.ID)
	}
	jsonOut(w, a.manager.List())
}
func (a *app) instanceByID(w http.ResponseWriter, r *http.Request) {
	parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/api/instances/"), "/")
	if len(parts) != 2 {
		jsonErr(w, 404, "实例不存在")
		return
	}
	id, action := parts[0], parts[1]
	p, ok := a.store.Get(id)
	if !ok {
		jsonErr(w, 404, "配置不存在")
		return
	}
	switch action {
	case "logs":
		if r.Method == http.MethodGet {
			jsonOut(w, a.manager.Logs(id))
			return
		}
		if r.Method == http.MethodDelete {
			if e := a.manager.ClearLogs(id); e != nil {
				jsonErr(w, 400, e.Error())
				return
			}
			w.WriteHeader(204)
			return
		}
	case "start":
		if r.Method == http.MethodPost {
			if e := valid(p); e != "" {
				jsonErr(w, 400, e)
				return
			}
			x, e := a.manager.Start(id, p.Raw, true)
			if e != nil {
				jsonOutStatus(w, 400, x)
				return
			}
			jsonOut(w, x)
			return
		}
	case "stop":
		if r.Method == http.MethodPost {
			_ = a.manager.Stop(id)
			x, _ := a.manager.Get(id)
			jsonOut(w, x)
			return
		}
	case "restart":
		if r.Method == http.MethodPost {
			x, e := a.manager.Restart(id, p.Raw)
			if e != nil {
				jsonOutStatus(w, 400, x)
				return
			}
			jsonOut(w, x)
			return
		}
	case "reload":
		if r.Method == http.MethodPost {
			x, e := a.manager.Reload(id, p.Raw)
			if e != nil {
				jsonOutStatus(w, 400, x)
				return
			}
			jsonOut(w, x)
			return
		}
	case "status":
		if r.Method == http.MethodGet {
			x, e := a.manager.Status(id)
			if e != nil {
				jsonErr(w, 400, e.Error())
				return
			}
			jsonOut(w, x)
			return
		}
	}
	jsonErr(w, 404, "实例操作不存在")
}
func (a *app) status(w http.ResponseWriter, _ *http.Request) {
	items := a.manager.List()
	running, failed := 0, 0
	for _, x := range items {
		if x.Status == process.Running {
			running++
		}
		if x.Status == process.Failed {
			failed++
		}
	}
	jsonOut(w, map[string]any{"totalConfigs": len(a.store.List()), "runningInstances": running, "failedInstances": failed, "instances": items})
}
func (a *app) diagnostics(w http.ResponseWriter, _ *http.Request) {
	coreSocket := os.Getenv("FRPDESK_SOCKET")
	_, socketErr := os.Stat(coreSocket)
	frpcOK := false
	if a.frpc != "" {
		if info, err := os.Stat(a.frpc); err == nil {
			frpcOK = info.Mode()&0o111 != 0
		}
	}
	items := a.manager.List()
	running, failed := 0, 0
	for _, item := range items {
		if item.Status == process.Running {
			running++
		}
		if item.Status == process.Failed {
			failed++
		}
	}
	jsonOut(w, map[string]any{
		"core":             map[string]any{"ok": socketErr == nil, "app": "FrpDesk fnOS Core", "socket": coreSocket, "socketPresent": socketErr == nil},
		"frpc":             map[string]any{"path": a.frpc, "available": frpcOK},
		"dataDir":          a.data,
		"configs":          len(a.store.List()),
		"instances":        len(items),
		"runningInstances": running,
		"failedInstances":  failed,
	})
}
func (a *app) autostart(w http.ResponseWriter, r *http.Request) {
	path := filepath.Join(a.data, "fnos-autostart.json")
	if r.Method == http.MethodGet {
		b, err := os.ReadFile(path)
		var x struct {
			Enabled bool `json:"enabled"`
		}
		if err == nil {
			_ = json.Unmarshal(b, &x)
		}
		registered := fileExists("/etc/systemd/system/frpdesk.service")
		jsonOut(w, map[string]any{"available": registered, "desired": x.Enabled, "enabled": x.Enabled, "healthy": registered && x.Enabled, "serviceName": "frpdesk.service"})
		return
	}
	if r.Method == http.MethodPut {
		var q struct {
			Enabled bool `json:"enabled"`
		}
		if decode(r, &q) != nil {
			jsonErr(w, 400, "请求无效")
			return
		}
		b, _ := json.Marshal(q)
		if err := os.WriteFile(path, b, 0o600); err != nil {
			jsonErr(w, http.StatusInternalServerError, "无法保存开机自启设置："+err.Error())
			return
		}
		registered := fileExists("/etc/systemd/system/frpdesk.service")
		jsonOut(w, map[string]any{"available": registered, "desired": q.Enabled, "enabled": q.Enabled, "healthy": registered && q.Enabled, "serviceName": "frpdesk.service"})
		return
	}
	jsonErr(w, 405, "不支持此方法")
}
func fileExists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}
func (a *app) notFound(w http.ResponseWriter, _ *http.Request) { jsonErr(w, 404, "接口不存在") }
func valid(p *model.Profile) string {
	if x := config.Validate(p); len(x) > 0 {
		return strings.Join(x, "；")
	}
	return ""
}
func decode(r *http.Request, v any) error {
	r.Body = io.NopCloser(io.LimitReader(r.Body, 8<<20+1))
	return json.NewDecoder(r.Body).Decode(v)
}
func jsonOut(w http.ResponseWriter, v any) { jsonOutStatus(w, 200, v) }
func jsonOutStatus(w http.ResponseWriter, status int, v any) {
	w.Header().Set("Content-Type", "application/json; charset=utf-8")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(v)
}
func jsonErr(w http.ResponseWriter, status int, msg string) {
	jsonOutStatus(w, status, map[string]string{"error": msg})
}
func env(k, d string) string {
	if v := os.Getenv(k); v != "" {
		return v
	}
	return d
}
func within(root, path string) bool {
	r, _ := filepath.Abs(root)
	p, _ := filepath.Abs(path)
	return p == r || strings.HasPrefix(p, r+string(os.PathSeparator))
}
