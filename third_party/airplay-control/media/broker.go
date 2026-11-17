// SPDX-License-Identifier: LGPL-3.0-or-later
// FCUP protocol fields follow UxPlay's LGPL fcup_request.h (fduncanh, 2022).
// This adapter serves only phone-provided resources over a tokenized loopback URL.
package media

import (
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"encoding/binary"
	"encoding/hex"
	"encoding/xml"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"time"
)

const MaxBody = 1024 * 1024

type result struct {
	status int
	body   []byte
}
type pending struct {
	id       uint32
	location string
	done     chan result
}
type session struct {
	id, token string
	refs      int
	pending   map[uint32]*pending
	queue     []*pending
}
type Broker struct {
	mu       sync.Mutex
	sessions map[string]*session
	handles  map[uint64]*session
	next     uint32
	listener net.Listener
	server   *http.Server
}

func New() *Broker { return &Broker{sessions: map[string]*session{}, handles: map[uint64]*session{}} }
func (b *Broker) Bind(h uint64, id string) error {
	if h == 0 || len(id) == 0 || len(id) > 128 {
		return errors.New("invalid session")
	}
	for _, c := range id {
		if c < 33 || c > 126 {
			return errors.New("invalid session")
		}
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	if old := b.handles[h]; old != nil {
		if old.id != id {
			return errors.New("connection session changed")
		}
		return nil
	}
	s := b.sessions[id]
	if s == nil {
		if len(b.sessions) >= 64 {
			return errors.New("session limit")
		}
		s = &session{id: id, pending: map[uint32]*pending{}}
		b.sessions[id] = s
	}
	s.refs++
	b.handles[h] = s
	return nil
}
func cancel(s *session) {
	for _, p := range s.pending {
		p.done <- result{status: 410}
	}
	s.pending = map[uint32]*pending{}
	s.queue = nil
}
func (b *Broker) Detach(h uint64) {
	b.mu.Lock()
	defer b.mu.Unlock()
	s := b.handles[h]
	if s == nil {
		return
	}
	delete(b.handles, h)
	s.refs--
	if s.refs == 0 {
		cancel(s)
		delete(b.sessions, s.id)
	}
}
func (b *Broker) Close() {
	b.mu.Lock()
	for _, s := range b.sessions {
		cancel(s)
	}
	srv := b.server
	b.mu.Unlock()
	if srv != nil {
		srv.Close()
	}
}
func (b *Broker) Start(h uint64, location string) (string, error) {
	u, e := url.Parse(location)
	if e != nil || u.Scheme != "mlhls" || u.Host == "" || u.User != nil || len(location) > 16384 {
		return "", errors.New("unsupported location")
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	s := b.handles[h]
	if s == nil {
		return "", errors.New("missing session")
	}
	if b.listener == nil {
		l, e := net.Listen("tcp4", "127.0.0.1:0")
		if e != nil {
			return "", e
		}
		b.listener = l
		b.server = &http.Server{Handler: http.HandlerFunc(b.serve), ReadHeaderTimeout: 5 * time.Second, IdleTimeout: 30 * time.Second, MaxHeaderBytes: 8192}
		go b.server.Serve(l)
	}
	var token [24]byte
	if _, e = rand.Read(token[:]); e != nil {
		return "", e
	}
	cancel(s)
	s.token = hex.EncodeToString(token[:])
	return b.local(s, location), nil
}
func (b *Broker) local(s *session, location string) string {
	return "http://" + b.listener.Addr().String() + "/" + s.token + "/" + base64.RawURLEncoding.EncodeToString([]byte(location)) + "/stream.m3u8"
}
func escaped(s string) string {
	var out bytes.Buffer
	xml.EscapeText(&out, []byte(s))
	return out.String()
}
func (b *Broker) Poll(h uint64) []byte {
	b.mu.Lock()
	defer b.mu.Unlock()
	s := b.handles[h]
	if s == nil {
		return nil
	}
	for len(s.queue) > 0 {
		p := s.queue[0]
		s.queue = s.queue[1:]
		if s.pending[p.id] != p {
			continue
		}
		body := fmt.Sprintf(`<?xml version="1.0" encoding="UTF-8"?><plist version="1.0"><dict><key>sessionID</key><integer>1</integer><key>type</key><string>unhandledURLRequest</string><key>request</key><dict><key>FCUP_Response_ClientInfo</key><integer>1</integer><key>FCUP_Response_ClientRef</key><integer>40030004</integer><key>FCUP_Response_RequestID</key><integer>%d</integer><key>FCUP_Response_URL</key><string>%s</string><key>sessionID</key><integer>1</integer><key>FCUP_Response_Headers</key><dict><key>X-Playback-Session-Id</key><string>%s</string><key>User-Agent</key><string>AppleCoreMedia/1.0</string></dict></dict></dict></plist>`, p.id, escaped(p.location), escaped(s.id))
		return []byte(fmt.Sprintf("POST /event HTTP/1.1\r\nX-Apple-Session-ID: %s\r\nContent-Type: text/x-apple-plist+xml\r\nContent-Length: %d\r\n\r\n%s", s.id, len(body), body))
	}
	return nil
}

// Reply wire format is big-endian request ID, HTTP status, then response bytes.
func (b *Broker) Reply(h uint64, wire []byte) error {
	if len(wire) < 8 || len(wire) > MaxBody {
		return errors.New("invalid response size")
	}
	id, status := binary.BigEndian.Uint32(wire[:4]), binary.BigEndian.Uint32(wire[4:8])
	// Native FCUP can report transport success as 0 rather than HTTP 200.
	// Only a playlist body qualifies; never turn an empty/error response into media.
	if status == 0 {
		status = 502
		if bytes.HasPrefix(bytes.TrimSpace(wire[8:]), []byte("#EXTM3U")) {
			status = 200
		}
	}
	if status < 100 || status > 599 {
		return errors.New("invalid response status")
	}
	b.mu.Lock()
	defer b.mu.Unlock()
	s := b.handles[h]
	if s == nil {
		return errors.New("missing session")
	}
	p := s.pending[id]
	if p == nil {
		return errors.New("unknown request")
	}
	delete(s.pending, id)
	p.done <- result{int(status), append([]byte(nil), wire[8:]...)}
	return nil
}

var uriAttribute = regexp.MustCompile(`URI="([^"]*)"`)

func (b *Broker) rewrite(s *session, location string, data []byte) ([]byte, error) {
	data = selectDesktopVariant(data)
	base, e := url.Parse(location)
	if e != nil {
		return nil, e
	}
	convert := func(raw string) (string, error) {
		u, e := url.Parse(raw)
		if e != nil {
			return "", e
		}
		u = base.ResolveReference(u)
		switch u.Scheme {
		case "https", "http":
			return u.String(), nil
		case "mlhls":
			return b.local(s, u.String()), nil
		}
		return "", errors.New("unsupported playlist URI")
	}
	lines := strings.Split(string(data), "\n")
	for i, line := range lines {
		trim := strings.TrimSpace(line)
		if trim == "" {
			continue
		}
		if !strings.HasPrefix(trim, "#") {
			lines[i], e = convert(trim)
			if e != nil {
				return nil, e
			}
		} else {
			var failure error
			lines[i] = uriAttribute.ReplaceAllStringFunc(line, func(attr string) string {
				v, err := convert(attr[5 : len(attr)-1])
				if err != nil {
					failure = err
				}
				return `URI="` + v + `"`
			})
			if failure != nil {
				return nil, failure
			}
		}
	}
	return []byte(strings.Join(lines, "\n")), nil
}
func (b *Broker) serve(w http.ResponseWriter, r *http.Request) {
	if r.Method != "GET" && r.Method != "HEAD" {
		w.WriteHeader(405)
		return
	}
	parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/"), "/")
	if len(parts) != 3 {
		http.NotFound(w, r)
		return
	}
	decoded, e := base64.RawURLEncoding.DecodeString(parts[1])
	if e != nil || len(decoded) > 16384 {
		w.WriteHeader(400)
		return
	}
	location := string(decoded)
	u, e := url.Parse(location)
	if e != nil || u.Scheme != "mlhls" || u.Host == "" || u.User != nil {
		w.WriteHeader(400)
		return
	}
	b.mu.Lock()
	var s *session
	for _, candidate := range b.sessions {
		if candidate.token != "" && candidate.token == parts[0] {
			s = candidate
			break
		}
	}
	if s == nil {
		b.mu.Unlock()
		http.NotFound(w, r)
		return
	}
	if len(s.pending) >= 32 {
		b.mu.Unlock()
		w.WriteHeader(503)
		return
	}
	// A phone can send a late FCUP response on a new control connection after
	// reconnecting with the same session ID. IDs must not restart at one when
	// the final old handle detaches, or that response can answer a new item's
	// request with the previous item's playlist.
	for {
		b.next++
		if b.next == 0 {
			continue
		}
		used := false
		for _, candidate := range b.sessions {
			if candidate.pending[b.next] != nil {
				used = true
				break
			}
		}
		if !used {
			break
		}
	}
	p := &pending{id: b.next, location: location, done: make(chan result, 1)}
	s.pending[p.id] = p
	s.queue = append(s.queue, p)
	b.mu.Unlock()
	defer func() {
		b.mu.Lock()
		delete(s.pending, p.id)
		for i, queued := range s.queue {
			if queued == p {
				s.queue = append(s.queue[:i], s.queue[i+1:]...)
				break
			}
		}
		b.mu.Unlock()
	}()
	timer := time.NewTimer(15 * time.Second)
	defer timer.Stop()
	var response result
	select {
	case response = <-p.done:
	case <-r.Context().Done():
		return
	case <-timer.C:
		w.WriteHeader(504)
		return
	}
	if response.status >= 300 && response.status < 400 {
		target, err := url.Parse(string(response.body))
		if err != nil || len(response.body) == 0 || len(response.body) > 16384 {
			w.WriteHeader(502)
			return
		}
		target = u.ResolveReference(target)
		if target.User != nil {
			w.WriteHeader(502)
			return
		}
		switch target.Scheme {
		case "http", "https":
			w.Header().Set("Location", target.String())
		case "mlhls":
			b.mu.Lock()
			if s.token != parts[0] {
				b.mu.Unlock()
				w.WriteHeader(410)
				return
			}
			local := b.local(s, target.String())
			b.mu.Unlock()
			w.Header().Set("Location", local)
		default:
			w.WriteHeader(502)
			return
		}
		w.WriteHeader(response.status)
		return
	}
	if response.status < 200 || response.status >= 300 {
		w.WriteHeader(response.status)
		return
	}
	body := response.body
	if bytes.HasPrefix(bytes.TrimSpace(body), []byte("#EXTM3U")) {
		b.mu.Lock()
		if s.token != parts[0] {
			b.mu.Unlock()
			w.WriteHeader(410)
			return
		}
		body, e = b.rewrite(s, location, body)
		b.mu.Unlock()
		if e != nil {
			w.WriteHeader(502)
			return
		}
		body = projectDesktopTrack(body, requestedDesktopTrack(r.URL.Query()))
		w.Header().Set("Content-Type", "application/vnd.apple.mpegurl")
	} else {
		w.Header().Set("Content-Type", "application/octet-stream")
	}
	w.Header().Set("Cache-Control", "no-store")
	w.Header().Set("Content-Length", strconv.Itoa(len(body)))
	w.WriteHeader(response.status)
	if r.Method != "HEAD" {
		io.Copy(w, bytes.NewReader(body))
	}
}
