package fairplay

import (
	"crypto/rand"
	"testing"
)

func controlSession(t *testing.T) (*Sender, *Receiver) {
	t.Helper()
	s, e := NewSender(rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	r, e := NewReceiver(rand.Reader)
	if e != nil {
		t.Fatal(e)
	}
	m2, e := r.Exchange(s.Start())
	if e != nil {
		t.Fatal(e)
	}
	m3, e := s.Challenge(m2)
	if e != nil {
		t.Fatal(e)
	}
	m4, e := r.Exchange(m3)
	if e != nil {
		t.Fatal(e)
	}
	if e = s.Confirm(m4); e != nil {
		t.Fatal(e)
	}
	return s, r
}
func TestControlEnvelopeFreshSessionsAndTampering(t *testing.T) {
	for _, offset := range []int{-1, 0, 16, 32, 36, 56, 119} {
		s, r := controlSession(t)
		var material [64]byte
		rand.Read(material[:])
		wire, e := s.Wrap(material, rand.Reader)
		if e != nil {
			t.Fatal(e)
		}
		if offset >= 0 {
			wire[offset] ^= 1
		}
		got, e := r.Unwrap(wire[:])
		if offset < 0 {
			if e != nil || got != material {
				t.Fatalf("unwrap: %v", e)
			}
		} else if e == nil || got != [64]byte{} {
			t.Fatalf("tamper %d accepted", offset)
		}
		if _, e = r.Unwrap(wire[:]); e == nil {
			t.Fatal("reused envelope")
		}
	}
}
func TestControlEnvelopeRequiresConfirmedSession(t *testing.T) {
	s, _ := NewSender(rand.Reader)
	if _, e := s.Wrap([64]byte{}, rand.Reader); e == nil {
		t.Fatal("unconfirmed sender")
	}
	r, _ := NewReceiver(rand.Reader)
	if _, e := r.Unwrap(make([]byte, 120)); e == nil {
		t.Fatal("unconfirmed receiver")
	}
}

func TestQuickTimeOpenCapabilities(t *testing.T) {
	s, _ := NewSender(rand.Reader)
	r, _ := NewReceiver(rand.Reader)
	m1 := s.Start()
	m1[14] = 1
	m2, e := r.Exchange(m1)
	if e != nil {
		t.Fatal(e)
	}
	if m2[13] != 1 {
		t.Fatal("wrong negotiated mode")
	}
	m3, e := s.Challenge(m2)
	if e != nil {
		t.Fatal(e)
	}
	m4, e := r.Exchange(m3)
	if e != nil {
		t.Fatal(e)
	}
	if e = s.Confirm(m4); e != nil {
		t.Fatal(e)
	}
}
