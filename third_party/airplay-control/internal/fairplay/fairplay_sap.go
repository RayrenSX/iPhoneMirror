// Derived from github.com/omarroth/doubletake, internal/airplay/fairplay_sap.go.
// Copyright (C) the doubletake authors. Licensed under the GNU Lesser General
// Public License v3.0 or later (LGPL-3.0-or-later); the license texts are in
// third_party/doubletake/ and the attribution in THIRD_PARTY_NOTICES.md.
// Unmodified apart from the package name.

package fairplay

// FairPlay's proprietary SAP hash. It is not a standard cryptographic hash.

import "math/bits"

func rotateOrZero(input, count byte) byte {
	if count == 0 {
		return 0
	}
	return bits.RotateLeft8(input, int(count))
}

func wideSeed(input, count byte) byte {
	if count == 0 {
		return sapSeed[0]
	}
	return sapSeed[(int(input)<<count|int(input)>>(8-count))%len(sapSeed)]
}

func majority(a, b, c byte) byte                { return a ^ (a^b)&(a^c) }
func selectBits(mask, ifSet, ifClear byte) byte { return ifClear ^ (ifSet^ifClear)&mask }
func square(value byte) byte                    { return value * value }
func cube(value byte) byte                      { return value * value * value }

var sapInitialState = sapState{
	hash: [20]byte{
		0x96, 0x5f, 0xc6, 0x53, 0xf8, 0x46, 0xcc, 0x18, 0xdf, 0xbe,
		0xb2, 0xf8, 0x38, 0x62, 0xec, 0x22, 0x93, 0xd1, 0x20, 0x8f,
	},
	matrix: [35]byte{
		0x43, 0x54, 0x62, 0x7a, 0x18, 0xc3, 0xd6, 0xb3, 0x9a, 0x56,
		0xf6, 0x1c, 0x14, 0x3f, 0x0c, 0x1d, 0x3b, 0x36, 0x83, 0xb1,
		0x39, 0x51, 0x4a, 0xaa, 0x09, 0x3e, 0xfe, 0x44, 0xaf, 0xde,
		0xc3, 0x20, 0x9d, 0x42, 0xb8,
	},
}

var sapSeed = [21]byte{
	0xed, 0x25, 0xd1, 0xbb, 0xbc, 0x27, 0x9f, 0x02, 0xa2, 0xa9, 0x11,
	0x00, 0x0c, 0xb3, 0x52, 0xc0, 0xbd, 0xe3, 0x1b, 0x49, 0xc7,
}

type sapState struct {
	hash   [20]byte
	matrix [35]byte
	aux    [10]byte
	work   [210]byte
}

func fairplaySAPHash(block []byte) (out [16]byte) {
	state := sapInitialState
	work := &state.work
	// Check once, then load input in reversed four-byte groups.
	_ = block[63]
	for i := range work {
		work[i] = block[(i&63)^3]
	}

	// uint32 underflow changes the first of four scramble passes.
	for i := range uint32(840) {
		x, y, z, w := work[(i-155)%210], work[(i-57)%210], work[(i-13)%210], work[i%210]
		work[i%210] = bits.RotateLeft8(y, 5) + (bits.RotateLeft8(z, 3) ^ w) - bits.RotateLeft8(x, 7)
	}

	state.nonlinearCircuit()
	// Include terminal work XORs directly in their folded output lanes.
	copy(out[:], state.aux[:3])
	copy(out[4:], state.aux[3:])
	for i := range out {
		out[i] += 0xe1
	}
	out[3], out[11] = 0x3d, 0x3c
	out[10] ^= state.aux[3] ^ 133

	for i, value := range work {
		if i < len(state.matrix) {
			value ^= state.matrix[i]
		}
		if i < len(state.hash) {
			value ^= state.hash[i]
		}
		out[i&15] ^= value
	}

	// Reverse scramble
	for i := range 256 {
		out[i&15] ^= bits.RotateLeft8(out[(i-7)&15], 1) ^
			bits.RotateLeft8(out[(i-5)&15], 6) ^
			bits.RotateLeft8(out[(i-1)&15], 5)
	}
	return
}

// Arithmetic wraps as bytes unless explicitly promoted before division or indexing.
func (state *sapState) nonlinearCircuit() {
	hash, matrix, aux, work := &state.hash, &state.matrix, &state.aux, &state.work
	// h/m/s read hash/matrix/seed through work; ma reads matrix through aux.
	hi := func(i byte) byte { return hash[i%20] }
	si := func(i byte) byte { return sapSeed[i%21] }
	h := func(i int) byte { return hi(work[i]) }
	m := func(i int) byte { return matrix[work[i]%35] }
	s := func(i int) byte { return si(work[i]) }
	ma := func(i int) byte { return matrix[aux[i]%35] }
	matrix[12] = 0x14 + selectBits(92, work[64], work[99]/3)&wideSeed(s(206), 4)
	work[4] = 2 * square(work[99]/5)
	work[153] ^= square(m(203)) * work[190]
	hash[3] = 0x13 ^ s(205)>>1&0x10
	work[33] -= s(36) &^ 9
	aux[5] = (m(67)&^2 | 1 | h(181)>>6&2 | hash[3]&0x10) - 15
	matrix[12] = 0x07
	work[2] -= 64
	hash[19] = s(58)
	aux[4] = 92 - m(32)
	aux[9] = m(15) + 0x9e
	work[34] += si(aux[9]) / 5
	hash[19] += 0xe6 ^ hi(aux[9])>>1&0x66
	work[15] ^= 3*rotateOrZero(work[72], -s(190)&7) - 9*s(126)
	hash[15] ^= cube(m(181))
	matrix[4] ^= work[202] / 3
	matrix[1] += cube(majority(92-hi(aux[4]), ^work[105], 0xc6))
	hash[19] ^= byte(int(224|s(92)&27) * int(m(41)) / 3)
	work[140] += rotateOrZero(92, -work[5]&7)
	matrix[12] += majority(^work[4]^m(12), work[182], 192)
	work[36] += 125
	work[124] = bits.RotateLeft8(majority(majority(work[138], hash[15], 74), h(43), 95), 4)
	auxHash := hi(aux[9])
	aux[1] = 0x4c &^ (auxHash & (s(68) << 1))
	aux[2] = 222 - majority(byte((int(work[177])+int(s(79)))>>1), byte(3*int(work[148])/5), matrix[1])
	matrix[16] += (ma(4)&^0x60 | auxHash | 8) - (bits.RotateLeft8(work[33], 2) | 128)
	hash[14] ^= ma(2)
	work[19] += majority(rotateOrZero(si(h(201)), m(112)<<1&6),
		((h(208)&^0x7c)|(h(164)&0x7c))/5, 37)
	matrix[8] = rotateOrZero(140, -square(s(45))&7) ^ aux[4]
	work[190] = 56
	work[53] = ^((h(83) | 204) / 5)
	hash[13] += h(41)
	hash[10] = majority(ma(4), work[2], aux[2]) / 15
	aux[3] = 92 - square(0x28|(ma(1)&(0x12|(s(2)&4))))
	seedBits := si(aux[4])
	matrix[13] ^= seedBits
	aux[6] = 92 + square(majority(m(179)-38, aux[2], 177))
	expansionBits := majority(aux[3]+(aux[4]&74), ^seedBits, 121)
	work[47] ^= m(89) + majority(expansionBits^0xa6, aux[4], 4)
	aux[7] = seedBits/3 - ma(9) -
		(0x14 | work[151]&(aux[4]&0x88|0x62) | aux[4]&0x22)
	expandedSelector := expansionBits ^ aux[4]&0xca>>1 ^ 75
	aux[9] += 0x80 | majority(aux[7], work[151], 0x20)&0x64 | seedBits&0x44 | ma(9)&0x1b
	matrix[33] ^= work[26]
	matrix[30] = (aux[9]/3 - (aux[4]&^8 | 0x13)) ^ h(122)
	work[22] = m(90)&0x1b | 0x44
	wide := int(selectBits(71, matrix[expandedSelector%35], si(aux[5])))
	matrix[18] += byte(wide * wide * wide >> 1)
	matrix[5] -= s(92)
	matrix[18] ^= selectBits(aux[3], ma(3), selectBits(16, m(183), work[41])) *
		selectBits(expandedSelector, h(59), work[17])
	matrix[22] = majority(selectBits(hash[14]|28, (work[7]&28)|0x82, h(93)),
		rotateOrZero(ma(4), rotateOrZero(work[11], -m(28)&7)&7), matrix[33]) + 74
	hash[15] -= majority(majority(aux[3], aux[4], 214), si(h(39)^217), aux[6])

	hash9 := hi(aux[9])
	indexedHash := hi(((aux[4] / 3) - (aux[9] | work[22])) ^ aux[6] ^
		(((m(57) | hash9) & (0x52 | (aux[9] & 0x0d))) | ((m(57)&hash9 | aux[9]) & 0x20)))
	aux[6] = square(square(h(99))) | ma(9)
	aux[1] += rotateOrZero(h(151)|s(202), h(50)&7) +
		majority(h(4), byte((int(selectBits(matrix[16], indexedHash, m(138)))+int(selectBits(17, work[33], s(39))))/5), 147)
	aux[0] = selectBits(hash[10]&7, ma(6)&h(209),
		selectBits(0x47, rotateOrZero(s(127), ma(6)&7), si(ma(5))<<1))
	selectedSquare := selectBits(198, square(m(14)), h(145)^aux[0])
	seed9 := si(aux[9])
	hash3 := hi(aux[3])
	matrix[2] += ((hash3 << 1) & ((work[25] & 0x96) | (seed9 & 8))) | (seed9 & 0x40)
	matrix[14] -= selectBits(34, work[97], ma(3)&(aux[0]^m(100)))
	work[23] ^= majority(majority(s(17), hash3, aux[0]), work[50]/3, 0x76) << 1
	hash[17] = 115
	hash[13] = majority(hi(aux[7]), work[10], 82)>>1&0x68 | h(39)&0x17
	matrix[33] -= work[113] & 9
	matrix[28] -= aux[3]&^0x20 | work[110]>>1&0x20
	work[95] = si(aux[3])
	hash[15] = majority(work[95]-48, ^work[184], 189) & cube(majority(aux[7], si(aux[1]), 0xaa))
	matrix[22] += work[183]
	aux[4] ^= 3 * s(1)
	aux[5] += 198 * majority(s(178), ma(1), 209) * h(13) * (s(26) >> 1)
	aux[8] = selectBits(10, ma(3), ma(9))
	matrix[18] -= selectBits(hash[15], aux[5]/15, cube(hi(aux[6])|81))
	aux[1] += si(hi(aux[1]))/3 - h(160)
	hash[16] = 147 - majority(aux[0], majority(s(69), work[172], aux[2]-selectedSquare+77), 0xc2|aux[0]&5)
	hash[3] -= wideSeed(majority(s(155), work[105], 141), majority(s(168), h(29), 6)&7)
	work[5] = rotateOrZero(0x38, -(h(61)/5)&7) ^ (^ma(8))/5
	work[198] += work[3]
	wide = int(162 | ma(9))
	work[164] += byte(wide * wide / 5)
	aux[2] = majority(rotateOrZero(139, -aux[5]&6), hi(aux[3]), 12) |
		selectBits(95, cube(seed9), hi(aux[7]))
	matrix[12] += (16 | (work[103]|60)&(aux[2]|work[103]&32)) / 3
	work[143] -= 0x12 | selectBits(aux[9], selectBits(matrix[8], work[35], aux[7]), aux[8]/3)&
		(0x4d|work[172]>>1&0x20)
	matrix[29] = 162
	hash[15] += majority(m(149)^square(work[43]), selectBits(95, h(125), si(aux[1]))>>1, 115)
	aux[9] -= hi(aux[7])
	hash[7] -= square(rotateOrZero(ma(5), -m(17)*(m(17)&1)))
	matrix[8] += cube(s(202)) - work[184]
	hash[16] = m(102) << 1 & 0x84
	aux[6] ^= si(aux[7]) >> 1
	hash[7] -= h(191) - selectBits(177, si(si(aux[1])), s(80)<<1)
	hash[6] = h(119)
	hash[12] = (hi(aux[8]) ^ (m(71) + m(15))) &
		majority(work[118]&^0x2c|2, square(hi(aux[9])), 27)
	digestIndex := selectBits(0xa9, s(57)*231, majority(work[32], ma(1), 23)) / 5
	seedSample := si(aux[6])
	aux[5] = majority(seedSample&0x1c|h(82)&0xa2|si(digestIndex)&0x41,
		majority(cube(hi(aux[7])), work[82], 92), 192) ^ digestIndex
	matrix[25] ^= 2*hi(aux[9])*work[5] - rotateOrZero(aux[4], seedSample&7)&(aux[3]+110)
}
