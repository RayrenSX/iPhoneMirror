package fairplay

import (
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/hmac"
	"crypto/sha1"
	"encoding/binary"
	"errors"
	"io"

	"github.com/jkiddo/go-atv/internal/fpcontrol"
)

type Sender struct {
	state     *fpsapSession
	confirmed bool
}

func (s *Sender) WrapStream(key [16]byte, entropy io.Reader) ([72]byte, error) {
	if !s.confirmed {
		return [72]byte{}, errors.New("fairplay: SAP is not confirmed")
	}
	return s.state.wrapKey(key, entropy)
}

func NewSender(entropy io.Reader) (*Sender, error) {
	s, e := newFPSAPSession(entropy)
	if e != nil {
		return nil, e
	}
	return &Sender{state: s}, nil
}
func (s *Sender) Start() []byte                       { return s.state.message1() }
func (s *Sender) Challenge(m2 []byte) ([]byte, error) { return s.state.exchangeM3(m2) }
func (s *Sender) Confirm(m4 []byte) error {
	if e := s.state.confirmM4(m4); e != nil {
		return e
	}
	s.confirmed = true
	return nil
}
func (s *Sender) Wrap(material [64]byte, entropy io.Reader) ([120]byte, error) {
	if !s.confirmed {
		return [120]byte{}, errors.New("fairplay: SAP is not confirmed")
	}
	var iv [16]byte
	if _, e := io.ReadFull(entropy, iv[:]); e != nil {
		return [120]byte{}, e
	}
	key := deriveFairPlayWrappingKey(s.state.remoteSAP, s.state.m3[:])
	mac := fpsapDescriptorForSAP(s.state.localSAP, s.state.remoteSAP)
	return fpcontrol.Wrap(material, key, mac, iv), nil
}

type Receiver struct {
	state    *receiverFPSAPState
	consumed bool
}

func NewReceiver(entropy io.Reader) (*Receiver, error) {
	s, e := newReceiverFPSAPState(entropy)
	if e != nil {
		return nil, e
	}
	return &Receiver{state: s}, nil
}
func (r *Receiver) UnwrapStream(body []byte) ([16]byte, error) { return r.state.unwrapKey(body) }
func (r *Receiver) Exchange(body []byte) ([]byte, error)       { return r.state.exchange(body) }
func (r *Receiver) Unwrap(body []byte) ([64]byte, error) {
	var material [64]byte
	if !r.state.complete() || r.consumed {
		return material, errors.New("fairplay: invalid envelope sequence")
	}
	// Reject retries after any envelope failure; the caller closes the connection.
	r.consumed = true
	if len(body) != 120 || !bytes.Equal(body[:16], []byte{'F', 'P', 'L', 'Y', 1, 2, 1, 0, 0, 0, 0, 108, 0, 0, 0, 0}) || binary.BigEndian.Uint32(body[32:36]) != 64 {
		return material, errors.New("fairplay: malformed control envelope")
	}
	key := deriveFairPlayWrappingKey(r.state.receiverSAP, r.state.m3[:])
	block, _ := aes.NewCipher(key[:])
	cipher.NewCBCDecrypter(block, body[16:32]).CryptBlocks(material[:], body[56:])
	var sender [128]byte
	decryptFairPlayMessage(r.state.m3[:], sender[:])
	macKey := fpsapDescriptorForSAP(sender, r.state.receiverSAP)
	mac := hmac.New(sha1.New, macKey[:])
	mac.Write(body[:36])
	mac.Write(material[:])
	if !hmac.Equal(body[36:56], mac.Sum(nil)) {
		return [64]byte{}, errors.New("fairplay: invalid control envelope authentication")
	}
	return material, nil
}
