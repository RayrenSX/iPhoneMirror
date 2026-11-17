package media

import (
	"fmt"
	"strings"
	"testing"
)

func TestDesktopVariantRetainsMatchingAudioAndSessionKey(t *testing.T) {
	input := "#EXTM3U\n#EXT-X-SESSION-KEY:METHOD=AES-128,URI=\"key\"\n" +
		"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"low\",URI=\"low-audio\"\n" +
		"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"high\",URI=\"high-audio\"\n" +
		"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"text\",URI=\"subtitles\"\n" +
		"#EXT-X-STREAM-INF:BANDWIDTH=10,CODECS=\"avc1.123,mp4a.40.2\",AUDIO=\"low\"\nlow-video\n" +
		"#EXT-X-STREAM-INF:BANDWIDTH=20,CODECS=\"avc1.456,mp4a.40.2\",AUDIO=\"high\",SUBTITLES=\"text\"\nhigh-video\n" +
		"#EXT-X-STREAM-INF:BANDWIDTH=30,CODECS=\"vp09.123,mp4a.40.2\",AUDIO=\"high\"\nvp9-video\n"
	output := string(selectDesktopVariant([]byte(input)))
	for _, keep := range []string{"SESSION-KEY", "high-video", "high-audio"} {
		if !strings.Contains(output, keep) {
			t.Fatalf("lost %s", keep)
		}
	}
	for _, drop := range []string{"low-video", "low-audio", "vp9-video", "SUBTITLES", "subtitles"} {
		if strings.Contains(output, drop) {
			t.Fatalf("retained %s", drop)
		}
	}
	if strings.Count(output, "#EXT-X-STREAM-INF:") != 1 {
		t.Fatal("not one rendition")
	}
}

func TestDesktopVariantLeavesMediaAndUnknownMastersIntact(t *testing.T) {
	for _, input := range []string{
		"#EXTM3U\n#EXTINF:2,\nsegment.ts\n#EXT-X-ENDLIST\n",
		"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=20,CODECS=\"hvc1.123\"\nhevc\n",
		"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=20,CODECS=\"avc1.123\",AUDIO=\"missing\"\nvideo\n",
	} {
		if string(selectDesktopVariant([]byte(input))) != input {
			t.Fatal("changed unsupported/media playlist")
		}
	}
}

func TestDesktopVariantSelectsOneSenderAudioLanguage(t *testing.T) {
	for _, tc := range []struct {
		name, first, second, third, want string
	}{
		{"default", "AUTOSELECT=YES", "DEFAULT=YES,AUTOSELECT=YES", "AUTOSELECT=YES", "a1.m3u8"},
		{"autoselect", "AUTOSELECT=NO", "AUTOSELECT=YES", "AUTOSELECT=YES", "a1.m3u8"},
		{"first fallback", "", "", "", "a0.m3u8"},
	} {
		t.Run(tc.name, func(t *testing.T) {
			input := "#EXTM3U\n"
			for i, options := range []string{tc.first, tc.second, tc.third} {
				input += fmt.Sprintf("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"sound\",NAME=\"language%d\",URI=\"a%d.m3u8\",%s\n", i, i, options)
			}
			input += "#EXT-X-STREAM-INF:BANDWIDTH=20,CODECS=\"avc1.456,mp4a.40.2\",AUDIO=\"sound\"\nvideo.m3u8\n"
			output := string(selectDesktopVariant([]byte(input)))
			if strings.Count(output, "#EXT-X-MEDIA:") != 1 || !strings.Contains(output, tc.want) {
				t.Fatalf("wrong language selection: %s", output)
			}
		})
	}
}
