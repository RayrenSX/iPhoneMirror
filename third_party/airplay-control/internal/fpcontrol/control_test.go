package fpcontrol

import (
	"bytes"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"os"
	"testing"
	"testing/iotest"
)

func unhex(t *testing.T, s string) []byte {
	t.Helper()
	b, err := hex.DecodeString(s)
	if err != nil {
		t.Fatal(err)
	}
	return b
}

func TestAppleNativeVectors(t *testing.T) {
	data, err := os.ReadFile("testdata/native-gcm.json")
	if err != nil {
		t.Fatal(err)
	}
	var fixture struct {
		Vectors []struct {
			Key               string
			InitialNonce      string `json:"initial_nonce"`
			Index             int
			Plaintext, Record string
		}
	}
	if err := json.Unmarshal(data, &fixture); err != nil {
		t.Fatal(err)
	}
	if len(fixture.Vectors) != 6 {
		t.Fatal("expected six native vectors")
	}
	var encoder, decoder *Records
	for _, v := range fixture.Vectors {
		if v.Index == 0 {
			encoder = NewRecords([16]byte(unhex(t, v.Key)), [16]byte(unhex(t, v.InitialNonce)))
			decoder = NewRecords([16]byte(unhex(t, v.Key)), [16]byte(unhex(t, v.InitialNonce)))
		}
		plain, record := unhex(t, v.Plaintext), unhex(t, v.Record)
		got, err := encoder.Seal(plain)
		if err != nil || !bytes.Equal(got, record) {
			t.Fatalf("native encode index %d: %x, %v", v.Index, got, err)
		}
		got, err = decoder.Read(iotest.OneByteReader(bytes.NewReader(record)))
		if err != nil || !bytes.Equal(got, plain) {
			t.Fatalf("native decode index %d: %v", v.Index, err)
		}
	}
}

func TestAuthenticationAndFramingFailClosed(t *testing.T) {
	newState := func() *Records { return NewRecords([16]byte{}, [16]byte{}) }
	encoder := newState()
	record, _ := encoder.Seal([]byte("GET /playback-info HTTP/1.1\r\n\r\n"))
	for _, offset := range []int{3, 4, len(record) - 1} {
		bad := bytes.Clone(record)
		bad[offset] ^= 1
		decoder := newState()
		if plain, err := decoder.Open(bad); err == nil || len(plain) != 0 {
			t.Fatal("accepted corruption", offset)
		}
		if _, err := decoder.Open(record); err == nil {
			t.Fatal("reused failed state")
		}
	}
	decoder := newState()
	if _, err := decoder.Open(record); err != nil {
		t.Fatal(err)
	}
	if _, err := decoder.Open(record); err == nil {
		t.Fatal("accepted replay")
	}
	second, _ := encoder.Seal([]byte("next"))
	if _, err := newState().Open(second); err == nil {
		t.Fatal("accepted reordered record")
	}
	for _, bad := range [][]byte{record[:10], record[:len(record)-1], {0, 4, 0, 1}, {255, 255, 255, 255}} {
		if plain, err := newState().Read(bytes.NewReader(bad)); err == nil || len(plain) != 0 {
			t.Fatal("accepted bad frame")
		}
	}
}

type shortWriter struct{}

func (shortWriter) Write(p []byte) (int, error) { return len(p) - 1, nil }

func TestLimitsAndWriteFailure(t *testing.T) {
	encoder := NewRecords([16]byte{}, [16]byte{})
	if _, err := encoder.Seal(make([]byte, MaxPayload+1)); err == nil {
		t.Fatal("accepted oversize")
	}
	plain := bytes.Repeat([]byte{42}, MaxPayload)
	record, err := encoder.Seal(plain)
	if err != nil {
		t.Fatal(err)
	}
	got, err := NewRecords([16]byte{}, [16]byte{}).Open(record)
	if err != nil || !bytes.Equal(got, plain) {
		t.Fatal("maximum record failed", err)
	}
	if err := encoder.Write(shortWriter{}, []byte("x")); !errors.Is(err, io.ErrShortWrite) {
		t.Fatal(err)
	}
	if err := encoder.Write(io.Discard, []byte("x")); !errors.Is(err, io.ErrShortWrite) {
		t.Fatal("reused failed writer")
	}
	var nonce [16]byte
	for i := range nonce {
		nonce[i] = 255
	}
	if _, err := NewRecords([16]byte{}, nonce).Seal(nil); err == nil {
		t.Fatal("nonce wrapped")
	}
}

func TestSenderDirectionsAndMixing(t *testing.T) {
	var material [64]byte
	for i := range material {
		material[i] = byte(i)
	}
	if _, _, err := Sender(material, nil, true); err == nil {
		t.Fatal("missing secret accepted")
	}
	write, read, err := Sender(material, bytes.Repeat([]byte{0x80}, 32), true)
	if err != nil {
		t.Fatal(err)
	}
	// Independently computed with Python hashlib.sha512, inputs 00..0f or
	// 20..2f followed by 32 bytes of 0x80; these also catch direction swaps.
	for _, pair := range []struct {
		state *Records
		key   string
		nonce []byte
	}{
		{write, "fa81f22d86f861faaa2a84a5ce9cfbea", material[16:32]},
		{read, "f2467b5f5d2cf3c039ca6cbd2444a9ca", material[48:64]},
	} {
		wire, err := NewRecords([16]byte(unhex(t, pair.key)), [16]byte(pair.nonce)).Seal([]byte("check"))
		if err != nil {
			t.Fatal(err)
		}
		if plain, err := pair.state.Open(wire); err != nil || string(plain) != "check" {
			t.Fatal("direction/mixing mismatch", err)
		}
	}
}

func TestWrappedMaterialVectors(t *testing.T) {
	paths := []string{"testdata/wrapped-material.json"}
	// Optional private captures remain outside version control. The test emits
	// only an index on failure, never their session material or envelopes.
	if path := os.Getenv("FP_CONTROL_CAPTURE_VECTORS"); path != "" {
		paths = append(paths, path)
	}
	for _, path := range paths {
		data, err := os.ReadFile(path)
		if err != nil {
			t.Fatal(err)
		}
		var vectors []struct{ Material, WrappingKey, MACKey, IV, Record string }
		if err := json.Unmarshal(data, &vectors); err != nil {
			t.Fatal(err)
		}
		if len(vectors) == 0 {
			t.Fatal("empty envelope fixture")
		}
		for i, v := range vectors {
			got := Wrap([64]byte(unhex(t, v.Material)), [16]byte(unhex(t, v.WrappingKey)), [20]byte(unhex(t, v.MACKey)), [16]byte(unhex(t, v.IV)))
			if !bytes.Equal(got[:], unhex(t, v.Record)) {
				t.Fatalf("envelope vector %d mismatch", i)
			}
		}
		t.Logf("matched %d envelope vectors", len(vectors))
	}
}
