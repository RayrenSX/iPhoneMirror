package media

import (
	"context"
	"encoding/binary"
	"fmt"
	"net/url"
	"os"
	"os/exec"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestFFmpegBrokerProjectedInputsReadOnlyTheirTrack(t *testing.T) {
	ffmpeg := os.Getenv("IPHONE_MIRROR_TEST_FFMPEG")
	if ffmpeg == "" {
		t.Skip("set IPHONE_MIRROR_TEST_FFMPEG to bundled FFmpeg")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 40*time.Second)
	defer cancel()
	run := func(arguments ...string) []byte {
		t.Helper()
		command := exec.CommandContext(ctx, ffmpeg, arguments...)
		var errors strings.Builder
		command.Stderr = &errors
		output, err := command.Output()
		if err != nil {
			t.Fatalf("FFmpeg failed: %v %s", err, errors.String())
		}
		return output
	}
	video := run("-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25", "-t", "3", "-c:v", "libx264", "-g", "25", "-f", "mpegts", "pipe:1")
	audio := run("-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3", "-c:a", "aac", "-f", "mpegts", "pipe:1")
	master := "#EXTM3U\n#EXT-X-VERSION:3\n" +
		"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",DEFAULT=YES,URI=\"audio.m3u8\"\n" +
		"#EXT-X-STREAM-INF:BANDWIDTH=500000,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"audio\"\nvideo.m3u8\n"
	assets := map[string][]byte{
		"/master.m3u8": []byte(master),
		"/video.m3u8":  []byte("#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\nvideo.ts\n#EXT-X-ENDLIST\n"),
		"/audio.m3u8":  []byte("#EXTM3U\n#EXT-X-TARGETDURATION:3\n#EXTINF:3,\naudio.ts\n#EXT-X-ENDLIST\n"),
		"/video.ts":    video,
		"/audio.ts":    audio,
	}
	b := New()
	defer b.Close()
	if err := b.Bind(1, "projected-ffmpeg"); err != nil {
		t.Fatal(err)
	}
	location, err := b.Start(1, "mlhls://fixture/master.m3u8")
	if err != nil {
		t.Fatal(err)
	}
	var mu sync.Mutex
	visited := map[string]int{}
	phoneErrors := make(chan error, 1)
	done := make(chan struct{})
	go func() {
		defer close(done)
		idPattern := regexp.MustCompile(`<key>FCUP_Response_RequestID</key><integer>([0-9]+)</integer>`)
		urlPattern := regexp.MustCompile(`<key>FCUP_Response_URL</key><string>([^<]+)</string>`)
		for ctx.Err() == nil {
			event := b.Poll(1)
			if len(event) == 0 {
				time.Sleep(time.Millisecond)
				continue
			}
			idText, locationText := idPattern.FindSubmatch(event), urlPattern.FindSubmatch(event)
			if idText == nil || locationText == nil {
				phoneErrors <- fmt.Errorf("unrecognized fixture event")
				return
			}
			requestID, _ := strconv.ParseUint(string(idText[1]), 10, 32)
			resource, err := url.Parse(string(locationText[1]))
			if err != nil || resource.RawQuery != "" {
				phoneErrors <- fmt.Errorf("local projection query leaked into sender URL")
				return
			}
			body, exists := assets[resource.Path]
			if !exists {
				phoneErrors <- fmt.Errorf("unexpected resource %s", resource.Path)
				return
			}
			mu.Lock()
			visited[resource.Path]++
			mu.Unlock()
			wire := make([]byte, 8+len(body))
			binary.BigEndian.PutUint32(wire, uint32(requestID))
			binary.BigEndian.PutUint32(wire[4:], 200)
			copy(wire[8:], body)
			if err := b.Reply(1, wire); err != nil {
				phoneErrors <- err
				return
			}
		}
	}()
	defer func() { cancel(); <-done }()
	for _, track := range []string{"video", "audio"} {
		mu.Lock()
		visited = map[string]int{}
		mu.Unlock()
		mapArgument := "0:v:0"
		other := "audio"
		if track == "audio" {
			mapArgument, other = "0:a:0", "video"
		}
		output := run("-v", "error", "-extension_picky", "0", "-http_persistent", "0", "-i", location+"?"+trackQuery+"="+track,
			"-map", mapArgument, "-c", "copy", "-f", "mpegts", "pipe:1")
		path := t.TempDir() + "/" + track + ".ts"
		if err := os.WriteFile(path, output, 0600); err != nil {
			t.Fatal(err)
		}
		if track == "video" {
			frames := run("-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-")
			count := 0
			for _, line := range strings.Split(string(frames), "\n") {
				if line != "" && !strings.HasPrefix(line, "#") {
					count++
				}
			}
			if count != 75 {
				t.Fatalf("projected video lost frames: %d", count)
			}
		} else {
			pcm := run("-v", "error", "-i", path, "-map", "0:a:0", "-ar", "48000", "-ac", "1", "-f", "s16le", "-")
			if seconds := float64(len(pcm)) / 96000; seconds < 3 || seconds > 3.05 {
				t.Fatalf("projected audio was truncated: %.3fs", seconds)
			}
		}
		mu.Lock()
		if visited["/master.m3u8"] == 0 || visited["/"+track+".m3u8"] == 0 || visited["/"+track+".ts"] == 0 ||
			visited["/"+other+".m3u8"] != 0 || visited["/"+other+".ts"] != 0 {
			t.Fatalf("input %s fetched another track: %v", track, visited)
		}
		t.Logf("input %s fetches only its master, media playlist and segment; output is complete", track)
		mu.Unlock()
		select {
		case err := <-phoneErrors:
			t.Fatal(err)
		default:
		}
	}
}
