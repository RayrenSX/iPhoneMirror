package media

import (
	"context"
	"fmt"
	"net/http"
	"net/http/httptest"
	"os"
	"os/exec"
	"strings"
	"sync"
	"testing"
	"time"
)

func TestFFmpegProbesOnlySelectedRendition(t *testing.T) {
	ffmpeg := os.Getenv("IPHONE_MIRROR_TEST_FFMPEG")
	if ffmpeg == "" {
		t.Skip("set IPHONE_MIRROR_TEST_FFMPEG to bundled FFmpeg")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()
	segment, err := exec.CommandContext(ctx, ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25", "-t", "1", "-c:v", "libx264", "-f", "mpegts", "pipe:1").Output()
	if err != nil {
		t.Fatal(err)
	}
	master := "#EXTM3U\n"
	for i := 0; i < 17; i++ {
		master += fmt.Sprintf("#EXT-X-STREAM-INF:BANDWIDTH=%d,CODECS=\"avc1.4D4015\"\nv%d.m3u8\n", (i+1)*100000, i)
	}
	for _, filtered := range []bool{false, true} {
		var mu sync.Mutex
		visited := map[string]bool{}
		server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
			switch {
			case r.URL.Path == "/master.m3u8":
				data := []byte(master)
				if filtered {
					data = selectDesktopVariant(data)
				}
				w.Write(data)
			case strings.HasSuffix(r.URL.Path, ".m3u8"):
				mu.Lock()
				visited[r.URL.Path] = true
				mu.Unlock()
				fmt.Fprint(w, "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\npart.ts\n#EXT-X-ENDLIST\n")
			default:
				w.Write(segment)
			}
		}))
		result, err := exec.CommandContext(ctx, ffmpeg, "-v", "error", "-i", server.URL+"/master.m3u8", "-map", "0:v:0", "-t", "0.2", "-f", "null", "-").CombinedOutput()
		server.Close()
		if err != nil {
			t.Fatalf("decode failed: %v %s", err, result)
		}
		mu.Lock()
		count := len(visited)
		mu.Unlock()
		if (!filtered && count != 17) || (filtered && count != 1) {
			t.Fatalf("filtered=%t probed=%d", filtered, count)
		}
		t.Logf("filtered=%t: decoded successfully, probed %d rendition playlists", filtered, count)
	}
}

func TestFFmpegProbesOnlySenderDefaultAudio(t *testing.T) {
	ffmpeg := os.Getenv("IPHONE_MIRROR_TEST_FFMPEG")
	if ffmpeg == "" {
		t.Skip("set IPHONE_MIRROR_TEST_FFMPEG to bundled FFmpeg")
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	video, err := exec.CommandContext(ctx, ffmpeg, "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x120:rate=25", "-t", "1", "-c:v", "libx264", "-f", "mpegts", "pipe:1").Output()
	if err != nil {
		t.Fatal(err)
	}
	audio, err := exec.CommandContext(ctx, ffmpeg, "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=44100", "-t", "1", "-c:a", "aac", "-f", "mpegts", "pipe:1").Output()
	if err != nil {
		t.Fatal(err)
	}
	master := "#EXTM3U\n"
	for _, language := range []string{"de-DE", "es-US", "fr-FR", "hi", "id", "it", "pt-BR", "en-US"} {
		isDefault := "NO"
		if language == "en-US" {
			isDefault = "YES"
		}
		master += fmt.Sprintf("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"%s\",LANGUAGE=\"%s\",DEFAULT=%s,AUTOSELECT=YES,URI=\"%s.m3u8\"\n", language, language, isDefault, language)
	}
	master += "#EXT-X-STREAM-INF:BANDWIDTH=200000,CODECS=\"avc1.4D4015,mp4a.40.2\",AUDIO=\"audio\"\nvideo.m3u8\n"
	var mu sync.Mutex
	visited := map[string]bool{}
	server := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case r.URL.Path == "/master.m3u8":
			w.Write(selectDesktopVariant([]byte(master)))
		case strings.HasSuffix(r.URL.Path, ".m3u8"):
			mu.Lock()
			visited[r.URL.Path] = true
			mu.Unlock()
			part := "audio.ts"
			if r.URL.Path == "/video.m3u8" {
				part = "video.ts"
			}
			fmt.Fprintf(w, "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\n%s\n#EXT-X-ENDLIST\n", part)
		case r.URL.Path == "/video.ts":
			w.Write(video)
		case r.URL.Path == "/audio.ts":
			w.Write(audio)
		default:
			http.NotFound(w, r)
		}
	}))
	defer server.Close()
	result, err := exec.CommandContext(ctx, ffmpeg, "-v", "error", "-i", server.URL+"/master.m3u8", "-map", "0:v:0", "-map", "0:a:0", "-t", "0.2", "-f", "null", "-").CombinedOutput()
	if err != nil {
		t.Fatalf("audio/video decode failed: %v %s", err, result)
	}
	mu.Lock()
	defer mu.Unlock()
	if len(visited) != 2 || !visited["/video.m3u8"] || !visited["/en-US.m3u8"] {
		t.Fatalf("probed unexpected audio languages: %v", visited)
	}
	t.Log("decoded video and sender-default English audio; skipped seven unused language playlists")
}
