package receiver

import (
	"bytes"
	"crypto/rand"
	"github.com/jkiddo/go-atv/internal/fairplay"
	"github.com/jkiddo/go-atv/internal/fpcontrol"
	"testing"
)

func connected(t *testing.T, mixed bool) (*State, *fpcontrol.Records, *fpcontrol.Records) {
	t.Helper()
	s, e := New()
	if e != nil {
		t.Fatal(e)
	}
	sender, e := fairplay.NewSender(rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	m2, e := s.Exchange(sender.Start())
	if e != nil {
		t.Fatal(e)
	}
	m3, e := sender.Challenge(m2)
	if e != nil {
		t.Fatal(e)
	}
	m4, e := s.Exchange(m3)
	if e != nil {
		t.Fatal(e)
	}
	if e = sender.Confirm(m4); e != nil {
		t.Fatal(e)
	}
	var material [64]byte
	rand.Read(material[:])
	envelope, e := sender.Wrap(material, rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	secret := bytes.Repeat([]byte{91}, 32)
	if e = s.Setup(envelope[:], secret, mixed); e != nil {
		t.Fatal(e)
	}
	write, read, e := fpcontrol.Sender(material, secret, mixed)
	if e != nil {
		t.Fatal(e)
	}
	return s, write, read
}
func TestFragmentedAndCoalescedRecords(t *testing.T) {
	for _, mixed := range []bool{false, true} {
		s, w, r := connected(t, mixed)
		original := bytes.Repeat([]byte("a"), fpcontrol.MaxPayload)
		wire, _ := w.Seal(original)
		next, _ := w.Seal([]byte("next"))
		wire = append(wire, next...)
		var got []byte
		for len(wire) > 0 {
			n := 1024
			if n > len(wire) {
				n = len(wire)
			}
			b, e := s.Feed(wire[:n])
			if e != nil {
				t.Fatal(e)
			}
			got = append(got, b...)
			wire = wire[n:]
		}
		if !bytes.Equal(got, append(original, []byte("next")...)) {
			t.Fatal("fragmented payload mismatch")
		}
		reply, e := s.Seal([]byte("response"))
		if e != nil {
			t.Fatal(e)
		}
		b, e := r.Open(reply)
		if e != nil || string(b) != "response" {
			t.Fatal("reply direction", e)
		}
	}
}
func TestTamperingAndReplayAreTerminal(t *testing.T) {
	for _, replay := range []bool{false, true} {
		s, w, _ := connected(t, false)
		wire, _ := w.Seal([]byte("request"))
		if replay {
			if _, e := s.Feed(wire); e != nil {
				t.Fatal(e)
			}
		} else {
			wire[len(wire)-1] ^= 1
		}
		if b, e := s.Feed(wire); e == nil || len(b) > 0 {
			t.Fatal("invalid record released plaintext")
		}
		valid, _ := w.Seal([]byte("later"))
		if _, e := s.Feed(valid); e == nil {
			t.Fatal("failed connection reused")
		}
	}
}
