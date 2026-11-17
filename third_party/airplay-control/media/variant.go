// SPDX-License-Identifier: LGPL-3.0-or-later
package media

import (
	"regexp"
	"strconv"
	"strings"
)

var attribute = regexp.MustCompile(`(?:^|,)([A-Z0-9-]+)=("[^"]*"|[^,]*)`)

func attributes(line string) map[string]string {
	result := map[string]string{}
	_, value, _ := strings.Cut(line, ":")
	for _, m := range attribute.FindAllStringSubmatch(value, -1) {
		result[m[1]] = strings.Trim(m[2], `"`)
	}
	return result
}

// The desktop bridge does not perform adaptive rendition switching. FFmpeg
// probes every rendition before producing its first packet, even with -map.
// Pick one AVC rendition and its audio group before that probe. Preserve
// unfamiliar codec-only masters rather than guessing their compatibility.
func selectDesktopVariant(data []byte) []byte {
	lines := strings.Split(string(data), "\n")
	best, bestBandwidth := -1, int64(-1)
	for i, line := range lines {
		if !strings.HasPrefix(line, "#EXT-X-STREAM-INF:") || i+1 >= len(lines) {
			continue
		}
		a := attributes(line)
		avc := false
		for _, codec := range strings.Split(a["CODECS"], ",") {
			avc = avc || strings.HasPrefix(codec, "avc1.") || strings.HasPrefix(codec, "avc3.")
		}
		uri := strings.TrimSpace(lines[i+1])
		bandwidth, err := strconv.ParseInt(a["BANDWIDTH"], 10, 64)
		if avc && err == nil && bandwidth > bestBandwidth && uri != "" && !strings.HasPrefix(uri, "#") {
			best, bestBandwidth = i, bandwidth
		}
	}
	if best < 0 {
		return data
	}
	chosen := attributes(lines[best])
	// FFmpeg probes every audio rendition as well as every video variant. Keep
	// the sender's default language (then AUTOSELECT, then the first rendition)
	// so the desktop bridge neither fetches all languages nor maps the first
	// language in the master instead of the sender's default.
	audio, audioPriority := -1, -1
	for i, line := range lines {
		if strings.HasPrefix(line, "#EXT-X-MEDIA:") {
			a := attributes(line)
			if a["TYPE"] == "AUDIO" && a["GROUP-ID"] == chosen["AUDIO"] {
				priority := 0
				if a["AUTOSELECT"] == "YES" {
					priority = 1
				}
				if a["DEFAULT"] == "YES" {
					priority = 2
				}
				if priority > audioPriority {
					audio, audioPriority = i, priority
				}
			}
		}
	}
	// Don't remove a referenced audio group if the master is malformed.
	if chosen["AUDIO"] != "" && audio < 0 {
		return data
	}
	out := make([]string, 0, len(lines))
	for i := 0; i < len(lines); i++ {
		line := lines[i]
		if strings.HasPrefix(line, "#EXT-X-STREAM-INF:") {
			if i == best {
				line = regexp.MustCompile(`,SUBTITLES="[^"]*"`).ReplaceAllString(line, "")
				out = append(out, line, lines[i+1])
			}
			i++
			continue
		}
		if strings.HasPrefix(line, "#EXT-X-I-FRAME-STREAM-INF:") {
			continue
		}
		if strings.HasPrefix(line, "#EXT-X-MEDIA:") {
			if i != audio {
				continue
			}
		}
		out = append(out, line)
	}
	return []byte(strings.Join(out, "\n"))
}
