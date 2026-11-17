// Local integration test against the actual native video HTTP listener.
package main

import (
	"bufio"
	"bytes"
	"crypto/aes"
	"crypto/cipher"
	"crypto/ecdh"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha512"
	"fmt"
	"github.com/jkiddo/go-atv/internal/fairplay"
	"github.com/jkiddo/go-atv/internal/fpcontrol"
	"io"
	"net"
	"net/http"
	"os"
	"time"
)

func check(e error) {
	if e != nil {
		panic(e)
	}
}
func message(path string, body []byte) []byte {
	return append([]byte(fmt.Sprintf("POST %s HTTP/1.1\r\nHost: localhost\r\nContent-Length: %d\r\n\r\n", path, len(body))), body...)
}
func response(r *bufio.Reader) []byte {
	v, e := http.ReadResponse(r, nil)
	check(e)
	defer v.Body.Close()
	if v.StatusCode != 200 {
		panic(v.Status)
	}
	b, e := io.ReadAll(v.Body)
	check(e)
	return b
}
func run(mixed bool) {
	endpoint := "127.0.0.1:7011"
	if len(os.Args) > 1 {
		endpoint = os.Args[1]
	}
	c, e := net.DialTimeout("tcp", endpoint, 3*time.Second)
	check(e)
	defer c.Close()
	check(c.SetDeadline(time.Now().Add(10 * time.Second)))
	r := bufio.NewReader(c)
	var shared []byte
	if mixed {
		public, private, e := ed25519.GenerateKey(rand.Reader)
		check(e)
		_, e = c.Write(message("/pair-setup", public))
		check(e)
		serverPublic := response(r)
		key, e := ecdh.X25519().GenerateKey(rand.Reader)
		check(e)
		body := append([]byte{1, 0, 0, 0}, key.PublicKey().Bytes()...)
		body = append(body, public...)
		verify := message("/pair-verify", body)
		verify = bytes.Replace(verify, []byte("Host: localhost\r\n"), []byte("Host: localhost\r\nX-Apple-PD: 1\r\n"), 1)
		_, e = c.Write(verify)
		check(e)
		reply := response(r)
		if len(reply) != 96 {
			panic("pair verify response length")
		}
		remote, e := ecdh.X25519().NewPublicKey(reply[:32])
		check(e)
		shared, e = key.ECDH(remote)
		check(e)
		aesKey := sha512.Sum512(append([]byte("Pair-Verify-AES-Key"), shared...))
		iv := sha512.Sum512(append([]byte("Pair-Verify-AES-IV"), shared...))
		block, e := aes.NewCipher(aesKey[:16])
		check(e)
		stream := cipher.NewCTR(block, iv[:16])
		signature := make([]byte, 64)
		stream.XORKeyStream(signature, reply[32:])
		if !ed25519.Verify(serverPublic, append(append([]byte{}, reply[:32]...), key.PublicKey().Bytes()...), signature) {
			panic("server signature")
		}
		signature = ed25519.Sign(private, append(key.PublicKey().Bytes(), reply[:32]...))
		stream.XORKeyStream(signature, signature)
		_, e = c.Write(message("/pair-verify", append([]byte{0, 0, 0, 0}, signature...)))
		check(e)
		response(r)
	}
	sender, e := fairplay.NewSender(rand.Reader)
	check(e)
	_, e = c.Write(message("/fp-setup", sender.Start()))
	check(e)
	m3, e := sender.Challenge(response(r))
	check(e)
	_, e = c.Write(message("/fp-setup", m3))
	check(e)
	check(sender.Confirm(response(r)))
	var material [64]byte
	_, e = rand.Read(material[:])
	check(e)
	body, e := sender.Wrap(material, rand.Reader)
	check(e)
	write, read, e := fpcontrol.Sender(material, shared, mixed)
	check(e)
	first, e := write.Seal([]byte("GET /server-info HTTP/1.1\r\nHost: localhost\r\n\r\n"))
	check(e)
	// Pipeline the first encrypted command behind the plaintext upgrade to verify
	// that no bytes are dropped at the protocol boundary.
	upgrade := message("/fp-setup2", body[:])
	// X-Apple-PD belongs to pair-verify step 1, not fp-setup2. Its negotiated
	// value must survive step 2 and both SAP requests on this connection.
	_, e = c.Write(append(upgrade, first...))
	check(e)
	response(r)
	b, e := read.Read(r)
	check(e)
	response(bufio.NewReader(bytes.NewReader(b)))
	play := message("/play", []byte("Content-Location: http://127.0.0.1:9999/control-smoke.mp4\r\nStart-Position: 0\r\n"))
	wire, e := write.Seal(play)
	check(e)
	// Fragment the record header, ciphertext and authentication tag.
	for _, part := range [][]byte{wire[:2], wire[2:9], wire[9 : len(wire)-3], wire[len(wire)-3:]} {
		_, e = c.Write(part)
		check(e)
	}
	b, e = read.Read(r)
	check(e)
	response(bufio.NewReader(bytes.NewReader(b)))
	wire, e = write.Seal([]byte("GET /server-info HTTP/1.1\r\n\r\n"))
	check(e)
	wire[len(wire)-1] ^= 1
	_, e = c.Write(wire)
	check(e)
	var scratch [1]byte
	_, e = c.Read(scratch[:])
	if e == nil {
		panic("tampered record accepted")
	}
	if timeout, ok := e.(net.Error); ok && timeout.Timeout() {
		panic("tampered connection not closed")
	}
	fmt.Println("PASS: native SAP, fp-setup2, pipelined upgrade, encrypted /play, fragmented record, invalid tag disconnect")
}
func main() { run(false); run(true) }
