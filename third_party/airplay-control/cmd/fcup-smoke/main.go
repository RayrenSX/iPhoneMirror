// Exercise the native encrypted reverse channel with a simulated HLS sender.
package main

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"encoding/base64"
	"encoding/xml"
	"fmt"
	"github.com/jkiddo/go-atv/internal/fairplay"
	"github.com/jkiddo/go-atv/internal/fpcontrol"
	"html"
	"io"
	"net"
	"net/http"
	"os"
	"regexp"
	"strconv"
	"strings"
	"time"
)

func check(e error) {
	if e != nil {
		panic(e)
	}
}

type channel struct {
	conn        net.Conn
	reader      *bufio.Reader
	write, read *fpcontrol.Records
}

func request(path string, body []byte) []byte {
	return append([]byte(fmt.Sprintf("POST %s HTTP/1.1\r\nHost: localhost\r\nX-Apple-Session-ID: fcup-integration-test\r\nContent-Length: %d\r\n\r\n", path, len(body))), body...)
}
func response(r *bufio.Reader, expected int) []byte {
	v, e := http.ReadResponse(r, nil)
	check(e)
	if v.StatusCode != expected {
		panic(v.Status)
	}
	b, e := io.ReadAll(v.Body)
	check(e)
	v.Body.Close()
	return b
}
func connect() *channel {
	address := "127.0.0.1:7011"
	if port := os.Getenv("IPHONE_MIRROR_TEST_PORT"); port != "" {
		address = "127.0.0.1:" + port
	}
	c, e := net.DialTimeout("tcp", address, 3*time.Second)
	check(e)
	c.SetDeadline(time.Now().Add(120 * time.Second))
	r := bufio.NewReader(c)
	s, e := fairplay.NewSender(rand.Reader)
	check(e)
	_, e = c.Write(request("/fp-setup", s.Start()))
	check(e)
	m3, e := s.Challenge(response(r, 200))
	check(e)
	_, e = c.Write(request("/fp-setup", m3))
	check(e)
	check(s.Confirm(response(r, 200)))
	var material [64]byte
	rand.Read(material[:])
	wrapped, e := s.Wrap(material, rand.Reader)
	check(e)
	_, e = c.Write(request("/fp-setup2", wrapped[:]))
	check(e)
	response(r, 200)
	w, rd, e := fpcontrol.Sender(material, nil, false)
	check(e)
	return &channel{c, r, w, rd}
}
func (c *channel) send(data []byte) {
	wire, e := c.write.Seal(data)
	check(e)
	_, e = c.conn.Write(wire)
	check(e)
}
func (c *channel) reply(status int) {
	data, e := c.read.Read(c.reader)
	check(e)
	response(bufio.NewReader(bytes.NewReader(data)), status)
}

func (c *channel) videoEventDetails() map[string]string {
	wire, e := c.read.Read(c.reader)
	check(e)
	event, e := http.ReadRequest(bufio.NewReader(bytes.NewReader(wire)))
	check(e)
	body, e := io.ReadAll(event.Body)
	check(e)
	event.Body.Close()
	if event.URL.Path != "/event" || event.Header.Get("X-Apple-Session-ID") != "fcup-integration-test" {
		panic("invalid video event identity")
	}
	values := map[string]string{}
	decoder := xml.NewDecoder(bytes.NewReader(body))
	key := ""
	for {
		token, err := decoder.Token()
		if err == io.EOF {
			break
		}
		check(err)
		if start, ok := token.(xml.StartElement); ok {
			switch start.Name.Local {
			case "key":
				check(decoder.DecodeElement(&key, &start))
			case "string", "integer":
				var value string
				check(decoder.DecodeElement(&value, &start))
				values[key] = value
			}
		}
	}
	if values["category"] != "video" || values["state"] == "" || values["sessionID"] != "1" {
		panic("invalid video event contract: " + string(body))
	}
	c.send([]byte("EVENT/1.0 200 OK\r\nContent-Length: 0\r\n\r\n"))
	return values
}
func (c *channel) videoEvent() string {
	return c.videoEventDetails()["state"]
}

// These queue decisions follow actual Apple TV event examples published by
// python-airplay's author: natural completion is stopped with reason=ended;
// loading/pause/user stop/failure must not advance the queue.
func queueHandoff(control, reverse *channel) {
	for item := 1; item <= 3; item++ {
		url := fmt.Sprintf("http://fixture.invalid/queue/%d", item)
		control.send(request("/play", []byte("Content-Location: "+url+"\r\nStart-Position: 0\r\n")))
		control.reply(200)
		for _, expected := range []string{"loading", "playing", "stopped"} {
			event := reverse.videoEventDetails()
			if event["state"] != expected {
				panic("queue item state out of order")
			}
			if expected == "stopped" && event["reason"] != "ended" {
				panic("queue cannot advance from a generic stop or failure")
			}
		}
	}
	fmt.Println("PASS: three item natural-end queue handoff on the same encrypted control/reverse connection")
}

func explicitStates(control, reverse *channel) {
	set := func(state, command, tail int, expected string) {
		url := fmt.Sprintf("http://fixture.invalid/state/%d/%d/%d", state, command, tail)
		control.send(request("/play", []byte("Content-Location: "+url+"\r\nStart-Position: 0\r\n")))
		control.reply(200)
		if expected == "" { // Unchanged state and command must not notify again.
			return
		}
		event := reverse.videoEventDetails()
		if actual := event["state"]; actual != expected {
			panic(fmt.Sprintf("state %d: expected %s got %s", state, expected, actual))
		}
		reason := ""
		if state == 6 {
			reason = "ended"
		}
		if state == 5 {
			reason = "error"
		}
		if event["reason"] != reason {
			panic(fmt.Sprintf("state %d has wrong completion reason: %s", state, event["reason"]))
		}
		control.send([]byte("GET /playback-info HTTP/1.1\r\nHost: localhost\r\nContent-Length: 0\r\n\r\n"))
		wire, e := control.read.Read(control.reader)
		check(e)
		body := response(bufio.NewReader(bytes.NewReader(wire)), 200)
		ready, empty := "true", "false"
		if state == 3 {
			ready, empty = "false", "true"
		}
		if state == 5 {
			ready = "false"
		}
		for key, value := range map[string]string{"readyToPlay": ready, "playbackBufferEmpty": empty, "playbackLikelyToKeepUp": ready} {
			if !bytes.Contains(body, []byte("<key>"+key+"</key><"+value+"/>")) {
				panic("incorrect playback-info flag: " + key + " " + string(body))
			}
		}
	}
	for command := 1; command <= 3; command++ {
		set(3, command, 0, "loading")
		set(3, command, 0, "")
		set(1, command, 0, "playing")
		set(3, command, 0, "loading")
		set(1, command, 0, "playing")
		set(3, command, 1, "loading") // Frozen tail is not completion or user pause.
		set(2, command, 1, "paused")  // A genuine pause at the end stays paused.
		set(1, command, 1, "playing")
		set(4, command, 1, "stopped") // User stop must not look like natural EOF.
		set(6, command, 1, "stopped") // Explicit natural completion advances items.
	}
	set(6, 4, 1, "stopped") // Next item with same state still has its own event.
	set(5, 4, 0, "stopped")
	set(3, 5, 0, "loading")
	// A separately authenticated reverse session must not receive this item's
	// states or observe its playback position through playback-info.
	other := connect()
	defer other.conn.Close()
	otherSession := func(wire []byte) []byte {
		return bytes.ReplaceAll(wire, []byte("fcup-integration-test"), []byte("other-session"))
	}
	other.send(otherSession(request("/reverse", nil)))
	other.reply(101)
	other.send([]byte("GET /playback-info HTTP/1.1\r\nHost: localhost\r\nX-Apple-Session-ID: other-session\r\nContent-Length: 0\r\n\r\n"))
	wire, e := other.read.Read(other.reader)
	check(e)
	body := response(bufio.NewReader(bytes.NewReader(wire)), 200)
	if !bytes.Contains(body, []byte("<key>readyToPlay</key><false/>")) || bytes.Contains(body, []byte("<key>position</key>")) {
		panic("playback-info leaked across sessions")
	}
	set(1, 5, 0, "playing")
	other.conn.SetReadDeadline(time.Now().Add(150 * time.Millisecond))
	_, e = other.read.Read(other.reader)
	if timeout, ok := e.(net.Error); !ok || !timeout.Timeout() {
		panic("inactive reverse session received another session's state")
	}
}
func main() {
	reverse := connect()
	defer reverse.conn.Close()
	reverse.send(request("/reverse", nil))
	reverse.reply(101)
	control := connect()
	defer control.conn.Close()
	if os.Getenv("IPHONE_MIRROR_TEST_EXPLICIT_STATES") == "1" {
		explicitStates(control, reverse)
		queueHandoff(control, reverse)
	}
	control.send(request("/play", []byte("Content-Location: mlhls://localhost/master.m3u8\r\nStart-Position: 0\r\n")))
	control.reply(200)
	rounds := 3
	if value, err := strconv.Atoi(os.Getenv("IPHONE_MIRROR_TEST_ROUNDS")); err == nil && value >= 3 && value <= 200 {
		rounds = value
	}
	states := map[string]int{}
	for i := 0; i < rounds; i++ {
		wire, e := reverse.read.Read(reverse.reader)
		check(e)
		event, e := http.ReadRequest(bufio.NewReader(bytes.NewReader(wire)))
		check(e)
		body, e := io.ReadAll(event.Body)
		check(e)
		event.Body.Close()
		if event.URL.Path != "/event" {
			panic("not FCUP event")
		}
		if bytes.Contains(body, []byte("<key>category</key><string>video</string>")) {
			for _, state := range []string{"loading", "playing", "paused", "stopped"} {
				if bytes.Contains(body, []byte("<key>state</key><string>"+state+"</string>")) {
					states[state]++
				}
			}
			reverse.send([]byte("EVENT/1.0 200 OK\r\nContent-Length: 0\r\n\r\n"))
			i--
			continue
		}
		if os.Getenv("IPHONE_MIRROR_TEST_EXPLICIT_STATES") == "1" && i%30 == 0 {
			// A FCUP request is in flight while video state transitions and
			// their EVENT acknowledgements share the same encrypted reverse.
			for _, state := range []int{3, 1} {
				url := fmt.Sprintf("http://fixture.invalid/state/%d/5/0", state)
				control.send(request("/play", []byte("Content-Location: "+url+"\r\nStart-Position: 0\r\n")))
				control.reply(200)
				expected := "playing"
				if state == 3 {
					expected = "loading"
				}
				if reverse.videoEvent() != expected {
					panic("lost state during FCUP request")
				}
			}
		}
		// A reverse transport is bidirectional: acknowledgements can be RTSP,
		// and control requests can arrive on the same encrypted connection.
		protocol := "HTTP/1.1"
		if i%2 == 1 {
			protocol = "RTSP/1.0"
		}
		if i%3 == 2 {
			protocol = "EVENT/1.0"
		}
		ack := []byte(protocol + " 200 OK\r\nContent-Length: 0\r\n\r\n")
		if i%7 == 0 {
			ack = append(ack, request("/action", []byte(`<?xml version="1.0"?><plist version="1.0"><dict><key>type</key><string>testControlEvent</string></dict></plist>`))...)
		}
		reverse.send(ack)
		if i%7 == 0 {
			reverse.reply(200)
		}
		id := regexp.MustCompile(`<key>FCUP_Response_RequestID</key><integer>([0-9]+)</integer>`).FindSubmatch(body)
		location := regexp.MustCompile(`<key>FCUP_Response_URL</key><string>([^<]+)</string>`).FindSubmatch(body)
		if len(id) != 2 || len(location) != 2 {
			panic("malformed FCUP event")
		}
		target := html.UnescapeString(string(location[1]))
		var playlist string
		if i == 0 && target == "mlhls://localhost/master.m3u8" {
			playlist = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100000\nvideo.m3u8\n"
		} else if i == 1 && strings.HasSuffix(target, "/video.m3u8") {
			playlist = ""
		} else if i >= 2 && strings.HasSuffix(target, "/redirected.m3u8") {
			playlist = "#EXTM3U\n#EXT-X-TARGETDURATION:2\n#EXTINF:2,\nhttps://example.invalid/segment.ts\n#EXT-X-ENDLIST\n"
		} else {
			panic("unexpected playlist request")
		}
		status := 200
		if i == 0 {
			status = 0
		} // iOS native FCUP success, distinct from HTTP status.
		action := fmt.Sprintf(`<?xml version="1.0"?><plist version="1.0"><dict><key>type</key><string>unhandledURLResponse</string><key>params</key><dict><key>FCUP_Response_RequestID</key><integer>%s</integer><key>FCUP_Response_StatusCode</key><integer>%d</integer><key>FCUP_Response_Data</key><data>%s</data></dict></dict></plist>`, id[1], status, base64.StdEncoding.EncodeToString([]byte(playlist)))
		if i == 1 {
			action = fmt.Sprintf(`<?xml version="1.0"?><plist version="1.0"><dict><key>type</key><string>unhandledURLResponse</string><key>params</key><dict><key>FCUP_Response_RequestID</key><integer>%s</integer><key>FCUP_Response_StatusCode</key><integer>302</integer><key>FCUP_Response_Headers</key><dict><key>Location</key><string>redirected.m3u8</string></dict></dict></dict></plist>`, id[1])
		}
		control.send(request("/action", []byte(action)))
		control.reply(200)
	}
	fmt.Println("PASS: encrypted video state events and reverse ACK, native status 0, FCUP 302 Location, redirected media playlist delivery")
}
