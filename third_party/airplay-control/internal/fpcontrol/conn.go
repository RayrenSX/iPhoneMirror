package fpcontrol

import (
	"bytes"
	"io"
	"net"
	"sync"
)

// Conn wraps one already-negotiated socket. buffered contains encrypted bytes
// read past the fp-setup2 HTTP response; they must not be discarded or parsed
// as plaintext. Each direction has its own lock, nonce and failure state.
type Conn struct {
	net.Conn
	source          io.Reader
	out, in         *Records
	readMu, writeMu sync.Mutex
	pending         []byte
}

func NewConn(conn net.Conn, buffered []byte, out, in *Records) *Conn {
	return &Conn{Conn: conn, source: io.MultiReader(bytes.NewReader(buffered), conn), out: out, in: in}
}
func (c *Conn) Read(p []byte) (int, error) {
	c.readMu.Lock()
	defer c.readMu.Unlock()
	if len(p) == 0 {
		return 0, nil
	}
	for len(c.pending) == 0 {
		data, err := c.in.Read(c.source)
		if err != nil {
			c.Conn.Close()
			return 0, err
		}
		c.pending = data
	}
	n := copy(p, c.pending)
	c.pending = c.pending[n:]
	return n, nil
}
func (c *Conn) Write(p []byte) (int, error) {
	c.writeMu.Lock()
	defer c.writeMu.Unlock()
	written := 0
	for len(p) > 0 {
		n := min(len(p), MaxPayload)
		if err := c.out.Write(c.Conn, p[:n]); err != nil {
			c.Conn.Close()
			return written, err
		}
		written += n
		p = p[n:]
	}
	return written, nil
}
