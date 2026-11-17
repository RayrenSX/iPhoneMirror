// Derived from github.com/omarroth/doubletake, internal/airplay/receiver_fairplay.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Modified for go-atv.

package fairplay

import (
	"bytes"
	"crypto/aes"
	"crypto/hmac"
	"crypto/rand"
	"crypto/sha1"
	"encoding/binary"
	"fmt"
	"io"
)

// receiverFPSAPState is the receiver half of the two-request FairPlay SAP
// exchange. It uses the same clean protocol primitives as the sender while
// keeping the direction and sequencing explicit.
type receiverFPSAPState struct {
	receiverSAP [128]byte
	mode        byte
	phase       byte
	m3          [164]byte
}

func newReceiverFPSAPState(entropy io.Reader) (*receiverFPSAPState, error) {
	if entropy == nil {
		entropy = rand.Reader
	}
	state := &receiverFPSAPState{mode: 3}
	state.receiverSAP[1] = 1
	if _, err := io.ReadFull(entropy, state.receiverSAP[2:]); err != nil {
		return nil, fmt.Errorf("initialize receiver SAP: %w", err)
	}
	return state, nil
}

func (s *receiverFPSAPState) exchange(request []byte) ([]byte, error) {
	if s == nil {
		return nil, fmt.Errorf("FairPlay SAP state is nil")
	}
	switch s.phase {
	case 0:
		return s.exchangeM2(request)
	case 1:
		return s.exchangeM4(request)
	default:
		return nil, fmt.Errorf("FairPlay SAP exchange is already complete")
	}
}

func (s *receiverFPSAPState) exchangeM2(m1 []byte) ([]byte, error) {
	if err := validateFPSAPRecord(m1, 1, len(fpsapM1Payload)); err != nil {
		return nil, fmt.Errorf("invalid m1: %w", err)
	}
	if m1[12] != 2 || m1[13] != 0 || m1[14] > 3 || m1[15] != 0xbb {
		return nil, fmt.Errorf("unsupported m1 capabilities %x", m1[12:])
	}

	// QuickTime advertises 1 in open mode; select a mode within its capabilities.
	s.mode = m1[14]
	m2 := newFPSAPRecord(2, 130)
	m2[12] = 2
	m2[13] = s.mode
	if err := encryptFairPlayMessage(s.mode, s.receiverSAP[:], m2[14:142]); err != nil {
		return nil, fmt.Errorf("encrypt m2 SAP: %w", err)
	}
	s.phase = 1
	return m2, nil
}

func (s *receiverFPSAPState) exchangeM4(m3 []byte) ([]byte, error) {
	if err := validateFPSAPRecord(m3, 3, 152); err != nil {
		return nil, fmt.Errorf("invalid m3: %w", err)
	}
	if m3[12] != s.mode {
		return nil, fmt.Errorf("m3 mode %d does not match selected mode %d", m3[12], s.mode)
	}
	if !bytes.Equal(m3[13:16], fpsapM3Label[:]) {
		return nil, fmt.Errorf("invalid m3 label %x", m3[13:16])
	}

	var senderSAP [128]byte
	decryptFairPlayMessage(m3, senderSAP[:])
	wantConfirmation := fpsapExchangeForSAP(senderSAP, s.receiverSAP)
	if !bytes.Equal(m3[144:], wantConfirmation[:]) {
		return nil, fmt.Errorf("m3 exchange confirmation is invalid")
	}

	copy(s.m3[:], m3)
	s.phase = 2
	m4 := newFPSAPRecord(4, 20)
	copy(m4[12:], m3[144:])
	return m4, nil
}

func (s *receiverFPSAPState) complete() bool {
	return s != nil && s.phase == 2
}

// unwrapKey authenticates and unwraps the FairPlay ekey sent at the SETUP
// root. The HMAC binds both the record prefix and recovered raw key to the SAP
// exchange, so an unauthenticated key is never handed to the media decryptor.
func (s *receiverFPSAPState) unwrapKey(ekey []byte) ([16]byte, error) {
	var rawKey [16]byte
	if !s.complete() {
		return rawKey, fmt.Errorf("FairPlay SAP exchange is not complete")
	}
	if len(ekey) != 72 {
		return rawKey, fmt.Errorf("FairPlay ekey length is %d, want 72", len(ekey))
	}
	wantHeader := []byte{
		'F', 'P', 'L', 'Y', 0x01, 0x02, 0x01, 0x00,
		0x00, 0x00, 0x00, 0x3c, 0x00, 0x00, 0x00, 0x00,
	}
	if !bytes.Equal(ekey[:16], wantHeader) {
		return rawKey, fmt.Errorf("invalid FairPlay ekey header %x", ekey[:16])
	}
	if keyLength := binary.BigEndian.Uint32(ekey[32:36]); keyLength != uint32(len(rawKey)) {
		return rawKey, fmt.Errorf("FairPlay ekey declares key length %d, want %d", keyLength, len(rawKey))
	}

	wrappingKey := deriveFairPlayWrappingKey(s.receiverSAP, s.m3[:])
	block, err := aes.NewCipher(wrappingKey[:])
	if err != nil {
		return rawKey, fmt.Errorf("create FairPlay unwrapping cipher: %w", err)
	}
	block.Decrypt(rawKey[:], ekey[56:72])
	for index := range rawKey {
		rawKey[index] ^= ekey[16+index]
	}

	var senderSAP [128]byte
	decryptFairPlayMessage(s.m3[:], senderSAP[:])
	macKey := fpsapDescriptorForSAP(senderSAP, s.receiverSAP)
	mac := hmac.New(sha1.New, macKey[:])
	_, _ = mac.Write(ekey[:36])
	_, _ = mac.Write(rawKey[:])
	if !hmac.Equal(ekey[36:56], mac.Sum(nil)) {
		return [16]byte{}, fmt.Errorf("FairPlay ekey authentication failed")
	}
	return rawKey, nil
}
