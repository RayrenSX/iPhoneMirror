// Derived from github.com/omarroth/doubletake, internal/airplay/fairplay_crypto.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Modified for go-atv.

package fairplay

import (
	"crypto/aes"
	"crypto/hmac"
	"crypto/sha1"
	"encoding/binary"
	"fmt"
	"io"
)

var fairplayInitialSessionKey = [16]byte{
	0xdc, 0xdc, 0xf3, 0xb9, 0x0b, 0x74, 0xdc, 0xfb,
	0x86, 0x7f, 0xf7, 0x60, 0x16, 0x72, 0x90, 0x51,
}

var fairplayKDFPrefix = [17]byte{
	0xfa, 0x9c, 0xad, 0x4d, 0x4b, 0x68, 0x26, 0x8c,
	0x7f, 0xf3, 0x88, 0x99, 0xde, 0x92, 0x2e, 0x95, 0x1e,
}

var fairplayKDFSuffix = [17]byte{
	0xec, 0x4e, 0x27, 0x5e, 0xfd, 0xf2, 0xe8, 0x30,
	0x97, 0xae, 0x70, 0xfb, 0xe0, 0x00, 0x3f, 0x1c, 0x39,
}

func deriveFairPlayWrappingKey(receiverSAP [128]byte, message []byte) [16]byte {
	var decrypted [128]byte
	decryptFairPlayMessage(message, decrypted[:])

	// The KDF input is a 290-byte protocol record followed by ordinary MD5
	// padding. The compression itself is FairPlay's modified MD5/SAP-hash
	// combination, not a standard MD5 digest.
	var material [320]byte
	offset := copy(material[:], fairplayKDFPrefix[:])
	offset += copy(material[offset:], decrypted[:])
	offset += copy(material[offset:], receiverSAP[:])
	offset += copy(material[offset:], fairplayKDFSuffix[:])
	material[offset] = 0x80
	binary.LittleEndian.PutUint64(material[len(material)-8:], uint64(offset)*8)

	state := fairplayWordsFromLittleEndian(fairplayInitialSessionKey)
	for offset := 0; offset < len(material); offset += 64 {
		block := material[offset : offset+64]
		modified := fairplayMD5Compress(state, block, fairplayKDFMutation)
		hashed := fairplaySAPHash(block)
		for word := range state {
			state[word] = modified[word] + binary.LittleEndian.Uint32(hashed[word*4:])
		}
	}
	return fairplayWordsBigEndian(state)
}

// wrapFairPlayKey emits the 72-byte AirPlay v3 record produced by Apple's
// FairPlay sender:
//
//	[0:16]  FPLY encrypted-key header
//	[16:32] per-key random mask
//	[32:36] big-endian raw-key length (16)
//	[36:56] HMAC-SHA1(session MAC key, record[0:36] || raw key)
//	[56:72] AES-wrapped (raw key XOR mask)
//
// Both session keys depend on the receiver's decrypted m2 SAP. Reusing a
// captured receiver SAP makes the record self-consistent only for that capture.
// The native sender obtains the mask from its session PRNG; accepting an entropy
// source here preserves the wire semantics without reproducing that PRNG.
func wrapFairPlayKey(receiverSAP [128]byte, m3 []byte, rawKey [16]byte, entropy io.Reader) ([72]byte, error) {
	var ekey [72]byte
	if err := validateFPSAPRecord(m3, 3, 152); err != nil {
		return ekey, fmt.Errorf("invalid m3: %w", err)
	}
	if mode := m3[12]; int(mode) >= len(fairplayMessageIV) {
		return ekey, fmt.Errorf("unsupported FairPlay mode %d", mode)
	}

	copy(ekey[:], []byte{
		'F', 'P', 'L', 'Y', 0x01, 0x02, 0x01, 0x00,
		0x00, 0x00, 0x00, 0x3c, 0x00, 0x00, 0x00, 0x00,
	})
	if _, err := io.ReadFull(entropy, ekey[16:32]); err != nil {
		return [72]byte{}, fmt.Errorf("generate FairPlay key mask: %w", err)
	}
	binary.BigEndian.PutUint32(ekey[32:36], uint32(len(rawKey)))

	wrappingKey := deriveFairPlayWrappingKey(receiverSAP, m3)
	cipher, err := aes.NewCipher(wrappingKey[:])
	if err != nil {
		return [72]byte{}, fmt.Errorf("create FairPlay wrapping cipher: %w", err)
	}
	var masked [16]byte
	for i := range masked {
		masked[i] = rawKey[i] ^ ekey[16+i]
	}
	cipher.Encrypt(ekey[56:72], masked[:])

	var senderSAP [128]byte
	decryptFairPlayMessage(m3, senderSAP[:])
	macKey := fpsapDescriptorForSAP(senderSAP, receiverSAP)
	mac := hmac.New(sha1.New, macKey[:])
	_, _ = mac.Write(ekey[:36])
	_, _ = mac.Write(rawKey[:])
	copy(ekey[36:56], mac.Sum(nil))
	return ekey, nil
}
