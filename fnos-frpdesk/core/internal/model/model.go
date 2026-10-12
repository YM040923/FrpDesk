package model

import "time"

type Proxy struct {
	Name              string   `json:"name"`
	Type              string   `json:"type"`
	LocalIP           string   `json:"localIP,omitempty"`
	LocalPort         int      `json:"localPort,omitempty"`
	RemotePort        int      `json:"remotePort,omitempty"`
	CustomDomains     []string `json:"customDomains,omitempty"`
	Subdomain         string   `json:"subdomain,omitempty"`
	Locations         []string `json:"locations,omitempty"`
	HostHeaderRewrite string   `json:"hostHeaderRewrite,omitempty"`
	SecretKey         string   `json:"secretKey,omitempty"`
}

type Profile struct {
	ID         string    `json:"id"`
	Name       string    `json:"name"`
	Filename   string    `json:"filename"`
	ServerAddr string    `json:"serverAddr"`
	ServerPort int       `json:"serverPort"`
	Proxies    []Proxy   `json:"proxies"`
	Raw        string    `json:"raw"`
	CreatedAt  time.Time `json:"createdAt"`
	UpdatedAt  time.Time `json:"updatedAt"`
	AutoStart  bool      `json:"autoStart"`
}

type Endpoint struct {
	Name          string `json:"name"`
	Type          string `json:"type"`
	LocalAddress  string `json:"localAddress"`
	RemoteAddress string `json:"remoteAddress"`
	RemoteURL     string `json:"remoteURL,omitempty"`
}

type Diagnostic struct {
	Summary   string    `json:"summary"`
	Detail    string    `json:"detail,omitempty"`
	Severity  string    `json:"severity"`
	ExitCode  *int      `json:"exitCode,omitempty"`
	FailureAt time.Time `json:"failureAt,omitempty"`
}

type Instance struct {
	ProfileID  string      `json:"profileId"`
	Status     string      `json:"status"`
	PID        int         `json:"pid,omitempty"`
	StartedAt  time.Time   `json:"startedAt,omitempty"`
	StoppedAt  time.Time   `json:"stoppedAt,omitempty"`
	ExitCode   *int        `json:"exitCode,omitempty"`
	Error      string      `json:"error,omitempty"`
	ConfigPath string      `json:"configPath,omitempty"`
	LogPath    string      `json:"logPath,omitempty"`
	Desired    bool        `json:"desired"`
	Retries    int         `json:"retries"`
	Diagnostic *Diagnostic `json:"diagnostic,omitempty"`
}

type LogLine struct {
	At   time.Time `json:"at"`
	Line string    `json:"line"`
}
