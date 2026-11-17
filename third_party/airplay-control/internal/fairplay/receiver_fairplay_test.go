// Derived from github.com/omarroth/doubletake, internal/airplay/receiver_fairplay_test.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Unmodified apart from the package name.

package fairplay

import (
	"bytes"
	"testing"
)

func TestReceiverFPSAPCompletesSenderExchange(t *testing.T) {
	sender, err := newFPSAPSession(bytes.NewReader(bytes.Repeat([]byte{0x31}, 126)))
	if err != nil {
		t.Fatalf("create sender FPSAP state: %v", err)
	}
	receiver, err := newReceiverFPSAPState(bytes.NewReader(bytes.Repeat([]byte{0x72}, 126)))
	if err != nil {
		t.Fatalf("create receiver FPSAP state: %v", err)
	}

	m2, err := receiver.exchange(sender.message1())
	if err != nil {
		t.Fatalf("exchange m2: %v", err)
	}
	m3, err := sender.exchangeM3(m2)
	if err != nil {
		t.Fatalf("exchange m3: %v", err)
	}
	m4, err := receiver.exchange(m3)
	if err != nil {
		t.Fatalf("exchange m4: %v", err)
	}
	if err := sender.confirmM4(m4); err != nil {
		t.Fatalf("confirm m4: %v", err)
	}
	if !receiver.complete() {
		t.Fatal("receiver did not mark FPSAP exchange complete")
	}

	rawKey := [16]byte{0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87, 0x98, 0xa9, 0xba, 0xcb, 0xdc, 0xed, 0xfe, 0x0f}
	ekey, err := sender.wrapKey(rawKey, bytes.NewReader(bytes.Repeat([]byte{0xa5}, 16)))
	if err != nil {
		t.Fatalf("wrap FairPlay key: %v", err)
	}
	unwrapped, err := receiver.unwrapKey(ekey[:])
	if err != nil {
		t.Fatalf("unwrap FairPlay key: %v", err)
	}
	if unwrapped != rawKey {
		t.Fatalf("unwrapped key = %x, want %x", unwrapped, rawKey)
	}

	for _, index := range []int{16, 36, 56} {
		tampered := ekey
		tampered[index] ^= 1
		if _, err := receiver.unwrapKey(tampered[:]); err == nil {
			t.Fatalf("receiver accepted ekey tampered at byte %d", index)
		}
	}
}

func TestReceiverFPSAPRejectsMalformedAndOutOfOrderMessages(t *testing.T) {
	newState := func(t *testing.T) *receiverFPSAPState {
		t.Helper()
		state, err := newReceiverFPSAPState(bytes.NewReader(bytes.Repeat([]byte{0x44}, 126)))
		if err != nil {
			t.Fatalf("create receiver FPSAP state: %v", err)
		}
		return state
	}

	if _, err := newState(t).exchange([]byte("not an FPSAP record")); err == nil {
		t.Fatal("receiver accepted malformed m1")
	}

	sender, err := newFPSAPSession(bytes.NewReader(bytes.Repeat([]byte{0x55}, 126)))
	if err != nil {
		t.Fatal(err)
	}
	receiver := newState(t)
	m2, err := receiver.exchange(sender.message1())
	if err != nil {
		t.Fatal(err)
	}
	m3, err := sender.exchangeM3(m2)
	if err != nil {
		t.Fatal(err)
	}
	m3[len(m3)-1] ^= 1
	if _, err := receiver.exchange(m3); err == nil {
		t.Fatal("receiver accepted an invalid m3 confirmation")
	}

	receiver = newState(t)
	m2, err = receiver.exchange(sender.message1())
	if err != nil {
		t.Fatal(err)
	}
	m3, err = sender.exchangeM3(m2)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := receiver.exchange(m3); err != nil {
		t.Fatal(err)
	}
	if _, err := receiver.exchange(m3); err == nil {
		t.Fatal("receiver accepted a second m3 after completion")
	}
}
