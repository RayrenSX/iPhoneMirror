// SPDX-License-Identifier: LGPL-3.0-or-later
// This DLL owns all Go memory; only integer handles and caller-owned buffers cross the ABI.
package main

/*
#include <stdint.h>
*/
import "C"
import (
	"github.com/jkiddo/go-atv/media"
	"github.com/jkiddo/go-atv/receiver"
	"sync"
	"unsafe"
)

var registry = struct {
	sync.Mutex
	next   uint64
	states map[uint64]*receiver.State
}{states: make(map[uint64]*receiver.State)}
var mediaBroker = media.New()

//export IMControlCreate
func IMControlCreate() C.uint64_t {
	registry.Lock()
	defer registry.Unlock()
	if len(registry.states) >= 128 {
		return 0
	}
	s, e := receiver.New()
	if e != nil {
		return 0
	}
	registry.next++
	registry.states[registry.next] = s
	return C.uint64_t(registry.next)
}

//export IMControlDestroy
func IMControlDestroy(h C.uint64_t) {
	registry.Lock()
	defer registry.Unlock()
	delete(registry.states, uint64(h))
	mediaBroker.Detach(uint64(h))
}

func bytes(p unsafe.Pointer, n C.int, limit int) ([]byte, bool) {
	if n < 0 || int(n) > limit || (n > 0 && p == nil) {
		return nil, false
	}
	if n == 0 {
		return nil, true
	}
	return unsafe.Slice((*byte)(p), int(n)), true
}

//export IMControlRun
func IMControlRun(h C.uint64_t, op C.int, src unsafe.Pointer, n C.int, dst unsafe.Pointer, capacity C.int, secret unsafe.Pointer, mixed C.int) C.int {
	registry.Lock()
	defer registry.Unlock()
	s := registry.states[uint64(h)]
	if s == nil {
		return -1
	}
	b, ok := bytes(src, n, receiver.MaxOutput)
	if !ok {
		return -1
	}
	outbuf, ok := bytes(dst, capacity, receiver.MaxOutput)
	if !ok {
		return -1
	}
	// Validate the output buffer before advancing any nonce or handshake state.
	need := 0
	switch op {
	case 1:
		need = 142
	case 2:
		need = 0
	case 3:
		need = receiver.MaxOutput
	case 4:
		need = int(n) + 20
	default:
		return -1
	}
	if int(capacity) < need {
		return -1
	}
	var out []byte
	var e error
	switch op {
	case 1:
		out, e = s.Exchange(b)
	case 2:
		var key []byte
		if mixed != 0 {
			if secret == nil {
				return -1
			}
			key = unsafe.Slice((*byte)(secret), 32)
		}
		e = s.Setup(b, key, mixed != 0)
	case 3:
		out, e = s.Feed(b)
	case 4:
		out, e = s.Seal(b)
	}
	if e != nil || len(out) > len(outbuf) {
		return -1
	}
	copy(outbuf, out)
	return C.int(len(out))
}
func main() {}

//export IMMediaRun
func IMMediaRun(h C.uint64_t, op C.int, src unsafe.Pointer, n C.int, dst unsafe.Pointer, capacity C.int) C.int {
	registry.Lock()
	defer registry.Unlock()
	if registry.states[uint64(h)] == nil {
		return -1
	}
	input, ok := bytes(src, n, media.MaxBody)
	if !ok {
		return -1
	}
	output, ok := bytes(dst, capacity, 65536)
	if !ok {
		return -1
	}
	var data []byte
	var err error
	switch op {
	case 1:
		err = mediaBroker.Bind(uint64(h), string(input))
	case 2:
		if capacity < 65536 {
			return -1
		}
		data = mediaBroker.Poll(uint64(h))
	case 3:
		if capacity < 65536 {
			return -1
		}
		var location string
		location, err = mediaBroker.Start(uint64(h), string(input))
		data = []byte(location)
	case 4:
		err = mediaBroker.Reply(uint64(h), input)
	default:
		return -1
	}
	if err != nil || len(data) > len(output) {
		return -1
	}
	copy(output, data)
	return C.int(len(data))
}
