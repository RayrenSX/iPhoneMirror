package fpcontrol

import (
	"bytes"
	"io"
	"net"
	"testing"
	"time"
)

func TestConnBufferedUpgradeAndChunkedDuplex(t *testing.T) {
	var key, nonce [16]byte
	key[0], nonce[0] = 1, 2
	first := []byte("HTTP/1.1 200 OK\r\nContent-Length: 0\r\n\r\n")
	sender := NewRecords(key, nonce)
	record, err := sender.Seal(first)
	if err != nil {
		t.Fatal(err)
	}
	a, b := net.Pipe()
	defer a.Close()
	defer b.Close()
	a.SetDeadline(time.Now().Add(3 * time.Second))
	b.SetDeadline(time.Now().Add(3 * time.Second))
	client := NewConn(a, record[:7], NewRecords(key, nonce), NewRecords(key, nonce))
	server := NewConn(b, nil, sender, NewRecords(key, nonce))
	payload := bytes.Repeat([]byte("fragmented duplex payload"), 20000)
	done := make(chan error, 1)
	go func() {
		if _, e := b.Write(record[7:]); e != nil {
			done <- e
			return
		}
		got := make([]byte, len(payload))
		if _, e := io.ReadFull(server, got); e != nil {
			done <- e
			return
		}
		if !bytes.Equal(got, payload) {
			done <- io.ErrUnexpectedEOF
			return
		}
		_, e := server.Write([]byte("ack"))
		done <- e
	}()
	got := make([]byte, len(first))
	if _, e := io.ReadFull(client, got); e != nil {
		t.Fatal(e)
	}
	if !bytes.Equal(got, first) {
		t.Fatal("lost buffered encrypted bytes")
	}
	if n, e := client.Write(payload); e != nil || n != len(payload) {
		t.Fatalf("write %d: %v", n, e)
	}
	ack := make([]byte, 3)
	if _, e := io.ReadFull(client, ack); e != nil {
		t.Fatal(e)
	}
	if string(ack) != "ack" {
		t.Fatal("bad duplex reply")
	}
	if e := <-done; e != nil {
		t.Fatal(e)
	}
}

func TestConnAuthenticationFailureClosesSocket(t *testing.T) {
	var key, nonce [16]byte
	record, _ := NewRecords(key, nonce).Seal([]byte("untrusted"))
	record[len(record)-1] ^= 1
	a, b := net.Pipe()
	defer b.Close()
	c := NewConn(a, record, NewRecords(key, nonce), NewRecords(key, nonce))
	got := make([]byte, 32)
	if n, e := c.Read(got); n != 0 || e == nil {
		t.Fatalf("exposed unauthenticated data: %d %v", n, e)
	}
	if _, e := b.Write([]byte("x")); e == nil {
		t.Fatal("socket remains open")
	}
	if _, e := c.Write([]byte("retry")); e == nil {
		t.Fatal("reused failed connection")
	}
}
