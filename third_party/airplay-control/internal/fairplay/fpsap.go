// Derived from github.com/omarroth/doubletake, internal/airplay/fpsap.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Modified for go-atv.

package fairplay

import (
	"bytes"
	"encoding/binary"
	"fmt"
	"io"
)

// This m1 capability byte is a bit mask, not the message mode. Apple's sender
// derives it as 3 with any unavailable capabilities cleared; this
// implementation supports the full set. The receiver selects mode 0..3 in m2.
const fpsapM1Capabilities = byte(3)

var fpsapM1Payload = [...]byte{0x02, 0x00, fpsapM1Capabilities, 0xbb}

var fpsapM3Label = [...]byte{0x8f, 0x1a, 0x9c}

var fpsapDescriptorPrefix = [...]byte{
	0xa0, 0x44, 0x9c, 0x4d, 0x09, 0xe4, 0xbd, 0x7f, 0x6e,
	0xc5, 0xd0, 0xcc, 0x35, 0x9d, 0xa7, 0x46, 0x7a,
}

var fpsapDescriptorSuffix = [...]byte{
	0x97, 0xb5, 0x0f, 0x84, 0xe2, 0x15, 0x5a, 0x9c, 0x24,
	0x99, 0x1c, 0xf4, 0x3a, 0x09, 0x63, 0x55, 0x47,
}

var fpsapFixedBlock = [16]byte{
	0xaf, 0xc2, 0x2b, 0xa0, 0x49, 0xef, 0xfc, 0xfb,
	0xfe, 0x67, 0xac, 0x5e, 0xbe, 0xf6, 0xfb, 0xcb,
}

var fpsapFirstPositionMap = [...]uint8{
	0, 5, 10, 15, 4, 9, 14, 3, 8, 13, 2, 7, 12, 1, 6, 11,
}

var fpsapSecondPositionMap = [...]uint8{
	0, 13, 10, 7, 4, 1, 14, 11, 8, 5, 2, 15, 12, 9, 6, 3,
}

type fpsapSession struct {
	localSAP  [128]byte
	remoteSAP [128]byte
	m3        [164]byte
	hasM3     bool
}

// newFPSAPSession models the stateful lifecycle visible in Apple's
// sender: one opaque context is created before m1 and reused for m3 and key
// wrapping. The native implementation fills the local SAP from an
// arc4random-seeded internal generator, then overwrites its first two bytes with
// 00 01. Using the caller's cryptographic entropy source for the remaining 126
// opaque bytes preserves those protocol semantics without porting its PRNG.
func newFPSAPSession(entropy io.Reader) (*fpsapSession, error) {
	session := &fpsapSession{}
	session.localSAP[1] = 1
	if _, err := io.ReadFull(entropy, session.localSAP[2:]); err != nil {
		return nil, fmt.Errorf("initialize local SAP: %w", err)
	}
	return session, nil
}

func (session *fpsapSession) message1() []byte {
	m1 := newFPSAPRecord(1, len(fpsapM1Payload))
	copy(m1[12:], fpsapM1Payload[:])
	return m1
}

func decryptFPSAPBody(mode byte, payload [128]byte) (out [128]byte, err error) {
	if int(mode) >= len(fairplayMessageIV) {
		return out, fmt.Errorf("unsupported FairPlay mode %d", mode)
	}
	message := make([]byte, 144)
	message[12] = mode
	copy(message[16:], payload[:])
	decryptFairPlayMessage(message, out[:])
	return out, nil
}

// fpsapDescriptorForSAP derives the white-box seed from both halves of an
// exchange, without assuming a captured sender or receiver SAP value.
func fpsapDescriptorForSAP(m3SAP, m2SAP [128]byte) (out [20]byte) {
	var padded [320]byte
	offset := copy(padded[:], fpsapDescriptorPrefix[:])
	offset += copy(padded[offset:], m3SAP[:])
	offset += copy(padded[offset:], m2SAP[:])
	offset += copy(padded[offset:], fpsapDescriptorSuffix[:])
	padded[offset] = 0x80
	binary.LittleEndian.PutUint64(padded[len(padded)-8:], uint64(offset)*8)

	state := fairplayWordsFromLittleEndian(fairplayInitialSessionKey)
	var firstFinal [4]uint32
	for blockOffset := 0; blockOffset < len(padded); blockOffset += 64 {
		block := padded[blockOffset : blockOffset+64]
		add := fairplaySAPHash(block)
		for i := range state {
			state[i] += binary.LittleEndian.Uint32(add[i*4:])
		}
		state = fairplayMD5Compress(state, block, fpsapCycleMutation)
		if blockOffset == len(padded)-64 {
			firstFinal = state
			state = fairplayMD5Compress(state, block, fpsapCycleMutation)
		}
	}

	binary.BigEndian.PutUint32(out[:4], firstFinal[0])
	tail := fairplayWordsBigEndian(state)
	copy(out[4:], tail[:])
	return out
}

func fpsapMasks(seed [20]byte) (masks [9][16]byte) {
	state := [4]uint32{0x1d4a4587, 0x92f39fcc, 0x1d87d836, 0xcdc86697}
	suffix := [...]byte{
		0x57, 0xd8, 0xee, 0xcb, 0xde, 0xfb, 0xcf, 0x59,
		0x1c, 0x27, 0xa2, 0xcf, 0xbe, 0xb0, 0x89,
	}
	for i := range masks {
		var block [64]byte
		copy(block[:20], seed[:])
		block[20] = byte(i)
		copy(block[21:36], suffix[:])
		block[36] = 0x80
		binary.LittleEndian.PutUint32(block[56:60], 0x320)
		digest := fairplayWordsBigEndian(fairplayMD5Compress(state, block[:], fpsapSwapMutation))
		masks[i] = digest
	}
	return masks
}

func fpsapDigest32(left, right [16]byte) [16]byte {
	var block [64]byte
	copy(block[:16], left[:])
	copy(block[16:32], right[:])
	block[32] = 0x80
	binary.LittleEndian.PutUint32(block[56:60], 0x100)
	state := [4]uint32{0xb9f3dcdc, 0xfbdc740b, 0x60f77f86, 0x51907216}
	return fairplayWordsBigEndian(fairplayMD5Compress(state, block[:], fpsapSwapMutation))
}

func fpsapFirstNetwork(masks [9][16]byte) [16]byte {
	state := fpsapFixedBlock
	for i := range state {
		state[i] ^= fpsapFirstInputMask[i]
	}
	for bank := 0; bank < 9; bank++ {
		var substituted [16]byte
		for output, input := range fpsapFirstPositionMap {
			substituted[output] = fpsapFirstTables.roundSubstitution[bank][input].substitute(state[input])
		}
		fpsapMix(&fpsapFirstTables, &state, substituted)
		for i := range state {
			state[i] ^= masks[bank][i]
		}
	}
	var out [16]byte
	for output, input := range fpsapFirstPositionMap {
		out[output] = fpsapFirstTables.finalSubstitution[input].substitute(state[input])
	}
	return out
}

func fpsapSecondNetwork(state [16]byte, masks [9][16]byte) [16]byte {
	for bank := 8; bank >= 0; bank-- {
		var substituted [16]byte
		for output, input := range fpsapSecondPositionMap {
			substituted[output] = fpsapSecondTables.roundSubstitution[bank][output].substitute(state[input]) ^ masks[bank][output]
		}
		fpsapMix(&fpsapSecondTables, &state, substituted)
	}
	var out [16]byte
	for output, input := range fpsapSecondPositionMap {
		out[output] = fpsapSecondTables.finalSubstitution[output].substitute(state[input]) ^ fpsapSecondOutputMask[output]
	}
	return out
}

func fpsapMix(tables *fpsapNetworkTables, state *[16]byte, substituted [16]byte) {
	for word := 0; word < 4; word++ {
		offset := word * 4
		for outputByte := 0; outputByte < 4; outputByte++ {
			var mixed byte
			for inputByte := 0; inputByte < 4; inputByte++ {
				mixed ^=
					tables.mixColumns[inputByte][outputByte].mix(substituted[offset+inputByte])
			}
			state[offset+outputByte] = mixed
		}
	}
}

func fpsapExchangeForSAP(m3SAP, m2SAP [128]byte) [20]byte {
	return fpsapExchangeSeed(fpsapDescriptorForSAP(m3SAP, m2SAP))
}

func fpsapExchangeSeed(seed [20]byte) [20]byte {
	masks := fpsapMasks(seed)
	intermediate := fpsapFirstNetwork(masks)
	left := fpsapDigest32(intermediate, fpsapFixedBlock)
	whiteboxOutput := fpsapSecondNetwork(left, masks)
	digest := fpsapDigest32(left, whiteboxOutput)

	var out [20]byte
	copy(out[:4], whiteboxOutput[:4])
	copy(out[4:], digest[:])
	return out
}

func (session *fpsapSession) exchangeM3(m2 []byte) ([]byte, error) {
	if err := validateFPSAPRecord(m2, 2, 130); err != nil {
		return nil, fmt.Errorf("invalid m2: %w", err)
	}
	if m2[12] != 2 {
		return nil, fmt.Errorf("invalid m2 payload marker %d", m2[12])
	}
	mode := m2[13]
	if int(mode) >= len(fairplayMessageIV) {
		return nil, fmt.Errorf("m2 selected unsupported mode %d", mode)
	}

	m3 := newFPSAPRecord(3, 152)
	m3[12] = mode
	copy(m3[13:16], fpsapM3Label[:])
	if err := encryptFairPlayMessage(mode, session.localSAP[:], m3[16:144]); err != nil {
		return nil, fmt.Errorf("encrypt m3 SAP: %w", err)
	}

	var m2Ciphertext [128]byte
	copy(m2Ciphertext[:], m2[14:142])
	m2SAP, err := decryptFPSAPBody(mode, m2Ciphertext)
	if err != nil {
		return nil, fmt.Errorf("decrypt m2 SAP: %w", err)
	}
	tail := fpsapExchangeForSAP(session.localSAP, m2SAP)
	copy(m3[144:], tail[:])
	session.remoteSAP = m2SAP
	copy(session.m3[:], m3)
	session.hasM3 = true
	return append([]byte(nil), m3...), nil
}

func (session *fpsapSession) confirmM4(m4 []byte) error {
	if !session.hasM3 {
		return fmt.Errorf("m3 has not been generated")
	}
	return validateFPSAPM4(m4, session.m3[:])
}

func (session *fpsapSession) wrapKey(rawKey [16]byte, entropy io.Reader) ([72]byte, error) {
	if !session.hasM3 {
		return [72]byte{}, fmt.Errorf("m3 has not been generated")
	}
	return wrapFairPlayKey(session.remoteSAP, session.m3[:], rawKey, entropy)
}

func validateFPSAPM4(m4, m3 []byte) error {
	if err := validateFPSAPRecord(m4, 4, 20); err != nil {
		return err
	}
	if len(m3) != 164 {
		return fmt.Errorf("invalid m3 length %d", len(m3))
	}
	if !bytes.Equal(m4[12:], m3[144:]) {
		return fmt.Errorf("m4 confirmation does not match m3")
	}
	return nil
}

func validateFPSAPRecord(record []byte, messageType byte, payloadLength int) error {
	wantLength := 12 + payloadLength
	if len(record) != wantLength {
		return fmt.Errorf("length %d, want %d", len(record), wantLength)
	}
	if !bytes.Equal(record[:4], []byte("FPLY")) {
		return fmt.Errorf("invalid magic %x", record[:4])
	}
	if record[4] != 3 || record[5] != 1 || record[6] != messageType || record[7] != 0 {
		return fmt.Errorf("invalid version/type %x", record[4:8])
	}
	if got := int(binary.BigEndian.Uint32(record[8:12])); got != payloadLength {
		return fmt.Errorf("declared payload length %d, want %d", got, payloadLength)
	}
	return nil
}

func newFPSAPRecord(messageType byte, payloadLength int) []byte {
	record := make([]byte, 12+payloadLength)
	copy(record[:4], "FPLY")
	copy(record[4:8], []byte{3, 1, messageType, 0})
	binary.BigEndian.PutUint32(record[8:12], uint32(payloadLength))
	return record
}
