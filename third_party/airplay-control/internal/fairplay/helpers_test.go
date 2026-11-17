package fairplay

import "encoding/hex"

func mustDecodeHexFP(s string) []byte {
	b, e := hex.DecodeString(s)
	if e != nil {
		panic(e)
	}
	return b
}
