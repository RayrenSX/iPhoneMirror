// Derived from github.com/omarroth/doubletake, internal/airplay/fairplay_vectors_test.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Unmodified apart from the package name.

package fairplay

import (
	"crypto/sha256"
	"encoding/binary"
	"encoding/hex"
	"testing"
)

func requireFairPlayHex(t *testing.T, name string, got []byte, want string) {
	t.Helper()
	if gotHex := hex.EncodeToString(got); gotHex != want {
		t.Fatalf("%s = %s, want %s", name, gotHex, want)
	}
}

func TestFairPlayPrimitiveVectors(t *testing.T) {
	var block [64]byte
	for i := range block {
		block[i] = byte(i*3 + 1)
	}
	key := [16]byte{0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15}

	var modified [16]byte
	words := fairplayMD5Compress(fairplayWordsFromLittleEndian(key), block[:], fairplayKDFMutation)
	for i, word := range words {
		binary.LittleEndian.PutUint32(modified[i*4:], word)
	}
	requireFairPlayHex(t, "modified MD5", modified[:], "f6f728cb5a4397b675664f9291b859aa")

	hashed := fairplaySAPHash(block[:])
	requireFairPlayHex(t, "SAP hash", hashed[:], "75498a4e218773030e9cdf04f0c49367")

}

func TestFairPlaySAPHashCorpus(t *testing.T) {
	// This aggregate is generated independently by the upstream C reference
	// implementation and exercises inputs the original single vector missed.
	corpusHash := sha256.New()
	var state uint64 = 0x6a09e667f3bcc909
	for range 64 {
		var block [64]byte
		for i := range block {
			state ^= state << 13
			state ^= state >> 7
			state ^= state << 17
			block[i] = byte(state)
		}
		digest := fairplaySAPHash(block[:])
		_, _ = corpusHash.Write(digest[:])
	}
	requireFairPlayHex(t, "SAP hash corpus", corpusHash.Sum(nil), "36ad2a7920076af59452d9f0c91e3b7d1aebc53f9143bd6819e39119d4535c92")
}

func TestFairPlayMessageVectors(t *testing.T) {
	tests := []struct {
		mode      byte
		decrypted string
		aesKey    string
	}{
		{0, "b66a3295ffa6b56e02ed1b3d67fef74b90fe148570de65e6773669126a4905d8405644cae0b2f5ed6109c099c7aea7398dac8d623fbd69b87242b374d98f89502bb5a63e29c46a8ed0e98466966191ec1e6c8675087fde21337db1c8fab4c21db824026335f6fc37e2e5b6f53357d06994bd383d6029a0aff654fb1521bcdde4", "f7dd1ccb9e745f7951a6e325d73a1f5f"},
		{1, "0f95c6ddc8987eda18577da2db074e7c04715af8b3914a73be1b3d6c111953017ee0a39dfcab3e0d57f2f9fbd59c5e18101788c2ab8e3cbb403bcb48b53f3e5bf74f949e79fa5ca679df4bfcb33a69b1442675d03f948fe5bd0c5ffb64b73a5ab58f46d6baae097b599624147c2487991163ecffc4d966240f9526346a10fdb0", "b44ad891396f097aa309bc132f5b8889"},
		{2, "40f18751b44d733e0aa0416401a7d3f40375fad3ce56900602578bca14660909820e6ef3a5e943cafef5370f72c52177d9b82278b414811201a3d99202bedcca26a4d1ad08bc2669f4bae6ca54b8a120d0425edb6082f51f5aecdb547bfdb319099c9ea2729ae6a1c4480827ce9991e273843cf1c7d74ebbebc2657659bcea9f", "d38cd8efecdb20f333273c4312d9b236"},
		{3, "70a3c30edf0e1dfa1785ce4336ed547062672a47f714a0c1f89a83d95691103dfe5cf653d4cb8299793faf33fd0d4482ef5333b41ab094a90e1baf996bcf4989783f6918397fbacddaf00a2b97556dd8099841578bc5eb1444912b47298eaf356fdd6701bb3f64e725a80eb4c6f3556195de35c93e7cc703bdd24351468e9847", "769e2fe4c5ad7fbe6fd6772d00f529f4"},
	}

	for _, tc := range tests {
		message := make([]byte, 164)
		message[12] = tc.mode
		for i := 16; i < 144; i++ {
			message[i] = byte(i*5 + 7)
		}
		var decrypted [128]byte
		decryptFairPlayMessage(message, decrypted[:])
		requireFairPlayHex(t, "decrypted message", decrypted[:], tc.decrypted)
		var encrypted [128]byte
		if err := encryptFairPlayMessage(tc.mode, decrypted[:], encrypted[:]); err != nil {
			t.Fatal(err)
		}
		requireFairPlayHex(t, "encrypted message", encrypted[:], hex.EncodeToString(message[16:144]))
		if tc.mode == 3 {
			inPlace := append([]byte(nil), message...)
			decryptFairPlayMessage(inPlace, inPlace[16:144])
			requireFairPlayHex(t, "in-place decrypted message", inPlace[16:144], tc.decrypted)
		}

		aesKey := deriveFairPlayWrappingKey(fairPlayReferenceReceiverSAP(), message)
		requireFairPlayHex(t, "AES key", aesKey[:], tc.aesKey)
	}
}

func TestFairPlayKeyUnwrapVector(t *testing.T) {
	m3 := make([]byte, 164)
	m3[12] = 3
	for i := 16; i < 144; i++ {
		m3[i] = byte(i*5 + 7)
	}
	var ekey [72]byte
	for i := range ekey {
		ekey[i] = byte(i*7 + 3)
	}
	got := unwrapFairPlayKeyForTest(fairPlayReferenceReceiverSAP(), m3, ekey[:])
	requireFairPlayHex(t, "FairPlay key", got[:], "903e5be94732428e9965afb262b193a4")
}
