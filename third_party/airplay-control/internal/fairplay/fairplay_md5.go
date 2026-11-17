// Derived from github.com/omarroth/doubletake, internal/airplay/fairplay_md5.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Unmodified apart from the package name.

package fairplay

import (
	"encoding/binary"
	"math/bits"
)

// FairPlay uses the standard MD5 compression rounds and constants, but reads
// message words as big endian and mutates the message schedule after round 31.
// Those differences mean crypto/md5 cannot implement these compressions.
type fairplayMD5Mutation uint8

const (
	fpsapSwapMutation fairplayMD5Mutation = iota
	fpsapCycleMutation
	fairplayKDFMutation
)

var fairplayMD5Shift = [64]int{
	7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
	5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
	4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
	6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
}

var fairplayMD5Constant = [64]uint32{
	0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
	0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
	0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
	0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
	0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
	0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
	0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
	0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
	0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
	0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
	0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
	0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
	0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
	0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
	0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
	0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
}

func fairplayMD5Compress(state [4]uint32, block []byte, mutation fairplayMD5Mutation) [4]uint32 {
	var message [16]uint32
	for i := range message {
		message[i] = binary.BigEndian.Uint32(block[i*4:])
	}

	a, b, c, d := state[0], state[1], state[2], state[3]
	for round := 0; round < 64; round++ {
		var f uint32
		var word int
		switch {
		case round < 16:
			f, word = (b&c)|(^b&d), round
		case round < 32:
			f, word = (d&b)|(^d&c), (5*round+1)&15
		case round < 48:
			f, word = b^c^d, (3*round+5)&15
		default:
			f, word = c^(b|^d), (7*round)&15
		}

		a, b, c, d = d,
			b+bits.RotateLeft32(a+f+fairplayMD5Constant[round]+message[word], fairplayMD5Shift[round]),
			b, c

		if round == 31 {
			mutateFairplayMD5Message(&message, a, b, c, d, mutation)
		}
	}

	return [4]uint32{state[0] + a, state[1] + b, state[2] + c, state[3] + d}
}

func mutateFairplayMD5Message(message *[16]uint32, a, b, c, d uint32, mutation fairplayMD5Mutation) {
	swap := func(i, j int) { message[i], message[j] = message[j], message[i] }
	switch mutation {
	case fpsapSwapMutation:
		indices := [...]int{
			int(a & 15), int(b & 15), int(c & 15), int(d & 15),
			int((a >> 4) & 15), int((b >> 4) & 15), int((c >> 4) & 15), int((d >> 4) & 15),
		}
		for i, j := range indices {
			swap(i, j)
		}
	case fpsapCycleMutation:
		indices := [...]int{
			int(a & 15), int(b & 15), int(c & 15), int(d & 15),
			int((a >> 4) & 15), int((b >> 4) & 15), int((c >> 4) & 15), int((d >> 4) & 15),
		}
		first := message[indices[0]]
		for i := 0; i < len(indices)-1; i++ {
			message[indices[i]] = message[indices[i+1]]
		}
		message[indices[len(indices)-1]] = first
	case fairplayKDFMutation:
		swap(int(a&15), int(b&15))
		swap(int(c&15), int(d&15))
		for shift := 4; shift <= 12; shift += 4 {
			swap(int((a>>shift)&15), int((b>>shift)&15))
		}
	}
}

func fairplayWordsFromLittleEndian(in [16]byte) (out [4]uint32) {
	for i := range out {
		out[i] = binary.LittleEndian.Uint32(in[i*4:])
	}
	return out
}

func fairplayWordsBigEndian(words [4]uint32) (out [16]byte) {
	for i, word := range words {
		binary.BigEndian.PutUint32(out[i*4:], word)
	}
	return out
}
