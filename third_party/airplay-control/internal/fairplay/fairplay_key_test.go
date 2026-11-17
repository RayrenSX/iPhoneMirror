// Derived from github.com/omarroth/doubletake, internal/airplay/fairplay_key_test.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Modified for go-atv.

package fairplay

import (
	"bytes"
	"crypto/aes"
	"crypto/hmac"
	"crypto/sha1"
	"encoding/binary"
	"encoding/hex"
	"testing"
)

func TestFairPlayKeyWrapRoundTrip(t *testing.T) {
	for mode := byte(0); mode <= 3; mode++ {
		t.Run(string(rune('0'+mode)), func(t *testing.T) {
			m3 := testFairPlayM3(mode)
			receiverSAP := testFairPlayReceiverSAP(mode)
			rawKey := [16]byte{
				0x10, 0x21, 0x32, 0x43, 0x54, 0x65, 0x76, 0x87,
				0x98, 0xa9, 0xba, 0xcb, 0xdc, 0xed, 0xfe, mode,
			}
			entropy := make([]byte, 16)
			for i := range entropy {
				entropy[i] = byte(i + 1)
			}

			ekey, err := wrapFairPlayKey(receiverSAP, m3, rawKey, bytes.NewReader(entropy))
			if err != nil {
				t.Fatal(err)
			}
			if got := hex.EncodeToString(ekey[:16]); got != "46504c59010201000000003c00000000" {
				t.Fatalf("ekey header = %s", got)
			}
			if got := binary.BigEndian.Uint32(ekey[32:36]); got != 16 {
				t.Fatalf("ekey raw-key length = %d, want 16", got)
			}
			if !bytes.Equal(ekey[16:32], entropy[:16]) {
				t.Fatalf("ekey mask = %x, want %x", ekey[16:32], entropy[:16])
			}

			var senderSAP [128]byte
			decryptFairPlayMessage(m3, senderSAP[:])
			macKey := fpsapDescriptorForSAP(senderSAP, receiverSAP)
			mac := hmac.New(sha1.New, macKey[:])
			_, _ = mac.Write(ekey[:36])
			_, _ = mac.Write(rawKey[:])
			if !hmac.Equal(ekey[36:56], mac.Sum(nil)) {
				t.Fatalf("ekey MAC = %x, want %x", ekey[36:56], mac.Sum(nil))
			}
			if got := unwrapFairPlayKeyForTest(receiverSAP, m3, ekey[:]); got != rawKey {
				t.Fatalf("unwrapped key = %x, want %x", got, rawKey)
			}
		})
	}
}

func TestFairPlayKeyWrapRejectsInvalidInput(t *testing.T) {
	receiverSAP := testFairPlayReceiverSAP(3)
	if _, err := wrapFairPlayKey(receiverSAP, make([]byte, 143), [16]byte{}, bytes.NewReader(make([]byte, 16))); err == nil {
		t.Fatal("short m3 was accepted")
	}

	m3 := testFairPlayM3(3)
	m3[12] = 4
	if _, err := wrapFairPlayKey(receiverSAP, m3, [16]byte{}, bytes.NewReader(make([]byte, 16))); err == nil {
		t.Fatal("unsupported mode was accepted")
	}

	if _, err := wrapFairPlayKey(receiverSAP, testFairPlayM3(3), [16]byte{}, bytes.NewReader(make([]byte, 15))); err == nil {
		t.Fatal("short entropy source was accepted")
	}
}

func TestFairPlayKey25F84MACVector(t *testing.T) {
	// The m2, m3, mask, raw key, derived context key, and tag were captured at
	// Apple's 25F84 _U4HBs boundary. This checks the authenticated prefix only;
	// 25F84 uses a different final key transform from the AirPlay-v3 path.
	m2 := mustDecodeHexFP("46504c5903010200000000820201cf32a25714b2524f8aa0ad7af164e37bcf4424e200047efc0ad67afcd95ded1c2730bb591b962ed63a9c4ded88ba8fc78de64d91ccfd5c7b56da88e31f5cceafc7431995a01665a54e1939d25b94db64b9e45d8d063e1e6af07e9656162b0efa404275ea5a44d9591c7256b9fbe6513898b80227721988571650942ad946688a")
	m3 := mustDecodeHexFP("46504c590301030000000098018f1a9c5b9228300aafe0b41f28b66a62a6cd62bf84eb623273dead10b1f034a8d568126faa133f6ad5ab91acda3839817b4d9530b679fee43ac9e950f6e7aaf1381bd2d3d5198a03bf5648890d19234270a3583e4651893be09c6c75463c42e544fec9abc9f7722a2cc254364365ef91ded76b8c00f9674b08920fb9401e4be6d52a33f2f9ed6fadb672be45c3cde5ad94f3fea5b32ee4")
	rawKeyBytes := mustDecodeHexFP("000102030405060708090a0b0c0d0e0f")
	mask := mustDecodeHexFP("c853e777b9b65e7652d768d97c974f15")
	var rawKey [16]byte
	copy(rawKey[:], rawKeyBytes)
	var receiverSAP [128]byte
	m2Frame := make([]byte, 144)
	m2Frame[12] = m2[13]
	copy(m2Frame[16:], m2[14:])
	decryptFairPlayMessage(m2Frame, receiverSAP[:])
	if receiverSAP != fairPlayReferenceReceiverSAP() {
		t.Fatalf("upstream receiver SAP = %x, want reference SAP", receiverSAP)
	}

	ekey, err := wrapFairPlayKey(receiverSAP, m3, rawKey, bytes.NewReader(mask))
	if err != nil {
		t.Fatal(err)
	}
	if got := hex.EncodeToString(ekey[:56]); got != "46504c59010201000000003c00000000c853e777b9b65e7652d768d97c974f15000000102a53a0008888fe26bfb1e1f825f38f50d6730059" {
		t.Fatalf("authenticated ekey prefix = %s", got)
	}
	var senderSAP [128]byte
	decryptFairPlayMessage(m3, senderSAP[:])
	macKey := fpsapDescriptorForSAP(senderSAP, receiverSAP)
	if got := hex.EncodeToString(macKey[:]); got != "2fd95dc2c23122bc77c57b983a9188c4760db322" {
		t.Fatalf("session MAC key = %s", got)
	}
}

func TestCapturedFairPlayKeyDecrypt(t *testing.T) {
	// This m3/ekey vector and expected output are independently exercised by
	// the C playfair_decrypt reference implementation.
	m3 := mustDecodeHexFP("46504c590301030000000098018f1a9c7d0af257b31f21f5c2d2bc814c032d457835ad0b06250574bbc7ab4a58cca6eead2c911d7f3e1e7ed4c058955dff3d5ceef014387a985bdb34995015e3dfbdacc56047cb926e093b13e9fdb5e1eee317c018bbc87fc5453c7671647da686da3d564875d03f8aea9d60092de06110bc7be0c16f391c369c75344ae47f33acfcf10e63a9b58bfce215e96001c49e4be967c5067f2a")
	ekey := mustDecodeHexFP("46504c59010201000000003c0000000088e4f82c8178c18b4751ac24b27c0c2a00000010c899dc6965c1081de6a9d966e2ba3e34548cdbc651c322db18dc22f58fe154a60aecee18")

	if got := binary.BigEndian.Uint32(ekey[32:36]); got != 16 {
		t.Fatalf("captured ekey raw-key length = %d, want 16", got)
	}
	got := unwrapFairPlayKeyForTest(fairPlayReferenceReceiverSAP(), m3, ekey)
	if gotHex := hex.EncodeToString(got[:]); gotHex != "8e1214398d46d72e7b1b8e32f80c8bf0" {
		t.Fatalf("captured raw key = %s", gotHex)
	}
}

func unwrapFairPlayKeyForTest(receiverSAP [128]byte, m3, ekey []byte) [16]byte {
	aesKey := deriveFairPlayWrappingKey(receiverSAP, m3)
	cipher, err := aes.NewCipher(aesKey[:])
	if err != nil {
		panic(err)
	}
	var key [16]byte
	cipher.Decrypt(key[:], ekey[56:72])
	for i := range key {
		key[i] ^= ekey[16+i]
	}
	return key
}

func testFairPlayM3(mode byte) []byte {
	m3 := make([]byte, 164)
	copy(m3, []byte("FPLY"))
	copy(m3[4:8], []byte{3, 1, 3, 0})
	binary.BigEndian.PutUint32(m3[8:12], 152)
	m3[12] = mode
	for i := 13; i < len(m3); i++ {
		m3[i] = byte(i*7 + 3)
	}
	return m3
}

func testFairPlayReceiverSAP(seed byte) (sap [128]byte) {
	sap[1] = 1
	for i := 2; i < len(sap); i++ {
		sap[i] = byte(i*11) ^ seed
	}
	return sap
}

// This is a receiver SAP from the independent playfair_decrypt reference
// vector. Keeping it in tests avoids pinning production sessions to a capture.
func fairPlayReferenceReceiverSAP() (sap [128]byte) {
	decoded := mustDecodeHexFP(
		"0001cc342a5e5b1a6773c20e21b8224df862481864ef810aae2e3703c8819c23" +
			"539de5f5d749bc5b7a266c496283ce7f03937ae1f616de0c15ff338ccaffb09e" +
			"aabbe40f5d5f558fb97f1731f8f7da60a0ec6579c33ea98312c3b67135a6694f" +
			"f82305d9ba5c615fa254d2b1834583cee42d4426c835a7a5f6c8421c0da3f1c7")
	copy(sap[:], decoded)
	return sap
}
