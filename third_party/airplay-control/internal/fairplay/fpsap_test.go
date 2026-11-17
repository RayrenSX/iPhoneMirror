// Derived from github.com/omarroth/doubletake, internal/airplay/fpsap_test.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Unmodified apart from the package name.

package fairplay

import (
	"bytes"
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"testing"
)

func TestFPSAPTableData(t *testing.T) {
	const want = "28d0986abebe30458348dfa2957aa1d52d6f3ad5a9468c5d8a9c4139b7ca2b43"
	hash := sha256.New()
	written := 0
	write := func(data []byte) {
		_, _ = hash.Write(data)
		written += len(data)
	}
	write(fpsapFirstInputMask[:])
	for _, tables := range []*fpsapNetworkTables{&fpsapFirstTables, &fpsapSecondTables} {
		for _, round := range tables.roundSubstitution {
			for _, ref := range round {
				var expanded [256]byte
				for value := range expanded {
					expanded[value] = ref.substitute(byte(value))
				}
				write(expanded[:])
			}
		}
		for _, inputTable := range tables.mixColumns {
			var expanded [256 * 4]byte
			for value := 0; value < 256; value++ {
				for outputByte, ref := range inputTable {
					expanded[value*4+outputByte] = ref.mix(byte(value))
				}
			}
			write(expanded[:])
		}
		for _, ref := range tables.finalSubstitution {
			var expanded [256]byte
			for value := range expanded {
				expanded[value] = ref.substitute(byte(value))
			}
			write(expanded[:])
		}
	}
	if written != 90128 {
		t.Fatalf("expanded table length = %d, want 90128", written)
	}
	if gotHex := hex.EncodeToString(hash.Sum(nil)); gotHex != want {
		t.Fatalf("table checksum = %s, want %s", gotHex, want)
	}
}

func TestFPSAPExchangeGoldenVectors(t *testing.T) {
	capturedM2 := mustDecodeHexFP("46504c59030102000000008202034a114c26b77d4e2eec2c8f89fdb653b5b32d3576bc176816d110a14c3f53c08dbb936183bfdfe0a4f3c12e85216003b46f738c40c54da6c436d29d1b342d63c7b314309ae79a33bb1787709ef077cbfe4190117a3423e270fd1a2eac44da1a7934f59dc681d1b70783f228c4d077c2d495f5285c3bf8df586fc2ebfe17fb5b65")
	tests := []struct {
		name    string
		payload [128]byte
		want    string
	}{
		{name: "all-zeros", want: "6f627565f3e77f5b5ede91beee7baf92e4241e0b"},
		{name: "all-ff", payload: filledFPSAPPayload(0xff), want: "dc2cc74f2ed55484f59f95b96082f0f5c017dd17"},
		{name: "captured-m2", payload: func() (p [128]byte) { copy(p[:], capturedM2[14:142]); return }(), want: "4b911e48af23d8406368aeafbb61bfcd569e3e55"},
		{name: "42-at-0", payload: sparseFPSAPPayload(0), want: "9bfb9556b8659c2ac94b7ef9e587d71e159ea624"},
		{name: "42-at-63", payload: sparseFPSAPPayload(63), want: "150d9fa4eb456e73ba48de5779c5c996b16b3b23"},
		{name: "42-at-64", payload: sparseFPSAPPayload(64), want: "a167db30424ff8890d085c0f1c92b2c5cc06fc45"},
		{name: "42-at-127", payload: sparseFPSAPPayload(127), want: "d246ec5e7adc8118994b8df77146529486ac7caf"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := fpsapExchangeForSAP(fpsapReferenceLocalSAP(), mustDecryptFPSAPBody(t, 3, tc.payload))
			if gotHex := hex.EncodeToString(got[:]); gotHex != tc.want {
				t.Fatalf("hash = %s, want %s", gotHex, tc.want)
			}
		})
	}
}

func TestFPSAPDescriptor(t *testing.T) {
	tests := []struct {
		name    string
		payload [128]byte
		want    string
	}{
		{name: "zero", want: "7e38958ffe4ed433743919fe7eb16376afa4eb9e"},
		{name: "one-at-zero", payload: func() (p [128]byte) { p[0] = 1; return }(), want: "ea46797d726c6a9be43ffa72385ff97ce1c54f1b"},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			got := fpsapDescriptorForSAP(fpsapReferenceLocalSAP(), mustDecryptFPSAPBody(t, 3, tc.payload))
			if gotHex := hex.EncodeToString(got[:]); gotHex != tc.want {
				t.Fatalf("descriptor = %s, want %s", gotHex, tc.want)
			}
		})
	}
}

func TestFPSAPSessionExchange(t *testing.T) {
	m2 := make([]byte, 142)
	copy(m2, []byte("FPLY"))
	copy(m2[4:8], []byte{3, 1, 2, 0})
	binary.BigEndian.PutUint32(m2[8:12], 130)
	m2[12] = 2
	m2[13] = 3
	entropy := make([]byte, 126)
	for i := range entropy {
		entropy[i] = byte(i + 1)
	}
	session, err := newFPSAPSession(bytes.NewReader(entropy))
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(session.message1()); got != "46504c590301010000000004020003bb" {
		t.Fatalf("m1 = %s", got)
	}
	m3, err := session.exchangeM3(m2)
	if err != nil {
		t.Fatal(err)
	}
	if len(m3) != 164 {
		t.Fatalf("m3 length = %d, want 164", len(m3))
	}
	if string(m3[:4]) != "FPLY" {
		t.Fatalf("m3 header = %q", m3[:4])
	}
	var gotSAP [128]byte
	decryptFairPlayMessage(m3, gotSAP[:])
	var wantSAP [128]byte
	wantSAP[1] = 1
	copy(wantSAP[2:], entropy)
	if gotSAP != wantSAP {
		t.Fatalf("m3 SAP = %x, want %x", gotSAP, wantSAP)
	}

	var m2Ciphertext [128]byte
	copy(m2Ciphertext[:], m2[14:])
	wantReceiverSAP := mustDecryptFPSAPBody(t, 3, m2Ciphertext)
	if session.remoteSAP != wantReceiverSAP {
		t.Fatalf("stored receiver SAP = %x, want %x", session.remoteSAP, wantReceiverSAP)
	}
	wantTail := fpsapExchangeForSAP(wantSAP, wantReceiverSAP)
	if !bytes.Equal(m3[144:], wantTail[:]) {
		t.Fatalf("m3 tail = %x, want %x", m3[144:], wantTail)
	}
	rawKey := [16]byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15}
	ekey, err := session.wrapKey(rawKey, bytes.NewReader(bytes.Repeat([]byte{0x5a}, 16)))
	if err != nil {
		t.Fatal(err)
	}
	if got := unwrapFairPlayKeyForTest(wantReceiverSAP, m3, ekey[:]); got != rawKey {
		t.Fatalf("session-wrapped key = %x, want %x", got, rawKey)
	}
	if _, err := session.exchangeM3(make([]byte, 141)); err == nil {
		t.Fatal("short m2 was accepted")
	}

	for _, mode := range []byte{4, 0xff} {
		badMode := append([]byte(nil), m2...)
		badMode[13] = mode
		if _, err := session.exchangeM3(badMode); err == nil {
			t.Fatalf("m2 with unsupported mode %d was accepted", mode)
		}
	}
	if _, err := newFPSAPSession(bytes.NewReader(entropy[:125])); err == nil {
		t.Fatal("short entropy source was accepted")
	}

	otherEntropy := bytes.Repeat([]byte{0xa5}, 126)
	otherSession, err := newFPSAPSession(bytes.NewReader(otherEntropy))
	if err != nil {
		t.Fatal(err)
	}
	otherM3, err := otherSession.exchangeM3(m2)
	if err != nil {
		t.Fatal(err)
	}
	if bytes.Equal(m3[16:144], otherM3[16:144]) {
		t.Fatal("distinct FPSAP sessions reused the same m3 body")
	}
}

func TestFPSAPSessionUsesReceiverSelectedMode(t *testing.T) {
	var receiverSAP [128]byte
	receiverSAP[1] = 1
	for i := 2; i < len(receiverSAP); i++ {
		receiverSAP[i] = byte(i*7 + 3)
	}
	entropy := make([]byte, 126)
	for i := range entropy {
		entropy[i] = byte(i + 1)
	}
	var wantLocalSAP [128]byte
	wantLocalSAP[1] = 1
	copy(wantLocalSAP[2:], entropy)

	tests := []struct {
		name string
		mode byte
	}{
		{name: "mode-0", mode: 0},
		{name: "mode-1", mode: 1},
		{name: "mode-2", mode: 2},
		{name: "mode-3", mode: 3},
	}
	for _, tc := range tests {
		t.Run(tc.name, func(t *testing.T) {
			m2 := newFPSAPRecord(2, 130)
			m2[12] = 2
			m2[13] = tc.mode
			if err := encryptFairPlayMessage(tc.mode, receiverSAP[:], m2[14:142]); err != nil {
				t.Fatal(err)
			}

			session, err := newFPSAPSession(bytes.NewReader(entropy))
			if err != nil {
				t.Fatal(err)
			}
			m3, err := session.exchangeM3(m2)
			if err != nil {
				t.Fatal(err)
			}
			if got := m3[12]; got != tc.mode {
				t.Fatalf("m3 mode = %d, want receiver-selected mode %d", got, tc.mode)
			}

			var gotLocalSAP [128]byte
			decryptFairPlayMessage(m3, gotLocalSAP[:])
			if gotLocalSAP != wantLocalSAP {
				t.Fatalf("m3 SAP = %x, want %x", gotLocalSAP, wantLocalSAP)
			}
			if session.remoteSAP != receiverSAP {
				t.Fatalf("stored receiver SAP = %x, want %x", session.remoteSAP, receiverSAP)
			}
			wantTail := fpsapExchangeForSAP(wantLocalSAP, receiverSAP)
			if !bytes.Equal(m3[144:], wantTail[:]) {
				t.Fatalf("m3 tail = %x, want %x", m3[144:], wantTail)
			}
		})
	}
}

func TestValidateFPSAPM4(t *testing.T) {
	m3 := make([]byte, 164)
	for i := 144; i < len(m3); i++ {
		m3[i] = byte(i)
	}
	m4 := make([]byte, 32)
	copy(m4, []byte("FPLY"))
	copy(m4[4:8], []byte{3, 1, 4, 0})
	binary.BigEndian.PutUint32(m4[8:12], 20)
	copy(m4[12:], m3[144:])

	if err := validateFPSAPM4(m4, m3); err != nil {
		t.Fatalf("valid m4 rejected: %v", err)
	}
	session := &fpsapSession{hasM3: true}
	copy(session.m3[:], m3)
	if err := session.confirmM4(m4); err != nil {
		t.Fatalf("session rejected valid m4: %v", err)
	}
	m4[31] ^= 1
	if err := validateFPSAPM4(m4, m3); err == nil {
		t.Fatal("m4 with a mismatched confirmation was accepted")
	}
}

func TestFPSAPSessionRequiresM3(t *testing.T) {
	session, err := newFPSAPSession(bytes.NewReader(make([]byte, 126)))
	if err != nil {
		t.Fatal(err)
	}
	if err := session.confirmM4(make([]byte, 32)); err == nil {
		t.Fatal("m4 was confirmed before m3")
	}
	if _, err := session.wrapKey([16]byte{}, bytes.NewReader(make([]byte, 36))); err == nil {
		t.Fatal("key was wrapped before m3")
	}
}

func filledFPSAPPayload(value byte) (payload [128]byte) {
	for i := range payload {
		payload[i] = value
	}
	return payload
}

func sparseFPSAPPayload(index int) (payload [128]byte) {
	payload[index] = 0x42
	return payload
}

func mustDecryptFPSAPBody(t *testing.T, mode byte, payload [128]byte) [128]byte {
	t.Helper()
	decrypted, err := decryptFPSAPBody(mode, payload)
	if err != nil {
		t.Fatal(err)
	}
	return decrypted
}

// This SAP is a test vector recovered from the old post-m1 emulator snapshot.
// Keeping it in tests verifies the generalized exchange without embedding a
// captured sender session in production.
func fpsapReferenceLocalSAP() (sap [128]byte) {
	decoded := mustDecodeHexFP(
		"0001e4e3dd688293e6fa66b95ba41768e587c65f750218ff1be21543d573cefb" +
			"087bd36e0c6363c3c8242f4abcfa6d660b801032015405eb4ab04dda7aeff38f" +
			"fb36f4cfa48f0b5d92ae363f68b45925bbe6413ab6bdc4968f548d21e67d20f" +
			"1912b6820e53f1013cde29df7350a9b9fa7c51320aea62d2949786c87642e34ba")
	copy(sap[:], decoded)
	return sap
}
