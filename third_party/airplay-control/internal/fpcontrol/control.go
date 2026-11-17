// Package fpcontrol implements the experimentally verified fp-setup2 envelope
// and subsequent AirPlay GCM records. It does not implement FairPlay SAP,
// pair verification, HTTP negotiation, or the native video-session lifecycle.
package fpcontrol

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/sha1"
	"crypto/sha512"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
)

// MaxPayload is the native NetSocketGCM record payload limit.
const MaxPayload = 0x40000

// Wrap constructs a 120-byte /fp-setup2 body. Keys come from this connection's
// completed SAP exchange, not from an earlier capture. Production callers must
// generate fresh material and IV with crypto/rand for every connection.
func Wrap(material [64]byte, wrappingKey [16]byte, macKey [20]byte, iv [16]byte) [120]byte {
	var record [120]byte
	copy(record[:], []byte{'F', 'P', 'L', 'Y', 1, 2, 1, 0, 0, 0, 0, 108})
	copy(record[16:32], iv[:])
	binary.BigEndian.PutUint32(record[32:36], 64)
	mac := hmac.New(sha1.New, macKey[:])
	mac.Write(record[:36])
	mac.Write(material[:])
	copy(record[36:56], mac.Sum(nil))
	block, _ := aes.NewCipher(wrappingKey[:]) // fixed 16-byte key
	cipher.NewCBCEncrypter(block, iv[:]).CryptBlocks(record[56:], material[:])
	return record
}

// Sender creates independent client-write and client-read record states. Enable
// mixing only when negotiated (X-Apple-PD: 1 in the captured legacy path).
// Activate these states only after consuming the successful fp-setup2 HTTP
// response on the same connection. Never reuse states after reconnecting.
func Sender(material [64]byte, sharedSecret []byte, mix bool) (write, read *Records, err error) {
	if mix && len(sharedSecret) != 32 {
		return nil, nil, errors.New("fpcontrol: key mixing requires a 32-byte pair-verify secret")
	}
	var writeKey, readKey, writeNonce, readNonce [16]byte
	copy(writeKey[:], material[0:16])
	copy(writeNonce[:], material[16:32])
	copy(readKey[:], material[32:48])
	copy(readNonce[:], material[48:64])
	if mix {
		for _, key := range []*[16]byte{&writeKey, &readKey} {
			h := sha512.New()
			h.Write(key[:])
			h.Write(sharedSecret)
			copy(key[:], h.Sum(nil)[:16])
		}
	}
	return NewRecords(writeKey, writeNonce), NewRecords(readKey, readNonce), nil
}

// Records is one direction of one connection. Serialize calls in that
// direction. Use a separate instance for the opposite direction. Transport,
// framing and authentication failures permanently invalidate this instance.
type Records struct {
	aead  cipher.AEAD
	nonce [16]byte
	err   error
}

func NewRecords(key, initialNonce [16]byte) *Records {
	block, _ := aes.NewCipher(key[:])
	aead, err := cipher.NewGCMWithNonceSize(block, 16)
	if err != nil {
		panic(err)
	} // the fixed nonce size is supported
	return &Records{aead: aead, nonce: initialNonce}
}

func (r *Records) fail(err error) error {
	if r.err == nil {
		r.err = err
	}
	return r.err
}

func (r *Records) advance() error {
	if r.err != nil {
		return r.err
	}
	for i := len(r.nonce) - 1; i >= 0; i-- {
		r.nonce[i]++
		if r.nonce[i] != 0 {
			return nil
		}
	}
	return r.fail(errors.New("fpcontrol: nonce exhausted"))
}

// Seal encodes one complete record. Callers split larger messages at MaxPayload.
func (r *Records) Seal(plaintext []byte) ([]byte, error) {
	if r.err != nil {
		return nil, r.err
	}
	if len(plaintext) > MaxPayload {
		return nil, fmt.Errorf("fpcontrol: payload exceeds %d bytes", MaxPayload)
	}
	if err := r.advance(); err != nil {
		return nil, err
	}
	header := make([]byte, 4, 4+len(plaintext)+r.aead.Overhead())
	binary.BigEndian.PutUint32(header, uint32(len(plaintext)))
	return r.aead.Seal(header, r.nonce[:], plaintext, header), nil
}

// Open authenticates exactly one record before returning any plaintext.
func (r *Records) Open(record []byte) ([]byte, error) {
	if r.err != nil {
		return nil, r.err
	}
	if len(record) < 20 {
		return nil, r.fail(errors.New("fpcontrol: truncated record"))
	}
	n := binary.BigEndian.Uint32(record[:4])
	if n > MaxPayload || uint64(n)+20 != uint64(len(record)) {
		return nil, r.fail(errors.New("fpcontrol: invalid record length"))
	}
	if err := r.advance(); err != nil {
		return nil, err
	}
	plaintext, err := r.aead.Open(nil, r.nonce[:], record[4:], record[:4])
	if err != nil {
		return nil, r.fail(errors.New("fpcontrol: record authentication failed"))
	}
	return plaintext, nil
}

// Read reads one record, handling fragmented TCP reads and bounding allocations.
func (r *Records) Read(src io.Reader) ([]byte, error) {
	if r.err != nil {
		return nil, r.err
	}
	var header [4]byte
	if _, err := io.ReadFull(src, header[:]); err != nil {
		return nil, r.fail(err)
	}
	n := binary.BigEndian.Uint32(header[:])
	if n > MaxPayload {
		return nil, r.fail(errors.New("fpcontrol: oversized record"))
	}
	record := make([]byte, int(n)+20)
	copy(record, header[:])
	if _, err := io.ReadFull(src, record[4:]); err != nil {
		return nil, r.fail(err)
	}
	return r.Open(record)
}

// Write writes one record. Any short or failed write invalidates the state:
// retrying on the same stream would lose framing or reuse a nonce.
func (r *Records) Write(dst io.Writer, plaintext []byte) error {
	record, err := r.Seal(plaintext)
	if err != nil {
		return err
	}
	n, err := dst.Write(record)
	if err == nil && n != len(record) {
		err = io.ErrShortWrite
	}
	if err != nil {
		return r.fail(err)
	}
	return nil
}
