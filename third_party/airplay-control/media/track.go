// SPDX-License-Identifier: LGPL-3.0-or-later
package media

import (
	"net/url"
	"regexp"
	"strconv"
	"strings"
)

const trackQuery = "iphonemirror_track"

type playlistAttribute struct {
	key, raw, value string
}

// Unlike the rendition selector's permissive reader, a projection must fully
// understand the attribute list it changes. An unfamiliar/malformed master is
// still a valid fallback input to the ordinary two-demuxer path.
func trackAttributes(line, prefix string) ([]playlistAttribute, bool) {
	if !strings.HasPrefix(line, prefix) {
		return nil, false
	}
	rest := strings.TrimPrefix(line, prefix)
	var result []playlistAttribute
	seen := map[string]bool{}
	for rest != "" {
		key, tail, ok := strings.Cut(rest, "=")
		if !ok || key == "" || seen[key] {
			return nil, false
		}
		for _, c := range key {
			if !(c >= 'A' && c <= 'Z' || c >= '0' && c <= '9' || c == '-') {
				return nil, false
			}
		}
		seen[key] = true
		var raw string
		if strings.HasPrefix(tail, `"`) {
			end := strings.Index(tail[1:], `"`)
			if end < 0 {
				return nil, false
			}
			raw, rest = tail[:end+2], tail[end+2:]
		} else {
			raw, rest, _ = strings.Cut(tail, ",")
			if raw == "" || strings.ContainsAny(raw, "\"\r\n\t ") {
				return nil, false
			}
			// strings.Cut already consumed the separator in this branch.
			result = append(result, playlistAttribute{key, raw, strings.Trim(raw, `"`)})
			if rest == "" && strings.HasSuffix(tail, ",") {
				return nil, false
			}
			continue
		}
		result = append(result, playlistAttribute{key, raw, strings.Trim(raw, `"`)})
		if rest != "" {
			if !strings.HasPrefix(rest, ",") || len(rest) == 1 {
				return nil, false
			}
			rest = rest[1:]
		}
	}
	return result, len(result) > 0
}

func trackAttribute(attributes []playlistAttribute, key string) string {
	for _, attribute := range attributes {
		if attribute.key == key {
			return attribute.value
		}
	}
	return ""
}

func trackAttributeQuoted(attributes []playlistAttribute, key string) bool {
	for _, attribute := range attributes {
		if attribute.key == key {
			return strings.HasPrefix(attribute.raw, `"`) && strings.HasSuffix(attribute.raw, `"`)
		}
	}
	return false
}

var trackAVC = regexp.MustCompile(`^avc[13]\.[0-9a-fA-F]{6}$`)

func trackCodecs(value string) (video, audio string, ok bool) {
	for _, codec := range strings.Split(value, ",") {
		codec = strings.TrimSpace(codec)
		switch {
		case trackAVC.MatchString(codec) && video == "":
			video = codec
		case (codec == "mp4a.40.2" || codec == "mp4a.40.5" || codec == "mp4a.40.29") && audio == "":
			audio = codec
		default:
			return "", "", false
		}
	}
	return video, audio, video != "" && audio != ""
}

// projectDesktopTrack runs after rendition selection and URI rewriting. Keep
// the original master context (session keys, variables, start position, etc.)
// while preventing each independent FFmpeg input from probing both tracks.
func projectDesktopTrack(data []byte, track string) []byte {
	if track != "video" && track != "audio" {
		return data
	}
	lines := strings.Split(string(data), "\n")
	variant := -1
	var stream []playlistAttribute
	for index, line := range lines {
		line = strings.TrimSpace(line)
		if strings.HasPrefix(line, "#EXT") {
			known := line == "#EXTM3U" || line == "#EXT-X-INDEPENDENT-SEGMENTS"
			for _, prefix := range []string{"#EXT-X-VERSION:", "#EXT-X-SESSION-KEY:", "#EXT-X-SESSION-DATA:", "#EXT-X-DEFINE:", "#EXT-X-START:", "#EXT-X-MEDIA:", "#EXT-X-STREAM-INF:"} {
				known = known || strings.HasPrefix(line, prefix)
			}
			if !known {
				return data
			}
		}
		if strings.HasPrefix(line, "#EXT-X-STREAM-INF:") {
			var ok bool
			if variant >= 0 || index+1 >= len(lines) {
				return data
			}
			stream, ok = trackAttributes(line, "#EXT-X-STREAM-INF:")
			if !ok {
				return data
			}
			variant = index
		}
	}
	if variant < 0 {
		return data
	}
	group := trackAttribute(stream, "AUDIO")
	bandwidth := trackAttribute(stream, "BANDWIDTH")
	peak, bandwidthError := strconv.ParseInt(bandwidth, 10, 64)
	videoCodec, audioCodec, ok := trackCodecs(trackAttribute(stream, "CODECS"))
	videoURI := strings.TrimSpace(lines[variant+1])
	if !ok || group == "" || bandwidthError != nil || peak <= 0 || videoURI == "" || strings.HasPrefix(videoURI, "#") ||
		!trackAttributeQuoted(stream, "CODECS") || !trackAttributeQuoted(stream, "AUDIO") || trackAttributeQuoted(stream, "BANDWIDTH") ||
		trackAttribute(stream, "VIDEO") != "" || trackAttribute(stream, "SUBTITLES") != "" ||
		(trackAttribute(stream, "CLOSED-CAPTIONS") != "" && trackAttribute(stream, "CLOSED-CAPTIONS") != "NONE") {
		return data
	}
	for index, line := range lines {
		line = strings.TrimSpace(line)
		if line != "" && !strings.HasPrefix(line, "#") && index != variant+1 {
			return data
		}
	}
	audioURI := ""
	for _, line := range lines {
		line = strings.TrimSpace(line)
		if !strings.HasPrefix(line, "#EXT-X-MEDIA:") {
			continue
		}
		attributes, valid := trackAttributes(line, "#EXT-X-MEDIA:")
		if !valid || trackAttribute(attributes, "TYPE") != "AUDIO" ||
			trackAttribute(attributes, "GROUP-ID") != group || audioURI != "" ||
			!trackAttributeQuoted(attributes, "GROUP-ID") || !trackAttributeQuoted(attributes, "URI") {
			return data
		}
		audioURI = trackAttribute(attributes, "URI")
		if audioURI == "" || audioURI == videoURI {
			return data
		}
	}
	if audioURI == "" {
		return data
	}
	var projectedStream, projectedURI string
	if track == "video" {
		var attributes []string
		for _, attribute := range stream {
			if attribute.key == "AUDIO" {
				continue
			}
			value := attribute.raw
			if attribute.key == "CODECS" {
				value = `"` + videoCodec + `"`
			}
			attributes = append(attributes, attribute.key+"="+value)
		}
		projectedStream = "#EXT-X-STREAM-INF:" + strings.Join(attributes, ",")
		projectedURI = videoURI
	} else {
		// The aggregate peak is a safe upper bound. There is only one rendition
		// here, so it cannot cause a bitrate/quality selection change.
		projectedStream = "#EXT-X-STREAM-INF:BANDWIDTH=" + bandwidth + ",CODECS=\"" + audioCodec + "\""
		projectedURI = audioURI
	}
	out := make([]string, 0, len(lines))
	for index := 0; index < len(lines); index++ {
		line := lines[index]
		trimmed := strings.TrimSpace(line)
		if strings.HasPrefix(trimmed, "#EXT-X-MEDIA:") {
			continue
		}
		if index == variant {
			out = append(out, projectedStream, projectedURI)
			index++
			continue
		}
		out = append(out, line)
	}
	return []byte(strings.Join(out, "\n"))
}

func requestedDesktopTrack(query url.Values) string {
	values := query[trackQuery]
	if len(values) != 1 || values[0] != "video" && values[0] != "audio" {
		return ""
	}
	return values[0]
}
