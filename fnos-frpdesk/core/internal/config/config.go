package config

import (
	"crypto/rand"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"

	"github.com/BurntSushi/toml"
	"github.com/YM040923/FrpDesk-fnos/internal/model"
)

type parsedDocument struct {
	ServerAddr      string        `toml:"serverAddr"`
	ServerAddrSnake string        `toml:"server_addr"`
	ServerPort      int           `toml:"serverPort"`
	ServerPortSnake int           `toml:"server_port"`
	Proxies         []parsedProxy `toml:"proxies"`
}

type parsedProxy struct {
	Name                   string   `toml:"name"`
	Type                   string   `toml:"type"`
	LocalIP                string   `toml:"localIP"`
	LocalIPSnake           string   `toml:"local_ip"`
	LocalPort              int      `toml:"localPort"`
	LocalPortSnake         int      `toml:"local_port"`
	RemotePort             int      `toml:"remotePort"`
	RemotePortSnake        int      `toml:"remote_port"`
	CustomDomains          []string `toml:"customDomains"`
	CustomDomainsSnake     []string `toml:"custom_domains"`
	Subdomain              string   `toml:"subdomain"`
	Locations              []string `toml:"locations"`
	HostHeaderRewrite      string   `toml:"hostHeaderRewrite"`
	HostHeaderRewriteSnake string   `toml:"host_header_rewrite"`
	SecretKey              string   `toml:"secretKey"`
	SecretKeySnake         string   `toml:"secret_key"`
}

type Store struct {
	mu       sync.RWMutex
	dir      string
	profiles map[string]*model.Profile
}

func NewStore(dir string) (*Store, error) {
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return nil, err
	}
	s := &Store{dir: dir, profiles: map[string]*model.Profile{}}
	if err := s.load(); err != nil {
		return nil, err
	}
	return s, nil
}

func Parse(filename, raw string) (*model.Profile, error) {
	if strings.ToLower(filepath.Ext(filename)) != ".toml" {
		return nil, fmt.Errorf("只支持 TOML 配置文件")
	}
	var doc parsedDocument
	if _, err := toml.Decode(raw, &doc); err != nil {
		return nil, fmt.Errorf("TOML 解析失败：%w", err)
	}
	p := &model.Profile{Filename: filepath.Base(filename), Name: strings.TrimSuffix(filepath.Base(filename), filepath.Ext(filename)), Raw: raw, CreatedAt: time.Now().UTC(), UpdatedAt: time.Now().UTC(), ServerAddr: firstString(doc.ServerAddr, doc.ServerAddrSnake), ServerPort: firstInt(doc.ServerPort, doc.ServerPortSnake)}
	for _, x := range doc.Proxies {
		p.Proxies = append(p.Proxies, model.Proxy{Name: x.Name, Type: x.Type, LocalIP: firstString(x.LocalIP, x.LocalIPSnake), LocalPort: firstInt(x.LocalPort, x.LocalPortSnake), RemotePort: firstInt(x.RemotePort, x.RemotePortSnake), CustomDomains: firstList(x.CustomDomains, x.CustomDomainsSnake), Subdomain: x.Subdomain, Locations: x.Locations, HostHeaderRewrite: firstString(x.HostHeaderRewrite, x.HostHeaderRewriteSnake), SecretKey: firstString(x.SecretKey, x.SecretKeySnake)})
	}
	return p, nil
}

func (s *Store) List() []*model.Profile {
	s.mu.RLock()
	defer s.mu.RUnlock()
	out := make([]*model.Profile, 0, len(s.profiles))
	for _, p := range s.profiles {
		out = append(out, clone(p))
	}
	sort.Slice(out, func(i, j int) bool { return out[i].CreatedAt.Before(out[j].CreatedAt) })
	return out
}
func (s *Store) Get(id string) (*model.Profile, bool) {
	s.mu.RLock()
	defer s.mu.RUnlock()
	p, ok := s.profiles[id]
	return clone(p), ok
}
func (s *Store) Import(filename, raw string) (*model.Profile, error) {
	p, err := Parse(filename, raw)
	if err != nil {
		return nil, err
	}
	p.ID = newID()
	s.mu.Lock()
	s.profiles[p.ID] = clone(p)
	s.mu.Unlock()
	return s.persisted(p)
}
func (s *Store) Update(id, name, raw string) (*model.Profile, error) {
	s.mu.Lock()
	old, ok := s.profiles[id]
	if !ok {
		s.mu.Unlock()
		return nil, fmt.Errorf("配置不存在")
	}
	p, err := Parse(old.Filename, raw)
	if err != nil {
		s.mu.Unlock()
		return nil, err
	}
	p.ID = id
	p.CreatedAt = old.CreatedAt
	p.Name = firstString(name, old.Name)
	p.UpdatedAt = time.Now().UTC()
	p.AutoStart = old.AutoStart
	s.profiles[id] = clone(p)
	s.mu.Unlock()
	return s.persisted(p)
}
func (s *Store) SetAutoStart(id string, enabled bool) (*model.Profile, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	p, ok := s.profiles[id]
	if !ok {
		return nil, fmt.Errorf("配置不存在")
	}
	p = clone(p)
	p.AutoStart = enabled
	p.UpdatedAt = time.Now().UTC()
	s.profiles[id] = clone(p)
	return s.persistedLocked(p)
}
func (s *Store) Delete(id string) bool {
	s.mu.Lock()
	p, ok := s.profiles[id]
	if ok {
		delete(s.profiles, id)
	}
	s.mu.Unlock()
	if !ok {
		return false
	}
	_ = os.Remove(filepath.Join(s.dir, id+".json"))
	_ = os.Remove(filepath.Join(s.dir, id+".toml"))
	_ = p
	return true
}

func (s *Store) load() error {
	entries, err := os.ReadDir(s.dir)
	if err != nil {
		return err
	}
	for _, e := range entries {
		if e.IsDir() || filepath.Ext(e.Name()) != ".json" {
			continue
		}
		var meta struct {
			ID, Name, Filename   string
			CreatedAt, UpdatedAt time.Time
			AutoStart            bool
		}
		b, err := os.ReadFile(filepath.Join(s.dir, e.Name()))
		if err != nil {
			return err
		}
		if json.Unmarshal(b, &meta) != nil || meta.ID == "" {
			continue
		}
		raw, err := os.ReadFile(filepath.Join(s.dir, meta.ID+".toml"))
		if err != nil {
			continue
		}
		p, err := Parse(meta.Filename, string(raw))
		if err != nil {
			continue
		}
		p.ID = meta.ID
		p.Name = meta.Name
		p.CreatedAt = meta.CreatedAt
		p.UpdatedAt = meta.UpdatedAt
		p.AutoStart = meta.AutoStart
		s.profiles[p.ID] = p
	}
	return nil
}
func (s *Store) persisted(p *model.Profile) (*model.Profile, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.persistedLocked(p)
}
func (s *Store) persistedLocked(p *model.Profile) (*model.Profile, error) {
	if err := os.WriteFile(filepath.Join(s.dir, p.ID+".toml"), []byte(p.Raw), 0o600); err != nil {
		return nil, err
	}
	meta := struct {
		ID, Name, Filename   string
		CreatedAt, UpdatedAt time.Time
		AutoStart            bool
	}{p.ID, p.Name, p.Filename, p.CreatedAt, p.UpdatedAt, p.AutoStart}
	b, _ := json.MarshalIndent(meta, "", "  ")
	if err := os.WriteFile(filepath.Join(s.dir, p.ID+".json"), append(b, '\n'), 0o600); err != nil {
		return nil, err
	}
	return clone(p), nil
}
func clone(p *model.Profile) *model.Profile {
	if p == nil {
		return nil
	}
	q := *p
	q.Proxies = append([]model.Proxy(nil), p.Proxies...)
	return &q
}
func newID() string {
	var b [16]byte
	if _, err := rand.Read(b[:]); err != nil {
		return fmt.Sprintf("%d", time.Now().UnixNano())
	}
	return hex.EncodeToString(b[:])
}
func firstString(v ...string) string {
	for _, x := range v {
		if strings.TrimSpace(x) != "" {
			return strings.TrimSpace(x)
		}
	}
	return ""
}
func firstInt(v ...int) int {
	for _, x := range v {
		if x != 0 {
			return x
		}
	}
	return 0
}
func firstList(v ...[]string) []string {
	for _, x := range v {
		if len(x) > 0 {
			return append([]string(nil), x...)
		}
	}
	return nil
}

func Validate(p *model.Profile) []string {
	var out []string
	if p == nil {
		return []string{"配置为空"}
	}
	if strings.TrimSpace(p.ServerAddr) == "" {
		out = append(out, "缺少 serverAddr")
	}
	if p.ServerPort < 1 || p.ServerPort > 65535 {
		out = append(out, "serverPort 必须在 1-65535 之间")
	}
	seen := map[string]bool{}
	for i, x := range p.Proxies {
		prefix := fmt.Sprintf("proxies[%d]", i)
		if strings.TrimSpace(x.Name) == "" {
			out = append(out, prefix+" 缺少 name")
		}
		if seen[x.Name] {
			out = append(out, prefix+" 名称重复")
		}
		seen[x.Name] = true
		if x.LocalPort < 1 || x.LocalPort > 65535 {
			out = append(out, prefix+" localPort 无效")
		}
		switch strings.ToLower(x.Type) {
		case "tcp", "udp":
			if x.RemotePort < 1 || x.RemotePort > 65535 {
				out = append(out, prefix+" remotePort 无效")
			}
		case "http", "https":
			if len(x.CustomDomains) == 0 && x.Subdomain == "" {
				out = append(out, prefix+" 缺少 customDomains 或 subdomain")
			}
		}
	}
	return out
}

func Endpoints(p *model.Profile) []model.Endpoint {
	if p == nil {
		return nil
	}
	out := make([]model.Endpoint, 0, len(p.Proxies))
	for _, x := range p.Proxies {
		typ := strings.ToLower(x.Type)
		local := firstString(x.LocalIP, "127.0.0.1")
		if x.LocalPort > 0 {
			local = fmt.Sprintf("%s:%d", local, x.LocalPort)
		}
		remote := ""
		url := ""
		if typ == "tcp" || typ == "udp" {
			if x.RemotePort > 0 {
				remote = fmt.Sprintf("%s:%d", p.ServerAddr, x.RemotePort)
			}
		} else if typ == "http" || typ == "https" {
			if len(x.CustomDomains) > 0 {
				url = typ + "://" + x.CustomDomains[0] + "/"
				remote = url
			} else if x.Subdomain != "" {
				url = typ + "://" + x.Subdomain + "." + p.ServerAddr + "/"
				remote = url
			}
		} else {
			remote = "需配合访问端（访问名称：" + x.Name + "）"
		}
		out = append(out, model.Endpoint{Name: firstString(x.Name, "未命名代理"), Type: typ, LocalAddress: local, RemoteAddress: remote, RemoteURL: url})
	}
	return out
}
