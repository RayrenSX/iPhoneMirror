package media

import (
	"net/url"
	"regexp"
	"strings"
	"testing"
	"time"
)

const trackMaster = "#EXTM3U\n#EXT-X-VERSION:7\n" +
	"#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"shared/key\"\n" +
	"#EXT-X-DEFINE:NAME=\"item\",VALUE=\"selected\"\n#EXT-X-START:TIME-OFFSET=1.25,PRECISE=YES\n" +
	"#EXT-X-INDEPENDENT-SEGMENTS\n" +
	"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"sound\",NAME=\"English\",DEFAULT=YES,URI=\"audio/stream.m3u8\"\n" +
	"#EXT-X-STREAM-INF:BANDWIDTH=2500000,AVERAGE-BANDWIDTH=2000000,CODECS=\"avc1.640028,mp4a.40.2\",RESOLUTION=1920x1080,FRAME-RATE=30,AUDIO=\"sound\",CLOSED-CAPTIONS=NONE\nvideo/stream.m3u8\n"

func TestTrackProjectionKeepsMasterContextAndSelectedTrack(t *testing.T) {
	for _, track := range []string{"video", "audio"} {
		t.Run(track, func(t *testing.T) {
			output := string(projectDesktopTrack([]byte(trackMaster), track))
			for _, global := range []string{"#EXTM3U", "#EXT-X-VERSION:7", "#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"shared/key\"", "#EXT-X-DEFINE:NAME=\"item\",VALUE=\"selected\"", "#EXT-X-START:TIME-OFFSET=1.25,PRECISE=YES", "#EXT-X-INDEPENDENT-SEGMENTS"} {
				if !strings.Contains(output, global) {
					t.Fatalf("lost master context %s", global)
				}
			}
			if strings.Count(output, "#EXT-X-STREAM-INF:") != 1 || strings.Contains(output, "#EXT-X-MEDIA:") || strings.Contains(output, `AUDIO="sound"`) {
				t.Fatalf("projected master still references extra tracks: %s", output)
			}
			if track == "video" {
				if !strings.Contains(output, `CODECS="avc1.640028"`) || !strings.Contains(output, "RESOLUTION=1920x1080") || !strings.Contains(output, "video/stream.m3u8") || strings.Contains(output, "audio/stream.m3u8") || strings.Contains(output, "mp4a") {
					t.Fatalf("incorrect video projection: %s", output)
				}
			} else if !strings.Contains(output, `BANDWIDTH=2500000,CODECS="mp4a.40.2"`) || !strings.Contains(output, "audio/stream.m3u8") || strings.Contains(output, "video/stream.m3u8") || strings.Contains(output, "RESOLUTION=") || strings.Contains(output, "avc1") {
				t.Fatalf("incorrect audio projection: %s", output)
			}
		})
	}
}

func TestTrackProjectionFallsBackWithoutChangingUnknownInputs(t *testing.T) {
	for name, input := range map[string]string{
		"media playlist":      "#EXTM3U\n#EXTINF:2,\nsegment.ts\n#EXT-X-ENDLIST\n",
		"inline audio":        strings.Replace(trackMaster, `,URI="audio/stream.m3u8"`, "", 1),
		"unknown audio":       strings.Replace(trackMaster, "mp4a.40.2", "unknown.audio", 1),
		"unknown video":       strings.Replace(trackMaster, "avc1.640028", "hvc1.1.6.L120", 1),
		"muxed codec only":    strings.Replace(trackMaster, `,AUDIO="sound"`, "", 1),
		"same URI":            strings.Replace(trackMaster, "audio/stream.m3u8", "video/stream.m3u8", 1),
		"missing group":       strings.Replace(trackMaster, `AUDIO="sound"`, `AUDIO="missing"`, 1),
		"duplicate codec":     strings.Replace(trackMaster, "mp4a.40.2", "mp4a.40.2,mp4a.40.2", 1),
		"missing codec":       strings.Replace(trackMaster, ",mp4a.40.2", "", 1),
		"duplicate key":       strings.Replace(trackMaster, "BANDWIDTH=2500000", "BANDWIDTH=2500000,BANDWIDTH=2500001", 1),
		"missing comma":       strings.Replace(trackMaster, `CODECS="avc1.640028,mp4a.40.2",`, `CODECS="avc1.640028,mp4a.40.2"`, 1),
		"invalid bandwidth":   strings.Replace(trackMaster, "BANDWIDTH=2500000", "BANDWIDTH=0", 1),
		"quoted bandwidth":    strings.Replace(trackMaster, "BANDWIDTH=2500000", `BANDWIDTH="2500000"`, 1),
		"unquoted audio URI":  strings.Replace(trackMaster, `URI="audio/stream.m3u8"`, `URI=file:///unrewritten`, 1),
		"unquoted group":      strings.Replace(trackMaster, `GROUP-ID="sound"`, "GROUP-ID=sound", 1),
		"unquoted audio link": strings.Replace(trackMaster, `AUDIO="sound"`, "AUDIO=sound", 1),
		"unquoted codecs":     strings.Replace(trackMaster, `CODECS="avc1.640028,mp4a.40.2"`, "CODECS=avc1.640028", 1),
		"unknown master tag":  trackMaster + "#EXT-X-CONTENT-STEERING:SERVER-URI=\"steering.json\"\n",
		"trailing comma":      strings.Replace(trackMaster, "CLOSED-CAPTIONS=NONE", "CLOSED-CAPTIONS=NONE,", 1),
		"extra URI":           trackMaster + "other.m3u8\n",
		"extra rendition":     trackMaster + "#EXT-X-STREAM-INF:BANDWIDTH=1,CODECS=\"avc1.640028,mp4a.40.2\",AUDIO=\"sound\"\nother.m3u8\n",
	} {
		t.Run(name, func(t *testing.T) {
			for _, track := range []string{"video", "audio", "", "invalid"} {
				if string(projectDesktopTrack([]byte(input), track)) != input {
					t.Fatalf("changed fallback input for track %q", track)
				}
			}
		})
	}
}

func TestRequestedTrackRequiresOneKnownQueryValue(t *testing.T) {
	for _, query := range []string{"", "iphonemirror_track=", "iphonemirror_track=unknown", "iphonemirror_track=Video", "iphonemirror_track=video&iphonemirror_track=audio", "iphonemirror_track=video&iphonemirror_track=video"} {
		values, _ := url.ParseQuery(query)
		if requestedDesktopTrack(values) != "" {
			t.Fatalf("accepted ambiguous track query %s", query)
		}
	}
	for _, track := range []string{"video", "audio"} {
		if requestedDesktopTrack(url.Values{trackQuery: {track}}) != track {
			t.Fatalf("rejected %s", track)
		}
	}
}

func TestBrokerTrackProjectionIsLocalAndRetainsRewrittenKey(t *testing.T) {
	b := New()
	defer b.Close()
	if err := b.Bind(1, "track-session"); err != nil {
		t.Fatal(err)
	}
	location, err := b.Start(1, "mlhls://sender/path/master.m3u8?source=original")
	if err != nil {
		t.Fatal(err)
	}
	for _, track := range []string{"video", "audio"} {
		pending := fetchResource(location + "?" + trackQuery + "=" + track)
		deadline := time.Now().Add(time.Second)
		var event []byte
		for len(event) == 0 && time.Now().Before(deadline) {
			event = b.Poll(1)
			if len(event) == 0 {
				time.Sleep(time.Millisecond)
			}
		}
		if len(event) == 0 || strings.Contains(string(event), trackQuery) || !strings.Contains(string(event), "master.m3u8?source=original") {
			t.Fatalf("local projection leaked into sender resource request: %s", event)
		}
		id := regexp.MustCompile(`<key>FCUP_Response_RequestID</key><integer>([0-9]+)</integer>`).FindSubmatch(event)
		if id == nil {
			t.Fatal("missing request ID")
		}
		var requestID uint32
		for _, digit := range id[1] {
			requestID = requestID*10 + uint32(digit-'0')
		}
		if err := b.Reply(1, wireReply(requestID, trackMaster)); err != nil {
			t.Fatal(err)
		}
		response := <-pending
		if response.status != 200 || strings.Contains(response.body, trackQuery) || strings.Contains(response.body, "mlhls:") || strings.Count(response.body, "#EXT-X-STREAM-INF:") != 1 {
			t.Fatalf("unexpected projected response: %+v", response)
		}
		b.mu.Lock()
		key := b.local(b.handles[1], "mlhls://sender/path/shared/key")
		child := b.local(b.handles[1], "mlhls://sender/path/"+track+"/stream.m3u8")
		b.mu.Unlock()
		if !strings.Contains(response.body, key) || !strings.Contains(response.body, child) {
			t.Fatalf("lost rewritten key or child context: %s", response.body)
		}
	}
}
