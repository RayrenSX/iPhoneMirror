package media

import (
	"encoding/binary"
	"io"
	"net/http"
	"regexp"
	"strconv"
	"strings"
	"testing"
	"time"
)

func TestRewriteMasterAndMediaURIs(t *testing.T) {
	b := New()
	defer b.Close()
	if e := b.Bind(1, "test"); e != nil {
		t.Fatal(e)
	}
	_, e := b.Start(1, "mlhls://localhost/master.m3u8")
	if e != nil {
		t.Fatal(e)
	}
	source := []byte("#EXTM3U\n#EXT-X-MEDIA:TYPE=AUDIO,URI=\"audio.m3u8?lang=en\"\nvideo.m3u8\nhttps://cdn.example/video.ts\n")
	data, e := b.rewrite(b.handles[1], "mlhls://localhost/master.m3u8", source)
	if e != nil {
		t.Fatal(e)
	}
	if strings.Count(string(data), "http://127.0.0.1:") != 2 || !strings.Contains(string(data), "https://cdn.example/video.ts") {
		t.Fatal("URI rewrite mismatch")
	}
	if _, e = b.rewrite(b.handles[1], "mlhls://localhost/master.m3u8", []byte("#EXTM3U\nfile:///secret\n")); e == nil {
		t.Fatal("file URI accepted")
	}
}

type httpResult struct {
	status int
	body   string
}

func fetchResource(location string) <-chan httpResult {
	done := make(chan httpResult, 1)
	go func() {
		client := http.Client{Timeout: 3 * time.Second}
		r, err := client.Get(location)
		if err != nil {
			done <- httpResult{body: err.Error()}
			return
		}
		defer r.Body.Close()
		body, _ := io.ReadAll(r.Body)
		done <- httpResult{r.StatusCode, string(body)}
	}()
	return done
}

func nextRequest(t *testing.T, b *Broker, handle uint64) uint32 {
	t.Helper()
	deadline := time.Now().Add(time.Second)
	for time.Now().Before(deadline) {
		if event := b.Poll(handle); len(event) > 0 {
			id := regexp.MustCompile(`<key>FCUP_Response_RequestID</key><integer>([0-9]+)</integer>`).FindSubmatch(event)
			if id == nil {
				t.Fatalf("missing request ID: %s", event)
			}
			n, err := strconv.ParseUint(string(id[1]), 10, 32)
			if err != nil {
				t.Fatal(err)
			}
			return uint32(n)
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatal("no FCUP event")
	return 0
}

func wireReply(id uint32, body string) []byte {
	wire := make([]byte, len(body)+8)
	binary.BigEndian.PutUint32(wire, id)
	binary.BigEndian.PutUint32(wire[4:], 200)
	copy(wire[8:], body)
	return wire
}

func TestSessionControlHandoffKeepsReverseResources(t *testing.T) {
	b := New()
	defer b.Close()
	for _, handle := range []uint64{1, 2} {
		if err := b.Bind(handle, "handoff"); err != nil {
			t.Fatal(err)
		}
	}
	location, err := b.Start(1, "mlhls://localhost/ad.m3u8")
	if err != nil {
		t.Fatal(err)
	}
	ad := fetchResource(location)
	id := nextRequest(t, b, 2)
	b.Detach(1) // The reverse connection still owns the media session.
	if err := b.Bind(3, "handoff"); err != nil {
		t.Fatal(err)
	}
	if err := b.Reply(3, wireReply(id, "#EXTM3U\n#EXTINF:6,\nhttps://cdn.example/ad.ts\n#EXT-X-ENDLIST\n")); err != nil {
		t.Fatal(err)
	}
	if response := <-ad; response.status != 200 || !strings.Contains(response.body, "ad.ts") {
		t.Fatalf("control handoff lost ad request: %+v", response)
	}
	stale := fetchResource(location)
	id = nextRequest(t, b, 2)
	newLocation, err := b.Start(3, "mlhls://localhost/content.m3u8")
	if err != nil {
		t.Fatal(err)
	}
	if response := <-stale; response.status != 410 {
		t.Fatalf("replaced item still active: %+v", response)
	}
	if err := b.Reply(3, wireReply(id, "old item")); err == nil {
		t.Fatal("late replaced-item response accepted")
	}
	if response := <-fetchResource(location); response.status != 404 {
		t.Fatalf("replaced URL still active: %+v", response)
	}
	content := fetchResource(newLocation)
	id = nextRequest(t, b, 2)
	if err := b.Reply(3, wireReply(id, "content")); err != nil {
		t.Fatal(err)
	}
	if response := <-content; response.status != 200 || response.body != "content" {
		t.Fatalf("content request failed: %+v", response)
	}
}

func TestReconnectCannotDeliverOldReplyToNewItem(t *testing.T) {
	b := New()
	defer b.Close()
	if err := b.Bind(1, "reconnected"); err != nil {
		t.Fatal(err)
	}
	location, err := b.Start(1, "mlhls://localhost/ad.m3u8")
	if err != nil {
		t.Fatal(err)
	}
	old := fetchResource(location)
	oldID := nextRequest(t, b, 1)
	b.Detach(1)
	if response := <-old; response.status != 410 {
		t.Fatalf("disconnected item not cancelled: %+v", response)
	}
	if err := b.Bind(2, "reconnected"); err != nil {
		t.Fatal(err)
	}
	location, err = b.Start(2, "mlhls://localhost/content.m3u8")
	if err != nil {
		t.Fatal(err)
	}
	current := fetchResource(location)
	newID := nextRequest(t, b, 2)
	if err := b.Reply(2, wireReply(oldID, "wrong ad playlist")); err == nil {
		t.Fatal("late old-session reply accepted for current item")
	}
	if err := b.Reply(2, wireReply(newID, "correct content playlist")); err != nil {
		t.Fatal(err)
	}
	if response := <-current; response.status != 200 || response.body != "correct content playlist" {
		t.Fatalf("old session contaminated content: %+v", response)
	}
}

func TestSessionIsolationAndCancellation(t *testing.T) {
	b := New()
	defer b.Close()
	b.Bind(1, "one")
	b.Bind(2, "two")
	location, e := b.Start(1, "mlhls://localhost/master.m3u8")
	if e != nil {
		t.Fatal(e)
	}
	done := make(chan int, 1)
	go func() {
		c := http.Client{Timeout: 3 * time.Second}
		r, e := c.Get(location)
		if e != nil {
			done <- 0
			return
		}
		io.Copy(io.Discard, r.Body)
		r.Body.Close()
		done <- r.StatusCode
	}()
	deadline := time.Now().Add(time.Second)
	for len(b.Poll(1)) == 0 {
		if time.Now().After(deadline) {
			t.Fatal("no FCUP event")
		}
		time.Sleep(time.Millisecond)
	}
	if e = b.Reply(2, []byte{0, 0, 0, 1, 0, 0, 0, 200}); e == nil {
		t.Fatal("other session answered request")
	}
	b.Detach(1)
	if status := <-done; status != 410 {
		t.Fatalf("pending request not cancelled: %d", status)
	}
	if e = b.Bind(2, "changed"); e == nil {
		t.Fatal("connection rebound to another session")
	}
}
