// SPDX-License-Identifier: LGPL-3.0-or-later
package receiver

import (
	"crypto/rand"
	"encoding/binary"
	"errors"
	"github.com/jkiddo/go-atv/internal/fairplay"
	"github.com/jkiddo/go-atv/internal/fpcontrol"
)

const MaxOutput = fpcontrol.MaxPayload + 4096

type State struct {
	sap           *fairplay.Receiver
	input, output *fpcontrol.Records
	pending       []byte
	failed        bool
}

func New() (*State, error) { s, e := fairplay.NewReceiver(rand.Reader); return &State{sap: s}, e }
func (s *State) Exchange(b []byte) ([]byte, error) {
	if s.failed || s.input != nil {
		return nil, errors.New("invalid exchange state")
	}
	out, e := s.sap.Exchange(b)
	if e != nil {
		s.failed = true
	}
	return out, e
}
func (s *State) Setup(b, secret []byte, mixed bool) error {
	if s.failed || s.input != nil {
		return errors.New("invalid upgrade state")
	}
	material, e := s.sap.Unwrap(b)
	if e == nil {
		s.input, s.output, e = fpcontrol.Sender(material, secret, mixed)
	}
	if e != nil {
		s.failed = true
	}
	return e
}

// Feed authenticates complete records and retains only an incomplete bounded record.
// A failure is terminal and never returns plaintext from the failed batch.
func (s *State) Feed(b []byte) ([]byte, error) {
	if s.failed || s.input == nil || len(b) > 4096 {
		return nil, errors.New("invalid input state")
	}
	s.pending = append(s.pending, b...)
	var out []byte
	for len(s.pending) >= 4 {
		n := uint64(binary.BigEndian.Uint32(s.pending[:4]))
		if n > fpcontrol.MaxPayload {
			s.failed = true
			return nil, errors.New("oversized record")
		}
		if uint64(len(s.pending)) < n+20 {
			break
		}
		plain, e := s.input.Open(s.pending[:int(n)+20])
		if e != nil {
			s.failed = true
			return nil, e
		}
		out = append(out, plain...)
		s.pending = s.pending[int(n)+20:]
	}
	return out, nil
}
func (s *State) Seal(b []byte) ([]byte, error) {
	if s.failed || s.output == nil || len(b) > fpcontrol.MaxPayload {
		return nil, errors.New("invalid output state")
	}
	out, e := s.output.Seal(b)
	if e != nil {
		s.failed = true
	}
	return out, e
}
