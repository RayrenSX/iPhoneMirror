# Device frame sources

Apple exports come from Apple Design Resources and the pinned fastlane/frameit-frames,
jonnyjackson26/device-frames-media and TimBroddin/appleframer.com collections.
See the included Apple Design Resources License.rtf. Each asset's original URL
and SHA-256 is recorded in config/apple-device-frames.json in the source tree.

iPad (10th generation) uses the same unmodified A16 front-view artwork after
review of official dimensions, panel specifications, front camera/button layout
and named finishes. This is reviewed front-view reuse, not a dedicated iPad 10
bezel export or physical-device color measurement. The source references and
scope are recorded in config/device-frame-shared-fronts.json. Housing numeric
codes are kept separate for each ProductType.

The four older model-specific vector profiles come from pixelsign/html5-device-mockups.
Pinned revision and exact source/blob URLs are recorded in config/device-frame-supplements.json.
Their original publisher specified screen rectangles in src/scss/devices.scss.
Only that rectangle is made transparent; bezel pixels are preserved. Both original
and derived PNG SHA-256 values and the transformation coordinates are recorded.
These are credited vector renderings, not Apple bezel exports or true-device measurements.
See pixelsign-LICENSE.txt and pixelsign-CREDITS.txt for the upstream license and credits.

The first-generation iPad Pro asset was added in commit
7169a948288b197c1037dc56ca1a234df1dc467b on 2017-04-22, before the June 2017
second-generation release. Ambiguously labeled older iPad artwork was excluded.

The application embeds these graphics for optional local device-frame display.
The application's source-code license does not relicense Apple or other third-party artwork.

Original custom-drawn frames are separate from the third-party artwork above.
Their editable SVGs are in src/App/Assets/DeviceFrameDrawings and their generator
is scripts/draw_device_frames.py. They and their generated PNGs use the project's
source-code license. config/device-frame-drawings.json records model-specific
official dimensions/reference URLs and the limits of photo-estimated details.
Apple reference photographs are not bundled or raster-traced. These drawings
are not Apple bezel exports and have not been physically calibrated.
